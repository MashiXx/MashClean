using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Tests.Engine;

/// <summary>Mở rộng biến theo app và chống path traversal (mục 8.6).</summary>
public class RuleSubstitutionTests
{
    private static readonly RuleAppInfo App = new("Vendor.App_1", "Example App", "Acme Corp", null, ["example"]);

    [Fact]
    public void SubstitutesAllVariables()
    {
        Assert.Equal(@"%APPDATA%\Acme Corp\Example App", RuleEvaluator.Substitute(@"%APPDATA%\${publisher}\${appName}", App));
        Assert.Equal(@"%LOCALAPPDATA%\Packages\Vendor.App_1", RuleEvaluator.Substitute(@"%LOCALAPPDATA%\Packages\${appID}", App));
        Assert.Equal(@"%TEMP%\plain", RuleEvaluator.Substitute(@"%TEMP%\plain", App));
    }

    [Fact]
    public void MissingPublisherDropsOnlyPathsThatNeedIt()
    {
        var noPublisher = App with { Publisher = null };
        Assert.Null(RuleEvaluator.Substitute(@"%APPDATA%\${publisher}\${appName}", noPublisher));
        Assert.Equal(@"%APPDATA%\Example App", RuleEvaluator.Substitute(@"%APPDATA%\${appName}", noPublisher));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(@"..\x")]
    [InlineData(@"..\..\..")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData("C:")]
    [InlineData(@"C:\Windows")]
    [InlineData("*")]
    [InlineData("Vendor*")]
    [InlineData("x?")]
    [InlineData("[ab]")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("file:stream")]
    [InlineData("bad\0name")]
    [InlineData("a|b")]
    [InlineData("a<b>")]
    [InlineData("\"quoted\"")]
    public void RejectsTraversalAndGlobInjection(string evil)
    {
        Assert.False(RuleEvaluator.IsSafeComponent(evil));
        Assert.Null(RuleEvaluator.Substitute(@"%LOCALAPPDATA%\${appName}\Cache", App with { AppName = evil }));
        Assert.Null(RuleEvaluator.Substitute(@"%LOCALAPPDATA%\${appID}", App with { AppID = evil }));
    }

    [Theory]
    [InlineData("Example App")]
    [InlineData("Vendor.App_1.2")]
    [InlineData("{6F2D3C1A-0000-4C5B-9E9E-123456789ABC}")]
    [InlineData("Tiếng Việt")]
    [InlineData("..hidden")]
    [InlineData("a..b")]
    public void AcceptsOrdinaryNames(string name) => Assert.True(RuleEvaluator.IsSafeComponent(name));
}

public class PathResolverTests
{
    private const string Root = @"C:\fixture";
    private static readonly PathResolver R = PathResolver.Fixture(Root);

    [Fact]
    public void ResolvesTokensTildeAndUserHome()
    {
        Assert.Equal(@"C:\fixture\Users\tester", R.Home);
        Assert.Equal(@"C:\fixture\Users\tester\AppData\Local\Vendor\*", R.Resolve(@"%LOCALAPPDATA%\Vendor\*"));
        Assert.Equal(@"C:\fixture\Users\tester\AppData\Local\Vendor", R.Resolve("%localappdata%/Vendor"));
        Assert.Equal(@"C:\fixture\Windows\Temp\*", R.Resolve(@"%WINDIR%\Temp\*"));
        Assert.Equal(@"C:\fixture\Program Files (x86)\X", R.Resolve(@"%PROGRAMFILES(X86)%\X"));
        Assert.Equal(@"C:\fixture\Users\tester\.npm", R.Resolve(@"${userHome}\.npm"));
        Assert.Equal(@"C:\fixture\Users\tester\.gradle\caches", R.Resolve(@"~\.gradle\caches"));
        Assert.Equal(@"C:\fixture\Users\tester", R.Resolve("~"));
        Assert.Equal(@"C:\fixture\Users\tester\AppData\Local\Temp\a\C:\fixture\ProgramData", R.Resolve(@"%TEMP%\a\%PROGRAMDATA%"));
    }

