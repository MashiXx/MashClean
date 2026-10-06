using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Features.StartupItems;

namespace CleanBoost.App.Features.StartupItems;

/// <summary>
/// Màn Mục khởi động (LoginItemsView của bản Mac): ba nhóm Run / thư mục Startup / tác vụ lập lịch, mỗi dòng có biểu tượng
/// thật của chương trình, nhà phát hành, lệnh, nhãn hỏng/đang tắt/cần quyền quản trị, công tắc bật/tắt và menu "…".
/// </summary>
public sealed class StartupItemsView : Grid, IAccentScreen, IAutoStartScreen, IBusyScreen
{
    private static readonly Brush SecondaryBrush = Theme.Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TertiaryBrush = Theme.Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SectionBrush = Theme.Frozen(Color.FromArgb(0x2E, 0x00, 0x00, 0x00));
    private static readonly Brush RowHoverBrush = Theme.Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush IconBrush = Theme.Frozen(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));

    private static readonly StartupGroup[] Groups = [StartupGroup.Registry, StartupGroup.StartupFolder, StartupGroup.ScheduledTask];

    private readonly StartupItemsViewModel _vm;
    private readonly TextBlock _stats = new() { FontSize = 13, Foreground = SecondaryBrush, Margin = new Thickness(0, 4, 0, 0) };
    private readonly Button _removeBroken;
    private readonly TextBlock _removeBrokenText = new();
    private readonly Button _rescan;
    private readonly TextBox _search;
    private readonly TextBlock _searchPlaceholder;
    private readonly CheckBox _onlyProblems;
    private readonly Border _banner;
    private readonly TextBlock _bannerText = new() { FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Icon _bannerIcon = new() { Size = 16, Margin = new Thickness(0, 0, 10, 0) };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _loading;

    public Accent Accent => Accent.Maintenance;
    public bool IsBusy => _vm.IsBusy;

    public StartupItemsView(AppEnvironment env)
    {
        _vm = new StartupItemsViewModel(env);
        _vm.ConfirmPermanentDelete = path => Task.FromResult(MessageDialog.Show(Window.GetWindow(this), Loc.T("Xoá vĩnh viễn"),
            Loc.F("\"%@\" nằm trên ổ không có Thùng rác. Xoá vĩnh viễn? Thao tác này không khôi phục được.", Path.GetFileName(path)),
            Loc.T("Xoá vĩnh viễn"), Loc.T("Huỷ"), destructive: true, accent: Accent.Maintenance));
        Theme.Apply(this, Accent.Maintenance);
        Margin = new Thickness(24);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Tiêu đề + số liệu, nút bên phải
        var header = new DockPanel { LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _removeBroken = new Button { Style = Res("GlassButton"), Content = IconLabel("trash", _removeBrokenText), Margin = new Thickness(0, 0, 10, 0) };
        _removeBroken.Click += (_, _) => ConfirmRemoveBroken();
        _rescan = new Button { Style = Res("GlassButton"), Content = IconLabel("arrow.clockwise", new TextBlock { Text = Loc.T("Quét lại") }) };
        _rescan.Click += (_, _) => _ = _vm.ReloadAsync();
        buttons.Children.Add(_removeBroken);
        buttons.Children.Add(_rescan);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Loc.T("Mục khởi động"), Style = Res("TitleText") });
        titles.Children.Add(_stats);
        header.Children.Add(titles);
        Add(header, 0);

        // Thanh lọc: tìm kiếm + chỉ hiện mục có vấn đề
        var toolbar = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        _onlyProblems = new CheckBox { Style = Res("Switch"), Content = Loc.T("Chỉ hiện mục hỏng/đã tắt"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        _onlyProblems.Click += (_, _) => _vm.ShowOnlyProblems = _onlyProblems.IsChecked == true;
        DockPanel.SetDock(_onlyProblems, Dock.Right);
        toolbar.Children.Add(_onlyProblems);
        var searchHost = new Grid { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        _search = new TextBox { Style = Res("DarkTextBox"), Padding = new Thickness(26, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center };
        _search.TextChanged += (_, _) =>
        {
            _searchPlaceholder!.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _vm.SearchText = _search.Text;
        };
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) _search.Text = "";
        };
        _searchPlaceholder = new TextBlock
        {
            Text = Loc.T("Tìm theo tên, nhà phát hành hoặc lệnh"), Foreground = TertiaryBrush, FontSize = 13, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(29, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        searchHost.Children.Add(_search);
        searchHost.Children.Add(new Icon { Symbol = "magnifyingglass", Size = 12, Foreground = TertiaryBrush, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(11, 0, 0, 0), IsHitTestVisible = false });
        searchHost.Children.Add(_searchPlaceholder);
        toolbar.Children.Add(searchHost);
        Add(toolbar, 1);

        // Thông báo (NoticeBanner)
        var close = new Button { Style = Res("GlassButton"), Content = Loc.T("Đóng"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => _vm.Notice = null;
        var bannerContent = new DockPanel();
        DockPanel.SetDock(_bannerIcon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        bannerContent.Children.Add(_bannerIcon);
        bannerContent.Children.Add(close);
        bannerContent.Children.Add(_bannerText);
        _banner = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Theme.Frozen(Color.FromArgb(0x40, 0, 0, 0)), Padding = new Thickness(12, 8, 8, 8),
            Margin = new Thickness(0, 14, 0, 0), Child = bannerContent, Visibility = Visibility.Collapsed,
        };
        Add(_banner, 2);

        Add(StoreAppsCard(), 3);

        // Danh sách
        var body = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _list, Focusable = false };
        _list.Margin = new Thickness(0, 0, 4, 0);
        body.Children.Add(_scroll);
        _loading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _loading.Children.Add(new Spinner { Width = 36, Height = 36, Thickness = 3.5 });
        _loading.Children.Add(new TextBlock { Text = Loc.T("Đang đọc mục khởi động…"), Foreground = SecondaryBrush, FontSize = 13, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        body.Children.Add(_loading);
        Add(body, 4);

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.BusyChanged += Render;
        Loaded += (_, _) => _vm.LoadIfNeeded();
        // Smart Scan (hoặc màn khác) vừa dọn: đọc lại để danh sách không còn mục đã gỡ.
        CleanEngine.DidClean += () => Dispatcher.InvokeAsync(() =>
        {
            if (_vm.HasLoaded && !_vm.IsBusy) _ = _vm.ReloadAsync();
        });
        Render();
    }

    public void AutoStart()
    {
        if (!_vm.IsLoading) _ = _vm.ReloadAsync();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StartupItemsViewModel.Items) or nameof(StartupItemsViewModel.SearchText) or nameof(StartupItemsViewModel.ShowOnlyProblems)
                or nameof(StartupItemsViewModel.IsLoading) or nameof(StartupItemsViewModel.HasLoaded):
                Render();
                break;
            case nameof(StartupItemsViewModel.Notice):
                UpdateBanner();
                break;
        }
    }

    // MARK: Dựng giao diện

    private void Add(UIElement element, int row)
    {
        SetRow(element, row);
        Children.Add(element);
    }

    private static Style Res(string key) => (Style)Application.Current.FindResource(key);

    private static StackPanel IconLabel(string symbol, TextBlock label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Icon { Symbol = symbol, Size = 12, Margin = new Thickness(0, 0, 7, 0) });
        panel.Children.Add(label);
        return panel;
    }

    /// <summary>Thẻ giải thích mục của app Microsoft Store (tương đương thẻ "Login item hiện đại" của bản Mac).</summary>
    private static FrameworkElement StoreAppsCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Icon { Symbol = "square.grid.2x2", Size = 22, Foreground = Brushes.White, Margin = new Thickness(2, 0, 14, 0) };
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Loc.T("App Microsoft Store"), Style = Res("HeadlineText"), FontSize = 14 });
        text.Children.Add(new TextBlock
        {
            Text = Loc.T("App Microsoft Store tự mở cùng Windows (StartupTask) không nằm trong các nhóm dưới đây, chỉ quản lý được trong Cài đặt > Ứng dụng > Khởi động."),
            FontSize = 12, Foreground = SecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
        });
        SetColumn(text, 1);
        grid.Children.Add(text);
        var open = new Button { Style = Res("GlassButton"), Content = Loc.T("Mở Cài đặt Khởi động"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        open.Click += (_, _) => OpenUri("ms-settings:startupapps");
        SetColumn(open, 2);
        grid.Children.Add(open);
        return new Border { Style = Res("Card"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 14, 0, 0), Child = grid };
    }

    private void UpdateBanner()
    {
        if (_vm.Notice is not { } notice)
        {
            _banner.Visibility = Visibility.Collapsed;
            return;
        }
        _bannerText.Text = notice.Text;
        _bannerIcon.Symbol = notice.IsError ? "exclamationmark.triangle.fill" : "info.circle";
        _bannerIcon.Foreground = notice.IsError ? Theme.ReviewBrush : Brushes.White;
        _banner.Visibility = Visibility.Visible;
    }

    private void Render()
    {
        var items = _vm.Items;
        var broken = _vm.RemovableBroken.Count;
        _stats.Text = _vm.HasLoaded
            ? Loc.F("%lld mục · %lld đang bật · %lld hỏng", items.Count, _vm.EnabledCount, _vm.BrokenCount)
            : Loc.T("Chương trình tự chạy khi Windows khởi động hoặc khi bạn đăng nhập: xem của app nào, tắt bớt hoặc xoá mục hỏng.");
        _removeBrokenText.Text = Loc.F("Xoá mục hỏng (%lld)", broken);
        _removeBroken.Visibility = broken > 0 ? Visibility.Visible : Visibility.Collapsed;
        _removeBroken.IsEnabled = !_vm.IsBusy;
        _rescan.IsEnabled = !_vm.IsLoading;
        _loading.Visibility = _vm.IsLoading && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _vm.HasLoaded ? Visibility.Visible : Visibility.Collapsed;

        var offset = _scroll.VerticalOffset;
        _list.Children.Clear();
        if (!_vm.HasLoaded) return;
        var first = true;
        foreach (var group in Groups)
        {
            _list.Children.Add(Section(group, _vm.ItemsIn(group), first));
            first = false;
        }
        _scroll.ScrollToVerticalOffset(offset);
    }

    private FrameworkElement Section(StartupGroup group, IReadOnlyList<StartupItem> rows, bool first)
    {
        var stack = new StackPanel { Margin = new Thickness(0, first ? 0 : 18, 0, 0) };
        var head = new DockPanel { Margin = new Thickness(4, 0, 4, 6) };
        var count = new TextBlock { Text = rows.Count.ToString(Loc.Culture), FontSize = 12, Foreground = SecondaryBrush, VerticalAlignment = VerticalAlignment.Bottom };
        DockPanel.SetDock(count, Dock.Right);
        head.Children.Add(count);
        var titles = new StackPanel { Orientation = Orientation.Horizontal };
        titles.Children.Add(new TextBlock { Text = group.Title(), Style = Res("HeadlineText"), VerticalAlignment = VerticalAlignment.Bottom });
        titles.Children.Add(new TextBlock
        {
            Text = group switch
            {
                StartupGroup.Registry => @"HKCU · HKLM\…\CurrentVersion\Run",
                StartupGroup.StartupFolder => "shell:startup · shell:common startup",
                _ => Loc.T("Chạy khi đăng nhập hoặc khi khởi động máy"),
            },
            FontSize = 11.5, Foreground = TertiaryBrush, Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        });
        head.Children.Add(titles);
        stack.Children.Add(head);
        if (rows.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = _vm.SearchText.Trim().Length > 0 || _vm.ShowOnlyProblems ? Loc.T("Không có mục nào khớp") : Loc.T("Không có mục nào"),
                FontSize = 12, Foreground = TertiaryBrush, Margin = new Thickness(4, 2, 0, 4),
            });
            return stack;
        }
        var rowsPanel = new StackPanel();
        foreach (var item in rows) rowsPanel.Children.Add(Row(item));
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = SectionBrush, Padding = new Thickness(6), Child = rowsPanel });
        return stack;
    }

    private FrameworkElement Row(StartupItem item)
    {
        var busy = _vm.IsItemBusy(item);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Biểu tượng thật của chương trình (hoặc lối tắt), nếu không có thì glyph.
        grid.Children.Add(RowIcon(item));

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var line1 = new StackPanel { Orientation = Orientation.Horizontal };
        line1.Children.Add(new TextBlock
        {
            Text = item.DisplayName, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 340, VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var pill in Pills(item)) line1.Children.Add(pill);
        text.Children.Add(line1);
        if (Details(item) is { Length: > 0 } details)
        {
            text.Children.Add(new TextBlock { Text = details, FontSize = 11.5, Foreground = SecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        }
        var command = new MiddleEllipsisText
        {
            Text = string.IsNullOrWhiteSpace(item.Command) ? Loc.T("(lệnh trống)") : item.Command, Foreground = TertiaryBrush, FontSize = 11.5,
            Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = Tooltip(item),
        };
        ToolTipService.SetInitialShowDelay(command, 300);
        text.Children.Add(command);
        SetColumn(text, 1);
        grid.Children.Add(text);

        if (busy)
        {
            var spinner = new Spinner { Width = 14, Height = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            SetColumn(spinner, 2);
            grid.Children.Add(spinner);
        }

        var toggle = new CheckBox
        {
            Style = Res("Switch"), IsChecked = item.IsEnabled, IsEnabled = item.CanToggle && !busy, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = item.ToggleUnavailableReason ?? (item.IsEnabled ? Loc.T("Không tự chạy khi khởi động nữa") : Loc.T("Cho tự chạy khi khởi động")),
        };
        ToolTipService.SetShowOnDisabled(toggle, true);
        AutomationProperties.SetName(toggle, item.DisplayName);
        toggle.Click += (_, _) => _ = _vm.SetEnabledAsync(item, toggle.IsChecked == true);
        SetColumn(toggle, 3);
        grid.Children.Add(toggle);

        var menu = RowMenu(item);
        var more = new Button
        {
            Style = Res("IconButton"), Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Content = new Icon { Symbol = "ellipsis", Size = 14, Foreground = SecondaryBrush }, ToolTip = Loc.T("Tuỳ chọn khác"), IsEnabled = !busy,
        };
        more.Click += (_, _) =>
        {
            menu.PlacementTarget = more;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        SetColumn(more, 4);
        grid.Children.Add(more);

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 7, 6, 7), Background = Brushes.Transparent, Child = grid, ContextMenu = menu };
        row.MouseEnter += (_, _) => row.Background = RowHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    private static FrameworkElement RowIcon(StartupItem item)
    {
        ImageSource? image = null;
        if (item.TargetExists == true && item.TargetPath is { } target && File.Exists(target)) image = ShellIcons.For(target, false, large: true);
        if (image is null && item.ShortcutPath is { } lnk && File.Exists(lnk) && !item.IsBroken) image = ShellIcons.For(lnk, false, large: true);
        FrameworkElement icon = image is not null
            ? new Image { Source = image, Width = 32, Height = 32, Stretch = Stretch.Uniform }
            : new Icon
            {
                Symbol = item.IsBroken ? "exclamationmark.triangle" : "gearshape.2", Size = 20,
                Foreground = item.IsBroken ? Theme.RiskyBrush : IconBrush,
            };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        if (!item.IsEnabled && !item.IsBroken) icon.Opacity = 0.55;
        return icon;
    }

    private IEnumerable<FrameworkElement> Pills(StartupItem item)
    {
        if (item.IsBroken) yield return Pill(Loc.T("Hỏng"), Theme.RiskyColor);
        else if (!item.IsEnabled) yield return Pill(Loc.T("Đang tắt"), Theme.ReviewColor);
        if (item.IsRunning) yield return Pill(Loc.T("Đang chạy"), Theme.SafeColor);
        if (item.RequiresAdmin && !_vm.IsElevated) yield return Pill(Loc.T("Cần quyền quản trị"), Colors.White);
    }

    private static Border Pill(string text, Color color) => new()
    {
        Style = Res("PillBorder"),
        Background = Theme.Frozen(Color.FromArgb(color == Colors.White ? (byte)0x26 : (byte)0x40, color.R, color.G, color.B)),
        Margin = new Thickness(8, 0, 0, 0),
        Child = new TextBlock { Text = text, Style = Res("PillText"), Foreground = color == Colors.White ? SecondaryBrush : Theme.Frozen(color) },
    };

    /// <summary>Dòng phụ: tên mục (nếu khác tên hiển thị), app sở hữu, nhà phát hành, thời điểm chạy của tác vụ.</summary>
    private static string Details(StartupItem item)
    {
        var parts = new List<string>();
        void AddPart(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            if (s.Equals(item.DisplayName, StringComparison.OrdinalIgnoreCase)) return;
            if (parts.Any(p => p.Equals(s, StringComparison.OrdinalIgnoreCase))) return;
            parts.Add(s);
        }
        // Tên mục (tên giá trị Run/lối tắt) khi khác tên hiển thị; đường dẫn đầy đủ của tác vụ nằm trong chú thích.
        if (item.Source != StartupSource.ScheduledTask) AddPart(item.EntryName);
        if (item.OwnerName is { } owner && !owner.Equals(item.DisplayName, StringComparison.OrdinalIgnoreCase) && item.OwnerIsInstalledApp) AddPart(Loc.F("Của %@", owner));
        if (item.Publisher is { } publisher && !(item.OwnerName?.Equals(publisher, StringComparison.OrdinalIgnoreCase) ?? false)) AddPart(publisher);
        if (item.Source == StartupSource.RunOnce) AddPart(Loc.T("Chạy một lần ở lần đăng nhập tới"));
        AddPart(item.TriggerDescription);
        if (item.LastRunTime is { } last) AddPart(Loc.F("Lần chạy cuối %@", last.ToString("g", Loc.Culture)));
        return string.Join(" · ", parts);
    }

    private static object Tooltip(StartupItem item)
    {
        var lines = new List<string> { item.Command };
        if (item.TargetPath is { } t && !item.Command.Contains(t, StringComparison.OrdinalIgnoreCase)) lines.Add(Loc.F("Chương trình: %@", t));
        if (item.IsBroken) lines.Add(StartupItemsFeature.BrokenReason(item).Resolved);
        lines.Add(item.Label);
        if (item.TaskDescription is { } d) lines.Add(d);
        return new TextBlock { Text = string.Join("\n", lines.Distinct()), TextWrapping = TextWrapping.Wrap, MaxWidth = 500 };
    }

    // MARK: Menu "…"

    private ContextMenu RowMenu(StartupItem item)
    {
        var menu = new ContextMenu();
        var reveal = new MenuItem { Header = Loc.T("Hiện trong File Explorer"), IsEnabled = item.RevealPath is not null };
        reveal.Click += (_, _) => NodeOutlineView.Reveal(item.RevealPath);
        menu.Items.Add(reveal);
        var copy = new MenuItem { Header = Loc.T("Sao chép lệnh"), IsEnabled = item.Command.Length > 0 };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(item.Command); } catch (Exception ex) { Log.Warning(LogCategory.UI, "Không sao chép được: " + ex.Message); }
        };
        menu.Items.Add(copy);
        if (item.Source == StartupSource.ScheduledTask)
        {
            var scheduler = new MenuItem { Header = Loc.T("Mở Trình lập lịch tác vụ") };
            scheduler.Click += (_, _) => OpenUri("taskschd.msc");
            menu.Items.Add(scheduler);
        }
        menu.Items.Add(new Separator());
        var isTask = item.Source == StartupSource.ScheduledTask;
        var remove = new MenuItem
        {
            Header = isTask ? Loc.T("Tắt hẳn tác vụ (không xoá)") : Loc.T("Xoá khỏi khởi động"),
            IsEnabled = !item.IsSystemComponent && !(isTask && !item.IsEnabled),
            Foreground = Theme.RiskyBrush,
            ToolTip = item.IsSystemComponent ? Loc.T("Thành phần của Windows: chỉ có thể tắt") : null,
        };
        ToolTipService.SetShowOnDisabled(remove, true);
        remove.Click += (_, _) => ConfirmRemove(item);
        menu.Items.Add(remove);
        return menu;
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.UI, $"Không mở được {uri}: {ex.Message}");
        }
    }

    // MARK: Xác nhận

    private void ConfirmRemove(StartupItem item)
    {
        var isTask = item.Source == StartupSource.ScheduledTask;
        var action = (item.Source, item.Scope) switch
        {
            (StartupSource.ScheduledTask, _) => Loc.T("tắt tác vụ lập lịch (tác vụ không bị xoá, bật lại được bằng công tắc)"),
            (StartupSource.StartupFolder, StartupScope.User) => Loc.T("chuyển lối tắt vào Thùng rác"),
            (StartupSource.StartupFolder, _) => Loc.T("xoá lối tắt trong thư mục Startup chung qua helper (cần quyền quản trị)"),
            (_, StartupScope.User) => Loc.T("xoá giá trị trong khoá Run của bạn"),
            _ => Loc.T("xoá giá trị trong khoá Run của máy qua helper (cần quyền quản trị)"),
        };
        var title = isTask ? Loc.F("Tắt %@?", item.DisplayName) : Loc.F("Xoá %@?", item.DisplayName);
        var message = Loc.F("Gỡ \"%@\" khỏi khởi động cùng Windows: %@. App sở hữu có thể tạo lại mục này khi mở.", item.EntryName, action);
        if (!MessageDialog.Show(Window.GetWindow(this), title, message, isTask ? Loc.T("Tắt") : Loc.T("Xoá"), Loc.T("Huỷ"), destructive: true, accent: Accent.Maintenance)) return;
        _ = _vm.RemoveAsync([item]);
    }

    private void ConfirmRemoveBroken()
    {
        var items = _vm.RemovableBroken;
        if (items.Count == 0) return;
        var needsAdmin = items.Any(i => i.RequiresAdmin);
        var message = Loc.T("Các mục này trỏ tới chương trình không còn tồn tại nên không chạy được. Giá trị Run bị xoá, lối tắt chuyển vào Thùng rác, tác vụ lập lịch chỉ bị tắt.");
        if (needsAdmin) message += " " + Loc.T("Mục của máy cần quyền quản trị (Windows sẽ hỏi).");
        if (!MessageDialog.Show(Window.GetWindow(this), Loc.F("Xoá %lld mục khởi động hỏng?", items.Count), message, Loc.T("Xoá"), Loc.T("Huỷ"), destructive: true, accent: Accent.Maintenance)) return;
        _ = _vm.RemoveAsync(items);
    }
}
