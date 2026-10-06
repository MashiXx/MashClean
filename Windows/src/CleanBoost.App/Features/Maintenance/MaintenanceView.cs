using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Metrics;
using CleanBoost.Features.Maintenance;

namespace CleanBoost.App.Features.Maintenance;

/// <summary>
/// Màn Bảo trì (MaintenanceView của bản Mac): trạng thái hệ thống, danh sách tác vụ với gợi ý "Nên chạy", lần chạy gần nhất,
/// chạy lần lượt các tác vụ đã chọn với trạng thái từng dòng. Tác vụ lâu hoặc gây gián đoạn được hỏi lại trước khi chạy.
/// </summary>
public sealed class MaintenanceView : Grid, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private readonly MaintenanceViewModel _vm;
    private readonly Button _refreshButton = new();
    private readonly StackPanel _banners = new();
    private readonly Border _tilesPanel;
    private readonly UniformGrid _tiles = new() { Rows = 1 };
    private readonly ScrollViewer _list;
    private readonly FrameworkElement _loading;
    private readonly CheckBox _selectAll;
    private readonly TextBlock _selectedText = new() { Style = Res<Style>("SecondaryBodyText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly Button _runButton;

    public Accent Accent => Accent.Maintenance;
    public bool IsBusy => _vm.IsBusy;
    public MaintenanceViewModel ViewModel => _vm;

    public MaintenanceView(AppEnvironment env)
    {
        _vm = new MaintenanceViewModel(env);
        Margin = new Thickness(24);
        Theme.Apply(this, Accent.Maintenance);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Tiêu đề
        var header = new DockPanel { LastChildFill = true };
        _refreshButton.Style = Res<Style>("GlassButton");
        _refreshButton.VerticalAlignment = VerticalAlignment.Top;
        _refreshButton.Click += async (_, _) => await _vm.RefreshAsync();
        DockPanel.SetDock(_refreshButton, Dock.Right);
        header.Children.Add(_refreshButton);
        var titles = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        titles.Children.Add(new TextBlock { Text = Loc.T("Bảo trì"), Style = Res<Style>("TitleText") });
        titles.Children.Add(new TextBlock
        {
            Text = Loc.T("Chạy các tác vụ giúp máy ổn định hơn. Chỉ chạy khi cần; Clean Boost gợi ý dựa trên trạng thái hệ thống."),
            Style = Res<Style>("SecondaryBodyText"), Margin = new Thickness(0, 4, 0, 0), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
        });
        header.Children.Add(titles);
        Children.Add(header);

        // Thông báo
        _banners.Margin = new Thickness(0, 4, 0, 0);
        SetRow(_banners, 1);
        Children.Add(_banners);

        // Trạng thái hệ thống
        _tilesPanel = new Border { CornerRadius = new CornerRadius(14), Background = Theme.Frozen(Color.FromArgb(0x2E, 0, 0, 0)), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 14, 0, 0), Child = _tiles, Visibility = Visibility.Collapsed };
        SetRow(_tilesPanel, 2);
        Children.Add(_tilesPanel);

        // Danh sách tác vụ
        var rows = new StackPanel();
        foreach (var row in _vm.Rows)
        {
            rows.Children.Add(new TaskRowView(row, _vm) { Margin = new Thickness(0, 0, 0, 8) });
        }
        _list = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 0, 4, 0), Visibility = Visibility.Collapsed };
        SetRow(_list, 3);
        Children.Add(_list);
        _loading = new Spinner { Width = 36, Height = 36, Thickness = 3.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        SetRow(_loading, 3);
        Children.Add(_loading);

        // Chân màn
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        _runButton = new Button { Style = Res<Style>("PrimaryButton"), IsDefault = true, MinWidth = 96 };
        _runButton.Click += async (_, _) => await RunSelectedAsync();
        DockPanel.SetDock(_runButton, Dock.Right);
        footer.Children.Add(_runButton);
        if (_vm.DryRun)
        {
            var dry = new Border { Style = Res<Style>("PillBorder"), Background = Theme.Frozen(Color.FromArgb(0x40, 0xFF, 0xCC, 0x00)), Margin = new Thickness(0, 0, 12, 0) };
            dry.Child = new TextBlock { Text = "DRY RUN", Style = Res<Style>("PillText"), Foreground = Theme.ReviewBrush };
            DockPanel.SetDock(dry, Dock.Right);
            footer.Children.Add(dry);
        }
        _selectAll = new CheckBox { Style = Res<Style>("TriCheck"), IsThreeState = true, Foreground = Brushes.White, FontSize = 13 };
        _selectAll.Click += (_, _) => _vm.SetAll(_vm.SelectedCount < _vm.Rows.Count);
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_selectAll);
        left.Children.Add(_selectedText);
        footer.Children.Add(left);
        SetRow(footer, 4);
        Children.Add(footer);

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.Helper.StatusChanged += _ => Dispatcher.InvokeAsync(UpdateBanners);
        Loaded += (_, _) =>
        {
            if (_vm.Status is null && !_vm.IsLoading) _ = _vm.RefreshAsync();
        };
        IsVisibleChanged += (_, e) =>
        {
            // Quay lại màn: đọc lại trạng thái (RAM, ổ…) nếu đã cũ.
            if (e.NewValue is true && !_vm.IsBusy && _vm.Status is not null && DateTime.UtcNow - _vm.LastRefresh > TimeSpan.FromSeconds(30)) _ = _vm.RefreshAsync();
        };
        MaintenanceRequests.SetHandler(task => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = HandleRequestAsync(task)));
        UpdateAll();
    }

    public void AutoStart()
    {
        if (!_vm.IsBusy) _ = _vm.RefreshAsync();
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MaintenanceViewModel.Status):
                UpdateTiles();
                UpdateAll();
                break;
            case nameof(MaintenanceViewModel.SelectedCount):
            case nameof(MaintenanceViewModel.IsLoading):
            case nameof(MaintenanceViewModel.IsRunning):
                UpdateAll();
                break;
        }
    }

    private void UpdateAll()
    {
        var loaded = _vm.Status is not null;
        _list.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        _tilesPanel.Visibility = loaded ? Visibility.Visible : Visibility.Collapsed;
        _loading.Visibility = loaded ? Visibility.Collapsed : Visibility.Visible;

        var refresh = new StackPanel { Orientation = Orientation.Horizontal };
        if (_vm.IsLoading) refresh.Children.Add(new Spinner { Width = 12, Height = 12, Thickness = 1.6, Margin = new Thickness(0, 0, 7, 0) });
        else refresh.Children.Add(new Icon { Symbol = "arrow.clockwise", Size = 12, Foreground = Brushes.White, Margin = new Thickness(0, 0, 7, 0) });
        refresh.Children.Add(new TextBlock { Text = Loc.T("Làm mới") });
        _refreshButton.Content = refresh;
        _refreshButton.IsEnabled = !_vm.IsBusy;

        var count = _vm.SelectedCount;
        var total = _vm.Rows.Count;
        _selectAll.IsChecked = count == 0 ? false : count >= total ? true : null;
        _selectAll.Content = count >= total ? Loc.T("Bỏ chọn tất cả") : Loc.T("Chọn tất cả");
        _selectAll.IsEnabled = !_vm.IsRunning && loaded;
        _selectedText.Text = Loc.F("%lld tác vụ được chọn", count);
        _runButton.Content = _vm.IsRunning ? Loc.T("Đang chạy…") : Loc.T("Chạy");
        _runButton.IsEnabled = count > 0 && !_vm.IsRunning && loaded;
        UpdateBanners();
    }

    // MARK: Thông báo

    private void UpdateBanners()
    {
        _banners.Children.Clear();
        if (_vm.Status is { RebootPending: true })
        {
            _banners.Children.Add(Banner("exclamationmark.triangle.fill",
                Loc.T("Windows đang chờ khởi động lại để hoàn tất cập nhật. Hãy khởi động lại trước khi dọn WinSxS hoặc sửa file hệ thống."), null, null));
        }
        var needsAdmin = _vm.Rows.Any(r => r.IsSelected && r.Task.RequiresAdmin());
        var helper = _vm.Helper.Status;
        if (needsAdmin && !_vm.DryRun && !_vm.IsRunning && helper != HelperStatus.Running)
        {
            var text = helper switch
            {
                HelperStatus.Declined => Loc.T("Bạn đã không cấp quyền quản trị lần trước. Tác vụ có biểu tượng khiên cần quyền này để chạy."),
                HelperStatus.NotFound => Loc.T("Không tìm thấy CleanBoost.Helper.exe cạnh ứng dụng nên tác vụ cần quyền quản trị sẽ không chạy được."),
                _ => Loc.T("Tác vụ có biểu tượng khiên cần quyền quản trị: Windows sẽ hỏi xác nhận (UAC) một lần khi bắt đầu chạy."),
            };
            Action? action = helper == HelperStatus.NotFound ? null : () => _ = _vm.Helper.EnsureRunningAsync();
            _banners.Children.Add(Banner("lock.shield", text, action is null ? null : Loc.T("Cấp quyền"), action));
        }
        _banners.Visibility = _banners.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>NoticeBanner: nền đen 0.25 bo 10, biểu tượng vàng, chữ trắng, nút kính tuỳ chọn.</summary>
    private static Border Banner(string symbol, string text, string? actionTitle, Action? action)
    {
        var dock = new DockPanel();
        dock.Children.Add(new Icon { Symbol = symbol, Size = 15, Foreground = Theme.ReviewBrush, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(dock.Children[0], Dock.Left);
        if (actionTitle is not null && action is not null)
        {
            var button = new Button { Content = actionTitle, Style = Res<Style>("GlassButton"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => action();
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
        }
        dock.Children.Add(new TextBlock { Text = text, Style = Res<Style>("BodyText"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { CornerRadius = new CornerRadius(10), Background = Theme.Frozen(Color.FromArgb(0x40, 0, 0, 0)), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 10, 0, 0), Child = dock };
    }

    // MARK: Ô trạng thái

    private void UpdateTiles()
    {
        _tiles.Children.Clear();
        if (_vm.Status is not { } s) return;
        var pressure = s.Memory.Total > 0 ? MemorySample.PressureTitle(s.Memory.Pressure) : "";
        _tiles.Children.Add(StatTile("memorychip", Loc.T("RAM đang dùng"), s.Memory.Total > 0 ? s.MemoryLoadPercent + "%" : "—",
            s.Memory.Total > 0 ? Loc.F("%@ / %@ · %@", ByteCount.Format(s.Memory.Used), ByteCount.Format(s.Memory.Total), pressure) : null,
            s.Memory.Pressure >= MemoryPressure.Warning ? Theme.ReviewBrush : null));
        _tiles.Children.Add(StatTile("internaldrive", Loc.F("Ổ %@ còn trống", s.SystemDrive), s.SystemDriveFree is { } free ? ByteCount.Format(free) : "—",
            s.SystemDriveTotal is { } total ? Loc.F("trên tổng %@", ByteCount.Format(total)) : null, s.LowDiskSpace ? Theme.ReviewBrush : null));
        _tiles.Children.Add(StatTile("clock", Loc.T("Thời gian hoạt động"), MaintenanceStatus.FormatDuration(s.Uptime),
            s.RebootPending ? Loc.T("Đang chờ khởi động lại") : Loc.T("Từ lần khởi động gần nhất"), s.RebootPending ? Theme.ReviewBrush : null));
        var (search, searchDetail) = s.SearchService switch
        {
            SearchServiceState.Running => (Loc.T("Đang chạy"), Loc.T("Đang lập chỉ mục bình thường")),
            SearchServiceState.Stopped => (Loc.T("Đã dừng"), Loc.T("Dịch vụ không chạy")),
            SearchServiceState.Disabled => (Loc.T("Đã tắt"), Loc.T("Dịch vụ bị tắt")),
            SearchServiceState.Missing => (Loc.T("Không có"), Loc.T("Máy không có dịch vụ này")),
            _ => ("—", (string?)null),
        };
        _tiles.Children.Add(StatTile("magnifyingglass", "Windows Search", search, searchDetail,
            s.SearchService is SearchServiceState.Stopped ? Theme.ReviewBrush : null));
    }

    /// <summary>StatTile: biểu tượng + nhãn (caption), giá trị 22 đậm, dòng phụ.</summary>
    private static FrameworkElement StatTile(string symbol, string label, string value, string? detail, Brush? accent)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new Icon { Symbol = symbol, Size = 12, Foreground = Res<Brush>("SecondaryText"), Margin = new Thickness(0, 0, 6, 0) });
        top.Children.Add(new TextBlock { Text = label, Style = Res<Style>("CaptionText"), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(top);
        stack.Children.Add(new TextBlock
        {
            Text = value, FontFamily = AppFonts.Display, FontSize = 22, FontWeight = FontWeights.Bold, Foreground = accent ?? Brushes.White,
            Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        System.Windows.Documents.Typography.SetNumeralAlignment(stack.Children[^1], FontNumeralAlignment.Tabular);
        if (detail is not null)
        {
            stack.Children.Add(new TextBlock { Text = detail, Style = Res<Style>("TertiaryCaptionText"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        }
        return stack;
    }

    // MARK: Chạy

    private Window? Owner => Window.GetWindow(this);

    /// <summary>"Chạy": hỏi lại nếu có tác vụ lâu hoặc gây gián đoạn, rồi chạy lần lượt.</summary>
    private async Task RunSelectedAsync()
    {
        var tasks = _vm.SelectedTasks;
        if (tasks.Count == 0 || _vm.IsRunning) return;
        var heavy = tasks.Where(t => t.NeedsConfirmation()).ToList();
        if (heavy.Count > 0)
        {
            var title = tasks.Count == 1 ? Loc.F("Chạy “%@”?", tasks[0].Title()) : Loc.F("Chạy %lld tác vụ bảo trì?", tasks.Count);
            var lines = heavy.Count == 1 && tasks.Count == 1
                ? heavy[0].Description() + "\n\n" + heavy[0].Impact()
                : Loc.T("Một số tác vụ mất nhiều thời gian hoặc gây gián đoạn:") + "\n" + string.Join("\n", heavy.Select(t => "• " + t.Title() + ": " + t.Impact()));
            if (!MessageDialog.Show(Owner, title, lines + AdminNote(tasks), Loc.T("Chạy"), Loc.T("Huỷ"), destructive: heavy.Any(t => t.IsIrreversible()), accent: Accent.Maintenance))
                return;
        }
        await _vm.RunAsync(tasks);
    }

    /// <summary>Yêu cầu từ liên kết sâu hoặc khay: chọn đúng tác vụ đó và luôn hỏi trước khi chạy.</summary>
    private async Task HandleRequestAsync(MaintenanceTaskName task)
    {
        if (_vm.IsRunning)
        {
            Log.Info(LogCategory.UI, $"Bỏ qua yêu cầu bảo trì {task.RawValue()}: đang chạy tác vụ khác");
            return;
        }
        _vm.SelectOnly(task);
        if (_vm.Status is null) _ = _vm.RefreshAsync();
        var message = task.Description() + "\n\n" + task.Impact() + AdminNote([task]);
        if (!MessageDialog.Show(Owner, Loc.F("Chạy “%@”?", task.Title()), message, Loc.T("Chạy"), Loc.T("Huỷ"), destructive: task.IsIrreversible(), accent: Accent.Maintenance))
            return;
        await _vm.RunAsync([task]);
    }

    private string AdminNote(IEnumerable<MaintenanceTaskName> tasks) =>
        !_vm.DryRun && _vm.Helper.Status != HelperStatus.Running && tasks.Any(t => t.RequiresAdmin())
            ? "\n\n" + Loc.T("Windows sẽ hỏi quyền quản trị (UAC) một lần trước khi chạy.")
            : "";
}

/// <summary>Một dòng tác vụ: ô chọn, biểu tượng, tên + nhãn, mô tả, lý do gợi ý, ảnh hưởng, lần chạy gần nhất, trạng thái chạy.</summary>
internal sealed class TaskRowView : Border
{
    private static readonly Brush RowBackground = Theme.Frozen(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowHover = Theme.Frozen(Color.FromArgb(0x17, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowSelected = Theme.Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowStroke = Theme.Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowSelectedStroke = Theme.Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Tertiary = Theme.Frozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Secondary = Theme.Frozen(Color.FromArgb(0xC4, 0xFF, 0xFF, 0xFF));

    private readonly MaintenanceTaskRow _row;
    private readonly MaintenanceViewModel _vm;
    private readonly CheckBox _check = new();
    private readonly Border _recommendedPill;
    private readonly StackPanel _reason = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock _lastRun = new() { FontSize = 11.5, Foreground = Tertiary, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _result = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
    private readonly Grid _state = new() { Width = 26, Height = 26, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public TaskRowView(MaintenanceTaskRow row, MaintenanceViewModel vm)
    {
        _row = row;
        _vm = vm;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(14, 12, 14, 12);
        BorderThickness = new Thickness(1);
        Cursor = Cursors.Hand;
        Background = RowBackground;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _check.Style = (Style)Application.Current.FindResource("SquareCheck");
        _check.VerticalAlignment = VerticalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(_check, row.Task.Title());
        _check.Click += (_, _) => _row.IsSelected = _check.IsChecked == true;
        grid.Children.Add(_check);

        var iconTile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), 90),
            BorderBrush = Theme.Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Child = new Icon { Symbol = row.Task.Symbol(), Size = 19, Foreground = Brushes.White },
        };
        Grid.SetColumn(iconTile, 1);
        grid.Children.Add(iconTile);

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new DockPanel();
        _lastRun.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(_lastRun, Dock.Right);
        titleLine.Children.Add(_lastRun);
        var titleStack = new WrapPanel { Orientation = Orientation.Horizontal };
        titleStack.Children.Add(new TextBlock { Text = row.Task.Title(), FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _recommendedPill = Pill(null, Loc.T("Nên chạy"), Theme.Frozen(Color.FromArgb(0x4D, 0x4C, 0xD9, 0x64)), Brushes.White);
        titleStack.Children.Add(_recommendedPill);
        if (row.Task.RequiresAdmin())
        {
            var admin = Pill("lock.shield", Loc.T("Cần quyền quản trị"), Theme.Frozen(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)), Secondary);
            admin.ToolTip = Loc.T("Chạy qua helper quản trị; Windows sẽ hỏi xác nhận (UAC).");
            titleStack.Children.Add(admin);
        }
        titleLine.Children.Add(titleStack);
        content.Children.Add(titleLine);
        content.Children.Add(new TextBlock
        {
            Text = row.Task.Description() + " " + row.Task.WhenToRun(), FontSize = 12.5, Foreground = Secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
        });
        content.Children.Add(_reason);
        var impact = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        impact.Children.Add(new Icon { Symbol = "clock", Size = 10.5, Foreground = Tertiary, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Top });
        impact.Children.Add(new TextBlock { Text = row.Task.Impact(), FontSize = 11.5, Foreground = Tertiary, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(impact);
        content.Children.Add(_result);
        Grid.SetColumn(content, 2);
        grid.Children.Add(content);

        Grid.SetColumn(_state, 3);
        grid.Children.Add(_state);
        Child = grid;

        MouseEnter += (_, _) => UpdateBackground();
        MouseLeave += (_, _) => UpdateBackground();
        MouseLeftButtonUp += (_, e) =>
        {
            // Bấm vào dòng (ngoài ô chọn) cũng chọn/bỏ chọn.
            if (_vm.IsRunning || e.OriginalSource is DependencyObject d && IsInside(d, _check)) return;
            _row.IsSelected = !_row.IsSelected;
        };
        _row.PropertyChanged += (_, _) => Update();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaintenanceViewModel.IsRunning)) Update();
        };
        Update();
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (DependencyObject? x = d; x is not null; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
        {
            if (x == ancestor) return true;
        }
        return false;
    }

    private static Border Pill(string? symbol, string text, Brush background, Brush foreground)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        if (symbol is not null) stack.Children.Add(new Icon { Symbol = symbol, Size = 10, Foreground = foreground, Margin = new Thickness(0, 0, 4, 0) });
        stack.Children.Add(new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center });
        return new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 7, 2), Background = background, Margin = new Thickness(0, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center, Child = stack };
    }

    private void UpdateBackground()
    {
        Background = _row.IsSelected ? RowSelected : IsMouseOver && !_vm.IsRunning ? RowHover : RowBackground;
        BorderBrush = _row.IsSelected ? RowSelectedStroke : RowStroke;
    }

    private void Update()
    {
        _check.IsChecked = _row.IsSelected;
        _check.IsEnabled = !_vm.IsRunning;
        _check.Opacity = _vm.IsRunning ? 0.5 : 1;
        Cursor = _vm.IsRunning ? Cursors.Arrow : Cursors.Hand;
        UpdateBackground();

        var rec = _row.Recommendation;
        _recommendedPill.Visibility = rec?.Recommended == true ? Visibility.Visible : Visibility.Collapsed;

        _reason.Children.Clear();
        if (rec is { Why.Length: > 0 })
        {
            if (rec.Recommended)
            {
                _reason.Children.Add(new Icon { Symbol = "sparkles", Size = 11, Foreground = Theme.SafeBrush, Margin = new Thickness(0, 0, 6, 0) });
                _reason.Children.Add(new TextBlock { Text = rec.Why, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
            }
            else
            {
                _reason.Children.Add(new Icon { Symbol = "info.circle", Size = 11, Foreground = Tertiary, Margin = new Thickness(0, 0, 6, 0) });
                _reason.Children.Add(new TextBlock { Text = rec.Why, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
            }
        }
        _reason.Visibility = _reason.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_row.LastRun is { } last)
        {
            var date = MaintenanceStatus.FormatDate(last.RanAt);
            _lastRun.Text = last.Result.StartsWith("failed", StringComparison.Ordinal)
                ? Loc.F("Lần chạy gần nhất: %@ (lỗi)", date)
                : Loc.F("Lần chạy gần nhất: %@", date);
            _lastRun.ToolTip = last.Result.StartsWith("failed", StringComparison.Ordinal) ? last.Result["failed:".Length..].Trim() : null;
        }
        else
        {
            _lastRun.Text = Loc.T("Chưa chạy lần nào");
            _lastRun.ToolTip = null;
        }

        _state.Children.Clear();
        _result.Children.Clear();
        switch (_row.State)
        {
            case TaskRunState.Queued:
                _state.Children.Add(new Icon { Symbol = "clock", Size = 16, Foreground = Tertiary });
                _result.Children.Add(new TextBlock { Text = Loc.T("Đang chờ…"), FontSize = 12, Foreground = Secondary });
                break;
            case TaskRunState.Running:
                _state.Children.Add(new Spinner { Width = 20, Height = 20, Thickness = 2.4 });
                var elapsed = _row.Elapsed;
                var text = elapsed.TotalSeconds >= 1 ? Loc.T("Đang chạy…") + " " + $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}" : Loc.T("Đang chạy…");
                _result.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brushes.White });
                break;
            case TaskRunState.Succeeded:
            case TaskRunState.Failed:
                var ok = _row.State == TaskRunState.Succeeded;
                _state.Children.Add(new Icon { Symbol = ok ? "checkmark.circle.fill" : "xmark.octagon.fill", Size = 20, Foreground = ok ? Theme.SafeBrush : Theme.RiskyBrush });
                _state.ToolTip = _row.Message;
                _result.Children.Add(new Icon { Symbol = ok ? "checkmark" : "exclamationmark.circle.fill", Size = 11, Foreground = ok ? Theme.SafeBrush : Theme.RiskyBrush, Margin = new Thickness(0, 0, 6, 0) });
                _result.Children.Add(new TextBlock { Text = _row.Message ?? "", FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
                break;
        }
        _result.Visibility = _result.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Vòng xoay chờ (ProgressView không xác định của bản Mac): rãnh trắng mờ + cung trắng quay đều.</summary>
internal sealed class Spinner : FrameworkElement
{
    private static readonly Brush Track = Theme.Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private readonly RotateTransform _rotate = new();

    public double Thickness { get; set; } = 2.5;

    public Spinner()
    {
        RenderTransform = _rotate;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Animate();
        Loaded += (_, _) => Animate();
        Unloaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Animate()
    {
        if (IsVisible) _rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        else _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(Track, Thickness), center, radius, radius);
        const double sweep = 100;
        var start = new Point(center.X, center.Y - radius);
        var rad = (sweep - 90) * Math.PI / 180;
        var end = new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Brushes.White, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }
}
