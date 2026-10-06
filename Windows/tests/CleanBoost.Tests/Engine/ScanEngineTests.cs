using System.Collections.Concurrent;
using System.Diagnostics;
using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Tests.Engine;

/// <summary>Task giả cho test scheduler (mục 5).</summary>
internal sealed class StubTask(string id, params string[] dependencies) : IScanTask
{
    public enum Behavior { Succeed, Fail, Sleep, Block, ReadUpstream }

    public string Id { get; } = id;
    public IReadOnlyList<string> Dependencies { get; } = dependencies;
    public ScanPriority Priority { get; init; } = ScanPriority.Normal;
    public double EstimatedWeight { get; init; } = 1;
    public string Title => "Stub " + Id;
    public Behavior Kind { get; init; } = Behavior.Succeed;
    public long Size { get; init; } = 10;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);
    public string UpstreamKey { get; init; } = "";
    public Action<string>? OnStart { get; init; }
    public Action<string>? OnEnd { get; init; }

    public sealed class Failure() : Exception("stub failure");

    public async Task<ScanOutput> RunAsync(ScanContext context)
    {
        OnStart?.Invoke(Id);
        try
        {
            switch (Kind)
            {
                case Behavior.Fail:
                    throw new Failure();
                case Behavior.Sleep:
                    await Task.Delay(Duration, context.Cancellation);
                    return ScanOutput.Empty;
                case Behavior.Block:
                    Thread.Sleep(Duration); // giống lời gọi hệ thống bị kẹt: không nhìn token
                    return ScanOutput.Empty;
                case Behavior.ReadUpstream:
                {
                    var seen = context.UpstreamArtifact<object>(UpstreamKey) as long? ?? -1;
                    return new ScanOutput { Artifacts = { ["seen"] = seen } };
                }
                default:
                {
                    context.Progress.Report(0.5);
                    var leaf = Node.ForVirtual(new DockerPruneItem());
                    leaf.Size = new ByteCount(Size);
                    leaf.Safety = SafetyLevel.Safe;
                    leaf.Category = "test";
                    return new ScanOutput
                    {
                        Nodes = [Node.Group(new LocalizedText("G-" + Id, "G-" + Id), "folder", null, [leaf])],
                        Artifacts = { [Id] = Size },
                    };
                }
            }
        }
        finally
        {
            OnEnd?.Invoke(Id);
        }
    }
}

public class ScanGraphTests
{
    [Fact]
    public void CycleIsRejected()
    {
        Assert.Throws<ScanGraphException>(() => new ScanGraph([new StubTask("a", "b"), new StubTask("b", "c"), new StubTask("c", "a")]));
        var self = Assert.Throws<ScanGraphException>(() => new ScanGraph([new StubTask("a", "a")]));
        Assert.Contains("a", self.Message);
    }

    [Fact]
    public void MissingDependencyIsRejected()
    {
        var e = Assert.Throws<ScanGraphException>(() => new ScanGraph([new StubTask("a", "ghost")]));
        Assert.Contains("ghost", e.Message);
    }

    [Fact]
    public void TopologicalOrderAndDuplicates()
    {
        var first = new StubTask("a");
        var g = new ScanGraph([new StubTask("c", "b"), new StubTask("b", "a"), first, new StubTask("a") { Kind = StubTask.Behavior.Fail }]);
        Assert.Equal(3, g.Tasks.Count);
        Assert.Equal(new[] { "a", "b", "c" }, g.TopologicalOrder);
        Assert.Equal(new HashSet<string> { "b", "c" }, g.TransitiveDependents("a"));
        Assert.Equal(new HashSet<string> { "a", "b" }, g.TransitiveDependencies("c"));
        Assert.Same(first, g.Task("a")); // trùng id: giữ task đầu tiên
        Assert.Null(g.Task("zzz"));
    }

    [Fact]
    public void DiamondDependencies()
    {
        var g = new ScanGraph([new StubTask("d", "b", "c"), new StubTask("b", "a"), new StubTask("c", "a"), new StubTask("a")]);
        Assert.Equal("a", g.TopologicalOrder[0]);
        Assert.Equal("d", g.TopologicalOrder[^1]);
        Assert.Equal(new HashSet<string> { "b", "c", "d" }, g.TransitiveDependents("a"));
    }
}

public class ScanEngineTests
{
    private static readonly FileSystemService Fs = new(Path.GetTempPath());
    private static readonly ScanEnvironment Env = new() { Policy = PathPolicy.CurrentUser };