    [Fact]
    public void UnknownTokensAreLeftForLint()
    {
        var resolved = R.Resolve(@"%NOT_A_TOKEN%\x\%TEMP%");
        Assert.StartsWith("%NOT_A_TOKEN%", resolved);
        Assert.EndsWith(@"AppData\Local\Temp", resolved);
        Assert.True(PathResolver.HasUnknownToken(resolved));
        Assert.False(PathResolver.HasUnknownToken(R.Resolve(@"%TEMP%\x")));
    }

    [Fact]
    public void LiveResolverUsesRealFolders()
    {
        Assert.Equal(KnownPaths.LocalAppData + @"\x", PathResolver.Live.Resolve(@"%LOCALAPPDATA%\x"));
        Assert.Equal(KnownPaths.Windows + @"\Temp", PathResolver.Live.Resolve(@"%WINDIR%\Temp"));
    }

    [Fact]
    public void CompiledRuleKnowsWhenItIsPerApp()
    {
        Assert.NotNull(new CompiledRule(TestRules.Make("a.static", paths: [@"%TEMP%\*"]), R).StaticGlobs);
        Assert.Null(new CompiledRule(TestRules.Make("a.var", paths: [@"%APPDATA%\${appName}"]), R).StaticGlobs);
        Assert.Null(new CompiledRule(TestRules.Make("a.each", paths: [@"%APPDATA%\x"], forEachApp: true), R).StaticGlobs);
        Assert.Null(new CompiledRule(TestRules.Make("a.filter", paths: [@"%APPDATA%\x"], publishers: ["Acme*"]), R).StaticGlobs);
        Assert.Equal(new OSVersion(10, 0, 19041), new CompiledRule(TestRules.Make("a.os", paths: [], minOS: "10.0.19041"), R).MinOS);
    }

    [Fact]
    public void AppFiltersUseComponentGlobs()
    {
        var rule = new CompiledRule(TestRules.Make("f", paths: [@"%APPDATA%\x"], publishers: ["Blizzard*"], appNames: ["Battle.net"]), R);
        Assert.True(rule.AppliesTo(new RuleAppInfo("bnet", "battle.NET", "Blizzard Entertainment", null, [])));
        Assert.False(rule.AppliesTo(new RuleAppInfo("bnet", "Battle.net", "Other", null, [])));
        Assert.False(rule.AppliesTo(new RuleAppInfo("bnet", "Battle.net", null, null, [])));
    }
}

/// <summary>Đánh giá rule trên cây thư mục giả lập (mục 8.6, 18).</summary>
public sealed class RuleEvaluatorTests : IDisposable
{
    private readonly TempDir _tmp = new("eval");
    private readonly PathResolver _resolver;
    private readonly string _home;
    private readonly PathPolicy _policy;

    public RuleEvaluatorTests()
    {
        _resolver = PathResolver.Fixture(_tmp.Root);
        _home = _resolver.Home;
        Directory.CreateDirectory(_home);
        _policy = TestPolicy.For(_home);
    }

    public void Dispose() => _tmp.Dispose();

    private const string Local = @"Users\tester\AppData\Local";
    private const string Roaming = @"Users\tester\AppData\Roaming";

    private RuleSnapshot Snapshot(params Rule[] rules) =>
        new(1, rules, new Knowledge(), resolver: _resolver, source: RuleSnapshot.SourceKind.Development);

    private RuleEvaluationContext Context(IReadOnlyList<RuleAppInfo>? apps = null, string[]? running = null, string[]? runningApps = null,
        IgnoreList? ignore = null, OSVersion? os = null, CancellationToken ct = default) => new()
        {
            FileSystem = new FileSystemService(_home),
            Policy = _policy,
            Apps = apps ?? [],
            RunningProcesses = new HashSet<string>(running ?? []),
            RunningAppIDs = new HashSet<string>(runningApps ?? []),
            Ignore = ignore ?? IgnoreList.Empty,
            OSVersion = os ?? OSVersion.Current,
            Cancellation = ct,
        };

