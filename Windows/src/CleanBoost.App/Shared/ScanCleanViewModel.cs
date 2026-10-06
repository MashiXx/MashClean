using System.Collections.ObjectModel;
using System.Windows;
using CleanBoost.App.Services;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CleanBoost.App.Shared;

public enum Phase { Idle, Scanning, Results, Confirming, Cleaning, Done, Error }

/// <summary>
/// Máy trạng thái của một màn quét/dọn (ScanCleanViewModel của bản Mac, SYSTEM_DESIGN mục 22.3):
/// idle → scanning → results → confirming → cleaning → done, và error.
/// </summary>
public partial class ScanCleanViewModel : ObservableObject
{
    protected ScanServices Services { get; }
    public string Kind { get; }
    private readonly Func<IReadOnlyList<IScanTask>> _tasks;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _cleanCts;
    private ScanSessionRecord? _session;

    [ObservableProperty] private Phase _phase = Phase.Idle;
    [ObservableProperty] private ScanProgress _progress = ScanProgress.Zero;
    [ObservableProperty] private NodeTree _tree = NodeTree.Empty;
    [ObservableProperty] private CleanPlan? _plan;
    [ObservableProperty] private CleanReport? _report;
    [ObservableProperty] private double _cleanFraction;
    [ObservableProperty] private string _cleanItem = "";
    [ObservableProperty] private ByteCount _freed;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private SelectionState _selection = new();
    [ObservableProperty] private bool _showRiskyItems;

    public ObservableCollection<ScanWarning> Warnings { get; } = [];
    public ScanResult? LastResult { get; private set; }

    /// <summary>Hỏi xoá vĩnh viễn khi ổ không có Thùng rác; giao diện gắn hộp thoại vào đây.</summary>
    public Func<string, Task<bool>> ConfirmPermanentDelete { get; set; } = _ => Task.FromResult(false);

    public ScanCleanViewModel(ScanServices services, string kind, Func<IReadOnlyList<IScanTask>> tasks)
    {
        Services = services;
        Kind = kind;
        _tasks = tasks;
        _showRiskyItems = services.Settings.ShowRiskyItems;
    }

    public bool IsBusy => Phase is Phase.Scanning or Phase.Cleaning;
    public bool DryRun => Services.Settings.DryRun;
    public ByteCount SelectedBytes => Selection.SelectedBytes;
    public int SelectedCount => Selection.SelectedCount;

    partial void OnPhaseChanged(Phase value) => OnPropertyChanged(nameof(IsBusy));

    partial void OnSelectionChanged(SelectionState? oldValue, SelectionState newValue)
    {
        if (oldValue is not null) oldValue.Changed -= SelectionDidChange;
        newValue.Changed += SelectionDidChange;
        SelectionDidChange();
    }

