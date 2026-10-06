using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;
using CleanBoost.Features.Uninstaller;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.Uninstaller;

/// <summary>Pha của tab "Ứng dụng" (Phase của UninstallerViewModel bản Mac, thêm bước xem lại file sót sau khi gỡ).</summary>
public enum AppsPhase { Idle, Loading, Browsing, Confirming, Working, Review, Done }

public enum AppSortKey { Name, Size, LastUsed }

/// <summary>Bộ lọc danh sách app (spec mục 4.8). Windows: "Microsoft Store" / "Chương trình desktop" thay App Store / Nguồn khác.</summary>
public sealed record AppFilter(string Kind, string? Vendor = null, string? VendorTitle = null)
{
    public static readonly AppFilter All = new("all");
    public static readonly AppFilter Unused = new("unused");
    public static readonly AppFilter Store = new("store");
    public static readonly AppFilter Desktop = new("desktop");
    public static readonly AppFilter Checked = new("checked");

    public string Title => Kind switch
    {
        "all" => Loc.T("Tất cả"),
        "unused" => Loc.T("Không dùng > 6 tháng"),
        "store" => "Microsoft Store",
        "desktop" => Loc.T("Chương trình desktop"),
        "checked" => Loc.T("Đã chọn"),
        _ => VendorTitle ?? Vendor ?? "",
    };
}

public static class AppSortKeys
{
    public static string Title(this AppSortKey k) => k switch
    {
        AppSortKey.Name => Loc.T("Tên"),
        AppSortKey.Size => Loc.T("Dung lượng"),
        _ => Loc.T("Lần dùng cuối"),
    };
}

/// <summary>Một dòng trong danh sách app (AppRow của bản Mac).</summary>
public sealed partial class AppRow : ObservableObject
{
    public InstalledApp App { get; }
    /// <summary>Lý do không gỡ được (null = gỡ được).</summary>
    public string? BlockedReason { get; }
    public string? Caution { get; }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private ImageSource? _icon;

    public AppRow(InstalledApp app, string? blockedReason, string? caution)
    {
        App = app;
        BlockedReason = blockedReason;
        Caution = caution;
    }

    public string Id => App.Id;
    public string Name => App.Name;
    public bool CanUninstall => BlockedReason is null;
    public bool IsBlocked => BlockedReason is not null;
    public bool IsStore => App.IsStore;
    public bool IsSystemPublisher => App.IsSystemPublisher && !App.IsStore;
    public string SizeText => App.Size > 0 ? ByteCount.Format(App.Size) : "—";
    /// <summary>Chừa chỗ cho nhãn "Store"/"Microsoft" sau tên để nhãn không bị cắt.</summary>
    public double NameMaxWidth => 205 - (IsStore ? 50 : 0) - (IsSystemPublisher ? 72 : 0);
    public string LockTooltip => Loc.F("Không gỡ được: %@", BlockedReason ?? "");

    /// <summary>"v1.2.3 · Dùng 3 tuần trước" / "Chưa rõ lần dùng cuối".</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(App.Version)) parts.Add("v" + App.Version);
            parts.Add(App.LastUsed is { } d ? Loc.T("Dùng ") + RelativeTime.Format(d) : Loc.T("Chưa rõ lần dùng cuối"));
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Yêu cầu xác nhận trước khi gỡ / đặt lại.</summary>
public sealed record UninstallConfirmation(IReadOnlyList<InstalledApp> Apps, IReadOnlyList<Node> Leftovers, CleanPlan LeftoverPlan, bool ResetOnly, bool AnyRunning);

/// <summary>Bước xem lại file sót sau khi trình gỡ chạy xong.</summary>
public sealed record LeftoverReview(IReadOnlyList<InstalledApp> Removed, NodeTree Tree, SelectionState Selection, CleanReport UninstallReport);

/// <summary>Màn kết thúc.</summary>
public sealed record UninstallDone(string Message, IReadOnlyList<(string Title, string Message)> Failures, int InUse, bool Success);

/// <summary>
/// View model màn Gỡ cài đặt (UninstallerViewModel của bản Mac, spec mục 4.8): danh sách app, tìm file sót, gỡ qua CleanEngine.
/// Khác bản Mac: gỡ là hai bước — chạy trình gỡ của hãng (remover <c>app</c>), rồi tìm lại file sót còn lại và cho người dùng
/// xem trước khi dọn, vì trình gỡ của Windows tự xoá một phần và có thể bị huỷ giữa chừng.
/// </summary>
public sealed partial class UninstallerViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _workCts;
    private string? _pendingPath;
    private readonly Dictionary<string, LeftoverReport> _reports = [];
    private readonly HashSet<string> _loading = [];
    private readonly SemaphoreSlim _finderGate = new(2);