    private List<RuleMatch> Matches(Rule rule, RuleEvaluationContext? context = null, RuleSnapshot? snapshot = null)
    {
        var snap = snapshot ?? Snapshot(rule);
        var compiled = snap.Rule(rule.Id) ?? throw new InvalidOperationException("rule không có trong snapshot");
        return RuleEvaluator.Evaluate(compiled, snap, context ?? Context());
    }

    /// <summary>Đường dẫn tương đối gốc fixture, để so sánh gọn.</summary>
    private List<string> Eval(Rule rule, RuleEvaluationContext? context = null, RuleSnapshot? snapshot = null) =>
        Matches(rule, context, snapshot).Select(m => Path.GetRelativePath(_tmp.Root, m.Path)).Order(StringComparer.OrdinalIgnoreCase).ToList();

    [Fact]
    public void MinAgeDays()
    {
        _tmp.File(@"Users\tester\Downloads\old-setup.msi", ageDays: 45);
        _tmp.File(@"Users\tester\Downloads\new-setup.msi", ageDays: 2);
        _tmp.File(@"Users\tester\Downloads\old-notes.txt", ageDays: 45);
        var rule = TestRules.Make("t.age", RuleCategory.OldDownloads, [@"%DOWNLOADS%\*.msi"], SafetyLevel.Review, RemovalStrategy.MoveToTrash, minAgeDays: 30);
        Assert.Equal(new[] { @"Users\tester\Downloads\old-setup.msi" }, Eval(rule));
    }

    [Fact]
    public void MinAgeUsesNewestEntryInsideDirectory()
    {
        _tmp.File(Local + @"\Temp\a\old.tmp", ageDays: 60);
        _tmp.File(Local + @"\Temp\b\old.tmp", ageDays: 60);
        _tmp.File(Local + @"\Temp\b\fresh.tmp", ageDays: 1);
        TempDir.Age(_tmp.P(Local + @"\Temp\a"), 60);
        TempDir.Age(_tmp.P(Local + @"\Temp\b"), 60);
        var rule = TestRules.Make("t.age.dir", RuleCategory.UserTemp, [@"%TEMP%\*"], minAgeDays: 30);
        Assert.Equal(new[] { Local + @"\Temp\a" }, Eval(rule));
    }

    [Fact]
    public void MinAgeCountsLastAccess()
    {
        var f = _tmp.File(Local + @"\Temp\recently-read.tmp", ageDays: 60);
        File.SetLastAccessTimeUtc(f, DateTime.UtcNow.AddDays(-1));
        _tmp.File(Local + @"\Temp\stale.tmp", ageDays: 60);
        var rule = TestRules.Make("t.age.access", RuleCategory.UserTemp, [@"%TEMP%\*"], minAgeDays: 30);
        Assert.Equal(new[] { Local + @"\Temp\stale.tmp" }, Eval(rule));
    }

    [Fact]
    public void MinSizeBytes()
    {
        _tmp.File(Local + @"\Temp\big\blob", 256 * 1024);
        _tmp.File(Local + @"\Temp\small\blob", 10);
        var rule = TestRules.Make("t.size", RuleCategory.UserTemp, [@"%TEMP%\*"], minSizeBytes: 100 * 1024);
        Assert.Equal(new[] { Local + @"\Temp\big" }, Eval(rule));
    }

    [Fact]
    public void ExcludeAlwaysWins()
    {
        _tmp.File(Local + @"\Vendor\Cache\x");
        _tmp.File(Local + @"\KeepMe\Cache\x");
        _tmp.File(Local + @"\Other\Cache\x");
        var rule = TestRules.Make("t.exclude", paths: [@"%LOCALAPPDATA%\*\Cache"], removal: RemovalStrategy.DeleteContents,
            exclude: [@"%LOCALAPPDATA%\Keep*", @"%LOCALAPPDATA%\Other\Cache"]);
        Assert.Equal(new[] { Local + @"\Vendor\Cache" }, Eval(rule));
    }

