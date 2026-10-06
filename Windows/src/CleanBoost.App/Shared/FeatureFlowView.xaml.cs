using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.App.Shared;

/// <summary>Diện mạo của một màn tính năng (FeatureAppearance của bản Mac).</summary>
public sealed record FeatureAppearance(Accent Accent, string Symbol, string Title, string Subtitle, string? ScanTitle = null, string? CleanTitle = null)
{
    public string ScanButtonTitle => ScanTitle ?? Loc.T("Quét");
    public string CleanButtonTitle => CleanTitle ?? Loc.T("Dọn dẹp");
}

/// <summary>Một nhóm gốc ở cột trái của màn kết quả.</summary>
public sealed class GroupRow(Node node, Func<Node, CheckState> state) : INotifyPropertyChanged
{
    public Node Node { get; } = node;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Title => Node.Title;
    public string Glyph => Icons.Glyph(Node.Icon ?? "folder");
    public string Badges => string.Join(" · ", Node.Badges);
    public string SizeText => Node.Size.Formatted;
    public bool? IsChecked => state(Node) switch { CheckState.On => true, CheckState.Off => false, _ => null };
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
}

/// <summary>
/// Khung màn hình chung cho các tính năng quét/dọn (FeatureFlowView/PhaseViews của bản Mac): nền gradient theo tính năng,
/// các pha chuyển bằng hiệu ứng mờ dần 0,25 giây.
/// </summary>
public partial class FeatureFlowView : UserControl
{
    private ScanCleanViewModel? _vm;
    private FeatureAppearance _appearance = new(Accent.SmartScan, "sparkles", "", "");
    private readonly ObservableCollection<GroupRow> _groups = [];
    private FrameworkElement? _visiblePanel;

    public FeatureFlowView()
    {
        InitializeComponent();
        GroupList.ItemsSource = _groups;
        StopButton.Content = Loc.T("Dừng");
        RescanButton.Content = Loc.T("Quét lại");
        EmptyRescan.Content = Loc.T("Quét lại");
        EmptyTitle.Text = Loc.T("Không tìm thấy gì cần dọn");
        RiskyToggle.Content = Loc.T("Hiện mục nâng cao (rủi ro)");
        ConfirmTitle.Text = Loc.T("Xác nhận dọn dẹp");
        ConfirmCancel.Content = Loc.T("Huỷ");
        DoneButton.Content = Loc.T("Xong");
        DoneRescan.Content = Loc.T("Quét lại");
        ErrorTitle.Text = Loc.T("Đã có lỗi");
        ErrorRetry.Content = Loc.T("Thử lại");
        ScanRing.Label = Loc.T("đang quét");
        CleanRing.Label = Loc.T("đang dọn");
        Outline.OnIgnore = n => _vm?.Ignore(n);
        Outline.OnToggle = n => _vm?.Toggle(n);
        DataContextChanged += (_, e) => Attach(e.NewValue as ScanCleanViewModel);
    }

    // MARK: Cấu hình từ màn tính năng

