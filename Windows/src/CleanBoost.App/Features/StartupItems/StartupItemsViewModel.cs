using System.Windows;
using CleanBoost.App.Services;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Ipc;
using CleanBoost.Features.StartupItems;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.StartupItems;

/// <summary>Thông báo trên đầu màn (NoticeBanner của bản Mac).</summary>
public sealed record StartupNotice(string Text, bool IsError);

/// <summary>
/// View model màn Mục khởi động (LoginItemsViewModel của bản Mac): quét, lọc, bật/tắt, xoá. Danh sách app đã cài chỉ dùng để
/// tra app sở hữu nên được nạp song song và áp vào sau, danh sách mục khởi động hiện ngay khi đọc xong.
/// </summary>
public sealed partial class StartupItemsViewModel(AppEnvironment env) : ObservableObject
{
    private readonly StartupItemsFeature _feature = env.Feature<StartupItemsFeature>();
    private IReadOnlyList<InstalledApp>? _apps;
    private IReadOnlyList<string> _running = [];
    private int _generation;
    private readonly HashSet<string> _busy = [];

    [ObservableProperty] private IReadOnlyList<StartupItem> _items = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private StartupNotice? _notice;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showOnlyProblems;

    /// <summary>Đổi tập mục đang bận (đang bật/tắt/xoá).</summary>
    public event Action? BusyChanged;

    /// <summary>Hỏi xoá vĩnh viễn khi ổ không có Thùng rác; giao diện gắn hộp thoại vào đây.</summary>
    public Func<string, Task<bool>> ConfirmPermanentDelete { get; set; } = _ => Task.FromResult(false);

    public bool IsBusy => IsLoading || _busy.Count > 0;
    public bool DryRun => env.Settings.DryRun;
    public bool IsElevated { get; } = HelperClient.IsProcessElevated;
    public bool IsItemBusy(StartupItem item) => _busy.Contains(item.Id);

    public int EnabledCount => Items.Count(i => i.IsEnabled);
    public int BrokenCount => Items.Count(i => i.IsBroken);

    /// <summary>Mục hỏng xoá được (bỏ thành phần Windows và tác vụ hỏng đã tắt sẵn).</summary>
    public IReadOnlyList<StartupItem> RemovableBroken =>
        Items.Where(i => i.IsBroken && !i.IsSystemComponent && !(i.Source == StartupSource.ScheduledTask && !i.IsEnabled)).ToList();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    /// <summary>Mục trong một nhóm sau khi lọc; hỏng lên đầu rồi theo tên (như bản Mac).</summary>
    public IReadOnlyList<StartupItem> ItemsIn(StartupGroup group)
    {
        var comparer = StringComparer.Create(Loc.Culture, ignoreCase: true);
        return Items.Where(i => i.Group == group && Matches(i))
            .OrderBy(i => i.IsBroken ? 0 : 1)
            .ThenBy(i => i.DisplayName, comparer)
            .ToList();
    }

    private bool Matches(StartupItem item)
    {
        if (ShowOnlyProblems && !item.IsBroken && item.IsEnabled) return false;
        var q = SearchText.Trim();
        if (q.Length == 0) return true;
        return Contains(item.DisplayName, q) || Contains(item.Name, q) || Contains(item.OwnerName, q) || Contains(item.Publisher, q)
               || Contains(item.Command, q) || Contains(item.TaskPath, q);
    }

    private static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;

    // MARK: Quét

    public void LoadIfNeeded()
    {
        if (!HasLoaded && !IsLoading) _ = ReloadAsync();
    }