    [Fact]
    public void ExcludedSubdirectoryIsNotMeasured()
    {
        _tmp.File(Local + @"\App\Cache\keep\big", 512 * 1024);
        _tmp.File(Local + @"\App\Cache\drop", 4096);
        var rule = TestRules.Make("t.exclude.inner", paths: [@"%LOCALAPPDATA%\App\Cache"], removal: RemovalStrategy.DeleteContents,
            exclude: [@"%LOCALAPPDATA%\App\Cache\keep"]);
        var m = Assert.Single(Matches(rule));
        Assert.True(m.Measurement.AllocatedSize < 512 * 1024, $"đo cả thư mục loại trừ: {m.Measurement.AllocatedSize}");
        Assert.True(m.Measurement.AllocatedSize >= 4096);
    }

    [Fact]
    public void EmptyDirectoryIsSkippedUnlessDelete()
    {
        _tmp.Dir(Local + @"\Temp\empty");
        Assert.Empty(Eval(TestRules.Make("t.empty.contents", RuleCategory.UserTemp, [@"%TEMP%\*"], removal: RemovalStrategy.DeleteContents)));
        Assert.Empty(Eval(TestRules.Make("t.empty.trash", RuleCategory.UserTemp, [@"%TEMP%\*"], removal: RemovalStrategy.MoveToTrash)));
        Assert.Equal(new[] { Local + @"\Temp\empty" }, Eval(TestRules.Make("t.empty.delete", RuleCategory.UserTemp, [@"%TEMP%\*"], removal: RemovalStrategy.Delete)));
    }

    [Fact]
    public void MeasurementAndAllowedRoot()
    {
        _tmp.File(@"ProgramData\Vendor\Logs\a.log", 3000);
        _tmp.File(@"ProgramData\Vendor\Logs\old\b.log", 3000);
        var rule = TestRules.Make("t.root", RuleCategory.SystemLogs, [@"%PROGRAMDATA%\*\Logs"], requiresRoot: true);
        var m = Assert.Single(Matches(rule));
        Assert.Equal(_tmp.P(@"ProgramData\Vendor\Logs"), m.Path, ignoreCase: true);
        Assert.Equal(_tmp.P("ProgramData"), m.AllowedRoot, ignoreCase: true);
        Assert.True(m.Measurement.IsDirectory);
        Assert.Equal(3, m.Measurement.ItemCount); // a.log, old, old\b.log
        Assert.True(m.Measurement.AllocatedSize >= 6000);
        Assert.Same(rule, m.Rule);
        Assert.Null(m.App);
    }

    [Fact]
    public void AppNotRunningStaticRule()
    {
        _tmp.File(Local + @"\Google\Chrome\User Data\Default\Cache\x");
        var rule = TestRules.Make("t.running", RuleCategory.BrowserCaches, [@"%LOCALAPPDATA%\Google\Chrome\User Data\*\Cache"],
            appNotRunning: ["Chrome.exe"]);
        Assert.Single(Eval(rule));
        Assert.Empty(Eval(rule, Context(running: ["chrome"])));
        Assert.Single(Eval(rule, Context(running: ["msedge"])));
    }

    [Fact]
    public void ForEachInstalledAppWithVariables()
    {
        _tmp.File(Roaming + @"\Acme\Foo\Cache\x");
        _tmp.File(Roaming + @"\Acme\Bar\Cache\x");
        _tmp.File(Roaming + @"\Orphan\Baz\Cache\x");
        var rule = TestRules.Make("t.apps", RuleCategory.AppLeftovers, [@"%APPDATA%\${publisher}\${appName}\Cache"],
            removal: RemovalStrategy.DeleteContents, forEachApp: true, appNotRunning: ["${appID}"]);
        RuleAppInfo[] apps =
        [
            new("foo.id", "Foo", "Acme", null, []),
            new("bar.id", "Bar", "Acme", null, []),
            new("nopub.id", "Baz", null, null, []),
        ];

        Assert.Equal(new[] { Roaming + @"\Acme\Bar\Cache", Roaming + @"\Acme\Foo\Cache" }, Eval(rule, Context(apps)));
        Assert.Equal(new[] { Roaming + @"\Acme\Bar\Cache" }, Eval(rule, Context(apps, runningApps: ["foo.id"])));
        Assert.Equal(new[] { Roaming + @"\Acme\Foo\Cache" },
            Eval(rule, Context(apps, ignore: new IgnoreList(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "bar.id" }))));
        Assert.Empty(Eval(rule)); // không có app nào

