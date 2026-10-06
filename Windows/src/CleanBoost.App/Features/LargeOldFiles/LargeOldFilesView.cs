using System.Windows;
using System.Windows.Controls;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.LargeOldFiles;

namespace CleanBoost.App.Features.LargeOldFiles;

/// <summary>
/// View model File lớn &amp; cũ: máy trạng thái chung, nhưng không chọn sẵn gì (mục 11.7, 15.2). Thư mục quét do người dùng chỉnh;
/// task đọc bản chụp danh sách lúc bắt đầu quét.
/// </summary>
public sealed class LargeOldFilesViewModel : ScanCleanViewModel
{
    public LargeOldFilesFeature Feature { get; }
    public ScanRootList Roots { get; }
    public LargeOldFilter Filter { get; } = new();
    public IReadOnlyList<LargeOldItem> Items { get; private set; } = [];

    /// <summary>Có kết quả quét mới.</summary>
    public event Action? ItemsChanged;

    public LargeOldFilesViewModel(ScanServices services, LargeOldFilesFeature feature)
        : this(services, feature, new ScanRootList(() => feature.DefaultRoots))
    {
    }

    private LargeOldFilesViewModel(ScanServices services, LargeOldFilesFeature feature, ScanRootList roots)
        : base(services, FeatureIDs.LargeOldFiles, () => [feature.MakeTask(roots.Snapshot)])
    {
        Feature = feature;
        Roots = roots;
    }

    protected override SelectionState DefaultSelection(NodeTree tree) => new();

    protected override void ScanDidFinish(ScanResult result)
    {
        Items = LargeOldItem.Items(Tree);
        ItemsChanged?.Invoke();
    }
}

/// <summary>
/// Màn File lớn &amp; cũ (LargeOldFilesView của bản Mac): màn chờ có các loại file và thư mục quét; kết quả là bảng lọc/sắp xếp được,
/// chọn tay rồi chuyển vào Thùng rác qua Clean Engine.
/// </summary>
public sealed class LargeOldFilesView : Grid, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private readonly LargeOldFilesViewModel _vm;
    private readonly FeatureFlowView _flow;
    private readonly LargeOldResultsBody _body;
    private readonly TextBlock _criteria = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14) };

    public Accent Accent => Accent.Files;
    public bool IsBusy => _vm.IsBusy;
    public LargeOldFilesViewModel ViewModel => _vm;

    public static FeatureAppearance Appearance => new(Accent.Files, "doc.badge.clock", Loc.T("File lớn và cũ"),
        Loc.T("Tìm file lớn hoặc lâu không mở trong thư mục người dùng. Không có gì được chọn sẵn: bạn tự quyết định file nào chuyển vào Thùng rác."),
        CleanTitle: Loc.T("Chuyển vào Thùng rác"));

    public LargeOldFilesView(AppEnvironment env)
    {
        var feature = env.Feature<LargeOldFilesFeature>();
        _vm = new LargeOldFilesViewModel(env.Services, feature);
        var appearance = Appearance;
        _body = new LargeOldResultsBody(_vm, appearance.CleanButtonTitle);
        _criteria.Style = FileListUi.StyleOf("CaptionText");
        _flow = new FeatureFlowView
        {
            Appearance = appearance,
            IdleAccessory = IdleAccessory(),
            CustomResultsBody = _body,
            ShowFooter = false,
            DataContext = _vm,
        };
        _flow.EmptyTitle.Text = Loc.T("Không tìm thấy file lớn hoặc cũ");
        Children.Add(_flow);

        _body.VisibleChanged += UpdateSubtitle;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ScanCleanViewModel.Tree) or nameof(ScanCleanViewModel.Selection) or nameof(ScanCleanViewModel.Phase)) UpdateSubtitle();
        };
        env.Settings.Changed += key =>
        {
            if (key.StartsWith("largeFiles.", StringComparison.Ordinal) || key == "*") Dispatcher.InvokeAsync(UpdateCriteria);
        };
        UpdateCriteria();
    }

    public void AutoStart()
    {
        if (_vm.Phase == Phase.Idle) _ = _vm.StartScanAsync();
    }

    /// <summary>Màn chờ: các loại file (KindChips), ngưỡng đang dùng, thẻ thư mục quét.</summary>
    private FrameworkElement IdleAccessory()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(FileListUi.Chips(FileKinds.All.Select(k => k.Title())));
        stack.Children.Add(_criteria);
        stack.Children.Add(new RootsEditor(_vm.Roots, Loc.T("Bỏ qua AppData, thư mục hệ thống, liên kết và file đám mây chưa tải về.")));
        return stack;
    }

    private void UpdateCriteria()
    {
        var c = _vm.Feature.Criteria;
        _criteria.Text = Loc.F("Tìm file lớn hơn %@ hoặc không dùng hơn %lld ngày.", new ByteCount(c.LargeThreshold).Formatted, c.OldDays);
    }

    /// <summary>"218 file, 30,15 GB · đang hiện 218" thay cho dòng phụ đề chung của màn kết quả.</summary>
    private void UpdateSubtitle()
    {
        if (_vm.Phase != Phase.Results) return;
        var items = _vm.Items;
        _flow.ResultsSubtitle.Text = Loc.F("%@ file, %@ · đang hiện %@", items.Count.ToString("N0", Loc.Culture),
            items.Sum(i => i.Size).Formatted, _body.VisibleCount.ToString("N0", Loc.Culture));
    }
}