    public UninstallerFeature Feature { get; }
    public ScanCleanViewModel Orphans { get; }
    public ScanServices Services => _env.Services;

    [ObservableProperty] private AppsPhase _phase = AppsPhase.Idle;
    [ObservableProperty] private double _loadProgress;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AppSortKey _sort = AppSortKey.Size;
    [ObservableProperty] private AppFilter _filter = AppFilter.All;
    [ObservableProperty] private AppRow? _focused;
    [ObservableProperty] private NodeTree _tree = NodeTree.Empty;
    [ObservableProperty] private SelectionState _selection = new();
    [ObservableProperty] private string _workTitle = "";
    [ObservableProperty] private string _workItem = "";
    [ObservableProperty] private double _workProgress;
    [ObservableProperty] private bool _canStopWaiting;
    [ObservableProperty] private UninstallConfirmation? _confirmation;
    [ObservableProperty] private LeftoverReview? _review;
    [ObservableProperty] private UninstallDone? _done;

    /// <summary>Mọi app (theo thứ tự quét) và các dòng đang hiện sau khi lọc/sắp xếp.</summary>
    public List<AppRow> AllRows { get; private set; } = [];
    public ObservableCollection<AppRow> VisibleRows { get; } = [];

    /// <summary>Danh sách app hoặc file sót thay đổi (giao diện vẽ lại cột phải, chân trang).</summary>
    public event Action? DetailChanged;
    /// <summary>Không tìm thấy app cho liên kết sâu.</summary>
    public event Action<string>? AppNotFound;

    /// <summary>Hỏi buộc thoát khi app không tự thoát sau 10 giây (giao diện gắn hộp thoại).</summary>
    public Func<InstalledApp, Task<bool>> AskForceQuit { get; set; } = _ => Task.FromResult(false);
    /// <summary>Hỏi xoá vĩnh viễn khi ổ không có Thùng rác.</summary>
    public Func<string, Task<bool>> ConfirmPermanentDelete { get; set; } = _ => Task.FromResult(false);

