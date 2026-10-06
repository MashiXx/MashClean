using System.Diagnostics;
using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;

namespace CleanBoost.Engine.Scanning;

public enum TaskState { Pending, Ready, Running, Succeeded, Failed, Cancelled, Skipped }

public abstract record ScanEvent
{
    public sealed record ProgressChanged(ScanProgress Progress) : ScanEvent;
    public sealed record TaskStateChanged(string TaskID, TaskState State) : ScanEvent;
}

/// <summary>Kết quả một phiên quét (ScanSession, mục 5.1).</summary>
public sealed class ScanResult
{
    public required NodeTree Tree { get; init; }
    public required IReadOnlyDictionary<string, ScanOutput> Outputs { get; init; }
    public required IReadOnlyDictionary<string, TaskState> States { get; init; }
    public required IReadOnlyList<ScanWarning> Warnings { get; init; }
    public TimeSpan Duration { get; init; }
    public long FilesVisited { get; init; }

    /// <summary>Node gốc của cây đã gộp đến từ một nhóm task (Smart Scan tách theo feature).</summary>
    public List<Node> NodesOf(IReadOnlySet<string> taskIDs)
    {
        var wanted = Outputs.Where(kv => taskIDs.Contains(kv.Key)).SelectMany(kv => kv.Value.Nodes).Select(n => n.Id).ToHashSet();
        return Tree.Roots.Where(r => wanted.Contains(r.Id)).ToList();
    }
}

/// <summary>
/// Lõi quét (mục 5): chạy DAG với số task đồng thời giới hạn (4 trên SSD, 1 trên HDD), hàng đợi Ready sắp theo
/// (priority desc, weight desc). Task lỗi làm các task phụ thuộc thành Skipped; nhánh khác chạy tiếp. Mỗi task có timeout mềm.
/// </summary>
public sealed class ScanEngine(FileSystemService fileSystem, int? maxConcurrentIO = null, TimeSpan? taskTimeout = null)
{
    public FileSystemService FileSystem { get; } = fileSystem;
    public TimeSpan TaskTimeout { get; } = taskTimeout ?? TimeSpan.FromSeconds(120);

