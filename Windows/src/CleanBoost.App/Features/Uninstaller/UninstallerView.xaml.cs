using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;
using CleanBoost.Features.Uninstaller;

namespace CleanBoost.App.Features.Uninstaller;

/// <summary>
/// Màn Gỡ cài đặt (UninstallerView của bản Mac, spec mục 4.9): điều khiển phân đoạn "Ứng dụng" | "File sót của app đã gỡ".
/// Tab Ứng dụng: danh sách app có biểu tượng thật bên trái, chi tiết app và file sót theo mức tin cậy bên phải; gỡ = chạy trình
/// gỡ của hãng → tìm lại file sót → xem lại → dọn. Tab file sót dùng luồng quét/dọn chung.
/// </summary>
public partial class UninstallerView : UserControl, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private readonly UninstallerViewModel _vm;
    private FrameworkElement? _visiblePanel;
    private Node? _outlineRoot;
    private NodeTree? _outlineTree;
    private bool _updatingList;

    public Accent Accent => Accent.Applications;
    public bool IsBusy => _vm.IsBusy;

    /// <summary>"Gỡ cài đặt" của nút (khoá riêng vì "Gỡ cài đặt" ở thanh bên dịch là "Uninstaller").</summary>
    internal static string UninstallAction => Loc.IsEnglish ? Loc.T("action.uninstall") : "Gỡ cài đặt";

    public UninstallerView(AppEnvironment env, AppRouter router)
    {
        InitializeComponent();
        Theme.Apply(this, Accent.Applications);
        Resources["SegmentSelected"] = Theme.Frozen(Theme.SystemAccent);
        _vm = new UninstallerViewModel(env);

        ApplyTexts();
        OrphansFlow.Appearance = new FeatureAppearance(Accent.Applications, "puzzlepiece.extension", Loc.T("File sót của app đã gỡ"),
            Loc.T("Tìm dữ liệu còn lại của chương trình đã gỡ trong AppData và ProgramData. Mọi mục cần bạn xem lại trước khi xoá."),
            CleanTitle: Loc.T("Chuyển vào Thùng rác"));
        OrphansFlow.DataContext = _vm.Orphans;

        AppList.ItemsSource = _vm.VisibleRows;
        Outline.OnToggle = n => _vm.Selection.Toggle(n.Id, _vm.Tree);
        ReviewOutline.OnToggle = n => { if (_vm.Review is { } r) r.Selection.Toggle(n.Id, r.Tree); };

        _vm.PropertyChanged += OnVmChanged;
        _vm.DetailChanged += () => Dispatcher.InvokeAsync(UpdateBrowse);
        _vm.AppNotFound += path => MessageDialog.Show(Window.GetWindow(this), Loc.T("Không tìm thấy chương trình"),
            Loc.F("Không có chương trình đã cài nào chứa \"%@\".", Path.GetFileName(path)), Loc.T("OK"), accent: Accent.Applications);
        _vm.AskForceQuit = app => Task.FromResult(MessageDialog.Show(Window.GetWindow(this), Loc.F("%@ chưa thoát", app.Name),
            Loc.T("App không thoát sau 10 giây. Buộc thoát có thể làm mất dữ liệu chưa lưu."), Loc.T("Buộc thoát"), Loc.T("Bỏ qua app này"),
            destructive: true, accent: Accent.Applications));
        _vm.ConfirmPermanentDelete = path => Task.FromResult(MessageDialog.Show(Window.GetWindow(this), Loc.T("Không thể chuyển vào Thùng rác"),
            Loc.F("\"%@\" không chuyển vào Thùng rác được (ổ ngoài hoặc file của hệ thống). Xoá vĩnh viễn? Thao tác này không khôi phục được.",
                Path.GetFileName(path.TrimEnd('\\'))), Loc.T("Xoá vĩnh viễn"), Loc.T("Bỏ qua"), destructive: true, accent: Accent.Applications));
        router.UninstallPathRequested += path => Dispatcher.InvokeAsync(() =>
        {
            AppsSegment.IsChecked = true;
            _vm.OpenPath(path);
        });

        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever };
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, spin);

        ShowPhase(_vm.Phase, animate: false);
        UpdateBrowse();
    }

    private void ApplyTexts()
    {
        IdleIcon.Text = Icons.Glyph("xmark.app");
        IdleTitle.Text = Loc.T("Gỡ cài đặt");
        IdleSubtitle.Text = Loc.T("Gỡ chương trình bằng trình gỡ của chính nó, rồi dọn file sót: dữ liệu AppData, lối tắt, khoá registry. Mỗi mục kèm mức tin cậy.");
        ScanButtonText.Text = Loc.T("Quét");
        foreach (var chip in new[] { Loc.T("Chương trình desktop"), Loc.T("App Microsoft Store"), Loc.T("Dữ liệu AppData"), Loc.T("Lối tắt trong Start Menu"), Loc.T("Khoá registry") })
        {
            IdleChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)), Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(4), Child = new TextBlock { Text = chip, FontSize = 12, Foreground = Brushes.White },
            });
        }
        LoadText.Text = Loc.T("Đang tìm ứng dụng đã cài…");
        LoadStop.Content = Loc.T("Dừng");
        LoadRing.Label = Loc.T("đang quét");
        SearchPlaceholder.Text = Loc.T("Tìm ứng dụng");
        FilterButton.ToolTip = Loc.T("Sắp xếp và lọc");
        ReloadButton.ToolTip = Loc.T("Quét lại danh sách app");
        RevealButton.ToolTip = Loc.T("Hiện trong File Explorer");
        AppMenuReveal.Header = Loc.T("Hiện trong File Explorer");
        AppMenuSettings.Header = Loc.T("Mở Cài đặt Windows > Ứng dụng");
        LeftoversLoadingText.Text = Loc.T("Đang tìm file sót…");
        NoFocus.Text = Loc.T("Chọn một ứng dụng để xem file sót");
        LeftoverCaption.Text = Loc.T("Mục Chắc chắn và Cao được chọn sẵn. Mục Trung bình và Thấp cần bạn xem lại.");
        NoLeftoversText.Text = Loc.T("Không thấy file sót nào lúc này. Sau khi gỡ, Clean Boost sẽ kiểm tra lại thư mục cài đặt và lối tắt.");
        FooterCaption.Text = Loc.T("Chạy trình gỡ của từng app, rồi cho bạn xem lại file sót trước khi dọn");
        UninstallButton.Content = UninstallAction;
        ResetButton.Content = Loc.T("Đặt lại app");
        ResetButton.ToolTip = Loc.T("Chỉ xoá dữ liệu đã chọn của app, giữ lại ứng dụng");
        ConfirmCancel.Content = Loc.T("Huỷ");
        StopWaitingButton.Content = Loc.T("Dừng chờ");
        ReviewCaption.Text = Loc.T("Mục Chắc chắn và Cao được chọn sẵn. Mục Trung bình và Thấp cần bạn xem lại.");
        ReviewClean.Content = Loc.T("Dọn file sót");
        ReviewSkip.Content = Loc.T("Bỏ qua");
        DoneButton.Content = Loc.T("Xong");
        DoneSettings.Content = Loc.T("Mở Cài đặt Windows");
        AppsSegment.Content = Loc.T("Ứng dụng");
        OrphansSegment.Content = Loc.T("File sót của app đã gỡ");
    }

    // MARK: IAutoStartScreen

    public void AutoStart()
    {
        if (OrphansSegment.IsChecked == true)
        {
            if (!_vm.Orphans.IsBusy && _vm.Orphans.Phase == Phase.Idle) _ = _vm.Orphans.StartScanAsync();
            return;
        }
        if (_vm.Phase == AppsPhase.Idle) _ = _vm.LoadAppsAsync();
#if DEBUG
        DebugSnapshotState();
#endif
    }

