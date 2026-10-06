using System.Diagnostics;
using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Features.SpaceLens;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.SpaceLens;

public enum LensPhase { Idle, Scanning, Results, Error }

/// <summary>
/// Trạng thái màn Space Lens (SpaceLensViewModel của bản Mac): chọn ổ/thư mục → quét → bản đồ + danh sách,
/// cập nhật tăng dần khi thư mục đang xem thay đổi, chọn mục rồi chuyển vào Thùng rác qua Clean Engine.
/// Cây (<see cref="Tree"/>) chỉ được sửa trên luồng giao diện; I/O chạy nền.
/// </summary>
public sealed partial class SpaceLensViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly DiskScanner _scanner;
    private readonly SpaceLensCleaner _cleaner;
    private readonly DirectoryWatcher _watcher;
    private readonly DispatcherTimer _ticker;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource _refreshCts = new();
    private DiskScanProgress? _progress;
    private readonly List<string> _pendingRefresh = [];
    private bool _draining;
    private bool _active = true;
    private long _scanUsedBytes;

    [ObservableProperty] private LensPhase _phase = LensPhase.Idle;
    [ObservableProperty] private string _scanRoot = "";
    [ObservableProperty] private long _scanItems;
    [ObservableProperty] private long _scanBytes;
    /// <summary>Tiến độ ước tính khi quét cả ổ (dung lượng đã cộng / dung lượng đã dùng), <c>null</c> khi quét thư mục.</summary>
    [ObservableProperty] private double? _scanFraction;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isCleaning;
    [ObservableProperty] private double _scanDuration;
    [ObservableProperty] private SunburstSegment? _hovered;

    public SpaceLensViewModel(AppEnvironment env, System.Windows.Threading.Dispatcher dispatcher)
    {
        _env = env;
        _scanner = new DiskScanner(env.FileSystem);
        _cleaner = new SpaceLensCleaner(env.CleanEngine);
        _watcher = new DirectoryWatcher(dispatcher);
        _watcher.Changed += dirs => EnqueueRefresh(SpaceLensRefreshPlanner.Directories(dirs, Tree.RootPath));
        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(150) };
        _ticker.Tick += (_, _) => UpdateProgress();
    }

    public AppEnvironment Env => _env;
    public DiskTree Tree { get; private set; } = new(@"C:\");
    public SpaceLensNavigator Navigator { get; } = new();
    public IReadOnlyList<SunburstSegment> Segments { get; private set; } = [];
    /// <summary>Mục đang chọn: đường dẫn (không phân biệt hoa thường) → chỉ số trong cây hiện tại (tìm lại sau mỗi lần cây đổi).</summary>
    private readonly Dictionary<string, int> _selection = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Selection => _selection.Keys;
    public int SelectionCount => _selection.Count;
    /// <summary>Tên hiển thị của gốc (tên ổ khi quét cả ổ).</summary>
    public string RootTitle { get; private set; } = "";
    public bool IsBusy => Phase == LensPhase.Scanning || IsCleaning;
    public bool DryRun => _env.Settings.DryRun;

    /// <summary>Cây thay đổi (quét xong, cập nhật, xoá): vẽ lại bản đồ và danh sách.</summary>
    public event Action? TreeChanged;
    /// <summary>Đổi thư mục đang xem.</summary>
    public event Action? NavigationChanged;
    public event Action? SelectionChanged;

    partial void OnPhaseChanged(LensPhase value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsCleaningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    // MARK: Quét

    /// <summary>Thư mục quét gần nhất (giữ qua các lần mở app).</summary>
    public static string? LastPath
    {
        get
        {
            try { return File.Exists(LastPathFile) ? File.ReadAllText(LastPathFile).Trim() : null; } catch { return null; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(KnownPaths.AppData);
                File.WriteAllText(LastPathFile, value ?? "");
            }
            catch
            {
                // không quan trọng
            }
        }
    }

    private static string LastPathFile => Path.Combine(KnownPaths.AppData, "spacelens-last.txt");

    public async void Scan(string path)
    {
        _scanCts?.Cancel();
        CancelRefresh();
        _watcher.Stop();
        _selection.Clear();
        Hovered = null;
        Notice = null;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var progress = new DiskScanProgress();
        _progress = progress;
        var root = DiskTree.NormalizePath(path);
        ScanRoot = root;
        ScanItems = 0;
        ScanBytes = 0;
        _scanUsedBytes = 0;
        if (PathPolicy.VolumeRoot(root) is { } vr && DiskTree.NormalizePath(vr).Equals(root, StringComparison.OrdinalIgnoreCase)
            && _env.FileSystem.Volume(root) is { } volume)
        {
            _scanUsedBytes = Math.Max(1, volume.Total - volume.Available);
        }
        ScanFraction = _scanUsedBytes > 0 ? 0 : null;
        Phase = LensPhase.Scanning;
        _ticker.Start();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var tree = await Task.Run(() => _scanner.Scan(root, progress, cts.Token), cts.Token).ConfigureAwait(true);
            if (_scanCts != cts) return;
            Tree = tree;
            ScanDuration = stopwatch.Elapsed.TotalSeconds;
            Navigator.Reset();
            RootTitle = MakeRootTitle(tree.RootPath);
            LastPath = tree.RootPath;
            Phase = LensPhase.Results;
            OnTreeChanged();
            var stats = _scanner.LastStats;
            Log.Info(LogCategory.Scan, $"Space Lens: quét {tree.RootPath}: {tree.Count:N0} mục, {ByteCount.Format(tree.TotalSize)} trong {ScanDuration:F1} giây"
                + (stats is null ? "" : $", {stats.Threads} luồng, {stats.Directories:N0} thư mục, {stats.UnreadableDirectories} không đọc được, {stats.HardLinkDuplicates} hard link trùng")
                + $", bộ nhớ cây {tree.EstimatedMemory / 1_000_000.0:F1} MB");
        }
        catch (OperationCanceledException)
        {
            if (_scanCts == cts) Phase = LensPhase.Idle;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Scan, "Space Lens: quét lỗi", ex);
            if (_scanCts == cts)
            {
                ErrorMessage = Loc.F("Không quét được %@: %@", root.AbbreviatingHome(), ex.Message);
                Phase = LensPhase.Error;
            }
        }
        finally
        {
            if (_scanCts == cts)
            {
                _ticker.Stop();
                UpdateProgress();
            }
        }
    }

    private void UpdateProgress()
    {
        if (_progress is not { } p) return;
        ScanItems = p.Items;
        ScanBytes = p.Bytes;
        if (_scanUsedBytes > 0) ScanFraction = Math.Min(0.99, (double)p.Bytes / _scanUsedBytes);
    }

    public void Rescan()
    {
        if (Phase == LensPhase.Results) Scan(Tree.RootPath);
    }

    /// <summary>Dừng quét/cập nhật và về màn chọn ổ.</summary>
    public void ChooseAnother()
    {
        _scanCts?.Cancel();
        _scanCts = null;
        CancelRefresh();
        _watcher.Stop();
        _ticker.Stop();
        _selection.Clear();
        Hovered = null;
        Notice = null;
        Phase = LensPhase.Idle;
    }

    public void Cancel() => ChooseAnother();

    private string MakeRootTitle(string root)
    {
        if (PathPolicy.VolumeRoot(root) is { } vr && DiskTree.NormalizePath(vr).Equals(root, StringComparison.OrdinalIgnoreCase))
            return _env.FileSystem.Volume(root)?.Name ?? root;
        return SpaceLensNavigator.RootTitle(root);
    }

    // MARK: Điều hướng

    public int CurrentIndex => Navigator.CurrentIndex(Tree);
    public string CurrentPath => Tree.Path(CurrentIndex);

    /// <summary>Tên thư mục đang xem (gốc dùng tên hiển thị của gốc).</summary>
    public string CurrentTitle => CurrentIndex == DiskTree.Root ? RootTitle : Tree.Name(CurrentIndex);

    public bool Drill(int index)
    {
        if (!Navigator.Drill(index, Tree)) return false;
        Hovered = null;
        NavigationDidChange();
        return true;
    }

    public void GoUp()
    {
        if (!Navigator.CanGoUp) return;
        Navigator.GoUp();
        Hovered = null;
        NavigationDidChange();
    }

    public void GoTo(int level)
    {
        if (level >= Navigator.Components.Count) return;
        Navigator.GoTo(level);
        Hovered = null;
        NavigationDidChange();
    }

    private void NavigationDidChange()
    {
        Segments = SunburstLayout.Layout(Tree, CurrentIndex);
        NavigationChanged?.Invoke();
        WatchCurrent();
    }

    private void OnTreeChanged()
    {
        Navigator.Normalize(Tree);
        Segments = SunburstLayout.Layout(Tree, CurrentIndex);
        var resolved = SpaceLensCleaner.ResolvePaths(_selection.Keys, Tree);
        var lost = _selection.Count != resolved.Count;
        _selection.Clear();
        foreach (var (p, i) in resolved) _selection[p] = i;
        if (Hovered is { NodeIndex: { } h } && h >= Tree.Count) Hovered = null;
        TreeChanged?.Invoke();
        if (lost) SelectionChanged?.Invoke();
        WatchCurrent();
    }

    // MARK: Theo dõi thay đổi

    /// <summary>Màn hình đang hiện: bật/tắt theo dõi thay đổi (như onAppear/onDisappear của bản Mac).</summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (active) WatchCurrent();
        else _watcher.Stop();
    }

    /// <summary>Chỉ theo dõi thư mục đang xem (kể cả bên trong).</summary>
    private void WatchCurrent()
    {
        if (Phase != LensPhase.Results || !_active)
        {
            _watcher.Stop();
            return;
        }
        _watcher.Start(CurrentPath);
    }

    private void CancelRefresh()
    {
        _refreshCts.Cancel();
        _refreshCts = new CancellationTokenSource();
        _pendingRefresh.Clear();
    }

    public void EnqueueRefresh(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (!_pendingRefresh.Contains(p, StringComparer.OrdinalIgnoreCase)) _pendingRefresh.Add(p);
        }
        if (!_draining) DrainRefreshQueue();
    }

    /// <summary>Đọc lại từng thư mục được báo (một cấp; thư mục mới quét đủ), áp vào cây rồi cập nhật tổng lên tới gốc.</summary>
    private async void DrainRefreshQueue()
    {
        _draining = true;
        var changed = false;
        var token = _refreshCts.Token;
        var tree = Tree;
        try
        {
            while (_pendingRefresh.Count > 0 && Phase == LensPhase.Results && !token.IsCancellationRequested)
            {
                var requested = _pendingRefresh[0];
                _pendingRefresh.RemoveAt(0);
                var index = tree.NearestDirectory(requested);
                if (index < 0) continue;
                while (index != DiskTree.Root && !DirectoryReader.IsRealDirectory(tree.Path(index))) index = tree.Parent(index);
                var dirPath = tree.Path(index);
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in tree.Children(index))
                {
                    if (tree.IsDirectory(c)) known.Add(tree.Name(c));
                }
                ShallowListing listing;
                try
                {
                    listing = await Task.Run(() => _scanner.RefreshListing(dirPath, known, token), token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warning(LogCategory.Scan, "Space Lens: không cập nhật được " + dirPath + ": " + ex.Message);
                    continue;
                }
                if (token.IsCancellationRequested || !ReferenceEquals(tree, Tree)) return;
                var target = tree.IndexForPath(dirPath);
                if (target < 0) continue;
                tree.Apply(listing, target);
                changed = true;
            }
        }
        finally
        {
            _draining = false;
            if (changed && ReferenceEquals(tree, Tree) && Phase == LensPhase.Results)
            {
                tree.CompactIfNeeded();
                OnTreeChanged();
            }
            if (_pendingRefresh.Count > 0 && Phase == LensPhase.Results && !token.IsCancellationRequested) DrainRefreshQueue();
        }
    }

    // MARK: Chọn

    public bool IsSelected(int index) => _selection.ContainsKey(Tree.Path(index));
    public bool IsSelected(string path) => _selection.ContainsKey(path);

    public void Toggle(int index)
    {
        if (index == DiskTree.Root) return;
        var p = Tree.Path(index);
        if (!_selection.Remove(p)) _selection[p] = index;
        SelectionChanged?.Invoke();
    }

    /// <summary>Chọn hoặc bỏ mọi mục con của thư mục đang xem.</summary>
    public void SetCurrentChildrenSelected(bool on)
    {
        var current = CurrentIndex;
        var dir = Tree.Path(current);
        var prefix = dir.EndsWith('\\') ? dir : dir + "\\";
        foreach (var c in Tree.Children(current))
        {
            var p = prefix + Tree.Name(c);
            if (on) _selection[p] = c;
            else _selection.Remove(p);
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>Số mục đang chọn là con trực tiếp của thư mục đang xem.</summary>
    public int SelectedAmongCurrent()
    {
        var current = CurrentIndex;
        var n = 0;
        foreach (var i in _selection.Values)
        {
            if (i < Tree.Count && Tree.Parent(i) == current) n++;
        }
        return n;
    }

    public long SelectedBytes => SpaceLensCleaner.SelectedBytes(_selection.Values, Tree);

    // MARK: Xoá

    /// <summary>Lập kế hoạch qua Clean Engine (PathPolicy chặn vùng cấm). <c>null</c> nếu không có gì để xử lý.</summary>
    public async Task<CleanPlan?> MakePlanAsync()
    {
        var nodes = _cleaner.NodesForIndices(_selection.Values, Tree);
        if (nodes.Count == 0) return null;
        return await Task.Run(() => _cleaner.Plan(nodes)).ConfigureAwait(true);
    }

    /// <summary>Thực hiện kế hoạch (tôn trọng chạy thử), báo kết quả, gỡ mục đã xoá khỏi cây rồi đọc lại thư mục cha.</summary>
    public async Task RunPlanAsync(CleanPlan plan, Func<string, Task<bool>> confirmPermanentDelete)
    {
        if (plan.Items.Count == 0) return;
        IsCleaning = true;
        try
        {
            var dryRun = _env.Settings.DryRun;
            if (!dryRun && plan.Items.Any(i => i.RequiresRoot)) await _env.Helper.EnsureRunningAsync().ConfigureAwait(true);
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            var report = await Task.Run(() => _env.CleanEngine.RunAsync(plan, dryRun,
                path => dispatcher.InvokeAsync(() => confirmPermanentDelete(path)).Task.Unwrap())).ConfigureAwait(true);
            var moved = report.Succeeded.Count();
            var text = report.DryRun
                ? Loc.F("Chạy thử: %lld mục sẽ được chuyển vào Thùng rác.", moved)
                : Loc.F("Đã chuyển %lld mục (%@) vào Thùng rác.", moved, report.EstimatedFreed.Formatted);
            var failed = report.Failed.Count();
            var skipped = report.Skipped.Count();
            if (failed > 0) text += Loc.F(" %lld mục lỗi.", failed);
            if (skipped > 0) text += Loc.F(" %lld mục bị bỏ qua.", skipped);
            Notice = text;
            _selection.Clear();
            SelectionChanged?.Invoke();
            if (!report.DryRun && Phase == LensPhase.Results)
            {
                var removed = report.Succeeded.Select(e => e.Item.Path).Where(p => p is not null).Select(p => p!).ToList();
                // Gỡ theo chỉ số giảm dần: anh em cuối khối được dời vào chỗ trống luôn có chỉ số lớn hơn mục đang gỡ,
                // nên không bao giờ là mục còn chờ gỡ (chỉ số đã tìm vẫn đúng).
                foreach (var i in SpaceLensCleaner.ResolvePaths(removed, Tree).Values.Where(i => i > DiskTree.Root).Distinct().OrderDescending())
                {
                    Tree.Remove(i);
                }
                Tree.CompactIfNeeded();
                OnTreeChanged();
                EnqueueRefresh(SpaceLensCleaner.AffectedDirectories(report));
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Clean, "Space Lens: xoá lỗi", ex);
            Notice = Loc.F("Không xoá được: %@", ex.Message);
        }
        finally
        {
            IsCleaning = false;
        }
    }
}
