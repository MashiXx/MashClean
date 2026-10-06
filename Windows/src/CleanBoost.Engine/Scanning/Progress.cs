using CleanBoost.Core;
using CleanBoost.Engine.FileSystem;

namespace CleanBoost.Engine.Scanning;

/// <summary>Tiến độ tổng của một phiên quét (mục 5.4).</summary>
public sealed record ScanProgress(
    double Fraction,
    string CurrentTask,
    IReadOnlyList<string> RunningTasks,
    long FilesVisited,
    ByteCount BytesFound,
    int CompletedTasks,
    int TotalTasks)
{
    public static readonly ScanProgress Zero = new(0, "", [], 0, ByteCount.Zero, 0, 0);
}

/// <summary>Reporter tiến độ của một task: báo 0…1, dung lượng tìm được, số file đã duyệt.</summary>
public sealed class ProgressReporter
{
    private readonly ProgressAggregator _owner;
    public string TaskID { get; }
    public VisitCounter FileCounter => _owner.FileCounter;

    internal ProgressReporter(ProgressAggregator owner, string taskID)
    {
        _owner = owner;
        TaskID = taskID;
    }

    public void Report(double fraction) => _owner.Update(TaskID, Math.Clamp(fraction, 0, 1));
    public void AddBytesFound(ByteCount bytes) => _owner.AddBytes(bytes.Bytes);
    public void AddFilesVisited(int n) => FileCounter.Add(n);

    /// <summary>Reporter rời (dùng khi chạy task ngoài engine, vd test).</summary>
    public static ProgressReporter Detached(string taskID = "detached") => new ProgressAggregator(null, _ => { }).Reporter(taskID);
}

/// <summary>
/// Gom tiến độ các task: tổng = Σ(weightᵢ × progressᵢ) / Σ weightᵢ. Đẩy lên UI tối đa 10 lần/giây.
/// </summary>
public sealed class ProgressAggregator
{
    private readonly object _lock = new();
    private readonly Dictionary<string, double> _weights = [];
    private readonly Dictionary<string, double> _fractions = [];
    private readonly Dictionary<string, string> _titles = [];
    private readonly List<string> _running = [];
    private readonly Action<ScanProgress> _emit;
    private long _bytes;
    private int _completed;
    private DateTime _lastEmit = DateTime.MinValue;

    public VisitCounter FileCounter { get; } = new();

    public ProgressAggregator(ScanGraph? graph, Action<ScanProgress> emit)
    {
        _emit = emit;
        if (graph is null) return;
        foreach (var t in graph.Tasks)
        {
            _weights[t.Id] = Math.Max(0.0001, t.EstimatedWeight);
            _fractions[t.Id] = 0;
            _titles[t.Id] = t.Title;
        }
    }

    public ProgressReporter Reporter(string taskID) => new(this, taskID);

    internal void Update(string id, double fraction)
    {
        lock (_lock) _fractions[id] = fraction;
        EmitThrottled();
    }

    internal void AddBytes(long bytes)
    {
        Interlocked.Add(ref _bytes, bytes);
        EmitThrottled();
    }

    public void Started(string id)
    {
        lock (_lock) _running.Add(id);
        EmitThrottled();
    }

    public void Finished(string id)
    {
        lock (_lock)
        {
            _running.Remove(id);
            _fractions[id] = 1;
            _completed++;
        }
        EmitThrottled();
    }

    private void EmitThrottled()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastEmit).TotalMilliseconds < 100) return;
            _lastEmit = now;
        }
        _emit(Snapshot());
    }

    /// <summary>Gửi giá trị cuối, không throttle.</summary>
    public void Flush() => _emit(Snapshot());

    public ScanProgress Snapshot()
    {
        lock (_lock)
        {
            var totalWeight = _weights.Values.Sum();
            var done = _weights.Sum(kv => kv.Value * _fractions.GetValueOrDefault(kv.Key));
            var fraction = totalWeight > 0 ? Math.Min(1, done / totalWeight) : 0;
            var current = _running.Count > 0 ? _titles.GetValueOrDefault(_running[^1], _running[^1]) : "";
            return new ScanProgress(fraction, current, _running.Select(r => _titles.GetValueOrDefault(r, r)).ToArray(), FileCounter.Count,
                new ByteCount(Interlocked.Read(ref _bytes)), _completed, _weights.Count);
        }
    }
}