#if DEBUG
    /// <summary>
    /// Chỉ khi chụp ảnh kiểm thử (<c>CLEANBOOST_SNAPSHOT</c>): <c>CLEANBOOST_SNAPSHOT_UNINSTALLER=orphans|confirm|review</c> để chụp tab
    /// file sót, màn xác nhận (không chạy gì) hoặc màn xem lại file sót (dựng từ app đang chọn, không gỡ gì);
    /// <c>CLEANBOOST_SNAPSHOT_UNINSTALLER_APP=&lt;một phần tên&gt;</c> để chọn app.
    /// </summary>
    private async void DebugSnapshotState()
    {
        if (!DebugSnapshot.IsEnabled) return;
        var state = Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_UNINSTALLER") ?? "";
        var appName = Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_UNINSTALLER_APP") ?? "";
        if (state.Length == 0 && appName.Length == 0) return;
        _vm.HoldBusy = true;
        try
        {
            await DebugSnapshotStateCore(state, appName);
            await Task.Delay(300);
        }
        finally
        {
            _vm.HoldBusy = false;
        }
    }

    private async Task DebugSnapshotStateCore(string state, string appName)
    {
        if (state == "orphans")
        {
            OrphansSegment.IsChecked = true;
            _ = _vm.Orphans.StartScanAsync();
            return;
        }
        for (var i = 0; i < 240 && _vm.Phase != AppsPhase.Browsing; i++) await Task.Delay(250);
        if (appName.Length > 0 && _vm.AllRows.FirstOrDefault(r => r.Name.Contains(appName, StringComparison.OrdinalIgnoreCase)) is { } wanted) _vm.Focus(wanted);
        for (var i = 0; i < 240 && (_vm.Focused is null || !_vm.HasReport(_vm.Focused)); i++) await Task.Delay(250);
        if (_vm.Focused is not { } row) return;
        if (state == "confirm")
        {
            if (!row.IsChecked) _vm.ToggleChecked(row);
            for (var i = 0; i < 40 && _vm.AnyCheckedLoading; i++) await Task.Delay(250);
            _vm.RequestUninstall();
        }
        else if (state == "review")
        {
            await _vm.DebugShowReviewAsync(row);
        }
        else if (state.StartsWith("fixture", StringComparison.Ordinal) && row.Name.StartsWith("CB Uninstaller Test", StringComparison.Ordinal))
        {
            // Chỉ với app giả do test tạo trong HKCU: chạy cả luồng gỡ (trình gỡ giả) tới màn xem lại / màn xong.
            if (!row.IsChecked) _vm.ToggleChecked(row);
            for (var i = 0; i < 40 && _vm.AnyCheckedLoading; i++) await Task.Delay(250);
            _vm.RequestUninstall();
            await _vm.ConfirmAsync();
            if (state == "fixture-skip" && _vm.Phase == AppsPhase.Review) _vm.SkipReview();
        }
    }
