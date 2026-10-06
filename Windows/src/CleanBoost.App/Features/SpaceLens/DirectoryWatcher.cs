using System.Windows.Threading;
using CleanBoost.Core;

namespace CleanBoost.App.Features.SpaceLens;

/// <summary>
/// Theo dõi thay đổi dưới thư mục đang xem (thay FSEvents của bản Mac): <see cref="FileSystemWatcher"/> đệ quy, gom các
/// thư mục cha của mục thay đổi, báo về luồng giao diện tối đa mỗi giây một lần (độ trễ 1 giây như bản Mac).
/// </summary>
internal sealed class DirectoryWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string? _path;

    /// <summary>Danh sách thư mục có thay đổi (trên luồng giao diện).</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    public DirectoryWatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Flush();
    }

    public string? WatchedPath => _path;

    public void Start(string path)
    {
        if (_watcher is not null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase)) return;
        Stop();
        if (!Directory.Exists(path)) return;
        try
        {
            var w = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += OnEvent;
            w.Deleted += OnEvent;
            w.Changed += OnChanged;
            w.Renamed += OnRenamed;
            w.Error += (_, e) => Log.Warning(LogCategory.Scan, "Space Lens: theo dõi thay đổi lỗi: " + e.GetException().Message);
            w.EnableRaisingEvents = true;
            _watcher = w;
            _path = path;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, "Space Lens: không theo dõi được " + path + ": " + ex.Message);
        }
    }

    public void Stop()
    {
        var w = _watcher;
        _watcher = null;
        _path = null;
        if (w is not null)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _timer.Stop();
        lock (_gate) _pending.Clear();
    }

    public void Dispose() => Stop();

    private void OnEvent(object sender, FileSystemEventArgs e) => Add(e.FullPath);

    /// <summary>Thư mục tự đổi thời gian sửa khi bên trong thay đổi — đã có sự kiện riêng của mục bên trong, bỏ qua.</summary>
    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Directory.Exists(e.FullPath)) return;
        Add(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        Add(e.OldFullPath);
        Add(e.FullPath);
    }

    private void Add(string fullPath)
    {
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return;
        bool first;
        lock (_gate)
        {
            first = _pending.Count == 0;
            _pending.Add(parent);
        }
        if (first) _dispatcher.BeginInvoke(() => { if (!_timer.IsEnabled && _watcher is not null) _timer.Start(); });
    }

    private void Flush()
    {
        _timer.Stop();
        List<string> dirs;
        lock (_gate)
        {
            dirs = [.. _pending];
            _pending.Clear();
        }
        if (dirs.Count > 0 && _watcher is not null) Changed?.Invoke(dirs);
    }
}