        var m = Matches(rule, Context(apps)).First(x => x.Path.EndsWith(@"Foo\Cache", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("foo.id", m.App?.AppID);
        Assert.Equal(_tmp.P(Roaming + @"\Acme\Foo\Cache"), m.AllowedRoot, ignoreCase: true);
    }

    [Fact]
    public void PerAppExcludesAreSubstituted()
    {
        _tmp.File(Roaming + @"\Foo\Cache\x");
        _tmp.File(Roaming + @"\Foo\Settings\x");
        var rule = TestRules.Make("t.app.exclude", RuleCategory.AppLeftovers, [@"%APPDATA%\${appName}\*"], forEachApp: true,
            exclude: [@"%APPDATA%\${appName}\Settings"]);
        Assert.Equal(new[] { Roaming + @"\Foo\Cache" }, Eval(rule, Context([new RuleAppInfo("foo.id", "Foo", null, null, [])])));
    }

    [Fact]
    public void AppFilterRestrictsWhichAppsApply()
    {
        _tmp.File(Local + @"\Battle.net\Cache\x");
        var rule = TestRules.Make("t.filter", RuleCategory.AppLeftovers, [@"%LOCALAPPDATA%\Battle.net\Cache"], publishers: ["Blizzard*"]);
        Assert.Single(Eval(rule, Context([new RuleAppInfo("bnet", "Battle.net", "Blizzard Entertainment", null, [])])));
        Assert.Empty(Eval(rule, Context([new RuleAppInfo("other", "Other", "Contoso", null, [])])));
        Assert.Empty(Eval(rule));
    }

    [Fact]
    public void MaliciousAppNamesCannotTraverse()
    {
        _tmp.File(@"Users\tester\secret.txt");
        _tmp.File(Local + @"\Caches\x\y");
        var rule = TestRules.Make("t.evil", paths: [@"%LOCALAPPDATA%\Caches\${appName}"], forEachApp: true);
        var evil = new[] { "..", @"..\..", @"..\..\..\secret.txt", "*", "x?", ".", "C:", @"x\..\..", "" }
            .Select(n => new RuleAppInfo("evil-" + n, n, null, null, [])).ToList();
        Assert.Empty(Eval(rule, Context(evil)));
    }

    [Fact]
    public void ProtectedDirectoriesAreNeverProposed()
    {
        foreach (var d in new[] { "Microsoft", "Programs", "Packages", "Vendor" }) _tmp.File(Local + $@"\{d}\x");
        var rule = TestRules.Make("t.wide", paths: [@"%LOCALAPPDATA%\*"], removal: RemovalStrategy.DeleteContents);
        Assert.Equal(new[] { Local + @"\Packages", Local + @"\Vendor" }, Eval(rule)); // Packages là vỏ: được xoá nội dung
        var all = TestRules.Make("t.root", paths: [@"%USERPROFILE%"], removal: RemovalStrategy.DeleteContents);
        Assert.Empty(Eval(all));
    }

    [SkippableFact]
    public void JunctionIntoForbiddenZoneIsDropped()
    {
        _tmp.File(Local + @"\good\System32\x");
        Skip.IfNot(Links.TryCreateJunction(_tmp.P(Local + @"\evil"), KnownPaths.Windows), Links.JunctionSkipReason);
        var rule = TestRules.Make("t.junction", paths: [@"%LOCALAPPDATA%\*\System32"]);
        // evil\System32 sau chuẩn hoá là C:\Windows\System32: bị PathPolicy loại (mục 8.6 bước 4).
        Assert.Equal(new[] { Local + @"\good\System32" }, Eval(rule));
    }

    [SkippableFact]
    public void GlobstarDoesNotFollowJunctions()
    {
        _tmp.File(Local + @"\Vendor\a\Logs\x.log");
        var outside = _tmp.Dir(@"outside\Logs");
        _tmp.File(@"outside\Logs\y.log");
        Skip.IfNot(Links.TryCreateJunction(_tmp.P(Local + @"\Vendor\link"), Path.GetDirectoryName(outside)!), Links.JunctionSkipReason);
        var rule = TestRules.Make("t.globstar", RuleCategory.UserLogs, [@"%LOCALAPPDATA%\Vendor\**\Logs"]);
        Assert.Equal(new[] { Local + @"\Vendor\a\Logs" }, Eval(rule));
    }

    [Fact]
    public void IgnoreListAndDisabledRules()
    {
        _tmp.File(Local + @"\Logs\a\x");
        _tmp.File(Local + @"\Logs\b\x");
        var rule = TestRules.Make("t.ignore", RuleCategory.UserLogs, [@"%LOCALAPPDATA%\Logs\*"]);
        var ignoreA = new IgnoreList(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _tmp.P(Local + @"\Logs\a") }, new HashSet<string>(), new HashSet<string>());
        Assert.Equal(new[] { Local + @"\Logs\b" }, Eval(rule, Context(ignore: ignoreA)));
        Assert.Empty(Eval(rule, Context(ignore: new IgnoreList(new HashSet<string>(), new HashSet<string> { "t.ignore" }, new HashSet<string>()))));
        var disabled = Snapshot(rule).WithDisabled(["t.ignore"]);
        Assert.Empty(Eval(rule, snapshot: disabled));
        Assert.Empty(disabled.RulesIn(RuleCategory.UserLogs));
    }