#endif

    // MARK: Pha

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UninstallerViewModel.Phase):
                ShowPhase(_vm.Phase, animate: true);
                break;
            case nameof(UninstallerViewModel.LoadProgress):
                LoadRing.Progress = _vm.LoadProgress;
                break;
            case nameof(UninstallerViewModel.WorkTitle):
            case nameof(UninstallerViewModel.WorkItem):
            case nameof(UninstallerViewModel.WorkProgress):
            case nameof(UninstallerViewModel.CanStopWaiting):
                UpdateWorking();
                break;
            case nameof(UninstallerViewModel.Focused):
                if (!_updatingList && !Equals(AppList.SelectedItem, _vm.Focused))
                {
                    _updatingList = true;
                    AppList.SelectedItem = _vm.Focused;
                    if (_vm.Focused is not null) AppList.ScrollIntoView(_vm.Focused);
                    _updatingList = false;
                }
                UpdateBrowse();
                break;
            case nameof(UninstallerViewModel.Tree):
            case nameof(UninstallerViewModel.Selection):
                UpdateBrowse();
                break;
        }
    }

    private void ShowPhase(AppsPhase phase, bool animate)
    {
        FrameworkElement panel = phase switch
        {
            AppsPhase.Idle => IdlePanel,
            AppsPhase.Loading => LoadingPanel,
            AppsPhase.Browsing => BrowsePanel,
            AppsPhase.Confirming => ConfirmPanel,
            AppsPhase.Working => WorkingPanel,
            AppsPhase.Review => ReviewPanel,
            _ => DonePanel,
        };
        switch (phase)
        {
            case AppsPhase.Confirming: BuildConfirm(); break;
            case AppsPhase.Working: UpdateWorking(); break;
            case AppsPhase.Review: BuildReview(); break;
            case AppsPhase.Done: BuildDone(); break;
            case AppsPhase.Browsing: UpdateBrowse(); break;
            case AppsPhase.Loading: LoadRing.Progress = _vm.LoadProgress; break;
        }
        if (panel == _visiblePanel) return;
        foreach (var p in new FrameworkElement[] { IdlePanel, LoadingPanel, BrowsePanel, ConfirmPanel, WorkingPanel, ReviewPanel, DonePanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        _visiblePanel = panel;
        if (animate) panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (AppsTab is null || OrphansFlow is null) return;
        var apps = AppsSegment.IsChecked == true;
        AppsTab.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        OrphansFlow.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
        (apps ? (FrameworkElement)AppsTab : OrphansFlow).BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    // MARK: Duyệt

    private void UpdateBrowse()
    {
        if (_vm.Phase != AppsPhase.Browsing) return;
        // Danh sách
        var checkable = _vm.CheckableVisible;
        var checkedVisible = checkable.Count(r => r.IsChecked);
        SelectAllApps.IsEnabled = checkable.Count > 0;
        SelectAllApps.IsChecked = checkedVisible == 0 ? false : checkedVisible == checkable.Count ? true : null;
        SelectAllApps.Content = checkedVisible == checkable.Count && checkable.Count > 0 ? Loc.T("Bỏ chọn tất cả") : Loc.T("Chọn tất cả");
        ListCaption.Text = Loc.F("%@ · sắp theo %@", _vm.Filter.Title, _vm.Sort.Title().ToLower(Loc.Culture));
        var checkedCount = _vm.CheckedRows.Count();
        DeselectButton.Visibility = checkedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        DeselectText.Text = Loc.F("Bỏ chọn (%lld)", checkedCount);
        EmptyList.Visibility = _vm.VisibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyList.Text = _vm.AllRows.Count == 0 ? Loc.T("Không tìm thấy ứng dụng nào") : Loc.T("Không có ứng dụng nào khớp");
        // Danh sách vừa lọc/sắp xếp lại: giữ dòng đang xem được tô sáng.
        if (_vm.Focused is { } focused && !ReferenceEquals(AppList.SelectedItem, focused) && _vm.VisibleRows.Contains(focused))
        {
            _updatingList = true;
            AppList.SelectedItem = focused;
            _updatingList = false;
        }
        UpdateDetail();
        UpdateFooter();
    }

    private void UpdateDetail()
    {
        var row = _vm.Focused;
        Banners.Children.Clear();
        DetailHeader.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null)
        {
            ShowBody(NoFocus);
            return;
        }
        var app = row.App;
        var icon = row.Icon ?? AppIcons.For(app);
        DetailIcon.Source = icon;
        DetailIconFallback.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
        DetailName.Text = app.Name;
        DetailName.ToolTip = app.Name;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(app.Version)) parts.Add(Loc.F("Phiên bản %@", app.Version));
        if (!string.IsNullOrEmpty(app.Publisher)) parts.Add(app.Publisher!);
        if (app.InstallDate is { } d) parts.Add(Loc.F("Cài ngày %@", d.ToString("d", Loc.Culture)));
        if (app.IsStore) parts.Add("Microsoft Store");
        DetailLine.Text = string.Join(" · ", parts);
        var scope = app.IsStore ? "Microsoft Store" : app.Scope == RegistryScope.User ? Loc.T("Cài cho riêng bạn") : Loc.T("Cài cho mọi người dùng");
        DetailLine.ToolTip = DetailLine.Text + Environment.NewLine + scope;
        DetailLocationRow.Visibility = string.IsNullOrEmpty(app.InstallLocation) ? Visibility.Collapsed : Visibility.Visible;
        DetailLocation.Text = app.InstallLocation?.AbbreviatingHome() ?? "";
        DetailLocation.ToolTip = app.InstallLocation;
        RevealButton.Visibility = app.InstallLocation is { } loc && Directory.Exists(loc) && !app.IsStore ? Visibility.Visible : Visibility.Collapsed;

        if (row.BlockedReason is { } reason)
        {
            Banners.Children.Add(Banner("lock.fill", Loc.F("Không gỡ được: %@.", reason), Loc.T("Mở Cài đặt Windows"), OpenWindowsSettings));
            ShowBody(null);
            return;
        }
        if (row.Caution is { } caution) Banners.Children.Add(Banner("exclamationmark.triangle", caution, null, null, Theme.ReviewBrush));
        if (app.IsStore) Banners.Children.Add(Banner("info.circle", Loc.T("App Microsoft Store: Windows tự xoá dữ liệu trong gói khi gỡ. Clean Boost kiểm tra lại sau khi gỡ."), null, null));

        var root = _vm.AppRoot(row);
        if (_vm.IsLoadingLeftovers(row) && root is null)
        {
            ShowBody(LeftoversLoading);
            return;
        }
        if (!_vm.HasReport(row))
        {
            ShowBody(null);
            return;
        }
        ShowBody(LeftoversView);
        BuildTiles(row, root);
        var hasLeftovers = root is not null && root.Children.Count > 0;
        NoLeftovers.Visibility = hasLeftovers ? Visibility.Collapsed : Visibility.Visible;
        Outline.Visibility = hasLeftovers ? Visibility.Visible : Visibility.Collapsed;
        SelectAllLeftovers.Visibility = hasLeftovers ? Visibility.Visible : Visibility.Collapsed;
        LeftoverCaption.Visibility = hasLeftovers ? Visibility.Visible : Visibility.Collapsed;
        if (root is not null)
        {
            var state = _vm.Selection.State(root.Id, _vm.Tree);
            SelectAllLeftovers.IsChecked = state switch { CheckState.On => true, CheckState.Off => false, _ => null };
            SelectAllLeftovers.Content = state == CheckState.On ? Loc.T("Bỏ chọn tất cả") : Loc.T("Chọn tất cả");
        }
        if (!ReferenceEquals(_outlineTree, _vm.Tree) || !ReferenceEquals(Outline.Selection, _vm.Selection) || !ReferenceEquals(_outlineRoot, root))
        {
            _outlineTree = _vm.Tree;
            _outlineRoot = root;
            Outline.Selection = _vm.Selection;
            Outline.Tree = _vm.Tree;
            Outline.Roots = root?.Children ?? [];
        }
    }

    private void ShowBody(FrameworkElement? body)
    {
        foreach (var e in new FrameworkElement[] { LeftoversLoading, NoFocus, LeftoversView }) e.Visibility = e == body ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTiles(AppRow row, Node? root)
    {
        Tiles.Children.Clear();
        var leftovers = root?.Size ?? ByteCount.Zero;
        Tiles.Children.Add(Tile("app", Loc.T("Ứng dụng"), row.App.Size > 0 ? ByteCount.Format(row.App.Size) : "—"));
        Tiles.Children.Add(Tile("doc.on.doc", Loc.T("File sót"), leftovers.Formatted));
        Tiles.Children.Add(Tile("checkmark.circle", Loc.T("Đã chọn"), _vm.SelectedBytes(row).Formatted));
    }

    private FrameworkElement Tile(string symbol, string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = Icons.Glyph(symbol), FontFamily = Icons.Font, FontSize = 13, Foreground = (Brush)FindResource("SecondaryText"), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("TileLabel") });
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock { Text = value, Style = (Style)FindResource("TileValue"), TextTrimming = TextTrimming.CharacterEllipsis });
        return stack;
    }

    private Border Banner(string symbol, string text, string? actionTitle, Action? action, Brush? iconBrush = null)
    {
        var dock = new DockPanel();
        dock.Children.Add(new TextBlock { Text = Icons.Glyph(symbol), FontFamily = Icons.Font, FontSize = 14, Foreground = iconBrush ?? Brushes.White, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top });
        DockPanel.SetDock(dock.Children[0], Dock.Left);
        if (actionTitle is not null && action is not null)
        {
            var b = new Button { Content = actionTitle, Style = (Style)FindResource("GlassButton"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => action();
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        dock.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        return new Border { CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("BannerBackground"), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 12, 0, 0), Child = dock };
    }

    private void UpdateFooter()
    {
        var pending = _vm.PendingRows;
        FooterTitle.Text = pending.Count == 0 ? Loc.T("Chưa chọn app nào") : Loc.F("Gỡ %lld ứng dụng · %@", pending.Count, _vm.TotalSelectedBytes.Formatted);
        DryRunPill.Visibility = _vm.DryRun ? Visibility.Visible : Visibility.Collapsed;
        var focused = _vm.Focused;
        ResetButton.Visibility = focused is { CanUninstall: true } && _vm.SelectedLeaves(focused).Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = pending.Count > 0 && !_vm.AnyCheckedLoading;
    }

    // MARK: Hành động danh sách

    private async void OnScan(object sender, RoutedEventArgs e) => await _vm.LoadAppsAsync();
    private void OnStopLoading(object sender, RoutedEventArgs e) => _vm.CancelLoading();
    private async void OnReload(object sender, RoutedEventArgs e) => await _vm.LoadAppsAsync();
    private void OnDeselect(object sender, RoutedEventArgs e) => _vm.ClearChecked();

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _vm.Search = SearchBox.Text;
    }

    private void OnAppFocused(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList) return;
        if (AppList.SelectedItem is AppRow row && !ReferenceEquals(row, _vm.Focused))
        {
            _updatingList = true;
            _vm.Focus(row);
            _updatingList = false;
        }
        else if (AppList.SelectedItem is null && _vm.Focused is { } f && _vm.VisibleRows.Contains(f))
        {
            _updatingList = true;
            AppList.SelectedItem = f;
            _updatingList = false;
        }
    }

    private void OnRowCheck(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRow row)
        {
            _vm.ToggleChecked(row);
            // Checkbox hiển thị theo view model, không theo trạng thái nội bộ.
            if (sender is CheckBox cb) cb.IsChecked = row.IsChecked;
        }
        e.Handled = true;
    }

    private void OnSelectAllApps(object sender, RoutedEventArgs e)
    {
        var checkable = _vm.CheckableVisible;
        _vm.SetCheckedVisible(!checkable.All(r => r.IsChecked));
    }

    private void OnSelectAllLeftovers(object sender, RoutedEventArgs e)
    {
        if (_vm.Focused is not { } row || _vm.AppRoot(row) is not { } root) return;
        _vm.Selection.Set(root.Id, _vm.Selection.State(root.Id, _vm.Tree) != CheckState.On, _vm.Tree);
    }

    private void OnFilterMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        void Header(string text)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = text, IsEnabled = false, FontSize = 11.5, FontWeight = FontWeights.SemiBold });
        }
        Header(Loc.T("Sắp xếp"));
        foreach (var key in new[] { AppSortKey.Name, AppSortKey.Size, AppSortKey.LastUsed })
        {
            var item = new MenuItem { Header = key.Title(), IsChecked = _vm.Sort == key };
            item.Click += (_, _) => _vm.Sort = key;
            menu.Items.Add(item);
        }
        Header(Loc.T("Lọc"));
        foreach (var f in new[] { AppFilter.All, AppFilter.Unused, AppFilter.Store, AppFilter.Desktop, AppFilter.Checked })
        {
            var item = new MenuItem { Header = f.Title, IsChecked = _vm.Filter == f };
            item.Click += (_, _) => _vm.Filter = f;
            menu.Items.Add(item);
        }
        var vendors = _vm.Vendors;
        if (vendors.Count > 0)
        {
            Header(Loc.T("Nhà phát triển"));
            foreach (var v in vendors)
            {
                var item = new MenuItem { Header = v.Title, IsChecked = _vm.Filter == v };
                item.Click += (_, _) => _vm.Filter = v;
                menu.Items.Add(item);
            }
        }
        menu.PlacementTarget = FilterButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private AppRow? _menuRow;

    private void OnAppMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuRow = AppList.SelectedItem as AppRow;
        AppMenuReveal.IsEnabled = _menuRow?.App.InstallLocation is { } d && Directory.Exists(d) && !_menuRow.App.IsStore;
    }

    private void OnRevealApp(object sender, RoutedEventArgs e) => NodeOutlineView.Reveal(_menuRow?.App.InstallLocation);
    private void OnRevealFocused(object sender, RoutedEventArgs e) => NodeOutlineView.Reveal(_vm.Focused?.App.InstallLocation);
    private void OnOpenWindowsSettings(object sender, RoutedEventArgs e) => OpenWindowsSettings();

    private static void OpenWindowsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, "Không mở được Cài đặt Windows: " + ex.Message);
        }
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (UninstallButton.IsEnabled) _vm.RequestUninstall();
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (_vm.Focused is { } row) _vm.RequestReset(row);
    }

    // MARK: Xác nhận

    private void BuildConfirm()
    {
        ConfirmCards.Children.Clear();
        ConfirmIcons.Children.Clear();
        if (_vm.Confirmation is not { } c) return;
        var icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var app in c.Apps.Take(5))
        {
            var img = AppIcons.For(app);
            icons.Children.Add(img is not null
                ? new Image { Source = img, Width = 52, Height = 52, Margin = new Thickness(4, 0, 4, 0) }
                : new TextBlock { Text = Icons.Glyph("app"), FontFamily = Icons.Font, FontSize = 44, Foreground = Brushes.White, Margin = new Thickness(4, 0, 4, 0) });
        }
        ConfirmIcons.Children.Add(icons);
        var names = string.Join(", ", c.Apps.Select(a => a.Name));
        ConfirmTitle.Text = c.ResetOnly ? Loc.F("Đặt lại %@", c.Apps[0].Name) : Loc.F("Gỡ %@", names);
        var total = (c.ResetOnly ? ByteCount.Zero : c.Apps.Sum(a => new ByteCount(a.Size))) + c.Leftovers.Sum(l => l.Size);
        ConfirmSubtitle.Text = Loc.F("Sẽ xử lý %lld mục, khoảng %@.", (c.ResetOnly ? 0 : c.Apps.Count) + c.LeftoverPlan.Items.Count, total.Formatted);
        var note = c.ResetOnly ? Loc.T("Chỉ xoá dữ liệu đã chọn, giữ lại ứng dụng. App sẽ như mới cài.") : null;
        if (c.AnyRunning) note = (note is null ? "" : note + " ") + Loc.T("App đang chạy sẽ được yêu cầu thoát trước khi gỡ.");
        ConfirmNote.Text = note ?? "";
        ConfirmNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        ConfirmNote.Foreground = c.AnyRunning ? Theme.ReviewBrush : (Brush)FindResource("SecondaryText");
        ConfirmOk.Content = c.ResetOnly ? Loc.T("Đặt lại") : UninstallAction;

        if (!c.ResetOnly)
        {
            var steps = new StackPanel();
            steps.Children.Add(CardHeader("checklist", Loc.T("Các bước")));
            var n = 1;
            foreach (var app in c.Apps)
            {
                var cmd = UninstallCommand.For(app, out _);
                var detail = cmd?.Kind switch
                {
                    UninstallKind.Store => Loc.T("Gỡ gói Microsoft Store của app"),
                    UninstallKind.WindowsInstaller => Loc.T("Gỡ bằng Windows Installer (msiexec)"),
                    _ => cmd?.Executable?.AbbreviatingHome() ?? "",
                };
                if (app.RequiresAdminToUninstall && cmd?.Kind != UninstallKind.Store) detail += " · " + Loc.T("Có thể hỏi quyền quản trị");
                steps.Children.Add(Step(n++, Loc.F("Chạy trình gỡ cài đặt của %@", app.Name), detail));
            }
            var leftoverSize = c.Leftovers.Sum(l => l.Size);
            steps.Children.Add(Step(n, Loc.T("Tìm lại file sót và cho bạn xem trước khi dọn"),
                c.Leftovers.Count > 0 ? Loc.F("%lld mục đã chọn sẽ được chọn sẵn, khoảng %@", c.Leftovers.Count, leftoverSize.Formatted) : Loc.T("Thư mục cài đặt còn lại, lối tắt hỏng, dữ liệu AppData")));
            ConfirmCards.Children.Add(Card(steps));
        }

        var plan = c.LeftoverPlan;
        if (plan.Items.Count > 0)
        {
            var stack = new StackPanel();
            stack.Children.Add(CardHeader(c.ResetOnly ? "trash" : "doc.on.doc", Loc.F("File sót đã chọn (%lld)", plan.Items.Count)));
            var permanent = plan.PermanentItems.Count();
            if (permanent > 0) stack.Children.Add(new TextBlock { Text = Loc.F("%lld mục sẽ bị xoá vĩnh viễn (không qua Thùng rác).", permanent), FontSize = 11.5, Foreground = Theme.ReviewBrush, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
            foreach (var i in plan.Items.Take(50)) stack.Children.Add(ItemRow(i.Title, i.Path ?? (i.Virtual as RegistryKeyItem)?.DisplayPath, i.ExpectedSize.Formatted));
            if (plan.Items.Count > 50) stack.Children.Add(new TextBlock { Text = Loc.F("… và %lld mục khác", plan.Items.Count - 50), FontSize = 11.5, Foreground = (Brush)FindResource("TertiaryText") });
            ConfirmCards.Children.Add(Card(stack));
        }
        if (plan.BlockedItems.Count > 0)
        {
            var stack = new StackPanel();
            stack.Children.Add(CardHeader("hand.raised.fill", Loc.F("Bị chặn vì lý do an toàn (%lld)", plan.BlockedItems.Count)));
            foreach (var b in plan.BlockedItems.Take(50)) stack.Children.Add(ItemRow(b.Title, b.Violation.LocalizedDescription, null));
            ConfirmCards.Children.Add(Card(stack));
        }
    }

    private Border Card(UIElement child) =>
        new() { Style = (Style)FindResource("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = child };

    private static StackPanel CardHeader(string symbol, string title)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(new TextBlock { Text = Icons.Glyph(symbol), FontFamily = Icons.Font, FontSize = 14, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        header.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        return header;
    }

    private FrameworkElement Step(int number, string title, string detail)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = number.ToString(Loc.Culture), FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        g.Children.Add(badge);
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            s.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = (Brush)FindResource("TertiaryText"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = detail, Margin = new Thickness(0, 1, 0, 0) });
        Grid.SetColumn(s, 1);
        g.Children.Add(s);
        return g;
    }

    private Grid ItemRow(string title, string? detail, string? size)
    {
        var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, FontSize = 13, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail) && !detail.EndsWith(title, StringComparison.OrdinalIgnoreCase))
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

    private void OnConfirmCancel(object sender, RoutedEventArgs e) => _vm.CancelConfirmation();

    private async void OnConfirm(object sender, RoutedEventArgs e) => await _vm.ConfirmAsync();

    // MARK: Đang chạy

    private void UpdateWorking()
    {
        WorkRing.Progress = _vm.WorkProgress;
        WorkRing.Label = _vm.CanStopWaiting ? Loc.T("đang gỡ") : Loc.T("đang dọn");
        WorkTitleText.Text = _vm.WorkTitle;
        WorkItemText.Text = _vm.WorkItem;
        WorkHint.Text = _vm.CanStopWaiting ? Loc.T("Làm theo các bước trong cửa sổ của trình gỡ (có thể nằm sau cửa sổ này). Clean Boost tự tiếp tục khi gỡ xong.") : "";
        WorkHint.Visibility = _vm.CanStopWaiting ? Visibility.Visible : Visibility.Collapsed;
        StopWaitingButton.Visibility = _vm.CanStopWaiting ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStopWaiting(object sender, RoutedEventArgs e) => _vm.StopWaiting();

    // MARK: Xem lại file sót

    private void BuildReview()
    {
        if (_vm.Review is not { } r) return;
        ReviewTitle.Text = r.Removed.Count == 0 ? Loc.T("File sót") : Loc.F("Đã gỡ %@", string.Join(", ", r.Removed.Select(a => a.Name)));
        ReviewSubtitle.Text = Loc.F("Trình gỡ để lại %lld mục, khoảng %@. Xem lại rồi dọn.", r.Tree.AllLeaves.Count(), r.Tree.TotalSize.Formatted);
        ReviewDryRun.Visibility = _vm.DryRun ? Visibility.Visible : Visibility.Collapsed;
        ReviewOutline.Selection = r.Selection;
        ReviewOutline.Tree = r.Tree;
        // Một app: hiện phẳng mọi mục (đã sắp theo mức tin cậy, nhãn mức tin cậy nằm ở chú thích) để thấy ngay những gì còn lại.
        ReviewOutline.Roots = r.Tree.Roots.Count == 1 ? r.Tree.Roots[0].RemovableLeaves().ToList() : r.Tree.Roots;
        r.Selection.Changed -= UpdateReviewSelection;
        r.Selection.Changed += UpdateReviewSelection;
        UpdateReviewSelection();
    }

    private void UpdateReviewSelection()
    {
        if (_vm.Review is not { } r) return;
        ReviewSelected.Text = Loc.F("Đã chọn %@", r.Selection.SelectedBytes.Formatted);
        ReviewSelectedDetail.Text = Loc.F("%@ mục · dung lượng là ước tính", r.Selection.SelectedCount.ToString("N0", Loc.Culture));
        var states = r.Tree.Roots.Select(root => r.Selection.State(root.Id, r.Tree)).ToList();
        bool? all = states.All(s => s == CheckState.Off) ? false : states.All(s => s == CheckState.On) ? true : null;
        ReviewSelectAll.IsChecked = all;
        ReviewSelectAll.Content = all == true ? Loc.T("Bỏ chọn tất cả") : Loc.T("Chọn tất cả");
        ReviewClean.IsEnabled = r.Selection.SelectedCount > 0;
        // Mục cần quyền quản trị (Program Files, ProgramData) do helper xoá vĩnh viễn; khoá registry có bản sao .reg.
        var permanent = r.Selection.SelectedLeaves(r.Tree).Count(l => l.RequiresRoot || l.Removal.Kind is RemovalKind.Delete or RemovalKind.DeleteContents);
        ReviewPermanent.Text = permanent > 0 ? Loc.F("%lld mục sẽ bị xoá vĩnh viễn (không qua Thùng rác).", permanent) : "";
        ReviewPermanent.Visibility = permanent > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnReviewSelectAll(object sender, RoutedEventArgs e)
    {
        if (_vm.Review is not { } r) return;
        var allOn = r.Tree.Roots.All(root => r.Selection.State(root.Id, r.Tree) == CheckState.On);
        r.Selection.SetMany(r.Tree.Roots.Select(root => root.Id), !allOn, r.Tree);
    }

    private async void OnReviewClean(object sender, RoutedEventArgs e)
    {
        if (ReviewClean.IsEnabled) await _vm.CleanReviewAsync();
    }

    private void OnReviewSkip(object sender, RoutedEventArgs e) => _vm.SkipReview();

    // MARK: Xong

    private void BuildDone()
    {
        if (_vm.Done is not { } d) return;
        DoneIcon.Text = Icons.Glyph(d.Success ? "checkmark.circle.fill" : "exclamationmark.circle.fill");
        DoneTitle.Text = d.Message;
        var failures = d.Failures;
        DoneFailBanner.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneFailText.Text = Loc.F("%lld mục chưa xong: ", failures.Count) + string.Join(", ", failures.Take(4).Select(f => f.Title));
        DoneFailures.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneFailureList.ItemsSource = failures.Take(30).Select(f => Tuple.Create(f.Title, f.Message)).ToList();
        DoneInUseBanner.Visibility = d.InUse > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneInUseText.Text = Loc.F("%lld mục đang được sử dụng nên đã bỏ qua. Hãy tắt app rồi thử lại.", d.InUse);
        DoneSettings.Visibility = failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDone(object sender, RoutedEventArgs e) => _vm.DismissDone();
}
