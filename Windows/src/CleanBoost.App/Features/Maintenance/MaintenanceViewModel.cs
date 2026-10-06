using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;
using CleanBoost.Features.Maintenance;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.Maintenance;

public enum TaskRunState { Idle, Queued, Running, Succeeded, Failed }

/// <summary>Một dòng tác vụ: chọn hay không, gợi ý, lần chạy gần nhất, trạng thái chạy của lượt này.</summary>
public sealed partial class MaintenanceTaskRow(MaintenanceTaskName task) : ObservableObject
{
    public MaintenanceTaskName Task { get; } = task;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private MaintenanceRecommendation? _recommendation;
    [ObservableProperty] private MaintenanceRun? _lastRun;
    [ObservableProperty] private TaskRunState _state;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private TimeSpan _elapsed;
}

/// <summary>
/// View model màn Bảo trì (MaintenanceViewModel của bản Mac): đọc trạng thái, gợi ý, chạy lần lượt các tác vụ đã chọn theo
/// thứ tự cố định rồi đọc lại trạng thái.
/// </summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    private readonly AppEnvironment _env;
    private readonly MaintenanceFeature _feature;

    [ObservableProperty] private MaintenanceStatus? _status;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRunning;

    public IReadOnlyList<MaintenanceTaskRow> Rows { get; }
    public DateTime LastRefresh { get; private set; } = DateTime.MinValue;

    public MaintenanceViewModel(AppEnvironment env)
    {
        _env = env;
        _feature = env.Feature<MaintenanceFeature>();
        Rows = MaintenanceTaskInfo.All.Select(t => new MaintenanceTaskRow(t)).ToList();
        foreach (var row in Rows)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MaintenanceTaskRow.IsSelected)) OnPropertyChanged(nameof(SelectedCount));
            };
        }
    }

    public HelperClient Helper => _env.Helper;
    public bool DryRun => _env.Settings.DryRun;
    public int SelectedCount => Rows.Count(r => r.IsSelected);
    public bool IsBusy => IsLoading || IsRunning;
    public IReadOnlyList<MaintenanceTaskName> SelectedTasks => Rows.Where(r => r.IsSelected).Select(r => r.Task).ToList();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

    /// <summary>Đọc trạng thái. Chưa chọn gì thì chọn sẵn tác vụ nên chạy và an toàn (như bản Mac).</summary>
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var status = await Task.Run(() => _feature.StatusReader.ReadAsync()).ConfigureAwait(true);
            Status = status;
            LastRefresh = DateTime.UtcNow;
            foreach (var row in Rows)
            {
                row.Recommendation = status.Recommendation(row.Task);
                row.LastRun = status.LastRuns.GetValueOrDefault(row.Task);
            }
            if (SelectedCount == 0 && !IsRunning)
            {
                foreach (var row in Rows) row.IsSelected = row.Recommendation is { Recommended: true, Safety: SafetyLevel.Safe };
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.UI, "Không đọc được trạng thái bảo trì", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void SelectOnly(MaintenanceTaskName task)
    {
        foreach (var row in Rows) row.IsSelected = row.Task == task;
    }

    public void SetAll(bool on)
    {
        foreach (var row in Rows) row.IsSelected = on;
    }

    /// <summary>
    /// Chạy lần lượt theo thứ tự cố định. Có tác vụ cần quyền quản trị thì chạy helper (hỏi UAC) một lần trước; người dùng từ
    /// chối thì các tác vụ đó báo lỗi ngay, tác vụ còn lại vẫn chạy.
    /// </summary>
    public async Task RunAsync(IReadOnlyCollection<MaintenanceTaskName> selected)
    {
        if (IsRunning || selected.Count == 0) return;
        IsRunning = true;
        var dryRun = DryRun;
        var tasks = MaintenanceTaskInfo.All.Where(selected.Contains).ToList();
        foreach (var row in Rows.Where(r => selected.Contains(r.Task)))
        {
            row.State = TaskRunState.Queued;
            row.Message = null;
            row.Elapsed = TimeSpan.Zero;
        }
        try
        {
            string? adminUnavailable = null;
            if (!dryRun && tasks.Any(t => t.RequiresAdmin()) && !await Helper.EnsureRunningAsync().ConfigureAwait(true))
            {
                adminUnavailable = Helper.Status switch
                {
                    HelperStatus.Declined => Loc.T("Bạn đã không cấp quyền quản trị"),
                    HelperStatus.NotFound => Loc.T("Không tìm thấy helper quản trị (CleanBoost.Helper.exe)"),
                    _ => Loc.T("Không kết nối được helper quản trị"),
                };
            }
            foreach (var task in tasks)
            {
                var row = Rows.First(r => r.Task == task);
                row.State = TaskRunState.Running;
                var started = DateTime.UtcNow;
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                timer.Tick += (_, _) => row.Elapsed = DateTime.UtcNow - started;
                timer.Start();
                MaintenanceRunResult result;
                try
                {
                    if (adminUnavailable is not null && task.RequiresAdmin())
                    {
                        result = new MaintenanceRunResult(task, false, adminUnavailable, TimeSpan.Zero, Executed: false);
                    }
                    else
                    {
                        // Chạy thử: dừng một nhịp để thấy được tiến trình từng dòng.
                        if (dryRun) await Task.Delay(500).ConfigureAwait(true);
                        result = await _feature.Runner.RunAsync(task, Helper, dryRun).ConfigureAwait(true);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.UI, "Tác vụ bảo trì lỗi", ex);
                    result = new MaintenanceRunResult(task, false, ex.Message, DateTime.UtcNow - started);
                }
                finally
                {
                    timer.Stop();
                }
                row.State = result.Success ? TaskRunState.Succeeded : TaskRunState.Failed;
                row.Message = result.Message;
            }
        }
        finally
        {
            IsRunning = false;
        }
        await RefreshAsync().ConfigureAwait(true);
    }
}