    private static Task<ScanResult> Run(IEnumerable<IScanTask> tasks, int concurrency = 2, TimeSpan? timeout = null,
        Action<ScanEvent>? onEvent = null, CancellationToken ct = default) =>
        new ScanEngine(Fs, concurrency, timeout ?? TimeSpan.FromSeconds(30)).RunAsync(new ScanGraph(tasks), RuleSnapshot.Empty, Env, onEvent, ct);

    [Fact]
    public async Task FailedDependencySkipsDependentsButOthersRun()
    {
        var result = await Run([
            new StubTask("apps") { Kind = StubTask.Behavior.Fail },
            new StubTask("caches", "apps"),
            new StubTask("deep", "caches"),
            new StubTask("logs"),
        ]);
        Assert.Equal(TaskState.Failed, result.States["apps"]);
        Assert.Equal(TaskState.Skipped, result.States["caches"]);
        Assert.Equal(TaskState.Skipped, result.States["deep"]);
        Assert.Equal(TaskState.Succeeded, result.States["logs"]);
        Assert.Equal(2, result.Warnings.Count(w => w.Kind == ScanWarningKind.Skipped));
        Assert.Contains(result.Warnings, w => w.Kind == ScanWarningKind.Failed && w.TaskID == "apps" && w.Message.Contains("stub failure"));
        Assert.Single(result.Tree.AllLeaves);
    }

    [Fact]
    public async Task UpstreamArtifactsReachTransitiveDependents()
    {
        var result = await Run([
            new StubTask("producer") { Size = 42 },
            new StubTask("middle", "producer"),
            new StubTask("consumer", "middle") { Kind = StubTask.Behavior.ReadUpstream, UpstreamKey = "producer" },
            new StubTask("unrelated") { Kind = StubTask.Behavior.ReadUpstream, UpstreamKey = "producer" },
        ]);
        Assert.Equal(42L, result.Outputs["consumer"].Artifacts["seen"]);
        Assert.Equal(-1L, result.Outputs["unrelated"].Artifacts["seen"]); // không phụ thuộc → không thấy
        Assert.Equal(52, result.Tree.TotalSize.Bytes);                    // node từ nhiều task được gộp
        Assert.Equal(2, result.NodesOf(new HashSet<string> { "producer", "middle" }).Count);
    }

    [Fact]
    public async Task TimeoutMarksTaskFailedAndSkipsDependents()
    {
        var sw = Stopwatch.StartNew();
        var result = await Run([
            new StubTask("slow") { Kind = StubTask.Behavior.Sleep, Duration = TimeSpan.FromSeconds(30) },
            new StubTask("after", "slow"),
            new StubTask("fast"),
        ], timeout: TimeSpan.FromMilliseconds(300));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.Equal(TaskState.Failed, result.States["slow"]);
        Assert.Equal(TaskState.Skipped, result.States["after"]);
        Assert.Equal(TaskState.Succeeded, result.States["fast"]);
        Assert.Contains(result.Warnings, w => w.Kind == ScanWarningKind.Timeout && w.TaskID == "slow");
    }

    [Fact]
    public async Task TimeoutDoesNotWaitForBlockedTask()
    {
        var sw = Stopwatch.StartNew();
        var result = await Run([
            new StubTask("stuck") { Kind = StubTask.Behavior.Block, Duration = TimeSpan.FromSeconds(4) },
            new StubTask("fast"),
        ], timeout: TimeSpan.FromMilliseconds(300));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
        Assert.Equal(TaskState.Failed, result.States["stuck"]);
        Assert.Equal(TaskState.Succeeded, result.States["fast"]);
    }