    /// <summary>Đọc lại mọi mục khởi động; lần đầu nạp thêm danh sách app để tra app sở hữu.</summary>
    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        IsLoading = true;
        try
        {
            var appsTask = _apps is null ? LoadAppsAsync() : Task.FromResult<IReadOnlyList<InstalledApp>?>(_apps);
            _running = await Task.Run(() => RunningApps.Capture().Paths).ConfigureAwait(true);
            var running = _running;
            var cached = _apps;
            var items = await Task.Run(() => _feature.Scanner.Scan(cached, running)).ConfigureAwait(true);
            if (generation != _generation) return;
            Items = items;
            HasLoaded = true;
            if (cached is null && await appsTask.ConfigureAwait(true) is { } apps)
            {
                _apps = apps;
                var enriched = await Task.Run(() => StartupItemScanner.Enrich(items, apps, running)).ConfigureAwait(true);
                if (generation == _generation) Items = enriched;
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Scan, "Đọc mục khởi động lỗi", ex);
            if (generation == _generation) Notice = new StartupNotice(Loc.F("Không đọc được mục khởi động: %@", ex.Message), true);
        }
        finally
        {
            if (generation == _generation) IsLoading = false;
        }
    }

    private async Task<IReadOnlyList<InstalledApp>?> LoadAppsAsync()
    {
        try
        {
            return await env.AppScanner.ScanAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, "Không đọc được danh sách app cho mục khởi động: " + ex.Message);
            return null;
        }
    }

    /// <summary>Đọc lại trạng thái sau khi bật/tắt/xoá (dùng danh sách app đã có, nhanh).</summary>
    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        try
        {
            var apps = _apps;
            var running = _running;
            var items = await Task.Run(() => _feature.Scanner.Scan(apps, running)).ConfigureAwait(true);
            if (generation == _generation) Items = items;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Scan, "Đọc lại mục khởi động lỗi", ex);
        }
    }

    private void SetBusy(IEnumerable<StartupItem> items, bool busy)
    {
        foreach (var i in items)
        {
            if (busy) _busy.Add(i.Id);
            else _busy.Remove(i.Id);
        }
        OnPropertyChanged(nameof(IsBusy));
        BusyChanged?.Invoke();
    }

    // MARK: Bật/tắt

    public async Task SetEnabledAsync(StartupItem item, bool enabled)
    {
        if (IsItemBusy(item)) return;
        if (DryRun)
        {
            Notice = new StartupNotice(Loc.F(enabled ? "Chạy thử: sẽ bật %@" : "Chạy thử: sẽ tắt %@", item.DisplayName), false);
            OnPropertyChanged(nameof(Items));
            return;
        }
        SetBusy([item], true);
        CommandResult result;
        try
        {
            result = await StartupItemsController.SetEnabledAsync(item, enabled, env.Helper).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result = CommandResult.Fail(ex.Message);
        }
        if (result.Success)
        {
            Log.Info(LogCategory.UI, $"{(enabled ? "Đã bật" : "Đã tắt")} mục khởi động ({item.Source}, {item.Scope})");
        }
        else
        {
            Notice = new StartupNotice(Loc.F(enabled ? "Không bật được %@: %@" : "Không tắt được %@: %@", item.DisplayName, result.Message), true);
        }
        await RefreshAsync().ConfigureAwait(true);
        SetBusy([item], false);
    }

    // MARK: Xoá

    /// <summary>Node xoá cho một mục: mục hỏng giữ mức an toàn/lý do như Smart Scan, mục khác là "cần xem lại".</summary>
    private static Engine.Nodes.Node NodeFor(StartupItem item) => item.IsBroken
        ? StartupItemsFeature.NodeFor(item, StartupItemsFeature.BrokenSafety(item), StartupItemsFeature.BrokenReason(item), Loc.T("Hỏng"))
        : StartupItemsFeature.NodeFor(item, SafetyLevel.Review, StartupItemsFeature.Text("Gỡ khỏi danh sách khởi động cùng Windows"));

    /// <summary>Xoá (hoặc tắt hẳn, với tác vụ lập lịch) qua CleanEngine để có kiểm tra an toàn, Thùng rác và lịch sử.</summary>
    public async Task RemoveAsync(IReadOnlyList<StartupItem> items)
    {
        if (items.Count == 0) return;
        SetBusy(items, true);
        try
        {
            var plan = env.CleanEngine.MakePlan(items.Select(NodeFor));
            if (plan.Items.Count == 0)
            {
                var name = items.Count == 1 ? items[0].DisplayName : Loc.F("%lld mục", items.Count);
                Notice = new StartupNotice(Loc.F("Không xoá được %@: đường dẫn bị chặn bởi chính sách an toàn", name), true);
                return;
            }
            var dryRun = DryRun;
            // Mục của máy: chạy helper (hỏi UAC) trước để không hỏi giữa chừng.
            if (!dryRun && plan.Items.Any(i => i.RequiresRoot)) await env.Helper.EnsureRunningAsync().ConfigureAwait(true);
            var dispatcher = Application.Current.Dispatcher;
            var report = await Task.Run(() => env.CleanEngine.RunAsync(plan, dryRun,
                path => dispatcher.InvokeAsync(() => ConfirmPermanentDelete(path)).Task.Unwrap())).ConfigureAwait(true);
            Notice = Summarize(report, items);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Clean, "Xoá mục khởi động lỗi", ex);
            Notice = new StartupNotice(Loc.F("Không xoá được %@: %@", items[0].DisplayName, ex.Message), true);
        }
        finally
        {
            SetBusy(items, false);
        }
    }

    private static StartupNotice Summarize(CleanReport report, IReadOnlyList<StartupItem> items)
    {
        static StartupItem? ItemOf(CleanReport.Entry e) => (e.Item.Virtual as StartupItemEntry)?.Item;

        if (report.DryRun)
        {
            var label = items.Count == 1 ? items[0].DisplayName : Loc.F("%lld mục", items.Count);
            return new StartupNotice(Loc.F("Chạy thử: sẽ xoá %@", label), false);
        }
        var failed = report.Entries.Where(e => e.Result.IsFailure || (e.Result.Outcome == RemoveOutcomeKind.Skipped && e.Result.Reason != SkipReason.NotFound)).ToList();
        var ok = report.Entries.Where(e => e.Result.IsSuccess).ToList();
        if (items.Count == 1)
        {
            var item = items[0];
            if (failed.FirstOrDefault() is { } f)
                return new StartupNotice(Loc.F("Không xoá được %@: %@", item.DisplayName, f.Result.Message ?? f.Result.LocalizedDescription), true);
            if (ok.FirstOrDefault() is not { } done)
                return new StartupNotice(Loc.F("Không xoá được %@: đường dẫn bị chặn bởi chính sách an toàn", item.DisplayName), true);
            var detail = item.Source switch
            {
                StartupSource.ScheduledTask => Loc.T("Tác vụ lập lịch đã được tắt, không bị xoá."),
                StartupSource.StartupFolder when done.Result.TrashedPath is not null => Loc.T("Lối tắt đã chuyển vào Thùng rác."),
                StartupSource.StartupFolder => Loc.T("Lối tắt đã được xoá qua helper."),
                _ => done.Result.Message is { Length: > 0 } m ? m : "",
            };
            return new StartupNotice((Loc.F("Đã gỡ %@ khỏi khởi động.", item.DisplayName) + " " + detail).Trim(), false);
        }
        var text = Loc.F("Đã gỡ %lld mục khỏi khởi động.", ok.Count);
        if (failed.Count > 0)
        {
            var first = failed[0];
            var name = ItemOf(first)?.DisplayName ?? first.Item.Title;
            text += " " + Loc.F("%lld mục không xoá được: ", failed.Count) + name + " — " + (first.Result.Message ?? first.Result.LocalizedDescription);
        }
        return new StartupNotice(text, failed.Count > 0);
    }
}