    [Fact]
    public void MinOSCondition()
    {
        _tmp.File(Local + @"\Logs\a\x");
        var rule = TestRules.Make("t.os", RuleCategory.UserLogs, [@"%LOCALAPPDATA%\Logs\*"], minOS: "10.0.22000");
        Assert.Empty(Eval(rule, Context(os: new OSVersion(10, 0, 19045))));
        Assert.Single(Eval(rule, Context(os: new OSVersion(10, 0, 22631))));
    }

    [Fact]
    public void ProviderRulesAreLeftToProviders()
    {
        var rule = TestRules.Make("t.provider", RuleCategory.Trash, [], provider: "recycleBin");
        Assert.Empty(Eval(rule));
    }

    [Fact]
    public void DuplicateGlobsYieldOnePath()
    {
        _tmp.File(Local + @"\Temp\x\f");
        var rule = TestRules.Make("t.dup", RuleCategory.UserTemp, [@"%TEMP%\*", @"%LOCALAPPDATA%\Temp\x", @"%TEMP%\X"]);
        Assert.Single(Eval(rule));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        _tmp.File(Local + @"\Temp\x\f");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var rule = TestRules.Make("t.cancel", RuleCategory.UserTemp, [@"%TEMP%\*"]);
        Assert.ThrowsAny<OperationCanceledException>(() => Matches(rule, Context(ct: cts.Token)));
    }

    [Fact]
    public void GlobExpanderRequiresAbsoluteDrive()
    {
        Assert.Empty(GlobExpander.Expand(new CleanBoost.Core.Glob(@"relative\x", _home)));
        Assert.Empty(GlobExpander.Expand(new CleanBoost.Core.Glob(_tmp.P("missing") + @"\*", _home)));
        _tmp.File(@"g\a\b\c\d.txt");
        var all = GlobExpander.Expand(new CleanBoost.Core.Glob(_tmp.P("g") + @"\**", _home));
        Assert.Equal(new[] { _tmp.P("g"), _tmp.P(@"g\a"), _tmp.P(@"g\a\b"), _tmp.P(@"g\a\b\c") }, all);
    }
}