    public UninstallerViewModel(AppEnvironment env)
    {
        _env = env;
        Feature = env.Feature<UninstallerFeature>();
        Orphans = new ScanCleanViewModel(env.Services, OrphanedLeftoversTask.TaskID, Feature.ScanTasks);
        Orphans.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScanCleanViewModel.Phase)) OnPropertyChanged(nameof(IsBusy)); };
    }

    public bool DryRun => _env.Settings.DryRun;

    /// <summary>Giữ trạng thái "bận" khi chụp ảnh kiểm thử đang dựng một màn (chỉ bản Debug dùng).</summary>
    public bool HoldBusy { get; set; }

    public bool IsBusy => HoldBusy || Phase is AppsPhase.Loading or AppsPhase.Working
                          || (Phase == AppsPhase.Browsing && Focused is { } f && _loading.Contains(f.Id))
                          || Orphans.IsBusy;

    partial void OnPhaseChanged(AppsPhase value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnSearchChanged(string value) => RefreshVisible();
    partial void OnSortChanged(AppSortKey value) => RefreshVisible();
    partial void OnFilterChanged(AppFilter value) => RefreshVisible();

    partial void OnSelectionChanged(SelectionState? oldValue, SelectionState newValue)
    {
        if (oldValue is not null) oldValue.Changed -= RaiseDetail;
        newValue.Changed += RaiseDetail;
    }

    private void RaiseDetail()
    {
        DetailChanged?.Invoke();
        OnPropertyChanged(nameof(IsBusy));
    }

    // MARK: Danh sách app

    public async Task LoadAppsAsync()
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        LoadProgress = 0;
        if (AllRows.Count == 0) Phase = AppsPhase.Loading;
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var apps = await Task.Run(() => _env.AppScanner.ScanAsync(p => dispatcher.InvokeAsync(() => LoadProgress = p), cts.Token), cts.Token).ConfigureAwait(true);
            var rows = await Task.Run(() => apps.Select(a => new AppRow(a, UninstallPolicy.ReasonCannotUninstall(a), UninstallPolicy.Caution(a))).ToList(), cts.Token)
                .ConfigureAwait(true);
            if (_loadCts != cts) return;
            var checkedIDs = AllRows.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet();
            foreach (var r in rows) r.IsChecked = checkedIDs.Contains(r.Id) && r.CanUninstall;
            AllRows = rows;
            // Bỏ báo cáo file sót của app không còn trong danh sách.
            foreach (var id in _reports.Keys.Where(id => rows.All(r => r.Id != id)).ToList()) _reports.Remove(id);
            RebuildTree();
            Phase = AppsPhase.Browsing;
            RefreshVisible();
            LoadIconsInBackground(rows);
            if (_pendingPath is { } path)
            {
                _pendingPath = null;
                OpenPath(path);
            }
            else if (Focused is null || rows.All(r => r.Id != Focused.Id))
            {
                Focus(VisibleRows.FirstOrDefault());
            }
            else
            {
                Focus(rows.First(r => r.Id == Focused.Id));
            }
        }
        catch (OperationCanceledException)
        {
            if (_loadCts == cts && AllRows.Count == 0) Phase = AppsPhase.Idle;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không tải được danh sách app", ex);
            if (_loadCts == cts)
            {
                Done = new UninstallDone(Loc.T("Đã có lỗi"), [(Loc.T("Tìm ứng dụng đã cài"), ex.Message)], 0, false);
                Phase = AppsPhase.Done;
            }
        }
    }

    public void CancelLoading()
    {
        _loadCts?.Cancel();
        if (AllRows.Count == 0) Phase = AppsPhase.Idle;
    }

    private void LoadIconsInBackground(IReadOnlyList<AppRow> rows)
    {
        var dispatcher = Application.Current.Dispatcher;
        // Dòng đang hiện trước, phần còn lại sau.
        var ordered = VisibleRows.Concat(rows.Except(VisibleRows)).ToList();
        _ = Task.Run(() =>
        {
            foreach (var row in ordered)
            {
                var icon = AppIcons.For(row.App);
                if (icon is not null) dispatcher.InvokeAsync(() => row.Icon = icon);
            }
        });
    }

    public void RefreshVisible()
    {
        var q = Search.Trim();
        var cutoff = DateTime.Now.AddDays(-180);
        IEnumerable<AppRow> rows = AllRows.Where(r =>
        {
            var a = r.App;
            if (q.Length > 0 && !a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                             && !(a.Publisher?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)) return false;
            return Filter.Kind switch
            {
                "all" => true,
                "unused" => a.LastUsed is { } last ? last < cutoff : a.InstallDate is { } inst && inst < cutoff,
                "store" => a.IsStore,
                "desktop" => !a.IsStore,
                "checked" => r.IsChecked,
                _ => LeftoverMatcher.NormalizePublisher(a.Publisher) == Filter.Vendor,
            };
        });
        var comparer = StringComparer.Create(Loc.Culture, CompareOptions.IgnoreCase);
        rows = Sort switch
        {
            AppSortKey.Name => rows.OrderBy(r => r.Name, comparer),
            AppSortKey.Size => rows.OrderByDescending(r => r.App.Size),
            _ => rows.OrderBy(r => r.App.LastUsed ?? DateTime.MinValue),
        };
        VisibleRows.Clear();
        foreach (var r in rows) VisibleRows.Add(r);
        DetailChanged?.Invoke();
    }

    /// <summary>Nhà phát triển có từ 2 app trở lên (bộ lọc theo hãng), tối đa 12 hãng nhiều app nhất.</summary>
    public IReadOnlyList<AppFilter> Vendors =>
        AllRows.Where(r => !string.IsNullOrWhiteSpace(r.App.Publisher))
            .GroupBy(r => LeftoverMatcher.NormalizePublisher(r.App.Publisher))
            .Where(g => g.Key.Length > 0 && g.Count() >= 2)
            .OrderByDescending(g => g.Count()).Take(12)
            .Select(g => new AppFilter("vendor", g.Key, g.First().App.Publisher!.Trim()))
            .OrderBy(f => f.VendorTitle, StringComparer.Create(Loc.Culture, CompareOptions.IgnoreCase))
            .ToList();

    /// <summary>Liên kết sâu: chọn app chứa exe/lối tắt (spec mục 4.8 <c>open(appPath:)</c>).</summary>
    public void OpenPath(string path)
    {
        switch (Phase)
        {
            case AppsPhase.Idle:
                _pendingPath = path;
                _ = LoadAppsAsync();
                return;
            case AppsPhase.Loading:
                _pendingPath = path;
                return;
            case AppsPhase.Browsing:
                break;
            default:
                Log.Warning(LogCategory.UI, "Bỏ qua liên kết gỡ cài đặt vì đang bận");
                return;
        }
        var app = AppLocator.Find(path, AllRows.Select(r => r.App).ToList());
        if (app is null)
        {
            Log.Warning(LogCategory.UI, "Không tìm thấy app cho liên kết gỡ cài đặt");
            AppNotFound?.Invoke(path);
            return;
        }
        Search = "";
        Filter = AppFilter.All;
        var row = AllRows.First(r => r.Id == app.Id);
        if (row.CanUninstall) row.IsChecked = true;
        Focus(row);
    }

    public IEnumerable<AppRow> CheckedRows => AllRows.Where(r => r.IsChecked);

    public void Focus(AppRow? row)
    {
        Focused = row;
        if (row is not null) _ = LoadLeftoversAsync(row.Id);
        RaiseDetail();
    }

    public void ToggleChecked(AppRow row)
    {
        if (!row.CanUninstall) return;
        row.IsChecked = !row.IsChecked;
        if (row.IsChecked) _ = LoadLeftoversAsync(row.Id);
        if (Filter == AppFilter.Checked) RefreshVisible();
        RaiseDetail();
    }

    public IReadOnlyList<AppRow> CheckableVisible => VisibleRows.Where(r => r.CanUninstall).ToList();

    public void SetCheckedVisible(bool on)
    {
        foreach (var row in CheckableVisible)
        {
            if (row.IsChecked == on) continue;
            row.IsChecked = on;
            if (on) _ = LoadLeftoversAsync(row.Id);
        }
        RaiseDetail();
    }

    public void ClearChecked()
    {
        foreach (var r in AllRows) r.IsChecked = false;
        if (Filter == AppFilter.Checked) RefreshVisible();
        RaiseDetail();
    }

    // MARK: File sót

    public bool IsLoadingLeftovers(AppRow row) => _loading.Contains(row.Id);
    public bool HasReport(AppRow row) => _reports.ContainsKey(row.Id);

    public async Task LoadLeftoversAsync(string appID, bool force = false)
    {
        var row = AllRows.FirstOrDefault(r => r.Id == appID);
        if (row is null || !row.CanUninstall) return;
        if (!force && (_reports.ContainsKey(appID) || _loading.Contains(appID))) return;
        _loading.Add(appID);
        RaiseDetail();
        var installed = AllRows.Select(r => r.App).ToList();
        var rules = Services.Rules;
        // App đang xem được ưu tiên: các lượt tìm khác (chọn nhiều app) chạy tối đa 2 cùng lúc.
        var throttled = !ReferenceEquals(row, Focused);
        try
        {
            if (throttled) await _finderGate.WaitAsync().ConfigureAwait(true);
            var report = await Task.Run(() =>
            {
                var env = Services.MakeEnvironment();
                return new LeftoverFinder(Services.FileSystem, rules, env).Find(row.App, installed, LeftoverScanMode.Preview);
            }).ConfigureAwait(true);
            if (AllRows.Any(r => r.Id == appID)) _reports[appID] = report;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Scan, "Tìm file sót lỗi", ex);
            _reports[appID] = new LeftoverReport(row.App, []);
        }
        finally
        {
            if (throttled) _finderGate.Release();
            _loading.Remove(appID);
        }
        RebuildTree();
    }

    /// <summary>Dựng lại cây gộp mọi app đã tìm file sót; giữ lựa chọn cũ, mục mới thì chọn sẵn Chắc chắn/Cao.</summary>
    private void RebuildTree()
    {
        var ordered = AllRows.Where(r => _reports.ContainsKey(r.Id)).Select(r => _reports[r.Id]).ToList();
        var previous = Tree.AllLeaves.Select(l => l.Id).ToHashSet();
        var previouslySelected = Selection.SelectedIDs.ToHashSet();
        var tree = UninstallPlanner.Tree(ordered, includeApp: false);
        var selection = new SelectionState();
        var on = tree.AllLeaves.Where(l => previous.Contains(l.Id) ? previouslySelected.Contains(l.Id) : l.Safety == SafetyLevel.Safe).Select(l => l.Id).ToList();
        selection.SetMany(on, true, tree);
        Tree = tree;
        Selection = selection;
        RaiseDetail();
    }

    public Node? AppRoot(AppRow row) => Tree.Roots.FirstOrDefault(r => r.AppID == row.Id);

    public List<Node> SelectedLeaves(AppRow row) =>
        AppRoot(row) is { } root ? Tree.Leaves(root.Id).Where(l => Selection.IsSelected(l.Id)).ToList() : [];

    public ByteCount SelectedBytes(AppRow row) => (row.IsChecked ? new ByteCount(row.App.Size) : ByteCount.Zero) + SelectedLeaves(row).Sum(l => l.Size);

    /// <summary>App đã chọn và đã có kết quả tìm file sót.</summary>
    public List<AppRow> PendingRows => CheckedRows.Where(r => _reports.ContainsKey(r.Id)).ToList();
    public ByteCount TotalSelectedBytes => PendingRows.Sum(SelectedBytes);
    public bool AnyCheckedLoading => CheckedRows.Any(r => _loading.Contains(r.Id));

    // MARK: Gỡ

    public void RequestUninstall()
    {
        var targets = PendingRows;
        if (targets.Count == 0 || AnyCheckedLoading) return;
        var leftovers = targets.SelectMany(SelectedLeaves).ToList();
        var plan = Services.CleanEngine.MakePlan(leftovers);
        var running = targets.Any(r => AppProcesses.IsRunning(r.App, Services.Rules.Knowledge));
        Confirmation = new UninstallConfirmation(targets.Select(r => r.App).ToList(), leftovers, plan, ResetOnly: false, running);
        Phase = AppsPhase.Confirming;
    }

    /// <summary>"Đặt lại app": chỉ xoá file sót đã chọn, giữ lại app.</summary>
    public void RequestReset(AppRow row)
    {
        var leftovers = SelectedLeaves(row);
        if (leftovers.Count == 0) return;
        var plan = Services.CleanEngine.MakePlan(leftovers);
        Confirmation = new UninstallConfirmation([row.App], leftovers, plan, ResetOnly: true, AppProcesses.IsRunning(row.App, Services.Rules.Knowledge));
        Phase = AppsPhase.Confirming;
    }

    public void CancelConfirmation()
    {
        Confirmation = null;
        Phase = AppsPhase.Browsing;
    }

    public void StopWaiting() => _workCts?.Cancel();

    public async Task ConfirmAsync()
    {
        if (Confirmation is not { } c) return;
        _workCts = new CancellationTokenSource();
        var ct = _workCts.Token;
        try
        {
            if (c.ResetOnly) await RunResetAsync(c, ct).ConfigureAwait(true);
            else await RunUninstallAsync(c, ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Clean, "Gỡ cài đặt lỗi", ex);
            Done = new UninstallDone(Loc.T("Đã có lỗi"), [(Loc.T("Gỡ cài đặt"), ex.Message)], 0, false);
            Phase = AppsPhase.Done;
        }
        finally
        {
            CanStopWaiting = false;
        }
    }

    /// <summary>Bước 1 (spec mục 4.8 <c>run</c>): yêu cầu app đang chạy thoát, chờ 10 giây, chưa thoát thì hỏi buộc thoát.</summary>
    private async Task<List<InstalledApp>> QuitRunningAsync(IReadOnlyList<InstalledApp> apps, CancellationToken ct)
    {
        var know = Services.Rules.Knowledge;
        var skipped = new List<InstalledApp>();
        // Chạy thử: không đóng hay buộc thoát app thật.
        if (DryRun) return skipped;
        foreach (var app in apps)
        {
            if (!AppProcesses.IsRunning(app, know)) continue;
            SetWork(Loc.F("Đang yêu cầu %@ thoát…", app.Name), app.Name, 0);
            if (await AppProcesses.CloseAsync(app, know, TimeSpan.FromSeconds(10), ct).ConfigureAwait(true)) continue;
            var force = await AskForceQuit(app).ConfigureAwait(true);
            if (force && await AppProcesses.KillAsync(app, know, ct).ConfigureAwait(true)) continue;
            skipped.Add(app);
        }
        return skipped;
    }

    private void SetWork(string title, string item, double progress)
    {
        WorkTitle = title;
        WorkItem = item;
        WorkProgress = progress;
        Phase = AppsPhase.Working;
    }

    private async Task<CleanReport> ExecuteAsync(CleanPlan plan, string title, CancellationToken ct, Func<CleanProgress, string>? titleFor = null)
    {
        var dispatcher = Application.Current.Dispatcher;
        SetWork(title, "", 0);
        if (plan.Items.Any(i => i.RequiresRoot) && !DryRun) await Services.Helper.EnsureRunningAsync(ct).ConfigureAwait(true);
        return await Task.Run(() => Services.CleanEngine.RunAsync(plan, DryRun,
            path => dispatcher.InvokeAsync(() => ConfirmPermanentDelete(path)).Task.Unwrap(),
            p => dispatcher.InvokeAsync(() =>
            {
                WorkProgress = p.Fraction;
                WorkItem = p.CurrentItem.AbbreviatingHome();
                if (titleFor is not null) WorkTitle = titleFor(p);
            }), ct), CancellationToken.None).ConfigureAwait(true);
    }

    private async Task RunResetAsync(UninstallConfirmation c, CancellationToken ct)
    {
        var skipped = await QuitRunningAsync(c.Apps, ct).ConfigureAwait(true);
        if (skipped.Count > 0)
        {
            Phase = AppsPhase.Browsing;
            return;
        }
        var app = c.Apps[0];
        var report = await ExecuteAsync(c.LeftoverPlan, Loc.F("Đang đặt lại %@…", app.Name), ct).ConfigureAwait(true);
        var freed = report.DryRun ? c.LeftoverPlan.TotalSize : Freed(report);
        Done = new UninstallDone(UninstallPlanner.Summary([], freed, report.DryRun, resetOnly: true), Failures(report), report.InUse.Count(), !report.Failed.Any());
        Phase = AppsPhase.Done;
        _reports.Remove(app.Id);
        _ = LoadLeftoversAsync(app.Id, force: true);
    }

    private async Task RunUninstallAsync(UninstallConfirmation c, CancellationToken ct)
    {
        var skipped = await QuitRunningAsync(c.Apps, ct).ConfigureAwait(true);
        var targets = c.Apps.Where(a => skipped.All(s => s.Id != a.Id)).ToList();
        if (targets.Count == 0)
        {
            Confirmation = null;
            Phase = AppsPhase.Browsing;
            return;
        }

        // Bước 2: chạy trình gỡ của từng app (remover "app"), có thể chờ tới 10 phút mỗi app.
        var appNodes = targets.Select(UninstallPlanner.AppItemNode).ToList();
        var plan = Services.CleanEngine.MakePlan(appNodes);
        CanStopWaiting = true;
        var report = await ExecuteAsync(plan, Loc.T("Đang gỡ cài đặt…"), ct,
            p => string.IsNullOrEmpty(p.CurrentItem) ? Loc.T("Đang gỡ cài đặt…") : Loc.F("Đang chờ trình gỡ của %@…", p.CurrentItem)).ConfigureAwait(true);
        CanStopWaiting = false;

        var leftoverBytes = c.Leftovers.Sum(l => l.Size);
        if (report.DryRun)
        {
            var wouldFree = targets.Sum(a => new ByteCount(a.Size)) + leftoverBytes;
            Done = new UninstallDone(UninstallPlanner.Summary(targets, wouldFree, dryRun: true, resetOnly: false), [], 0, true);
            Phase = AppsPhase.Done;
            return;
        }

        var removed = UninstallPlanner.UninstalledApps(report);
        var failures = Failures(report);
        RemoveApps(removed);
        if (removed.Count == 0)
        {
            Done = new UninstallDone(UninstallPlanner.Summary([], ByteCount.Zero, false, false), failures, 0, false);
            Phase = AppsPhase.Done;
            return;
        }

        // Bước 3: trình gỡ thường để lại dữ liệu — tìm lại (kể cả thư mục cài, lối tắt) và cho xem trước khi dọn.
        SetWork(Loc.T("Đang tìm file sót…"), string.Join(", ", removed.Select(a => a.Name)), 1);
        var remaining = AllRows.Select(r => r.App).ToList();
        var rules = Services.Rules;
        var reports = await Task.Run(() =>
        {
            var env = Services.MakeEnvironment();
            var finder = new LeftoverFinder(Services.FileSystem, rules, env);
            return removed.Select(a => finder.Find(a, remaining, LeftoverScanMode.AfterUninstall)).ToList();
        }).ConfigureAwait(true);
        var tree = UninstallPlanner.Tree(reports, includeApp: false);
        if (tree.IsEmpty)
        {
            Done = new UninstallDone(UninstallPlanner.Summary(removed, report.EstimatedFreed, false, false), failures, 0, failures.Count == 0);
            Phase = AppsPhase.Done;
            return;
        }
        // Giữ lựa chọn người dùng đã làm trước khi gỡ (theo đường dẫn / khoá registry); mục mới chọn sẵn nếu an toàn.
        static string KeyOf(Node n) => n.Virtual is RegistryKeyItem r ? "REG:" + r.DisplayPath.ToUpperInvariant() : NodeTree.PathKey(n.Url ?? n.Title);
        var before = c.Leftovers.Select(KeyOf).ToHashSet();
        var knownUnselected = c.Apps.SelectMany(a => _previewLeaves.GetValueOrDefault(a.Id) ?? []).Select(KeyOf).Where(k => !before.Contains(k)).ToHashSet();
        var selection = new SelectionState();
        var on = tree.AllLeaves.Where(l =>
        {
            var key = KeyOf(l);
            if (before.Contains(key)) return true;
            if (knownUnselected.Contains(key)) return false;
            return l.Safety == SafetyLevel.Safe;
        }).Select(l => l.Id).ToList();
        selection.SetMany(on, true, tree);
        Review = new LeftoverReview(removed, tree, selection, report);
        Phase = AppsPhase.Review;
    }

    /// <summary>Lá xem trước của app đã gỡ (để giữ trạng thái "không chọn" của người dùng sang bước xem lại).</summary>
    private readonly Dictionary<string, List<Node>> _previewLeaves = [];

    /// <summary>Bỏ app đã gỡ khỏi danh sách, lựa chọn và báo cáo; chọn app đầu tiên nếu app đang xem đã gỡ.</summary>
    private void RemoveApps(IReadOnlyList<InstalledApp> removed)
    {
        if (removed.Count == 0) return;
        var ids = removed.Select(a => a.Id).ToHashSet();
        foreach (var id in ids)
        {
            if (AllRows.FirstOrDefault(r => r.Id == id) is { } row && AppRoot(row) is { } root) _previewLeaves[id] = Tree.Leaves(root.Id).ToList();
            _reports.Remove(id);
        }
        AllRows = AllRows.Where(r => !ids.Contains(r.Id)).ToList();
        RebuildTree();
        RefreshVisible();
        if (Focused is { } f && ids.Contains(f.Id)) Focused = VisibleRows.FirstOrDefault();
    }

    /// <summary>Bước 4: dọn file sót đã chọn sau khi gỡ.</summary>
    public async Task CleanReviewAsync()
    {
        if (Review is not { } r) return;
        _workCts = new CancellationTokenSource();
        var plan = Services.CleanEngine.MakePlan(r.Tree, r.Selection);
        var appsFreed = r.UninstallReport.EstimatedFreed;
        var failures = Failures(r.UninstallReport);
        if (plan.IsEmpty)
        {
            FinishReview(appsFreed, failures, 0);
            return;
        }
        try
        {
            var report = await ExecuteAsync(plan, Loc.T("Đang dọn file sót…"), _workCts.Token).ConfigureAwait(true);
            failures.AddRange(Failures(report));
            failures.AddRange(plan.BlockedItems.Select(b => (b.Title, b.Violation.LocalizedDescription)));
            FinishReview(appsFreed + Freed(report), failures, report.InUse.Count());
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Clean, "Dọn file sót lỗi", ex);
            failures.Add((Loc.T("Dọn file sót"), ex.Message));
            FinishReview(appsFreed, failures, 0);
        }
    }

    public void SkipReview()
    {
        if (Review is not { } r) return;
        FinishReview(r.UninstallReport.EstimatedFreed, Failures(r.UninstallReport), 0);
    }

    private void FinishReview(ByteCount freed, List<(string, string)> failures, int inUse)
    {
        var removed = Review?.Removed ?? [];
        Done = new UninstallDone(UninstallPlanner.Summary(removed, freed, false, false), failures, inUse, failures.Count == 0);
        Review = null;
        Phase = AppsPhase.Done;
    }

    public void DismissDone()
    {
        Done = null;
        Confirmation = null;
        Phase = AllRows.Count == 0 ? AppsPhase.Idle : AppsPhase.Browsing;
        if (Focused is { } f) _ = LoadLeftoversAsync(f.Id);
        RaiseDetail();
    }

