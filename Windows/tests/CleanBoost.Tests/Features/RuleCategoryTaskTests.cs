using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.SystemJunk;

namespace CleanBoost.Tests.Features;

/// <summary>Gom kết quả rule thành cây hiển thị (mục 11.2): theo rule, theo app, tên hiển thị.</summary>
public class RuleCategoryTaskGroupingTests
{
    private static readonly PathResolver R = PathResolver.Fixture(@"C:\fixture");

    private static Node Leaf(string path, long size, string? rule = "r1", string? title = null)
    {
        var n = Node.ForPath(path, true, title);
        n.Size = new ByteCount(size);
        n.RuleID = rule;
        n.Safety = SafetyLevel.Safe;
        return n;
    }

    private static InstalledApp App(string id, string name, string? installLocation = null) => new()
    {
        Id = "desktop:" + id,
        AppID = id,
        Name = name,
        InstallLocation = installLocation,
    };

    private static CompiledRule Compiled(string id, string? title = null, string? reason = null) =>
        new(TestRules.Make(id, paths: [@"%TEMP%\x"], title: title is null ? null : new LocalizedText(title, title),
            reason: reason is null ? null : new LocalizedText(reason, reason)), R);

    [Fact]
    public void SingleRuleGivesFlatListSortedBySize()
    {
        var leaves = new List<Node> { Leaf(@"C:\a", 10), Leaf(@"C:\b", 30), Leaf(@"C:\c", 20) };
        var result = RuleCategoryTask.GroupByRule(leaves, [Compiled("r1")]);
        Assert.Equal(new[] { 30L, 20L, 10L }, result.Select(n => n.Size.Bytes));
        Assert.All(result, n => Assert.False(n.IsContainer));
    }

    [Fact]
    public void MultipleRulesGroupRulesWithSeveralLeaves()
    {
        var leaves = new List<Node>
        {
            Leaf(@"C:\x\one", 5, "r1"), Leaf(@"C:\x\two", 7, "r1"),
            Leaf(@"C:\y\Cache", 100, "r2"),
            Leaf(@"C:\z\orphan", 1, rule: null),
        };
        var result = RuleCategoryTask.GroupByRule(leaves, [Compiled("r1", "Rule One", "Vì sao"), Compiled("r2", "Rule Two")]);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { 100L, 12L, 1L }, result.Select(n => n.Size.Bytes));
        var single = result[0];
        Assert.False(single.IsContainer);
        Assert.Equal("Rule Two", single.Title); // một lá mang tên thư mục → đổi sang tên rule
        var group = result[1];
        Assert.True(group.IsContainer);
        Assert.Equal("Rule One", group.Title);
        Assert.Equal("Vì sao", group.Reason.Resolved);
        Assert.Equal(new[] { 7L, 5L }, group.Children.Select(c => c.Size.Bytes));
        Assert.Null(result[2].RuleID);
    }

    [Fact]
    public void SingleLeafKeepsCustomTitle()
    {
        var leaves = new List<Node> { Leaf(@"C:\y\Cache", 100, "r2", title: "Vendor — Cache"), Leaf(@"C:\x\a", 1, "r1"), Leaf(@"C:\x\b", 1, "r1") };
        var result = RuleCategoryTask.GroupByRule(leaves, [Compiled("r1"), Compiled("r2", "Rule Two")]);
        Assert.Equal("Vendor — Cache", result.Single(n => !n.IsContainer).Title);
    }

    [Fact]
    public void GroupByAppMatchesByIdNameTitleAndInstallLocation()
    {
        InstalledApp[] apps =
        [
            App("Vendor.Tool", "Vendor Tool"),
            App("acme-editor", "Acme Editor", @"C:\Program Files\Acme"),
        ];
        var leaves = new List<Node>
        {
            Leaf(@"C:\L\Vendor.Tool", 10),                          // theo AppID
            Leaf(@"C:\R\Vendor Tool", 20),                          // theo tên
            Leaf(@"C:\L\Acme\Cache", 5, title: "Acme Editor — Cache"), // theo tiền tố tiêu đề
            Leaf(@"C:\Program Files\Acme\logs", 1),                // nằm trong thư mục cài
            Leaf(@"C:\L\Unknown", 50),                              // không rõ app: để phẳng
        };

        var result = RuleCategoryTask.GroupByApp(leaves, apps);

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { 50L, 30L, 6L }, result.Select(n => n.Size.Bytes));
        Assert.False(result[0].IsContainer);
        var tool = result[1];
        Assert.Equal(NodeKind.Application, tool.Kind);
        Assert.Equal("Vendor.Tool", tool.AppID);
        Assert.Equal("Vendor Tool", tool.Title);
        Assert.Equal(new[] { 20L, 10L }, tool.Children.Select(c => c.Size.Bytes));
        Assert.Equal(@"C:\Program Files\Acme", result[2].Path);
    }

    [Fact]
    public void GroupByAppKeepsSingleLeafFlat()
    {
        var result = RuleCategoryTask.GroupByApp([Leaf(@"C:\L\Vendor.Tool", 10)], [App("Vendor.Tool", "Vendor Tool")]);
        Assert.False(Assert.Single(result).IsContainer);
    }

    [Fact]
    public void DisplayTitles()
    {
        var rule = TestRules.Make("r", paths: [@"%TEMP%\x"]);
        var m = new PathMeasurement(0, 0, DateTime.MinValue, null, true, true);
        var perApp = new RuleMatch(@"C:\L\Foo\Cache", m, rule, new RuleAppInfo("foo", "Foo", null, null, []), @"C:\L");
        var sameName = new RuleMatch(@"C:\R\foo", m, rule, new RuleAppInfo("foo", "Foo", null, null, []), @"C:\R");
        var owned = new RuleMatch(@"C:\L\Vendor.Tool", m, rule, null, @"C:\L");
        var plain = new RuleMatch(@"C:\L\Something\", m, rule, null, @"C:\L");
        InstalledApp[] apps = [App("Vendor.Tool", "Vendor Tool")];

        Assert.Equal("Foo — Cache", RuleCategoryTask.DisplayTitle(perApp, apps));
        Assert.Equal("Foo", RuleCategoryTask.DisplayTitle(sameName, apps));
        Assert.Equal("Vendor Tool", RuleCategoryTask.DisplayTitle(owned, apps));
        Assert.Equal("Something", RuleCategoryTask.DisplayTitle(plain, apps));
    }
}