    public async Task<ScanResult> RunAsync(ScanGraph graph, RuleSnapshot rules, ScanEnvironment environment,
        Action<ScanEvent>? onEvent = null, CancellationToken cancellationToken = default)
    {
        var emit = onEvent ?? (_ => { });
        var stopwatch = Stopwatch.StartNew();
        var aggregator = new ProgressAggregator(graph, p => emit(new ScanEvent.ProgressChanged(p)));
        var limit = Math.Max(1, maxConcurrentIO ?? FileSystem.RecommendedConcurrency(FileSystem.Home));
        var states = graph.Tasks.ToDictionary(t => t.Id, _ => TaskState.Pending);
        var outputs = new Dictionary<string, ScanOutput>();
        var warnings = new List<ScanWarning>();
        var remaining = graph.Tasks.ToDictionary(t => t.Id, t => new HashSet<string>(t.Dependencies));
        var ready = new List<IScanTask>(graph.Tasks.Where(t => t.Dependencies.Count == 0));
        foreach (var t in ready) states[t.Id] = TaskState.Ready;
        var running = new Dictionary<Task<(string Id, TaskState State, ScanOutput? Output)>, string>();

        Log.Info(LogCategory.Scan, $"Bắt đầu quét {graph.Tasks.Count} task, đồng thời {limit}");

        void SortReady() => ready.Sort((a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : b.EstimatedWeight.CompareTo(a.EstimatedWeight));

        void LaunchReady()
        {
            SortReady();
            while (running.Count < limit && ready.Count > 0)
            {
                var task = ready[0];
                ready.RemoveAt(0);
                states[task.Id] = TaskState.Running;
                emit(new ScanEvent.TaskStateChanged(task.Id, TaskState.Running));
                aggregator.Started(task.Id);
                var upstream = graph.TransitiveDependencies(task.Id).Where(outputs.ContainsKey).ToDictionary(d => d, d => outputs[d]);
                var context = new ScanContext
                {
                    FileSystem = FileSystem,
                    Rules = rules,
                    Progress = aggregator.Reporter(task.Id),
                    Upstream = upstream,
                    Environment = environment,
                };
                running[RunWithTimeout(task, context, task.Timeout ?? TaskTimeout, cancellationToken)] = task.Id;
            }
        }

        LaunchReady();
        while (running.Count > 0)
        {
            var finished = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            running.Remove(finished);
            var (id, state, output) = await finished.ConfigureAwait(false);
            aggregator.Finished(id);
            states[id] = state;
            emit(new ScanEvent.TaskStateChanged(id, state));
            if (output is not null)
            {
                outputs[id] = output;
                warnings.AddRange(output.Warnings);
            }
            if (state == TaskState.Succeeded)
            {
                foreach (var dep in graph.Dependents.GetValueOrDefault(id) ?? [])
                {
                    remaining[dep].Remove(id);
                    if (remaining[dep].Count == 0 && states[dep] == TaskState.Pending && graph.Task(dep) is { } t)
                    {
                        states[dep] = TaskState.Ready;
                        ready.Add(t);
                    }
                }
            }
            else
            {
                if (state == TaskState.Failed)
                {
                    var message = output?.Warnings.FirstOrDefault()?.Message ?? "";
                    Log.Warning(LogCategory.Scan, $"Task {id} lỗi");
                }
                // Task lỗi: các task phụ thuộc thành Skipped, các nhánh khác vẫn chạy tiếp (mục 5.3 bước 4).
                foreach (var dep in graph.TransitiveDependents(id).Where(d => states[d] == TaskState.Pending))
                {
                    states[dep] = TaskState.Skipped;
                    emit(new ScanEvent.TaskStateChanged(dep, TaskState.Skipped));
                    warnings.Add(new ScanWarning(dep, ScanWarningKind.Skipped, Loc.T("Bỏ qua vì task phụ thuộc lỗi")));
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var key in states.Keys.ToList().Where(k => states[k] is TaskState.Pending or TaskState.Ready or TaskState.Running))
                {
                    states[key] = TaskState.Cancelled;
                    emit(new ScanEvent.TaskStateChanged(key, TaskState.Cancelled));
                }
                throw new OperationCanceledException(cancellationToken);
            }
            LaunchReady();
        }
        cancellationToken.ThrowIfCancellationRequested();
        aggregator.Flush();

        // Gộp nodes theo thứ tự topo, khử trùng, tính tổng (mục 23.2).
        var nodes = graph.TopologicalOrder.SelectMany(id => outputs.TryGetValue(id, out var o) ? o.Nodes : []);
        var tree = NodeTree.Merge(nodes);
        stopwatch.Stop();
        Log.Info(LogCategory.Scan, $"Quét xong trong {stopwatch.Elapsed.TotalSeconds:F2} giây, tìm được {tree.TotalSize.Bytes} byte");
        return new ScanResult
        {
            Tree = tree,
            Outputs = outputs,
            States = states,
            Warnings = warnings,
            Duration = stopwatch.Elapsed,
            FilesVisited = aggregator.FileCounter.Count,
        };
    }

    /// <summary>
    /// Task chạy riêng và đua với timeout/huỷ: kết quả trả về ngay khi một bên xong, không chờ task đang kẹt
    /// (vd lời gọi hệ thống bị chặn).
    /// </summary>
    private static async Task<(string, TaskState, ScanOutput?)> RunWithTimeout(IScanTask task, ScanContext context, TimeSpan timeout, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        var ctx = new ScanContext
        {
            FileSystem = context.FileSystem,
            Rules = context.Rules,
            Progress = context.Progress,
            Upstream = context.Upstream,
            Environment = context.Environment,
            Cancellation = cts.Token,
        };
        var work = Task.Run(() => task.RunAsync(ctx), cts.Token);
        var timer = Task.Delay(timeout, outer);
        var winner = await Task.WhenAny(work, timer).ConfigureAwait(false);
        if (winner != work)
        {
            cts.Cancel();
            _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
            if (outer.IsCancellationRequested) return (task.Id, TaskState.Cancelled, null);
            return (task.Id, TaskState.Failed, new ScanOutput { Warnings = [new ScanWarning(task.Id, ScanWarningKind.Timeout, Loc.F("%@: quá thời gian", task.Title))] });
        }
        try
        {
            var output = await work.ConfigureAwait(false);
            return (task.Id, TaskState.Succeeded, output);
        }
        catch (OperationCanceledException)
        {
            return (task.Id, TaskState.Cancelled, null);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Scan, $"Task {task.Id} lỗi", ex);
            return (task.Id, TaskState.Failed, new ScanOutput { Warnings = [new ScanWarning(task.Id, ScanWarningKind.Failed, task.Title + ": " + ex.Message)] });
        }
    }
}
