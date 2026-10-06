using System.Collections.Concurrent;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.SystemJunk;
using Microsoft.Data.Sqlite;

namespace CleanBoost.Tests.Engine;

/// <summary>Helper quản trị giả: ghi lại lệnh, không xoá gì.</summary>
internal sealed class StubHelper : IHelperClient
{
    public ConcurrentQueue<(string[] Paths, RemoveMode Mode)> Calls { get; } = new();
    public HelperStatus Status => HelperStatus.Running;
    public event Action<HelperStatus>? StatusChanged { add { } remove { } }
    public Task<bool> EnsureRunningAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<RemoveResult>> RemoveItemsAsync(string[] paths, RemoveMode mode, CancellationToken ct = default)
    {
        Calls.Enqueue((paths, mode));
        return Task.FromResult<IReadOnlyList<RemoveResult>>(paths.Reverse().Select(p => RemoveResult.Ok(p, 1234)).ToList()); // thứ tự đảo: engine phải ghép theo đường dẫn
    }

    public Task<CommandResult> RunMaintenanceAsync(MaintenanceTaskName task, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CommandResult> SetStartupApprovedAsync(string scope, string location, string name, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CommandResult> DeleteRunValueAsync(string scope, string location, string name, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CommandResult> SetScheduledTaskEnabledAsync(string taskPath, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
    public void Shutdown() { }
}

internal sealed class RecordingRemover(string id) : IRemover
{
    public string Id { get; } = id;
    public ConcurrentQueue<CleanPlan.Item> Seen { get; } = new();

    public Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        foreach (var i in items) Seen.Enqueue(i);
        return Task.FromResult<IReadOnlyList<RemoveResult>>(items.Select(i => RemoveResult.Ok(i.Label, 7)).ToList());
    }
}

/// <summary>Tích hợp Scan → Plan → Clean trên thư mục tạm có nội dung thật (mục 7, 18).</summary>
public sealed class CleanEngineTests : IDisposable
{
    private readonly TempDir _tmp = new("clean");
    private readonly PathResolver _resolver;
    private readonly string _home;
    private readonly PathPolicy _policy;
    private readonly Storage _storage;
    private const string Local = @"Users\tester\AppData\Local";

    public CleanEngineTests()
    {
        _resolver = PathResolver.Fixture(_tmp.Root);
        _home = _resolver.Home;
        Directory.CreateDirectory(_home);
        _policy = TestPolicy.For(_home);
        _storage = new Storage(_tmp.P(@"db\cleanboost.sqlite"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _tmp.Dispose();
    }

    private CleanEngine Engine(IHelperClient? helper = null, PathPolicy? policy = null) =>
        new(new FileSystemService(_home), helper, _storage, policy ?? _policy);

    private static readonly Rule[] Rules =
    [
        TestRules.Make("t.caches", RuleCategory.UserCaches, [@"%LOCALAPPDATA%\*\Cache"], removal: RemovalStrategy.DeleteContents,
            exclude: [@"%LOCALAPPDATA%\Keep*"]),
        TestRules.Make("t.logs", RuleCategory.UserLogs, [@"%LOCALAPPDATA%\*\Logs"], removal: RemovalStrategy.Delete),
        TestRules.Make("t.downloads", RuleCategory.OldDownloads, [@"%DOWNLOADS%\*.msi"], SafetyLevel.Review, RemovalStrategy.MoveToTrash, minAgeDays: 30),
    ];

    private async Task<ScanResult> ScanAsync()
    {
        var snapshot = new RuleSnapshot(1, Rules, new Knowledge(), resolver: _resolver, source: RuleSnapshot.SourceKind.Development);
        var providers = new Dictionary<string, IJunkProvider>();
        IScanTask[] tasks =
        [
            new RuleCategoryTask(new JunkCategorySpec("userCaches", RuleCategory.UserCaches, 1), providers, needsApps: false),
            new RuleCategoryTask(new JunkCategorySpec("userLogs", RuleCategory.UserLogs, 1), providers, needsApps: false),
            new RuleCategoryTask(new JunkCategorySpec("oldDownloads", RuleCategory.OldDownloads, 1), providers, needsApps: false),
        ];
        return await new ScanEngine(new FileSystemService(_home), 2).RunAsync(new ScanGraph(tasks), snapshot, new ScanEnvironment { Policy = _policy });
    }

    private void CreateJunkTree()
    {
        _tmp.File(Local + @"\VendorApp\Cache\blob", 64 * 1024);
        _tmp.File(Local + @"\VendorApp\Cache\sub\blob2", 16 * 1024);
        _tmp.File(Local + @"\KeepMe\Cache\keep", 4096);
        _tmp.File(Local + @"\Vendor\Logs\x.log", 8192);
        _tmp.File(@"Users\tester\Downloads\cb-test-old.msi", 4096, ageDays: 60);
    }

    private Node FileNode(string relative, RemovalStrategy removal, string? allowedRoot = null, SafetyLevel safety = SafetyLevel.Safe, long size = 100)
    {
        var path = _tmp.P(relative);
        var n = Node.ForPath(path, Directory.Exists(path));
        n.Removal = removal;
        n.Safety = safety;
        n.Size = new ByteCount(size);
        n.Category = RuleCategory.UserCaches;
        n.RuleID = "t.node";
        n.AllowedRoots = allowedRoot is null ? [] : [_tmp.P(allowedRoot)];
        return n;
    }

    [Fact]
    public async Task ScanPlanCleanWithDefaultSelection()
    {
        CreateJunkTree();
        var result = await ScanAsync();
        Assert.All(result.States.Values, s => Assert.Equal(TaskState.Succeeded, s));
        Assert.Equal(3, result.Tree.AllLeaves.Count());

        // Mặc định chỉ chọn mục safe (bản tải về là review).
        var engine = Engine();
        var plan = engine.MakePlan(result.Tree, SelectionState.Defaults(result.Tree));
        Assert.Equal(2, plan.Items.Count);
        Assert.Empty(plan.BlockedItems);
        Assert.False(plan.RequiresConfirmation);
        Assert.All(plan.Items, i => Assert.Equal(RemoverIDs.File, i.RemoverID));

        var progress = new ConcurrentQueue<CleanProgress>();
        var report = await engine.RunAsync(plan, dryRun: false, onProgress: progress.Enqueue);

        Assert.Empty(report.Failed);
        Assert.Equal(2, report.Succeeded.Count());
        Assert.True(report.EstimatedFreed.Bytes >= 80 * 1024, report.EstimatedFreed.Bytes.ToString());
        Assert.False(report.DryRun);
        Assert.Equal(1, progress.Last().Fraction);

        // deleteContents giữ thư mục, delete xoá cả thư mục; mục loại trừ và review không bị đụng.
        Assert.True(_tmp.Exists(Local + @"\VendorApp\Cache"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_tmp.P(Local + @"\VendorApp\Cache")));
        Assert.False(_tmp.Exists(Local + @"\Vendor\Logs"));
        Assert.True(_tmp.Exists(Local + @"\KeepMe\Cache\keep"));
        Assert.True(_tmp.Exists(@"Users\tester\Downloads\cb-test-old.msi"));

        // Ghi clean_operation và clean_item_log.
        var op = Assert.Single(_storage.Operations());
        Assert.Equal(report.OperationID, op.Id);
        Assert.Equal(2, op.ItemsOk);
        Assert.Equal(0, op.ItemsFailed);
        Assert.NotNull(op.FinishedAt);
        var logs = _storage.Items(report.OperationID);
        Assert.Equal(new HashSet<string> { "deleteContents", "delete" }, logs.Select(l => l.Strategy).ToHashSet());
        Assert.All(logs, l => Assert.Equal("ok", l.Result));
        Assert.Contains(logs, l => l.RuleId == "t.caches");
    }

    [Fact]
    public async Task DryRunDeletesNothing()
    {
        CreateJunkTree();
        var result = await ScanAsync();
        var engine = Engine();
        var plan = engine.MakePlan(result.Tree, SelectionState.Defaults(result.Tree));
        Assert.Equal(2, plan.Items.Count);

        var report = await engine.RunAsync(plan, dryRun: true);

        Assert.True(report.DryRun);
        Assert.All(report.Entries, e => Assert.Equal(SkipReason.DryRun, e.Result.Reason));
        Assert.Null(report.MeasuredFreed);
        Assert.True(_tmp.Exists(Local + @"\VendorApp\Cache\blob"));
        Assert.True(_tmp.Exists(Local + @"\Vendor\Logs\x.log"));
        Assert.Equal(0, _storage.Operations().Single().FreedBytes);
        Assert.All(_storage.Items(report.OperationID), l => Assert.Equal("skipped:dryRun", l.Result));
    }

    [Fact]
    public async Task MissingFileIsSuccessWithZeroBytes()
    {
        var node = FileNode(Local + @"\Vendor\Cache\gone.db", RemovalStrategy.Delete, allowedRoot: Local + @"\Vendor\Cache", size: 5000);
        var engine = Engine();
        var plan = engine.MakePlan([node]);
        Assert.Single(plan.Items);

        var report = await engine.RunAsync(plan, dryRun: false);

        var r = Assert.Single(report.Entries).Result;
        Assert.True(r.IsSuccess);
        Assert.Equal(SkipReason.NotFound, r.Reason);
        Assert.Equal(0, r.FreedBytes);
        Assert.Empty(report.Skipped);
        Assert.Single(report.Succeeded);
    }

    [Fact]
    public async Task DeleteContentsOnLooseFileRemovesIt()
    {
        _tmp.File(Local + @"\Vendor\Cache\loose.db", 4096);
        var engine = Engine();
        var plan = engine.MakePlan([FileNode(Local + @"\Vendor\Cache\loose.db", RemovalStrategy.DeleteContents, Local)]);
        var report = await engine.RunAsync(plan, dryRun: false);
        Assert.True(report.Entries.Single().Result.IsSuccess);
        Assert.False(_tmp.Exists(Local + @"\Vendor\Cache\loose.db"));
    }

    [Fact]
    public async Task BlockedPathsNeverReachRemovers()
    {
        _tmp.File(@"outside\secret.txt");
        _tmp.File(@"Users\tester\Documents\report.txt");
        _tmp.File(@"Users\tester\NTUSER.DAT");
        _tmp.File(Local + @"\Temp\x.tmp");
        _tmp.Dir(Local + @"\Vendor\Cache");
        var custom = new RecordingRemover("test.custom");

        var nodes = new List<Node>
        {
            Node.ForPath(KnownPaths.Windows + @"\System32\drivers", true),
            FileNode(@"Users\tester\Documents", RemovalStrategy.Delete),
            FileNode(Local + @"\Temp", RemovalStrategy.Delete),                          // vỏ: không xoá chính nó
            FileNode(@"Users\tester\NTUSER.DAT", RemovalStrategy.Custom("test.custom")), // remover riêng vẫn bị chặn vùng cấm
            Node.ForPath(@"\\server\share\cache", true),
            FileNode(Local + @"\Vendor\Cache\..\..\..\..\Documents\report.txt", RemovalStrategy.Delete, Local + @"\Vendor\Cache"),
        };
        if (Links.TryCreateJunction(_tmp.P(Local + @"\Vendor\Cache\link"), _tmp.P("outside")))
            nodes.Add(FileNode(Local + @"\Vendor\Cache\link\secret.txt", RemovalStrategy.Delete, Local + @"\Vendor\Cache"));

        var engine = Engine(new StubHelper());
        engine.Register(custom);
        var plan = engine.MakePlan(nodes);

        Assert.Empty(plan.Items);
        Assert.Equal(nodes.Count, plan.BlockedItems.Count);
        Assert.Contains(plan.BlockedItems, b => b.Violation.Kind == PathPolicy.ViolationKind.ForbiddenZone);
        Assert.Contains(plan.BlockedItems, b => b.Violation.Kind == PathPolicy.ViolationKind.ProtectedDirectory);
        Assert.Contains(plan.BlockedItems, b => b.Violation.Kind == PathPolicy.ViolationKind.NetworkPath);
        Assert.Contains(plan.BlockedItems, b => b.Violation.Kind == PathPolicy.ViolationKind.OutsideAllowedRoots);

        var report = await engine.RunAsync(plan, dryRun: false);
        Assert.Empty(report.Entries);
        Assert.Empty(custom.Seen);
        Assert.True(_tmp.Exists(@"outside\secret.txt"));
        Assert.True(_tmp.Exists(@"Users\tester\Documents\report.txt"));
        Assert.True(_tmp.Exists(@"Users\tester\NTUSER.DAT"));
        Assert.True(_tmp.Exists(Local + @"\Temp\x.tmp"));
    }

    [Fact]
    public async Task NotOwnedItemGoesToHelperWhenAvailable()
    {
        var file = _tmp.File(Local + @"\Vendor\Cache\other-user.bin", 2048);
        var foreign = new PathPolicy(PathPolicy.PolicyMode.User, TestPolicy.ForeignSid, false, _home, []);
        var node = FileNode(Local + @"\Vendor\Cache\other-user.bin", RemovalStrategy.MoveToTrash, Local + @"\Vendor\Cache");

        // Không có helper: bị chặn với lý do cần quyền quản trị.
        var noHelper = Engine(null, foreign).MakePlan([node]);
        Assert.Empty(noHelper.Items);
        Assert.Equal(PathPolicy.ViolationKind.NeedsAdminHelper, Assert.Single(noHelper.BlockedItems).Violation.Kind);

        // Có helper: chuyển sang helper, xoá vĩnh viễn (Thùng rác không dùng qua helper).
        var helper = new StubHelper();
        var engine = Engine(helper, foreign);
        var plan = engine.MakePlan([node]);
        var item = Assert.Single(plan.Items);
        Assert.True(item.RequiresRoot);
        Assert.Equal(RemovalStrategy.Delete, item.Strategy);
        Assert.Equal(RemoverIDs.PrivilegedFile, item.RemoverID);
        Assert.True(plan.RequiresConfirmation);

        var report = await engine.RunAsync(plan, dryRun: false);

        var call = Assert.Single(helper.Calls);
        Assert.Equal(RemoveMode.Delete, call.Mode);
        Assert.Equal(new[] { file }, call.Paths, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1234, report.Entries.Single().Result.FreedBytes);
        Assert.True(File.Exists(file)); // helper giả không xoá
        Assert.Equal("delete", _storage.Items(report.OperationID).Single().Strategy);
    }

    [Fact]
    public async Task RequiresRootItemsAreCheckedAgainstHelperPolicy()
    {
        var helper = new StubHelper();
        var engine = Engine(helper);
        var inside = Node.ForPath(KnownPaths.Windows + @"\Temp\cb-test-nonexistent", true);
        inside.Removal = RemovalStrategy.DeleteContents;
        inside.RequiresRoot = true;
        var outside = Node.ForPath(KnownPaths.SystemDrive + @"\cb-test-nonexistent\x.tmp", false);
        outside.Removal = RemovalStrategy.Delete;
        outside.RequiresRoot = true;
        var shell = Node.ForPath(KnownPaths.Windows + @"\Temp", true);
        shell.Removal = RemovalStrategy.Delete;
        shell.RequiresRoot = true;

        var plan = engine.MakePlan([inside, outside, shell]);

        var item = Assert.Single(plan.Items);
        Assert.Equal(inside.Id, item.NodeID);
        Assert.Equal(RemoverIDs.PrivilegedFile, item.RemoverID);
        Assert.Contains(plan.BlockedItems, b => b.Violation.Kind == PathPolicy.ViolationKind.NotInRootAllowlist);
        Assert.Contains(plan.BlockedItems, b => b.Violation.Kind == PathPolicy.ViolationKind.ProtectedDirectory);

        var report = await engine.RunAsync(plan, dryRun: false);
        Assert.Equal(RemoveMode.DeleteContents, Assert.Single(helper.Calls).Mode);
        Assert.True(report.Entries.Single().Result.IsSuccess);

        Assert.Equal(PathPolicy.ViolationKind.NeedsAdminHelper, Assert.Single(Engine().MakePlan([inside]).BlockedItems).Violation.Kind);
    }

    [Fact]
    public async Task PrivilegedDryRunNeverCallsHelper()
    {
        var helper = new StubHelper();
        var engine = Engine(helper);
        var node = Node.ForPath(KnownPaths.Windows + @"\Temp\cb-test-nonexistent", true);
        node.Removal = RemovalStrategy.DeleteContents;
        node.RequiresRoot = true;
        var report = await engine.RunAsync(engine.MakePlan([node]), dryRun: true);
        Assert.Empty(helper.Calls);
        Assert.Equal(SkipReason.DryRun, report.Entries.Single().Result.Reason);
    }

    [Fact]
    public async Task CustomRemoversAreDispatchedAndUnknownOnesSkipped()
    {
        var custom = new RecordingRemover("test.custom");
        var engine = Engine();
        engine.Register(custom);
        Assert.Same(custom, engine.Remover("test.custom"));
        var known = Node.ForVirtual(new RegistryKeyItem("HKCU", @"Software\Vendor\App"));
        known.Removal = RemovalStrategy.Custom("test.custom");
        var unknown = Node.ForVirtual(new DockerPruneItem());
        unknown.Removal = RemovalStrategy.Custom("no.such.remover");

        var plan = engine.MakePlan([known, unknown]);
        Assert.Equal(2, plan.Items.Count);
        var report = await engine.RunAsync(plan, dryRun: false);

        Assert.Equal(known.Id, Assert.Single(custom.Seen).NodeID);
        var byNode = report.Entries.ToDictionary(e => e.Item.NodeID);
        Assert.Equal(RemoveOutcomeKind.Ok, byNode[known.Id].Result.Outcome);
        Assert.Equal(SkipReason.Unsupported, byNode[unknown.Id].Result.Reason);
    }

    [Fact]
    public async Task ReportCountsOkNotFoundAndInUse()
    {
        _tmp.File(Local + @"\Vendor\Cache\ok.bin", 4096);
        var locked = _tmp.File(Local + @"\Vendor\Cache\locked.bin", 4096);
        var engine = Engine();
        var plan = engine.MakePlan([
            FileNode(Local + @"\Vendor\Cache\ok.bin", RemovalStrategy.Delete, Local),
            FileNode(Local + @"\Vendor\Cache\missing.bin", RemovalStrategy.Delete, Local),
            FileNode(Local + @"\Vendor\Cache\locked.bin", RemovalStrategy.Delete, Local),
        ]);
        Assert.Equal(3, plan.Items.Count);

        CleanReport report;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            report = await engine.RunAsync(plan, dryRun: false);
        }

        Assert.Equal(2, report.Succeeded.Count());
        Assert.Single(report.Skipped);
        Assert.Single(report.InUse);
        Assert.Empty(report.Failed);
        Assert.True(File.Exists(locked));
        var op = _storage.Operations().Single();
        Assert.Equal(2, op.ItemsOk);
        Assert.Equal(0, op.ItemsFailed);
        var results = _storage.Items(report.OperationID).Select(l => l.Result).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "ok", "skipped:inUse", "skipped:notFound" }, results);
    }

    [Fact]
    public async Task CancelledRunRemovesNothing()
    {
        _tmp.File(Local + @"\Vendor\Cache\a.bin");
        var engine = Engine();
        var plan = engine.MakePlan([FileNode(Local + @"\Vendor\Cache\a.bin", RemovalStrategy.Delete, Local)]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await engine.RunAsync(plan, dryRun: false, ct: cts.Token);
        Assert.Empty(report.Entries);
        Assert.True(_tmp.Exists(Local + @"\Vendor\Cache\a.bin"));
    }

    [Fact]
    public void PlanFlagsConfirmationForReviewItems()
    {
        _tmp.File(Local + @"\Vendor\Cache\a.bin");
        var engine = Engine();
        var review = FileNode(Local + @"\Vendor\Cache\a.bin", RemovalStrategy.MoveToTrash, Local, SafetyLevel.Review, size: 300);
        var plan = engine.MakePlan([review, NodeFactory.Group("G", [])]);
        Assert.True(plan.RequiresConfirmation);
        Assert.Single(plan.ReviewItems);
        Assert.Empty(plan.PermanentItems);
        Assert.Equal(300, plan.TotalSize.Bytes);
        Assert.False(plan.IsEmpty);
    }

    [SkippableFact]
    public async Task MoveToTrashRecordsTrashedPathAndRestores()
    {
        var name = $"cb-test-{Guid.NewGuid():N}.msi";
        var path = _tmp.File($@"Users\tester\Downloads\{name}", 8192, ageDays: 90);
        var why = RecycleBinGuard.SkipReason(path);
        Skip.If(why is not null, why);

        var engine = Engine();
        var node = FileNode($@"Users\tester\Downloads\{name}", RemovalStrategy.MoveToTrash, @"Users\tester\Downloads", SafetyLevel.Review, 8192);
        var plan = engine.MakePlan([node]);
        Assert.True(plan.RequiresConfirmation);
        var report = await engine.RunAsync(plan, dryRun: false);
        var trashed = report.Entries.Single().Result.TrashedPath;
        try
        {
            Assert.True(report.Entries.Single().Result.IsSuccess);
            Assert.NotNull(trashed);
            Assert.True(DirectoryReader.Exists(trashed));
            Assert.False(File.Exists(path));

            var log = _storage.Items(report.OperationID).Single();
            Assert.True(log.IsRestorable);
            Assert.Equal("trash", log.Strategy);
            engine.Restore(log);
            trashed = null;

            Assert.True(File.Exists(path));
            var restored = _storage.Items(report.OperationID).Single();
            Assert.Equal("restored", restored.Result);
            Assert.Throws<CleanEngine.RestoreException>(() => engine.Restore(restored));
        }
        finally
        {
            Core.RecycleBinTests.PurgeTestItem(trashed);
        }
    }
}