    private void SelectionDidChange()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedCount));
        PrepareCleanCommand.NotifyCanExecuteChanged();
    }

    // MARK: Hook cho lớp con

    protected virtual NodeTree PostProcess(NodeTree tree) => tree;
    protected virtual SelectionState DefaultSelection(NodeTree tree) => SelectionState.Defaults(tree);
    protected virtual void ScanDidFinish(ScanResult result) { }
    protected virtual void CleanDidFinish(CleanReport report) { }

    // MARK: Quét

    [RelayCommand]
    public async Task StartScanAsync()
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        Warnings.Clear();
        Progress = ScanProgress.Zero;
        Phase = Phase.Scanning;
        var env = await Task.Run(Services.MakeEnvironment).ConfigureAwait(true);
        var rules = Services.Rules;
        try { _session = Services.Storage?.BeginSession(Kind, rules.Version); } catch { _session = null; }
        var dispatcher = Application.Current.Dispatcher;
        try
        {
            var graph = new ScanGraph(_tasks());
            var result = await Task.Run(() => Services.ScanEngine.RunAsync(graph, rules, env, e =>
            {
                if (e is ScanEvent.ProgressChanged p) dispatcher.InvokeAsync(() => { if (_scanCts == cts && Phase == Phase.Scanning) Progress = p.Progress; });
            }, cts.Token), cts.Token).ConfigureAwait(true);
            if (_scanCts != cts) return;
            LastResult = result;
            foreach (var w in result.Warnings) Warnings.Add(w);
            var tree = PostProcess(result.Tree);
            Tree = tree;
            Selection = DefaultSelection(tree);
            Phase = Phase.Results;
            FinishSession("succeeded", tree.TotalSize.Bytes);
            ScanDidFinish(result);
        }
        catch (OperationCanceledException)
        {
            FinishSession("cancelled", 0);
            if (_scanCts == cts) Phase = Phase.Idle;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Scan, "Quét lỗi", ex);
            FinishSession("failed", 0);
            if (_scanCts == cts)
            {
                ErrorMessage = ex.Message;
                Phase = Phase.Error;
            }
        }
    }

    private void FinishSession(string status, long found)
    {
        if (_session is null) return;
        try { Services.Storage?.FinishSession(_session.Id, status, found); } catch { }
    }

    [RelayCommand]
    public void Cancel()
    {
        _scanCts?.Cancel();
        _cleanCts?.Cancel();
        if (Phase == Phase.Scanning) Phase = Phase.Idle;
    }

    // MARK: Chọn

    public void Toggle(Node node) => Selection.Toggle(node.Id, Tree);
    public void SetSelected(Node node, bool on) => Selection.Set(node.Id, on, Tree);

    /// <summary>"Không bao giờ đề xuất mục này": thêm vào danh sách bỏ qua và bỏ chọn.</summary>
    public void Ignore(Node node)
    {
        Services.Ignore(node);
        Selection.Set(node.Id, false, Tree);
    }

    // MARK: Dọn

    private bool CanPrepareClean() => Selection.SelectedCount > 0 && Phase == Phase.Results;

    /// <summary>Lập kế hoạch; cần xác nhận nếu có mục không an toàn, cần quyền quản trị hoặc bị chặn.</summary>
    [RelayCommand(CanExecute = nameof(CanPrepareClean))]
    public async Task PrepareCleanAsync()
    {
        var plan = await Task.Run(() => Services.CleanEngine.MakePlan(Tree, Selection, _session?.Id)).ConfigureAwait(true);
        if (plan.RequiresConfirmation || plan.BlockedItems.Count > 0)
        {
            Plan = plan;
            Phase = Phase.Confirming;
            return;
        }
        await ExecuteAsync(plan).ConfigureAwait(true);
    }

    [RelayCommand]
    public void CancelConfirmation()
    {
        Plan = null;
        Phase = Phase.Results;
    }

    [RelayCommand]
    public Task ConfirmAsync() => Plan is { } plan ? ExecuteAsync(plan) : Task.CompletedTask;

    public async Task ExecuteAsync(CleanPlan plan)
    {
        _cleanCts = new CancellationTokenSource();
        CleanFraction = 0;
        CleanItem = "";
        Freed = ByteCount.Zero;
        Phase = Phase.Cleaning;
        var dispatcher = Application.Current.Dispatcher;
        // Mục cần quyền quản trị: chạy helper (hỏi UAC) trước khi bắt đầu để không hỏi giữa chừng.
        if (plan.Items.Any(i => i.RequiresRoot) && !Services.Settings.DryRun) await Services.Helper.EnsureRunningAsync().ConfigureAwait(true);
        var report = await Task.Run(() => Services.CleanEngine.RunAsync(plan, Services.Settings.DryRun,
            path => dispatcher.InvokeAsync(() => ConfirmPermanentDelete(path)).Task.Unwrap(),
            p => dispatcher.InvokeAsync(() =>
            {
                CleanFraction = p.Fraction;
                CleanItem = p.CurrentItem;
                Freed = p.FreedSoFar;
            }), _cleanCts.Token)).ConfigureAwait(true);
        Report = report;
        Phase = Phase.Done;
        CleanDidFinish(report);
    }

    /// <summary>Hiện thẳng kết quả (Gỡ cài đặt dùng khi đã có cây).</summary>
    public void Show(NodeTree tree, SelectionState selection)
    {
        Tree = tree;
        Selection = selection;
        Phase = Phase.Results;
    }

    [RelayCommand]
    public void Reset()
    {
        _scanCts?.Cancel();
        _cleanCts?.Cancel();
        Selection = new SelectionState();
        Tree = NodeTree.Empty;
        Plan = null;
        Report = null;
        Warnings.Clear();
        Phase = Phase.Idle;
    }

    [RelayCommand]
    public Task RescanAsync()
    {
        Reset();
        return StartScanAsync();
    }
}