    [Fact]
    public async Task CancellationStopsTheScanPromptly()
    {
        using var cts = new CancellationTokenSource();
        var events = new ConcurrentQueue<ScanEvent>();
        var run = Run([new StubTask("slow") { Kind = StubTask.Behavior.Sleep }, new StubTask("later", "slow")], concurrency: 1,
            timeout: TimeSpan.FromSeconds(60), onEvent: events.Enqueue, ct: cts.Token);
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), sw.Elapsed.ToString());
        Assert.Contains(events, e => e is ScanEvent.TaskStateChanged { TaskID: "slow", State: TaskState.Cancelled });
        Assert.DoesNotContain(events, e => e is ScanEvent.TaskStateChanged { TaskID: "later", State: TaskState.Running });
    }

    [Fact]
    public async Task CancellationDoesNotWaitForBlockedTask()
    {
        using var cts = new CancellationTokenSource();
        var run = Run([new StubTask("stuck") { Kind = StubTask.Behavior.Block, Duration = TimeSpan.FromSeconds(4) }], concurrency: 1,
            timeout: TimeSpan.FromSeconds(60), ct: cts.Token);
        await Task.Delay(200);
        var sw = Stopwatch.StartNew();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), sw.Elapsed.ToString());
    }

    [Fact]
    public async Task AlreadyCancelledTokenThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run([new StubTask("a")], ct: cts.Token));
    }

    [Fact]
    public async Task ReadyQueueIsOrderedByPriorityThenWeight()
    {
        var order = new ConcurrentQueue<string>();
        await Run([
            new StubTask("low") { Priority = ScanPriority.Low, EstimatedWeight = 100, OnStart = order.Enqueue },
            new StubTask("normal-light") { EstimatedWeight = 1, OnStart = order.Enqueue },
            new StubTask("high") { Priority = ScanPriority.High, OnStart = order.Enqueue },
            new StubTask("normal-heavy") { EstimatedWeight = 5, OnStart = order.Enqueue },
        ], concurrency: 1);
        Assert.Equal(new[] { "high", "normal-heavy", "normal-light", "low" }, order);
    }

    [Fact]
    public async Task ConcurrencyLimitIsRespected()
    {
        var running = 0;
        var peak = 0;
        void Start(string _)
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
        }
        void End(string _) => Interlocked.Decrement(ref running);
        var tasks = Enumerable.Range(0, 8).Select(i => (IScanTask)new StubTask("t" + i)
        {
            Kind = StubTask.Behavior.Sleep, Duration = TimeSpan.FromMilliseconds(80), OnStart = Start, OnEnd = End,
        }).ToList();
        var result = await Run(tasks, concurrency: 3);
        Assert.All(result.States.Values, s => Assert.Equal(TaskState.Succeeded, s));
        Assert.InRange(peak, 1, 3);
    }

    [Fact]
    public async Task EmitsFinalProgressOfOne()
    {
        ScanProgress? last = null;
        var gate = new object();
        var states = new ConcurrentQueue<(string, TaskState)>();
        var result = await Run([new StubTask("a") { EstimatedWeight = 1 }, new StubTask("b") { EstimatedWeight = 4 }], onEvent: e =>
        {
            if (e is ScanEvent.ProgressChanged p) lock (gate) last = p.Progress;
            if (e is ScanEvent.TaskStateChanged s) states.Enqueue((s.TaskID, s.State));
        });
        Assert.NotNull(last);
        Assert.Equal(1, last.Fraction);
        Assert.Equal(2, last.CompletedTasks);
        Assert.Equal(2, last.TotalTasks);
        Assert.Empty(last.RunningTasks);
        Assert.All(result.States.Values, s => Assert.Equal(TaskState.Succeeded, s));
        Assert.Contains(("a", TaskState.Running), states);
        Assert.Contains(("b", TaskState.Succeeded), states);
    }

    [Fact]
    public async Task EmptyGraphProducesEmptyTree()
    {
        var result = await Run([]);
        Assert.True(result.Tree.IsEmpty);
        Assert.Empty(result.States);
    }

    [Fact]
    public void ProgressAggregatorWeightsTasks()
    {
        var graph = new ScanGraph([new StubTask("a") { EstimatedWeight = 1 }, new StubTask("b") { EstimatedWeight = 3 }]);
        var agg = new ProgressAggregator(graph, _ => { });
        agg.Started("a");
        agg.Reporter("b").Report(0.5);
        agg.Finished("a");
        var p = agg.Snapshot();
        Assert.Equal((1 * 1 + 3 * 0.5) / 4, p.Fraction, 6);
        Assert.Equal(1, p.CompletedTasks);
        agg.Reporter("b").Report(7); // bị kẹp về 1
        Assert.Equal(1, agg.Snapshot().Fraction);
        agg.Reporter("a").AddBytesFound(new ByteCount(10));
        agg.Reporter("a").AddFilesVisited(5);
        Assert.Equal(10, agg.Snapshot().BytesFound.Bytes);
        Assert.Equal(5, agg.Snapshot().FilesVisited);
    }
}