/// <summary>Chạy task một nhóm rule trên cây giả lập: một node nhóm gốc, cache trùng tên tiến trình đang chạy bị bỏ.</summary>
public sealed class RuleCategoryTaskRunTests : IDisposable
{
    private readonly TempDir _tmp = new("category");
    public void Dispose() => _tmp.Dispose();

    private async Task<ScanResult> Run(JunkCategorySpec spec, Rule[] rules, ScanEnvironment env, Knowledge? knowledge = null)
    {
        var resolver = PathResolver.Fixture(_tmp.Root);
        var snapshot = new RuleSnapshot(1, rules, knowledge ?? new Knowledge(), resolver: resolver, source: RuleSnapshot.SourceKind.Development);
        var task = new RuleCategoryTask(spec, new Dictionary<string, IJunkProvider>(), needsApps: false);
        return await new ScanEngine(new FileSystemService(resolver.Home), 1).RunAsync(new ScanGraph([task]), snapshot, env);
    }

    [Fact]
    public async Task ProducesOneGroupPerCategory()
    {
        var home = _tmp.Dir(@"Users\tester");
        _tmp.File(@"Users\tester\AppData\Local\A\Cache\x", 5000);
        _tmp.File(@"Users\tester\AppData\Local\B\Cache\x", 9000);
        _tmp.File(@"Users\tester\AppData\Local\chrome\Cache\x", 9000);
        _tmp.File(@"Users\tester\AppData\Local\Steam\Cache\x", 9000);
        var rule = TestRules.Make("t.caches", RuleCategory.UserCaches, [@"%LOCALAPPDATA%\*"], removal: RemovalStrategy.DeleteContents);
        var env = new ScanEnvironment { Policy = TestPolicy.For(home), RunningProcesses = new HashSet<string> { "chrome" } };

        var result = await Run(new JunkCategorySpec("userCaches", RuleCategory.UserCaches, 1), [rule], env,
            new Knowledge { ProtectedCacheNames = ["Ste*"] });

        var root = Assert.Single(result.Tree.Roots);
        Assert.Equal(NodeKind.Group, root.Kind);
        Assert.Equal(RuleCategory.UserCaches, root.Category);
        Assert.Equal(new[] { "B", "A" }, root.Children.Select(c => c.Title));
        Assert.All(root.Children, c => Assert.Equal("t.caches", c.RuleID));
        Assert.All(root.Children, c => Assert.Equal(RemovalStrategy.DeleteContents, c.Removal));
        Assert.All(root.Children, c => Assert.Single(c.AllowedRoots));
        Assert.Equal(root.Children.Sum(c => c.Size.Bytes), root.Size.Bytes);
    }

    [Fact]
    public async Task NoMatchesGiveNoNodes()
    {
        var home = _tmp.Dir(@"Users\tester");
        var rule = TestRules.Make("t.none", RuleCategory.UserLogs, [@"%LOCALAPPDATA%\*\Logs"]);
        var result = await Run(new JunkCategorySpec("userLogs", RuleCategory.UserLogs, 1), [rule], new ScanEnvironment { Policy = TestPolicy.For(home) });
        Assert.True(result.Tree.IsEmpty);
        Assert.Equal(TaskState.Succeeded, result.States["userLogs"]);
    }
}