    public FeatureAppearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value;
            Theme.Apply(this, value.Accent);
            IdleIcon.Text = Icons.Glyph(value.Symbol);
            IdleTitle.Text = value.Title;
            IdleSubtitle.Text = value.Subtitle;
            ScanButtonText.Text = value.ScanButtonTitle;
            ResultsTitle.Text = value.Title;
            CleanButton.Content = value.CleanButtonTitle;
            ConfirmOk.Content = value.CleanButtonTitle;
        }
    }

    /// <summary>Nội dung thêm dưới tiêu đề ở màn chờ (vd các nhóm sẽ quét).</summary>
    public object? IdleAccessory { get => IdleAccessoryHost.Content; set => IdleAccessoryHost.Content = value; }

    /// <summary>Nút/điều khiển thêm ở góc phải tiêu đề màn kết quả.</summary>
    public object? ResultsAccessory { get => ResultsAccessoryHost.Content; set => ResultsAccessoryHost.Content = value; }

    /// <summary>Nội dung chen trên tiêu đề màn kết quả (vd nút "Tổng hợp" của Smart Scan).</summary>
    public object? ResultsTop { get => ResultsTopHost.Content; set => ResultsTopHost.Content = value; }

    /// <summary>Điều khiển thêm ở chân trang kết quả (cạnh nút dọn).</summary>
    public object? FooterAccessory { get => FooterAccessoryHost.Content; set => FooterAccessoryHost.Content = value; }

    /// <summary>Thay thân màn kết quả hai cột mặc định bằng giao diện riêng của tính năng.</summary>
    public FrameworkElement? CustomResultsBody
    {
        get => CustomBodyHost.Content as FrameworkElement;
        set
        {
            CustomBodyHost.Content = value;
            UpdateResults();
        }
    }

    /// <summary>Ẩn nút chạy thử/nút dọn khi tính năng có hành động riêng.</summary>
    public bool ShowFooter
    {
        get => ResultsFooter.Visibility == Visibility.Visible;
        set => ResultsFooter.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public NodeOutlineView DetailOutline => Outline;

    // MARK: Gắn view model

    private void Attach(ScanCleanViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnVmChanged;
        vm.ConfirmPermanentDelete = AskPermanentDelete;
        RiskyToggle.IsChecked = vm.ShowRiskyItems;
        ShowPhase(vm.Phase, animate: false);
        UpdateAll();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        switch (e.PropertyName)
        {
            case nameof(ScanCleanViewModel.Phase):
                ShowPhase(_vm.Phase, animate: true);
                UpdateAll();
                break;
            case nameof(ScanCleanViewModel.Progress):
                UpdateScanning();
                break;
            case nameof(ScanCleanViewModel.Tree):
            case nameof(ScanCleanViewModel.Selection):
                UpdateResults();
                break;
            case nameof(ScanCleanViewModel.SelectedBytes):
            case nameof(ScanCleanViewModel.SelectedCount):
                UpdateSelection();
                break;
            case nameof(ScanCleanViewModel.CleanFraction):
            case nameof(ScanCleanViewModel.CleanItem):
            case nameof(ScanCleanViewModel.Freed):
                UpdateCleaning();
                break;
        }
    }

    private void UpdateAll()
    {
        UpdateScanning();
        UpdateResults();
        UpdateConfirm();
        UpdateCleaning();
        UpdateDone();
        ErrorText.Text = _vm?.ErrorMessage ?? "";
    }

    private void ShowPhase(Phase phase, bool animate)
    {
        FrameworkElement panel = phase switch
        {
            Phase.Idle => IdlePanel,
            Phase.Scanning => ScanningPanel,
            Phase.Results => ResultsPanel,
            Phase.Confirming => ConfirmPanel,
            Phase.Cleaning => CleaningPanel,
            Phase.Done => DonePanel,
            _ => ErrorPanel,
        };
        if (panel == _visiblePanel) return;
        foreach (var p in new FrameworkElement[] { IdlePanel, ScanningPanel, ResultsPanel, ConfirmPanel, CleaningPanel, DonePanel, ErrorPanel })
        {
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        }
        _visiblePanel = panel;
        if (animate) panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    // MARK: Quét

    private void UpdateScanning()
    {
        if (_vm is null) return;
        var p = _vm.Progress;
        ScanRing.Progress = p.Fraction;
        ScanTaskText.Text = string.IsNullOrEmpty(p.CurrentTask) ? Loc.T("Đang chuẩn bị…") : p.CurrentTask;
        ScanFilesText.Text = Loc.F("%@ mục đã duyệt", p.FilesVisited.ToString("N0", Loc.Culture));
        ScanBytesPanel.Visibility = p.BytesFound.Bytes > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScanBytesText.Text = Loc.F("Tìm thấy %@", p.BytesFound.Formatted);
        ScanStepsText.Text = Loc.F("%lld/%lld bước", p.CompletedTasks, p.TotalTasks);
    }

    // MARK: Kết quả

    private IEnumerable<Node> VisibleRoots()
    {
        if (_vm is null) return [];
        var showRisky = RiskyToggle.IsChecked == true;
        return _vm.Tree.Roots.Where(r => showRisky || r.Safety != SafetyLevel.Risky);
    }

    private void UpdateResults()
    {
        if (_vm is null) return;
        var tree = _vm.Tree;
        var hasRisky = tree.Roots.Any(r => r.Safety == SafetyLevel.Risky);
        RiskyToggle.Visibility = hasRisky ? Visibility.Visible : Visibility.Collapsed;
        var leafCount = tree.AllLeaves.Count();
        ResultsSubtitle.Text = Loc.F("Tìm thấy %@ trong %@ mục", tree.TotalSize.Formatted, leafCount.ToString("N0", Loc.Culture));
        UpdateWarnings();

        var custom = CustomBodyHost.Content is not null;
        var empty = tree.IsEmpty;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DefaultBody.Visibility = !empty && !custom ? Visibility.Visible : Visibility.Collapsed;
        CustomBodyHost.Visibility = !empty && custom ? Visibility.Visible : Visibility.Collapsed;
        ResultsFooter.IsEnabled = !empty;

        if (!custom)
        {
            var previous = (GroupList.SelectedItem as GroupRow)?.Node.Id;
            _groups.Clear();
            foreach (var r in VisibleRoots()) _groups.Add(new GroupRow(r, n => _vm.Selection.State(n.Id, _vm.Tree)));
            Outline.Tree = tree;
            Outline.Selection = _vm.Selection;
            GroupList.SelectedItem = _groups.FirstOrDefault(g => g.Node.Id == previous) ?? _groups.FirstOrDefault();
            if (_groups.Count == 0) ShowFocus(null);
        }
        _vm.Selection.Changed -= RefreshGroupChecks;
        _vm.Selection.Changed += RefreshGroupChecks;
        UpdateSelection();
    }

    private void UpdateWarnings()
    {
        if (_vm is null) return;
        var warnings = _vm.Warnings.Where(w => w.Kind != ScanWarningKind.Skipped || _vm.Warnings.Count < 3).ToList();
        WarningAction.Visibility = Visibility.Collapsed;
        if (warnings.Any(w => w.Kind == ScanWarningKind.NeedsAdmin))
        {
            WarningText.Text = Loc.T("Một số thư mục cần quyền quản trị để quét đầy đủ.");
            WarningBanner.Visibility = Visibility.Visible;
            if (!Engine.Ipc.HelperClient.IsProcessElevated)
            {
                WarningAction.Content = Loc.T("Chạy với quyền quản trị");
                WarningAction.Visibility = Visibility.Visible;
                var kind = _vm.Kind;
                WarningActionHandler ??= () => App.RestartElevated($"{Identifiers.UrlScheme}://scan?feature={kind}");
            }
        }
        else if (warnings.Count > 0)
        {
            WarningText.Text = Loc.F("%lld cảnh báo khi quét: ", warnings.Count) + string.Join("; ", warnings.Take(2).Select(w => w.Message));
            WarningBanner.Visibility = Visibility.Visible;
        }
        else
        {
            WarningBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshGroupChecks()
    {
        foreach (var g in _groups) g.Refresh();
        UpdateSelectAll();
    }

    private void UpdateSelection()
    {
        if (_vm is null) return;
        SelectedText.Text = Loc.F("Đã chọn %@", _vm.SelectedBytes.Formatted);
        SelectedDetail.Text = Loc.F("%@ mục · dung lượng là ước tính", _vm.SelectedCount.ToString("N0", Loc.Culture));
        DryRunPill.Visibility = _vm.DryRun ? Visibility.Visible : Visibility.Collapsed;
        CleanButton.IsEnabled = _vm.SelectedCount > 0;
        UpdateSelectAll();
    }

    private void UpdateSelectAll()
    {
        if (_vm is null) return;
        var roots = VisibleRoots().Where(r => _vm.Tree.LeafIDs.TryGetValue(r.Id, out var l) && l.Length > 0).ToList();
        var states = roots.Select(r => _vm.Selection.State(r.Id, _vm.Tree)).ToList();
        bool? state = states.Count == 0 || states.All(s => s == CheckState.Off) ? false : states.All(s => s == CheckState.On) ? true : null;
        SelectAll.IsChecked = state;
        SelectAll.Content = state == true ? Loc.T("Bỏ chọn tất cả") : Loc.T("Chọn tất cả");
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var roots = VisibleRoots().ToList();
        var allOn = roots.All(r => _vm.Selection.State(r.Id, _vm.Tree) == CheckState.On);
        _vm.Selection.SetMany(roots.Select(r => r.Id), !allOn, _vm.Tree);
        UpdateSelectAll();
    }

    private void OnGroupCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow row) _vm?.Toggle(row.Node);
        e.Handled = true;
    }

    private void OnGroupFocused(object sender, SelectionChangedEventArgs e) => ShowFocus((GroupList.SelectedItem as GroupRow)?.Node);

    private void ShowFocus(Node? node)
    {
        if (node is null)
        {
            FocusTitle.Text = "";
            FocusReason.Text = "";
            FocusSafety.Visibility = Visibility.Collapsed;
            Outline.Roots = [];
            return;
        }
        FocusTitle.Text = node.Title;
        FocusReason.Text = node.Reason.Resolved;
        FocusReason.Visibility = node.Reason.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        FocusSafety.Visibility = Visibility.Visible;
        FocusSafety.Background = new SolidColorBrush(Color.FromArgb(0x40, Theme.SafetyBrush(node.Safety).Color.R, Theme.SafetyBrush(node.Safety).Color.G, Theme.SafetyBrush(node.Safety).Color.B));
        FocusSafetyText.Text = node.Safety.LocalizedTitle();
        FocusSafetyText.Foreground = Theme.SafetyBrush(node.Safety);
        Outline.Roots = node.IsContainer ? node.Children.OrderByDescending(c => c.Size.Bytes).ToList() : [node];
    }

    private void OnRiskyToggled(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.ShowRiskyItems = RiskyToggle.IsChecked == true;
        UpdateResults();
    }

    // MARK: Xác nhận

    private void UpdateConfirm()
    {
        ConfirmCards.Children.Clear();
        if (_vm?.Plan is not { } plan) return;
        ConfirmSubtitle.Text = Loc.F("Sẽ xử lý %lld mục, khoảng %@.", plan.Items.Count, plan.TotalSize.Formatted);
        ConfirmOk.IsEnabled = !plan.IsEmpty;
        var root = plan.RootItems.ToList();
        if (root.Count > 0) ConfirmCards.Children.Add(ConfirmCard("lock.fill", Loc.F("Cần quyền quản trị (%lld)", root.Count), root, null));
        var review = plan.ReviewItems.ToList();
        if (review.Count > 0)
        {
            var permanent = review.Count(i => !i.Strategy.IsRecoverable && !i.Strategy.IsCustom && i.Path is not null);
            var note = permanent > 0 ? Loc.F("%lld mục sẽ bị xoá vĩnh viễn (không qua Thùng rác).", permanent) : null;
            ConfirmCards.Children.Add(ConfirmCard("eye", Loc.F("Cần xem lại (%lld)", review.Count), review, note));
        }
        if (plan.BlockedItems.Count > 0)
        {
            ConfirmCards.Children.Add(BlockedCard(plan.BlockedItems));
        }
    }

    private Border ConfirmCard(string symbol, string title, IReadOnlyList<CleanPlan.Item> items, string? note)
    {
        var stack = new StackPanel();
        stack.Children.Add(CardHeader(symbol, title));
        if (note is not null) stack.Children.Add(new TextBlock { Text = note, FontSize = 11.5, Foreground = Theme.ReviewBrush, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        foreach (var i in items.Take(50)) stack.Children.Add(ItemRow(i.Title, i.Path, i.ExpectedSize.Formatted));
        if (items.Count > 50) stack.Children.Add(new TextBlock { Text = Loc.F("… và %lld mục khác", items.Count - 50), FontSize = 11.5, Foreground = (Brush)FindResource("TertiaryText") });
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = stack };
    }

    private Border BlockedCard(IReadOnlyList<CleanPlan.Blocked> blocked)
    {
        var stack = new StackPanel();
        stack.Children.Add(CardHeader("hand.raised.fill", Loc.F("Bị chặn vì lý do an toàn (%lld)", blocked.Count)));
        foreach (var b in blocked.Take(50)) stack.Children.Add(ItemRow(b.Title, b.Violation.LocalizedDescription, null));
        if (blocked.Count > 50) stack.Children.Add(new TextBlock { Text = Loc.F("… và %lld mục khác", blocked.Count - 50), FontSize = 11.5, Foreground = (Brush)FindResource("TertiaryText") });
        return new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 10), Child = stack };
    }

    private static StackPanel CardHeader(string symbol, string title)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = Icons.Glyph(symbol), FontFamily = Icons.Font, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        header.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        return header;
    }

    private Grid ItemRow(string title, string? detail, string? size)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail))
            s.Children.Add(new TextBlock { Text = detail.AbbreviatingHome(), FontSize = 11, Foreground = (Brush)FindResource("TertiaryText"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = detail });
        g.Children.Add(s);
        if (size is not null)
        {
            var t = new TextBlock { Text = size, FontSize = 12, Foreground = (Brush)FindResource("SecondaryText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
        }
        return g;
    }

    // MARK: Đang dọn

    private void UpdateCleaning()
    {
        if (_vm is null) return;
        CleanRing.Progress = _vm.CleanFraction;
        CleanFreedText.Text = Loc.F("Đã giải phóng %@", _vm.Freed.Formatted);
        CleanItemText.Text = _vm.CleanItem.AbbreviatingHome();
    }

    // MARK: Xong

    private sealed record Failure(string Title, string Message);

    private void UpdateDone()
    {
        if (_vm?.Report is not { } report) return;
        var failed = report.Failed.ToList();
        DoneIcon.Text = Icons.Glyph(failed.Count > 0 ? "exclamationmark.circle.fill" : "checkmark.circle.fill");
        DoneTitle.Text = report.DryRun ? Loc.T("Chạy thử xong (không xoá gì)") : Loc.T("Hoàn tất");
        DoneTiles.Children.Clear();
        DoneTiles.Children.Add(StatTile("sparkles", Loc.T("Ước tính đã dọn"), report.EstimatedFreed.Formatted));
        if (report.MeasuredFreed is { } measured) DoneTiles.Children.Add(StatTile("internaldrive", Loc.T("Đo thực tế trên ổ"), measured.Formatted));
        DoneTiles.Children.Add(StatTile("checkmark", Loc.T("Mục thành công"), report.Succeeded.Count().ToString("N0", Loc.Culture)));
        if (failed.Count > 0) DoneTiles.Children.Add(StatTile("xmark", Loc.T("Mục lỗi"), failed.Count.ToString("N0", Loc.Culture)));
        var lower = report.MeasuredFreed is { } m && m < report.EstimatedFreed;
        DoneNote.Text = lower ? Loc.T("Con số thực tế có thể thấp hơn do file đang được Windows giữ lại hoặc ổ đang ghi dữ liệu khác.") : "";
        DoneNote.Visibility = lower ? Visibility.Visible : Visibility.Collapsed;
        var inUse = report.InUse.Count();
        InUseBanner.Visibility = inUse > 0 ? Visibility.Visible : Visibility.Collapsed;
        InUseText.Text = Loc.F("%lld mục đang được sử dụng nên đã bỏ qua. Hãy tắt app liên quan rồi thử lại.", inUse);
        FailuresCard.Visibility = failed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FailuresList.ItemsSource = failed.Take(30).Select(f => new Failure(f.Item.Title, f.Result.LocalizedDescription)).ToList();
    }

    private static StackPanel StatTile(string symbol, string label, string value)
    {
        var s = new StackPanel { Margin = new Thickness(16, 0, 16, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = Icons.Glyph(symbol), FontFamily = Icons.Font, FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255)), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255)) });
        s.Children.Add(head);
        s.Children.Add(new TextBlock { Text = value, FontFamily = AppFonts.Display, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
        return s;
    }

    // MARK: Hành động

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.StartScanAsync();
    }

    private void OnStop(object sender, RoutedEventArgs e) => _vm?.Cancel();

    private async void OnRescan(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.RescanAsync();
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && _vm.PrepareCleanCommand.CanExecute(null)) await _vm.PrepareCleanAsync();
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e) => _vm?.CancelConfirmation();

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.ConfirmAsync();
    }

    private void OnDone(object sender, RoutedEventArgs e) => _vm?.Reset();

    /// <summary>Hành động của banner cảnh báo (mặc định: chạy lại với quyền quản trị không áp dụng — ẩn).</summary>
    public Action? WarningActionHandler { get; set; }

    private void OnWarningAction(object sender, RoutedEventArgs e) => WarningActionHandler?.Invoke();

    private Task<bool> AskPermanentDelete(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        var ok = MessageDialog.Show(Window.GetWindow(this), Loc.T("Không thể chuyển vào Thùng rác"),
            Loc.F("\"%@\" nằm trên ổ không có Thùng rác (ổ ngoài hoặc ổ mạng). Bạn có muốn xoá vĩnh viễn không? Thao tác này không khôi phục được.", name),
            Loc.T("Xoá vĩnh viễn"), Loc.T("Bỏ qua"), destructive: true);
        return Task.FromResult(ok);
    }
}