#if DEBUG
    /// <summary>Chỉ dùng khi chụp ảnh kiểm thử: dựng màn xem lại file sót từ app đang chọn mà không gỡ gì.</summary>
    public async Task DebugShowReviewAsync(AppRow row)
    {
        var installed = AllRows.Select(r => r.App).ToList();
        var rules = Services.Rules;
        var report = await Task.Run(() => new LeftoverFinder(Services.FileSystem, rules, Services.MakeEnvironment())
            .Find(row.App, installed, LeftoverScanMode.AfterUninstall)).ConfigureAwait(true);
        var tree = UninstallPlanner.Tree([report], includeApp: false);
        Review = new LeftoverReview([row.App], tree, SelectionState.Defaults(tree), new CleanReport { OperationID = "", Entries = [], DryRun = true });
        Phase = AppsPhase.Review;
    }
#endif

    private static ByteCount Freed(CleanReport report) =>
        report.MeasuredFreed is { } m && m > report.EstimatedFreed ? m : report.EstimatedFreed;

    private static List<(string Title, string Message)> Failures(CleanReport report) =>
        report.Failed.Select(e => (e.Item.Title, e.Result.LocalizedDescription))
            .Concat(report.Entries.Where(e => e.Result.Outcome == RemoveOutcomeKind.Skipped && e.Result.Reason == SkipReason.UserDeclined && e.Item.Virtual is InstalledAppItem)
                .Select(e => (e.Item.Title, Loc.T("Trình gỡ đã bị huỷ hoặc bạn đã dừng chờ"))))
            .ToList();
}
