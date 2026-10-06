using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Features.SpaceLens;

namespace CleanBoost.App.Features.SpaceLens;

/// <summary>
/// Màn Space Lens (SpaceLensView của bản Mac): chọn ổ/thư mục, bản đồ sunburst bên trái, danh sách con theo dung lượng
/// bên phải (chọn, đi sâu), thanh dưới hiện dung lượng đã chọn với "Hiện trong File Explorer" và "Chuyển vào Thùng rác".
/// </summary>
public partial class SpaceLensView : UserControl, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private const int ListLimit = 400;
    private readonly SpaceLensViewModel _vm;
    private readonly AppEnvironment _env;
    private List<LensRow> _rows = [];
    private string? _rowsPath;
    private LensRow? _highlightedRow;
    private LensPhase? _shownPhase;
    private string? _crumbsKey;
    private Window? _window;
#if DEBUG
    private bool _debugApplied;
#endif

    public Accent Accent => Accent.SpaceLens;
    public bool IsBusy => _vm.IsBusy;
    public SpaceLensViewModel ViewModel => _vm;

    public SpaceLensView(AppEnvironment env, AppRouter router)
    {
        _env = env;
        InitializeComponent();
        Theme.Apply(this, Accent.SpaceLens);
        _vm = new SpaceLensViewModel(env, Dispatcher);

        IdleIcon.Text = Icons.Glyph(SidebarItem.SpaceLens.Symbol());
        IdleSubtitle.Text = Loc.T("Xem trực quan thư mục nào đang chiếm nhiều dung lượng nhất, đi sâu dần và dọn ngay trên bản đồ.");
        HomeText.Text = Loc.T("Thư mục người dùng");
        FolderGlyph.Text = Icons.Glyph("folder");
        FolderText.Text = Loc.T("Chọn thư mục…");
        StopButton.Content = Loc.T("Dừng");
        ScanRing.Label = Loc.T("đang quét");
        RescanButton.Content = Loc.T("Quét lại");
        ChooseButton.Content = Loc.T("Chọn khác");
        BackButton.ToolTip = Loc.T("Lên một cấp");
        NoticeClose.Content = Loc.T("Đóng");
        RevealButton.Content = Loc.T("Hiện trong File Explorer");
        TrashButton.Content = Loc.T("Chuyển vào Thùng rác");
        ErrorTitle.Text = Loc.T("Đã có lỗi");
        ErrorRetry.Content = Loc.T("Thử lại");

        Chart.HoverChanged += seg => _vm.Hovered = seg;
        Chart.SegmentClicked += index => _vm.Drill(index);
        Chart.CenterClicked += () => _vm.GoUp();

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.TreeChanged += () => ShowResults(animate: false);
        _vm.NavigationChanged += () => ShowResults(animate: true);
        _vm.SelectionChanged += UpdateSelection;

        router.SpaceLensPathRequested += path => Dispatcher.InvokeAsync(() => ScanRequested(path));
        // Kéo một thư mục/ổ từ File Explorer vào màn hình để quét.
        AllowDrop = true;
        DragOver += (_, e) =>
        {
            e.Effects = !_vm.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
        Drop += (_, e) =>
        {
            if (!_vm.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) ScanRequested(paths[0]);
        };
        IsVisibleChanged += (_, _) => _vm.SetActive(IsVisible);
        Loaded += (_, _) =>
        {
            if (_window is not null) return;
            _window = Window.GetWindow(this);
            if (_window is not null) _window.PreviewKeyDown += OnWindowKeyDown;
        };
        UpdatePhase();
    }

    // MARK: Bắt đầu quét

    /// <summary>Liên kết sâu / phím tắt: quét thư mục ghi đè (chụp ảnh kiểm thử), thư mục quét gần nhất, hoặc ổ hệ thống.</summary>
    public void AutoStart()
    {
        if (_vm.IsBusy) return;
        var target = Environment.GetEnvironmentVariable("CLEANBOOST_SPACELENS_PATH") is { Length: > 0 } overridePath ? overridePath
            : SpaceLensViewModel.LastPath is { Length: > 0 } last && Directory.Exists(last) ? last
            : KnownPaths.SystemDrive + "\\";
        _vm.Scan(target);
    }

    private void ScanRequested(string path)
    {
        if (File.Exists(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent) path = parent;
        if (Directory.Exists(path)) _vm.Scan(path);
    }

    private void OnVolumeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LensVolume v) _vm.Scan(v.Info.MountPoint);
    }

    private void OnHome(object sender, RoutedEventArgs e) => _vm.Scan(KnownPaths.UserHome);

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Chọn thư mục để xem bản đồ dung lượng"),
            Multiselect = false,
        };
        if (SpaceLensViewModel.LastPath is { Length: > 0 } last && Directory.Exists(last)) dialog.InitialDirectory = last;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && !string.IsNullOrEmpty(dialog.FolderName)) _vm.Scan(dialog.FolderName);
    }

    private void OnStop(object sender, RoutedEventArgs e) => _vm.Cancel();
    private void OnRescan(object sender, RoutedEventArgs e) => _vm.Rescan();
    private void OnChooseAnother(object sender, RoutedEventArgs e) => _vm.ChooseAnother();
    private void OnBack(object sender, RoutedEventArgs e) => _vm.GoUp();
    private void OnCloseNotice(object sender, RoutedEventArgs e) => _vm.Notice = null;

    /// <summary>Esc dừng quét; Alt+← hoặc Backspace lên một cấp (⌘← của bản Mac).</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible || e.OriginalSource is TextBox) return;
        if (e.Key == Key.Escape && _vm.Phase == LensPhase.Scanning)
        {
            _vm.Cancel();
            e.Handled = true;
        }
        else if (_vm.Phase == LensPhase.Results && _vm.Navigator.CanGoUp
                 && (e.Key == Key.Back || (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)))
        {
            _vm.GoUp();
            e.Handled = true;
        }
    }

    // MARK: Trạng thái

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SpaceLensViewModel.Phase):
                UpdatePhase();
                break;
            case nameof(SpaceLensViewModel.ScanItems):
            case nameof(SpaceLensViewModel.ScanBytes):
            case nameof(SpaceLensViewModel.ScanFraction):
                UpdateScanning();
                break;
            case nameof(SpaceLensViewModel.Notice):
                NoticeText.Text = _vm.Notice ?? "";
                NoticeBanner.Visibility = string.IsNullOrEmpty(_vm.Notice) ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(SpaceLensViewModel.IsCleaning):
                UpdateFooter();
                break;
            case nameof(SpaceLensViewModel.Hovered):
                HighlightRow(_vm.Hovered?.NodeIndex);
                break;
        }
    }

    private void UpdatePhase()
    {
        var phase = _vm.Phase;
        if (_shownPhase == phase) return;
        _shownPhase = phase;
        Show(IdlePanel, phase == LensPhase.Idle);
        Show(ScanningPanel, phase == LensPhase.Scanning);
        Show(ResultsPanel, phase == LensPhase.Results);
        Show(ErrorPanel, phase == LensPhase.Error);
        switch (phase)
        {
            case LensPhase.Idle:
                LoadVolumes();
                break;
            case LensPhase.Scanning:
                UpdateScanning();
                break;
            case LensPhase.Error:
                ErrorText.Text = _vm.ErrorMessage;
                break;
        }
    }

    /// <summary>Chuyển pha mờ dần (như <c>.transition(.opacity)</c> của bản Mac).</summary>
    private static void Show(UIElement panel, bool visible)
    {
        var was = panel.Visibility == Visibility.Visible;
        panel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible && !was) panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    private async void LoadVolumes()
    {
        var fs = _env.FileSystem;
        var volumes = await Task.Run(() =>
        {
            var list = new List<LensVolume>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                    if (fs.Volume(d.RootDirectory.FullName) is { Total: > 0 } info) list.Add(new LensVolume(info));
                }
                catch
                {
                    // ổ không sẵn sàng: bỏ qua
                }
            }
            return list.OrderByDescending(v => v.Info.IsSystem).ThenBy(v => v.Info.MountPoint, StringComparer.OrdinalIgnoreCase).ToList();
        });
        VolumeList.ItemsSource = volumes;
    }

    private void UpdateScanning()
    {
        ScanTitle.Text = Loc.F("Đang quét %@", _vm.ScanRoot.AbbreviatingHome());
        ScanItemsText.Text = Loc.F("%@ mục", _vm.ScanItems.ToString("N0", Loc.Culture));
        ScanBytesText.Text = ByteCount.Format(_vm.ScanBytes);
        var determinate = _vm.ScanFraction is not null;
        ScanRing.Visibility = determinate ? Visibility.Visible : Visibility.Collapsed;
        ScanSpinner.Visibility = determinate ? Visibility.Collapsed : Visibility.Visible;
        if (_vm.ScanFraction is { } f) ScanRing.Progress = f;
    }

    // MARK: Kết quả

    private void ShowResults(bool animate)
    {
        if (_vm.Phase != LensPhase.Results) return;
        var tree = _vm.Tree;
        var current = _vm.CurrentIndex;
        Chart.Update(tree, _vm.Segments, _vm.CurrentTitle, tree.Size(current), _vm.Navigator.CanGoUp, animate);
        var empty = _vm.Segments.Count == 0;
        EmptyChart.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyChartText.Text = (tree.Flags(current) & DiskNodeFlags.Unreadable) != 0 && !tree.HasChildren(current)
            ? Loc.T("Không đọc được thư mục này (cần quyền quản trị)")
            : Loc.T("Thư mục này không chiếm dung lượng");
        BuildCrumbs();
        BackButton.IsEnabled = _vm.Navigator.CanGoUp;
        ScanTimeText.Text = Loc.F("Quét trong %@ giây", _vm.ScanDuration.ToString("0.0", Loc.Culture));
        BuildRows();
        UpdateSelection();
#if DEBUG
        ApplyDebugState();
#endif
    }

    private void BuildCrumbs()
    {
        var crumbs = _vm.Navigator.Breadcrumb(_vm.Tree, _vm.RootTitle);
        var key = string.Join("|", crumbs.Select(c => c.Title.Length)) + string.Join("/", crumbs.Select(c => c.Title));
        if (key == _crumbsKey) return;
        _crumbsKey = key;
        Crumbs.Children.Clear();
        for (var k = 0; k < crumbs.Count; k++)
        {
            var crumb = crumbs[k];
            var last = k == crumbs.Count - 1;
            var button = new Button
            {
                Style = (Style)FindResource("CrumbButton"),
                Content = new TextBlock
                {
                    Text = crumb.Title, FontSize = 15, FontWeight = last ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = last ? Brushes.White : (Brush)FindResource("SecondaryText"),
                    MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
                },
                Tag = crumb.Level,
            };
            button.Click += (_, _) => _vm.GoTo(crumb.Level);
            Crumbs.Children.Add(button);
            if (!last)
            {
                Crumbs.Children.Add(new TextBlock
                {
                    Text = Icons.Glyph("chevron.right"), FontFamily = Icons.Font, FontSize = 9, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource("TertiaryText"), Margin = new Thickness(2, 1, 2, 0),
                });
            }
        }
        CrumbScroller.UpdateLayout();
        CrumbScroller.ScrollToRightEnd();
    }

    /// <summary>
    /// Danh sách con (tối đa 400 dòng như bản Mac). Cùng thư mục và cùng thứ tự tên thì cập nhật dòng tại chỗ để danh sách
    /// không nhảy về đầu khi cây được làm mới.
    /// </summary>
    private void BuildRows()
    {
        var tree = _vm.Tree;
        var current = _vm.CurrentIndex;
        var path = tree.Path(current);
        var sorted = tree.SortedChildren(current);
        var shown = Math.Min(sorted.Length, ListLimit);
        var total = tree.Size(current);
        var sameDirectory = string.Equals(path, _rowsPath, StringComparison.OrdinalIgnoreCase) && _rows.Count == shown;
        if (sameDirectory)
        {
            for (var k = 0; k < shown && sameDirectory; k++)
            {
                sameDirectory = string.Equals(tree.Name(sorted[k]), _rows[k].Name, StringComparison.Ordinal);
            }
        }
        if (sameDirectory)
        {
            for (var k = 0; k < shown; k++) _rows[k].Apply(tree, sorted[k], total);
        }
        else
        {
            // Cùng thư mục nhưng thứ tự đổi (cập nhật trực tiếp): giữ vị trí cuộn; thư mục khác: về đầu danh sách.
            var keepOffset = string.Equals(path, _rowsPath, StringComparison.OrdinalIgnoreCase);
            var scroller = FindScrollViewer(Rows);
            var offset = scroller?.VerticalOffset ?? 0;
            var rows = new List<LensRow>(shown);
            for (var k = 0; k < shown; k++) rows.Add(new LensRow(tree, sorted[k], total));
            _rows = rows;
            _rowsPath = path;
            _highlightedRow = null;
            Rows.ItemsSource = rows;
            if (scroller is not null)
            {
                if (keepOffset) Dispatcher.InvokeAsync(() => scroller.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
                else scroller.ScrollToTop();
            }
        }
        foreach (var row in _rows) row.IsChecked = _vm.IsSelected(row.Path);
        CountText.Text = Loc.F("%@ mục", sorted.Length.ToString("N0", Loc.Culture));
        MoreText.Text = sorted.Length > ListLimit ? Loc.F("… và %lld mục nhỏ hơn", sorted.Length - ListLimit) : "";
        MoreText.Visibility = sorted.Length > ListLimit ? Visibility.Visible : Visibility.Collapsed;
        SelectAll.IsEnabled = sorted.Length > 0;
        HighlightRow(_vm.Hovered?.NodeIndex);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    private void HighlightRow(int? index)
    {
        var row = index is { } i ? _rows.FirstOrDefault(r => r.Index == i) : null;
        if (ReferenceEquals(row, _highlightedRow)) return;
        if (_highlightedRow is not null) _highlightedRow.IsHighlighted = false;
        _highlightedRow = row;
        if (row is not null) row.IsHighlighted = true;
    }

    private void UpdateSelection()
    {
        foreach (var row in _rows) row.IsChecked = _vm.IsSelected(row.Path);
        var children = _vm.Phase == LensPhase.Results ? _vm.Tree.ChildCount(_vm.CurrentIndex) : 0;
        var selected = children == 0 ? 0 : _vm.SelectedAmongCurrent();
        bool? state = selected == 0 ? false : selected >= children ? true : null;
        SelectAll.IsChecked = state;
        SelectAll.Content = state == true ? Loc.T("Bỏ chọn tất cả") : Loc.T("Chọn tất cả");
        UpdateFooter();
    }

    private void UpdateFooter()
    {
        var count = _vm.SelectionCount;
        SelectedText.Text = Loc.F("Đã chọn %@", ByteCount.Format(_vm.Phase == LensPhase.Results ? _vm.SelectedBytes : 0));
        SelectedDetail.Text = Loc.F("%@ mục · dung lượng là ước tính", count.ToString("N0", Loc.Culture));
        RevealButton.IsEnabled = count > 0;
        TrashButton.IsEnabled = count > 0 && !_vm.IsCleaning;
        CleaningSpinner.Visibility = _vm.IsCleaning ? Visibility.Visible : Visibility.Collapsed;
        DryRunPill.Visibility = _vm.DryRun ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: Dòng danh sách

    private static LensRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as LensRow;

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is { HasChildren: true } row)
        {
            Chart.HighlightNode(null);
            _vm.Drill(row.Index);
        }
    }

    private void OnRowCheck(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _vm.Toggle(row.Index);
        row.IsChecked = _vm.IsSelected(row.Path);
        e.Handled = true;
    }

    private void OnRowEnter(object sender, MouseEventArgs e)
    {
        if (RowOf(sender) is { } row) Chart.HighlightNode(row.Index);
    }

    private void OnRowLeave(object sender, MouseEventArgs e) => Chart.HighlightNode(null);

    private void OnRowContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } || RowOf(sender) is not { } row) return;
        menu.Items.Clear();
        menu.Items.Add(MenuItem(Loc.T("Hiện trong File Explorer"), () => ExplorerReveal.Reveal([row.Path])));
        menu.Items.Add(MenuItem(Loc.T("Sao chép đường dẫn"), () =>
        {
            try { Clipboard.SetText(row.Path); } catch { }
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(_vm.IsSelected(row.Path) ? Loc.T("Bỏ chọn") : Loc.T("Chọn"), () =>
        {
            _vm.Toggle(row.Index);
        }));
        if (row.HasChildren) menu.Items.Add(MenuItem(Loc.T("Mở thư mục này"), () => _vm.Drill(row.Index)));
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        var children = _vm.Tree.ChildCount(_vm.CurrentIndex);
        var allSelected = children > 0 && _vm.SelectedAmongCurrent() >= children;
        _vm.SetCurrentChildrenSelected(!allSelected);
    }

    // MARK: Hiện trong File Explorer / Thùng rác

    private void OnReveal(object sender, RoutedEventArgs e) => ExplorerReveal.Reveal(_vm.Selection.ToList());

    private async void OnTrash(object sender, RoutedEventArgs e)
    {
        if (_vm.IsCleaning) return;
        TrashButton.IsEnabled = false;
        CleanPlan? plan;
        try
        {
            plan = await _vm.MakePlanAsync();
        }
        finally
        {
            UpdateFooter();
        }
        if (plan is null) return;
        var owner = Window.GetWindow(this);
        if (plan.Items.Count == 0)
        {
            MessageDialog.Show(owner, Loc.T("Không thể chuyển vào Thùng rác"), BlockedText(plan, leadingBreak: false), Loc.T("Đóng"), accent: Accent.SpaceLens);
            return;
        }
        var permanent = plan.PermanentItems.Count();
        var allPermanent = permanent == plan.Items.Count;
        var text = allPermanent
            ? Loc.F("%lld mục, khoảng %@.", plan.Items.Count, plan.TotalSize.Formatted)
            : Loc.F("%lld mục, khoảng %@. Có thể khôi phục từ Thùng rác hoặc màn Lịch sử.", plan.Items.Count, plan.TotalSize.Formatted);
        if (permanent > 0) text += "\n\n" + Loc.F("%lld mục sẽ bị xoá vĩnh viễn (không qua Thùng rác).", permanent);
        if (plan.BlockedItems.Count > 0) text += BlockedText(plan, leadingBreak: true);
        // Có mục xoá vĩnh viễn thì tiêu đề và nút nói rõ là xoá, không phải "chuyển vào Thùng rác".
        var title = permanent > 0 ? Loc.T("Xoá các mục đã chọn?") : Loc.T("Chuyển vào Thùng rác?");
        var action = permanent > 0 ? Loc.T("Xoá") : Loc.T("Chuyển vào Thùng rác");
        if (!MessageDialog.Show(owner, title, text, action, Loc.T("Huỷ"), destructive: true, accent: Accent.SpaceLens)) return;
        await _vm.RunPlanAsync(plan, AskPermanentDelete);
    }

    /// <summary>"k mục bị chặn vì lý do an toàn:" và tối đa 5 dòng "• tên — lý do".</summary>
    private static string BlockedText(CleanPlan plan, bool leadingBreak)
    {
        var header = Loc.F("\n\n%lld mục bị chặn vì lý do an toàn:\n", plan.BlockedItems.Count);
        if (!leadingBreak) header = header.TrimStart('\n');
        var lines = plan.BlockedItems.Take(5).Select(b => "• " + b.Title + " — " + BlockReason(b.Violation));
        var text = header + string.Join("\n", lines);
        if (plan.BlockedItems.Count > 5) text += "\n" + Loc.F("… và %lld mục khác", plan.BlockedItems.Count - 5);
        return text;
    }

    private static string BlockReason(PathPolicy.Violation v) => v.Kind switch
    {
        PathPolicy.ViolationKind.ProtectedDirectory => Loc.T("thư mục được bảo vệ"),
        PathPolicy.ViolationKind.ForbiddenZone => Loc.T("vùng hệ thống không được xoá"),
        PathPolicy.ViolationKind.OutsideAllowedRoots => Loc.T("trỏ ra ngoài thư mục đang xem"),
        PathPolicy.ViolationKind.ReadOnlyVolume => Loc.T("ổ chỉ đọc"),
        PathPolicy.ViolationKind.NetworkPath => Loc.T("ổ mạng"),
        PathPolicy.ViolationKind.NeedsAdminHelper or PathPolicy.ViolationKind.NotInRootAllowlist or PathPolicy.ViolationKind.NotOwnedByUser => Loc.T("cần quyền quản trị"),
        _ => Loc.T("không an toàn"),
    };

    private Task<bool> AskPermanentDelete(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        var ok = MessageDialog.Show(Window.GetWindow(this), Loc.T("Không thể chuyển vào Thùng rác"),
            Loc.F("\"%@\" nằm trên ổ không có Thùng rác (ổ ngoài hoặc ổ mạng). Bạn có muốn xoá vĩnh viễn không? Thao tác này không khôi phục được.", name),
            Loc.T("Xoá vĩnh viễn"), Loc.T("Bỏ qua"), destructive: true);
        return Task.FromResult(ok);
    }

#if DEBUG
    /// <summary>
    /// Trạng thái dựng sẵn cho ảnh chụp kiểm thử: <c>CLEANBOOST_SPACELENS_DRILL</c> (tên thư mục con, cách nhau bằng <c>\</c>),
    /// <c>CLEANBOOST_SPACELENS_HOVER=1</c> (nổi cung lớn nhất), <c>CLEANBOOST_SPACELENS_SELECT=n</c> (chọn n dòng đầu).
    /// </summary>
    private void ApplyDebugState()
    {
        if (_debugApplied || !DebugSnapshot.IsEnabled) return;
        _debugApplied = true;
        if (Environment.GetEnvironmentVariable("CLEANBOOST_SPACELENS_DRILL") is { Length: > 0 } drill)
        {
            var index = _vm.Tree.IndexForComponents(drill.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries));
            if (index > 0) _vm.Drill(index);
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("CLEANBOOST_SPACELENS_SELECT"), out var n))
        {
            foreach (var row in _rows.Skip(1).Take(n)) _vm.Toggle(row.Index);
        }
        if (Environment.GetEnvironmentVariable("CLEANBOOST_SPACELENS_HOVER") == "1" && _vm.Segments.FirstOrDefault(s => s.Depth == 1) is { NodeIndex: { } first })
        {
            // Đợi biểu đồ được đo kích thước rồi mới đặt con trỏ giả.
            Dispatcher.InvokeAsync(() => Chart.SimulatePointer(first), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
    }
#endif
}

/// <summary>Mở File Explorer và chọn các mục (gom theo thư mục cha, mỗi thư mục một cửa sổ, tối đa 8).</summary>
internal static class ExplorerReveal
{
    public static void Reveal(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            NodeOutlineView.Reveal(paths[0]);
            return;
        }
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(p.TrimEnd('\\')) ?? "", StringComparer.OrdinalIgnoreCase).Take(8))
        {
            if (!OpenAndSelect(group.Key, group.ToList())) NodeOutlineView.Reveal(group.First());
        }
    }

    private static bool OpenAndSelect(string folder, IReadOnlyList<string> items)
    {
        var folderPidl = ILCreateFromPathW(folder);
        if (folderPidl == IntPtr.Zero) return false;
        var pidls = items.Select(ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
        try
        {
            return pidls.Length > 0 && SHOpenFolderAndSelectItems(folderPidl, (uint)pidls.Length, pidls, 0) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var p in pidls) ILFree(p);
            ILFree(folderPidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr[] apidl, uint flags);
}
