using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CleanBoost.App.Features.LargeOldFiles;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.Duplicates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Features.Duplicates;

/// <summary>
/// View model File trùng lặp (DuplicatesViewModel của bản Mac): thư mục quét do người dùng chỉnh, không chọn sẵn gì (mục 11.8, 15.2),
/// tiến độ theo bước đọc từ hộp tiến độ dùng chung với task.
/// </summary>
public sealed partial class DuplicatesViewModel : ScanCleanViewModel
{
    public DuplicatesFeature Feature { get; }
    public ScanRootList Roots { get; }
    public DuplicateProgressBox ProgressBox { get; }

    [ObservableProperty] private Node? _focusedGroup;
    [ObservableProperty] private IReadOnlyList<Node> _sortedGroups = [];

    public DuplicatesViewModel(ScanServices services, DuplicatesFeature feature)
        : this(services, feature, new ScanRootList(() => feature.DefaultRoots), new DuplicateProgressBox())
    {
    }

    private DuplicatesViewModel(ScanServices services, DuplicatesFeature feature, ScanRootList roots, DuplicateProgressBox box)
        : base(services, FeatureIDs.Duplicates, () =>
        {
            box.Set(DuplicateProgress.Start);
            return [feature.MakeTask(roots.Snapshot, box)];
        })
    {
        Feature = feature;
        Roots = roots;
        ProgressBox = box;
    }

    protected override SelectionState DefaultSelection(NodeTree tree) => new();

    protected override void ScanDidFinish(ScanResult result)
    {
        SortedGroups = Tree.Roots.OrderByDescending(g => DuplicateSelection.Wasted(g).Bytes).ToList();
        FocusedGroup = SortedGroups.FirstOrDefault();
    }

    public void AutoSelect() => Selection = DuplicateSelection.AutoSelect(Tree);

    /// <summary>Cảnh báo nếu có nhóm bị chọn hết mọi bản, rồi lập kế hoạch dọn.</summary>
    public async Task RequestCleanAsync(Window? owner)
    {
        if (!PrepareCleanCommand.CanExecute(null)) return;
        if (DuplicateSelection.GroupsWithEverythingSelected(Selection, Tree).Count > 0)
        {
            var go = MessageDialog.Show(owner, Loc.T("Chọn hết mọi bản trong nhóm?"),
                Loc.T("Có nhóm mà mọi bản đều được chọn: sau khi dọn sẽ không còn bản nào ngoài Thùng rác."),
                Loc.T("Vẫn tiếp tục"), Loc.T("Huỷ"), destructive: true, accent: Accent.Files);
            if (!go) return;
        }
        await PrepareCleanAsync();
    }
}

/// <summary>
/// Màn File trùng lặp (DuplicatesView của bản Mac): màn chờ có thẻ thư mục quét; khi quét hiện bước hiện tại
/// ("Bước 3/5: Băm nhanh đầu và cuối file"); kết quả là danh sách nhóm + chi tiết từng bản.
/// </summary>
public sealed class DuplicatesView : Grid, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private readonly DuplicatesViewModel _vm;
    private readonly FeatureFlowView _flow;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public Accent Accent => Accent.Files;
    public bool IsBusy => _vm.IsBusy;
    public DuplicatesViewModel ViewModel => _vm;

    public static FeatureAppearance Appearance => new(Accent.Files, "doc.on.doc", Loc.T("File trùng lặp"),
        Loc.T("Tìm file có nội dung giống hệt nhau (so dung lượng, băm nhanh rồi SHA-256). Bỏ qua hard link và file đám mây chưa tải về vì xoá chúng không giải phóng dung lượng."),
        ScanTitle: Loc.T("Tìm"), CleanTitle: Loc.T("Chuyển vào Thùng rác"));

    public DuplicatesView(AppEnvironment env)
    {
        var feature = env.Feature<DuplicatesFeature>();
        _vm = new DuplicatesViewModel(env.Services, feature);
        var appearance = Appearance;
        _flow = new FeatureFlowView
        {
            Appearance = appearance,
            IdleAccessory = new RootsEditor(_vm.Roots, Loc.T("Bỏ qua AppData, thư mục ẩn, file đám mây chưa tải về và file nhỏ hơn 1 MB.")),
            CustomResultsBody = new DuplicatesResultsBody(_vm, appearance.CleanButtonTitle),
            ShowFooter = false,
            DataContext = _vm,
        };
        _flow.EmptyTitle.Text = Loc.T("Không tìm thấy file trùng lặp");
        _flow.ScanRing.Label = Loc.T("đang tìm");
        Children.Add(_flow);

        _poll.Tick += (_, _) => UpdateScanning();
        _vm.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(ScanCleanViewModel.Phase):
                    if (_vm.Phase == Phase.Scanning)
                    {
                        _vm.ProgressBox.Set(DuplicateProgress.Start);
                        _poll.Start();
                    }
                    else
                    {
                        _poll.Stop();
                    }
                    UpdateScanning();
                    UpdateSubtitle();
                    break;
                case nameof(ScanCleanViewModel.Progress):
                    UpdateScanning();
                    break;
                case nameof(ScanCleanViewModel.Tree):
                case nameof(ScanCleanViewModel.Selection):
                case nameof(DuplicatesViewModel.SortedGroups):
                    UpdateSubtitle();
                    break;
            }
        };
    }

    public void AutoStart()
    {
        if (_vm.Phase == Phase.Idle) _ = _vm.StartScanAsync();
    }

    /// <summary>Thay dòng tiến độ chung bằng bước của thuật toán: "Bước 3/5: …", "1.204/3.877 file".</summary>
    private void UpdateScanning()
    {
        if (_vm.Phase != Phase.Scanning) return;
        var p = _vm.ProgressBox.Current;
        _flow.ScanRing.Progress = p.Fraction;
        _flow.ScanTaskText.Text = Loc.F("Bước %lld/5: %@", Math.Min(p.Stage.Step(), 5), p.Stage.Title());
        var showCount = p.Total > 0 && p.Stage is DuplicateStage.QuickHash or DuplicateStage.FullHash or DuplicateStage.Verifying;
        _flow.ScanStepsText.Text = showCount ? Loc.F("%@/%@ file", p.Processed.ToString("N0", Loc.Culture), p.Total.ToString("N0", Loc.Culture)) : "";
        // Ẩn cả biểu tượng đi kèm dòng đếm khi chưa có gì để đếm.
        if (_flow.ScanStepsText.Parent is Panel panel)
        {
            var index = panel.Children.IndexOf(_flow.ScanStepsText);
            if (index > 0) panel.Children[index - 1].Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
            _flow.ScanStepsText.Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>"87 nhóm · lãng phí khoảng 965,3 MB".</summary>
    private void UpdateSubtitle()
    {
        if (_vm.Phase != Phase.Results) return;
        var groups = _vm.SortedGroups;
        _flow.ResultsSubtitle.Text = Loc.F("%@ nhóm · lãng phí khoảng %@", groups.Count.ToString("N0", Loc.Culture),
            groups.Sum(DuplicateSelection.Wasted).Formatted);
    }
}
