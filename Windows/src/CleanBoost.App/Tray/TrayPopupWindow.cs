using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Metrics;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.App.Tray;

/// <summary>Việc người dùng chọn trong popup hoặc menu chuột phải của khay.</summary>
public enum TrayAction
{
    OpenApp,
    SmartScan,
    CleanJunk,
    FreeRam,
    Settings,
    Quit,
}

/// <summary>
/// Popup của biểu tượng khay (MenuBarPopoverView của bản Mac): rộng 360, nền gradient Smart Scan, bo góc 14, hiện ngay trên
/// vùng thông báo ở mép thanh tác vụ; tự ẩn khi mất focus. Nội dung: phần đầu (thời gian bật máy, mức RAM), 4 ô số liệu có
/// biểu đồ mini, mạng, "Đã dọn tháng này", nút hành động, ổ đĩa ngoài (nút tháo), chân (mở app, tuỳ chọn, thoát).
/// </summary>
public sealed class TrayPopupWindow : Window
{
    private const double CardWidth = 360;
    private const double ShadowMargin = 16;

    private static readonly Color CpuTint = Color.FromRgb(0x7C, 0xE7, 0xFF);
    private static readonly Color MemoryTint = Color.FromRgb(0xFF, 0xB8, 0x6C);
    private static readonly Color NetOutTint = Color.FromRgb(0xFF, 0x8A, 0xD8);

    private readonly TrayModel _model;
    private readonly AppSettings _settings;
    private readonly Border _root;
    private readonly Grid _cardHost;
    private readonly TextBlock _uptime;
    private readonly Border _pressurePill;
    private readonly TextBlock _pressureText;
    private readonly MetricTile _cpuTile = new();
    private readonly MetricTile _memoryTile = new();
    private readonly MetricTile _diskTile = new();
    private readonly MetricTile _fourthTile = new();
    private readonly TextBlock _netIn;
    private readonly TextBlock _netOut;
    private readonly TrayChart _netChart;
    private readonly TextBlock _freed;
    private readonly Border _volumesCard;
    private readonly StackPanel _volumeRows;
    private readonly TextBlock _ejectError;
    private readonly ContextMenu _gearMenu;
    private string? _volumesSignature;
    private POINT? _anchor;
    private bool _hiding;

    /// <summary>Không tự ẩn khi mất focus (chụp ảnh kiểm thử).</summary>
    public bool StayOpen { get; set; }

    public DateTime HiddenAt { get; private set; } = DateTime.MinValue;

    public event Action<TrayAction>? ActionRequested;
    public event Action? PopupHidden;

    public TrayPopupWindow(TrayModel model, AppSettings settings)
    {
        _model = model;
        _settings = settings;
        Title = "Clean Boost";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = CardWidth + 2 * ShadowMargin;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        Foreground = Brushes.White;
        FontFamily = AppFonts.Text;
        Theme.Apply(this, Accent.SmartScan);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var stack = new StackPanel();

        // 1. Phần đầu
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Grid { Width = 32, Height = 32 };
        badge.Children.Add(new Ellipse { Fill = Theme.Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)) });
        badge.Children.Add(new Icon { Symbol = "sparkles", Size = 17, Foreground = Brushes.White });
        header.Children.Add(badge);
        var titles = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Clean Boost", Style = Res<Style>("HeadlineText") });
        _uptime = new TextBlock { Style = Res<Style>("CaptionText"), Margin = new Thickness(0, 1, 0, 0) };
        titles.Children.Add(_uptime);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        _pressureText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        _pressurePill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Center, Child = _pressureText, Visibility = Visibility.Collapsed };
        Grid.SetColumn(_pressurePill, 2);
        header.Children.Add(_pressurePill);
        stack.Children.Add(header);

        // 2. Lưới 2×2 ô số liệu
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, _cpuTile, 0, 0);
        Place(grid, _memoryTile, 0, 2);
        Place(grid, _diskTile, 2, 0);
        Place(grid, _fourthTile, 2, 2);
        stack.Children.Add(grid);

        // 3. Mạng
        var netTop = new DockPanel();
        var netLabel = TileLabel("network", Loc.T("Mạng"));
        netTop.Children.Add(netLabel);
        var rates = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _netIn = RateText();
        _netOut = RateText();
        rates.Children.Add(RateLabel("arrow.down", _netIn, CpuTint));
        rates.Children.Add(RateLabel("arrow.up", _netOut, NetOutTint, new Thickness(10, 0, 0, 0)));
        netTop.Children.Add(rates);
        _netChart = new TrayChart
        {
            Height = 34, Margin = new Thickness(0, 6, 0, 0), Tint = CpuTint, SecondaryTint = NetOutTint, UpperBound = null, Fill = false,
        };
        var netStack = new StackPanel();
        netStack.Children.Add(netTop);
        netStack.Children.Add(_netChart);
        stack.Children.Add(Card(netStack, new Thickness(0, 12, 0, 0)));

        // 4. Đã dọn tháng này
        var freedRow = new DockPanel { Margin = new Thickness(4, 12, 4, 0) };
        var seal = new Icon { Symbol = "checkmark.seal.fill", Size = 15, Foreground = Theme.SafeBrush };
        freedRow.Children.Add(seal);
        _freed = new TextBlock
        {
            FontFamily = AppFonts.Display, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
        };
        Typography.SetNumeralAlignment(_freed, FontNumeralAlignment.Tabular);
        DockPanel.SetDock(_freed, Dock.Right);
        freedRow.Children.Add(_freed);
        freedRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Đã dọn tháng này"), Style = Res<Style>("SecondaryBodyText"), TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(freedRow);

        // 5. Hành động
        var smartScan = new Button
        {
            Style = Res<Style>("PrimaryButton"), Padding = new Thickness(22, 9, 22, 9), HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = IconAndText("sparkle.magnifyingglass", "Smart Scan", 15), Margin = new Thickness(0, 12, 0, 0),
            // Chữ màu giữa của gradient Smart Scan (đặt trực tiếp: binding (ui:Theme.AccentMid) của style không phân giải được ở đây).
            Foreground = Theme.Mid(Accent.SmartScan),
        };
        smartScan.Click += (_, _) => Request(TrayAction.SmartScan);
        stack.Children.Add(smartScan);
        var secondary = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        secondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        secondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        secondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var junk = GlassButton("trash.circle", Loc.T("Dọn rác"));
        junk.Click += (_, _) => Request(TrayAction.CleanJunk);
        secondary.Children.Add(junk);
        var ram = GlassButton("memorychip", Loc.T("Giải phóng RAM"));
        ram.Click += (_, _) => Request(TrayAction.FreeRam);
        Grid.SetColumn(ram, 2);
        secondary.Children.Add(ram);
        stack.Children.Add(secondary);

        // 6. Ổ đĩa ngoài
        var volumes = new StackPanel();
        volumes.Children.Add(new TextBlock { Text = Loc.T("Ổ đĩa ngoài"), Style = Res<Style>("CaptionText"), FontWeight = FontWeights.SemiBold });
        _volumeRows = new StackPanel();
        volumes.Children.Add(_volumeRows);
        _ejectError = new TextBlock
        {
            Style = Res<Style>("CaptionText"), Foreground = Theme.RiskyBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        volumes.Children.Add(_ejectError);
        _volumesCard = Card(volumes, new Thickness(0, 12, 0, 0), stroke: false);
        _volumesCard.Visibility = Visibility.Collapsed;
        stack.Children.Add(_volumesCard);

        // 7. Chân
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var open = GlassButton("macwindow", Loc.T("Mở Clean Boost"));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += (_, _) => Request(TrayAction.OpenApp);
        footer.Children.Add(open);
        var quit = new Button { Style = Res<Style>("GlassButton"), Content = Loc.T("Thoát"), Padding = new Thickness(14, 7, 14, 7) };
        quit.Click += (_, _) => Request(TrayAction.Quit);
        DockPanel.SetDock(quit, Dock.Right);
        var gear = new Button
        {
            Style = Res<Style>("IconButton"), Content = new Icon { Symbol = "gearshape.fill", Size = 15 }, Margin = new Thickness(0, 0, 6, 0),
            ToolTip = Loc.T("Tuỳ chọn hiển thị"), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(gear, Dock.Right);
        footer.Children.Add(quit);
        footer.Children.Add(gear);
        stack.Children.Add(footer);

        _gearMenu = new ContextMenu { PlacementTarget = gear, Placement = PlacementMode.Top };
        _gearMenu.Closed += (_, _) =>
        {
            if (!IsActive && !StayOpen) Dispatcher.BeginInvoke(HidePopup);
        };
        gear.Click += (_, _) =>
        {
            BuildGearMenu();
            _gearMenu.IsOpen = true;
        };

        _cardHost = new Grid { Width = CardWidth, RenderTransform = new TranslateTransform() };
        _cardHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14), Background = Theme.Background(Accent.SmartScan),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.4 },
        });
        _cardHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14), Background = Theme.Background(Accent.SmartScan), Padding = new Thickness(16),
            BorderBrush = Theme.Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), Child = stack,
        });
        _root = new Border { Padding = new Thickness(ShadowMargin), Background = Brushes.Transparent, Child = _cardHost };
        Content = _root;

        Deactivated += (_, _) =>
        {
            if (StayOpen || _gearMenu.IsOpen) return;
            HidePopup();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HidePopup();
        };
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, System.Windows.Threading.DispatcherPriority.Loaded);
        _model.Changed += OnModelChanged;
        Refresh();
    }

    /// <summary>Phần tử gốc (gồm lề bóng đổ) để chụp ảnh kiểm thử.</summary>
    public FrameworkElement SnapshotRoot => _root;

    private void OnModelChanged()
    {
        if (IsVisible) Refresh();
    }

    private void Request(TrayAction action)
    {
        if (action != TrayAction.Quit) HidePopup();
        ActionRequested?.Invoke(action);
    }

    // MARK: Hiện / ẩn

    /// <summary>Hiện popup gần điểm neo (vị trí con trỏ lúc bấm biểu tượng khay, đơn vị pixel màn hình).</summary>
    public void ShowAt(System.Drawing.Point? anchor)
    {
        _anchor = anchor is { } a ? new POINT { X = a.X, Y = a.Y } : null;
        _hiding = false;
        _model.SetPopupOpen(true);
        Refresh();
        var wasVisible = IsVisible;
        if (!wasVisible)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
        }
        UpdateLayout();
        Reposition();
        Activate();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        if (!wasVisible) Animate();
    }

    private void Animate()
    {
        var duration = TimeSpan.FromMilliseconds(160);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Opacity = 1;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        var offset = TaskbarEdge() switch
        {
            Edge.Top => -10,
            Edge.Left => 0,
            Edge.Right => 0,
            _ => 10,
        };
        var slideX = TaskbarEdge() switch { Edge.Left => -10, Edge.Right => 10, _ => 0 };
        var transform = (TranslateTransform)_cardHost.RenderTransform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slideX, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
    }

    public void HidePopup()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _gearMenu.IsOpen = false;
        HiddenAt = DateTime.UtcNow;
        Hide();
        _hiding = false;
        _model.SetPopupOpen(false);
        PopupHidden?.Invoke();
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.Changed -= OnModelChanged;
        base.OnClosed(e);
    }

    // MARK: Nội dung

    public void Refresh()
    {
        var snap = _model.Snapshot;
        _uptime.Text = Loc.F("Đã bật máy %@", TrayFormat.Duration(snap?.Uptime ?? TimeSpan.FromMilliseconds(Environment.TickCount64)));

        var memory = snap?.Memory;
        if (memory is { Pressure: not MemoryPressure.Normal })
        {
            var color = memory.Pressure == MemoryPressure.Critical ? Theme.RiskyColor : Theme.ReviewColor;
            _pressureText.Text = "RAM: " + MemorySample.PressureTitle(memory.Pressure);
            _pressureText.Foreground = Theme.Frozen(color);
            _pressurePill.Background = Theme.Frozen(Color.FromArgb(0x33, color.R, color.G, color.B));
            _pressurePill.Visibility = Visibility.Visible;
        }
        else
        {
            _pressurePill.Visibility = Visibility.Collapsed;
        }

        var cpu = snap?.Cpu;
        _cpuTile.Set("cpu", "CPU", cpu is null ? "–" : TrayFormat.Percent(cpu.Usage),
            cpu is null ? Loc.T("Đang đo…") : Loc.F("Người dùng %@ · Hệ thống %@", TrayFormat.Percent(cpu.User), TrayFormat.Percent(cpu.System)),
            _model.CpuHistory.Values, CpuTint);

        var hasMemory = memory is { Total: > 0 };
        _memoryTile.Set("memorychip", "RAM", hasMemory ? TrayFormat.Percent(memory!.UsedFraction) : "–",
            hasMemory ? $"{ByteCount.Format(memory!.Used)} / {ByteCount.Format(memory.Total)}" : "",
            _model.MemoryHistory.Values, memory?.Pressure == MemoryPressure.Critical ? Theme.RiskyColor : MemoryTint);

        var disk = snap?.Disk;
        var low = disk is not null && (disk.FreeFraction < 0.10 || disk.Available < 10_000_000_000);
        _diskTile.Set("internaldrive", Loc.T("Ổ đĩa"), disk is null ? "–" : ByteCount.Format(disk.Available),
            disk is null ? "" : Loc.F("trống / %@", ByteCount.Format(disk.Total)),
            _model.DiskHistory.Values, low ? Theme.RiskyColor : Theme.SafeColor);
        _diskTile.ToolTip = disk?.Name;

        if (snap?.Battery is { HasBattery: true } battery)
        {
            string detail;
            if (battery.IsCharging) detail = battery.MinutesRemaining is { } m ? Loc.F("Đang sạc · đầy sau %@", TrayFormat.Minutes(m)) : Loc.T("Đang sạc");
            else if (battery.IsOnACPower) detail = Loc.T("Đang cắm sạc");
            else detail = battery.MinutesRemaining is { } m ? Loc.F("Còn %@", TrayFormat.Minutes(m)) : Loc.T("Đang tính…");
            // "Pin" giữ nguyên như bản Mac (không dịch).
            _fourthTile.SetGlyph(BatteryGlyph(battery), "Pin", TrayFormat.Percent(battery.Level), detail,
                _model.BatteryHistory.Values, battery.Level < 0.2 && !battery.IsCharging ? Theme.RiskyColor : Theme.SafeColor);
        }
        else
        {
            _fourthTile.Set("trash", Loc.T("Thùng rác"), snap?.TrashBytes is { } trash ? ByteCount.Format(trash) : "–",
                _model.TrashUnreadable ? Loc.T("Không đọc được Thùng rác") : Loc.T("Dung lượng Thùng rác"), null, Theme.ReviewColor);
        }

        var net = snap?.Network;
        _netIn.Text = net is null ? "–" : TrayFormat.Rate(net.InPerSecond);
        _netOut.Text = net is null ? "–" : TrayFormat.Rate(net.OutPerSecond);
        _netChart.Values = _model.NetworkInHistory.Values;
        _netChart.SecondaryValues = _model.NetworkOutHistory.Values;

        _freed.Text = ByteCount.Format(_model.FreedThisMonth);

        RefreshVolumes();
    }

    private void RefreshVolumes()
    {
        var volumes = _model.ExternalVolumes;
        _volumesCard.Visibility = volumes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var error = _model.EjectError;
        _ejectError.Text = error ?? "";
        _ejectError.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        var signature = string.Join("|", volumes.Select(v => $"{v.Id}:{v.Name}:{v.Available}:{v.Total}:{_model.IsEjecting(v)}"));
        if (signature == _volumesSignature) return;
        _volumesSignature = signature;
        _volumeRows.Children.Clear();
        foreach (var volume in volumes)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var icon = new Icon { Symbol = "externaldrive.fill", Size = 16, Foreground = Res<Brush>("SecondaryText"), Margin = new Thickness(0, 0, 8, 0) };
            row.Children.Add(icon);
            FrameworkElement trailing;
            if (_model.IsEjecting(volume))
            {
                trailing = new Spinner { Margin = new Thickness(8, 0, 8, 0) };
            }
            else
            {
                var eject = new Button
                {
                    Style = Res<Style>("IconButton"), Width = 28, Height = 28, Content = new Icon { Symbol = "eject.fill", Size = 13 },
                    ToolTip = Loc.F("Tháo %@", volume.Name), Margin = new Thickness(8, 0, 0, 0),
                };
                eject.Click += (_, _) => _model.Eject(volume);
                trailing = eject;
            }
            trailing.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(trailing, Dock.Right);
            row.Children.Add(trailing);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = volume.Name, Style = Res<Style>("BodyText"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            texts.Children.Add(new TextBlock
            {
                Text = Loc.F("%@ trống / %@", ByteCount.Format(volume.Available), ByteCount.Format(volume.Total)),
                Style = Res<Style>("TertiaryCaptionText"), Margin = new Thickness(0, 1, 0, 0),
            });
            row.Children.Add(texts);
            _volumeRows.Children.Add(row);
        }
    }

    /// <summary>Glyph pin theo mức (Segoe Fluent Icons: Battery0–10, BatteryCharging0–10).</summary>
    private static string BatteryGlyph(BatterySample battery)
    {
        var level = (int)Math.Round(Math.Clamp(battery.Level, 0, 1) * 10);
        if (battery.IsCharging) return level >= 10 ? "" : ((char)(0xE85A + level)).ToString();
        return level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
    }

    // MARK: Menu tuỳ chọn (bánh răng)

    /// <summary>Mở menu bánh răng (chụp ảnh kiểm thử).</summary>
    internal ContextMenu OpenGearMenu()
    {
        BuildGearMenu();
        _gearMenu.IsOpen = true;
        return _gearMenu;
    }

    private void BuildGearMenu()
    {
        _gearMenu.Items.Clear();
        _gearMenu.Items.Add(SectionHeader("Ngôn ngữ · Language"));
        var current = _settings.Language;
        foreach (var language in Enum.GetValues<AppLanguage>())
        {
            var item = new MenuItem { Header = language.DisplayName(), IsCheckable = true, IsChecked = language == current };
            item.Click += (_, _) =>
            {
                if (language == _settings.Language)
                {
                    item.IsChecked = true;
                    return;
                }
                // Đổi ngôn ngữ cần khởi động lại (như bản Mac).
                _settings.Language = language;
                App.Restart();
            };
            _gearMenu.Items.Add(item);
        }
        _gearMenu.Items.Add(new Separator());
        _gearMenu.Items.Add(SectionHeader(Loc.T("Hiện trên khay hệ thống")));
        _gearMenu.Items.Add(StyleToggle("CPU (%)", TrayStatusParts.Cpu));
        _gearMenu.Items.Add(StyleToggle("RAM (%)", TrayStatusParts.Memory));
        _gearMenu.Items.Add(StyleToggle(Loc.T("Mạng (tải về / tải lên)"), TrayStatusParts.Network));
    }

    private static MenuItem SectionHeader(string text) => new()
    {
        Header = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)) },
        IsHitTestVisible = false,
        Focusable = false,
    };

    private MenuItem StyleToggle(string title, TrayStatusParts part)
    {
        var style = TrayStatusStyle.Parse(_settings.MenuBarStatusStyle);
        var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = style.HasFlag(part) };
        item.Click += (_, _) =>
        {
            var next = TrayStatusStyle.Parse(_settings.MenuBarStatusStyle);
            next = item.IsChecked ? next | part : next & ~part;
            _settings.MenuBarStatusStyle = TrayStatusStyle.Serialize(next);
        };
        return item;
    }

    // MARK: Dựng giao diện

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private static void Place(Grid grid, UIElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Border Card(UIElement child, Thickness margin, bool stroke = true) => new()
    {
        CornerRadius = new CornerRadius(12),
        Background = Res<Brush>("CardBackground"),
        BorderBrush = stroke ? Res<Brush>("CardStroke") : null,
        BorderThickness = new Thickness(stroke ? 1 : 0),
        Padding = new Thickness(10),
        Margin = margin,
        Child = child,
    };

    private static StackPanel TileLabel(string symbol, string title)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new Icon { Symbol = symbol, Size = 12, Foreground = Res<Brush>("SecondaryText"), Margin = new Thickness(0, 0, 5, 0) });
        label.Children.Add(new TextBlock { Text = title, Style = Res<Style>("CaptionText"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return label;
    }

    private static TextBlock RateText()
    {
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    private static StackPanel RateLabel(string symbol, TextBlock text, Color color, Thickness margin = default)
    {
        var brush = Theme.Frozen(color);
        text.Foreground = brush;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
        panel.Children.Add(new Icon { Symbol = symbol, Size = 11, Foreground = brush, Margin = new Thickness(0, 0, 3, 0) });
        panel.Children.Add(text);
        return panel;
    }

    private static StackPanel IconAndText(string symbol, string text, double iconSize = 14)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Icon { Symbol = symbol, Size = iconSize, Margin = new Thickness(0, 0, 7, 0) });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static Button GlassButton(string symbol, string text) => new()
    {
        Style = Res<Style>("GlassButton"),
        Padding = new Thickness(14, 7, 14, 7),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Content = IconAndText(symbol, text, 13),
    };

    /// <summary>Ô số liệu có biểu đồ mini (MetricTile của bản Mac).</summary>
    private sealed class MetricTile : Border
    {
        private readonly Icon _icon;
        private readonly TextBlock _title;
        private readonly TextBlock _value;
        private readonly TextBlock _detail;
        private readonly TrayChart _sparkline;

        public MetricTile()
        {
            CornerRadius = new CornerRadius(12);
            Background = Res<Brush>("CardBackground");
            BorderBrush = Res<Brush>("CardStroke");
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10);
            var stack = new StackPanel();
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            _icon = new Icon { Size = 12, Foreground = Res<Brush>("SecondaryText"), Margin = new Thickness(0, 0, 5, 0) };
            _title = new TextBlock { Style = Res<Style>("CaptionText"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(_icon);
            label.Children.Add(_title);
            stack.Children.Add(label);
            _value = new TextBlock { FontFamily = AppFonts.Display, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            Typography.SetNumeralAlignment(_value, FontNumeralAlignment.Tabular);
            // Chữ dài thì thu nhỏ (tối thiểu ~0.7 như bản Mac) thay vì cắt.
            stack.Children.Add(new Viewbox
            {
                Child = _value, StretchDirection = StretchDirection.DownOnly, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0), Height = 27,
            });
            _detail = new TextBlock { FontSize = 10, Foreground = Res<Brush>("TertiaryText"), TextWrapping = TextWrapping.NoWrap };
            Typography.SetNumeralAlignment(_detail, FontNumeralAlignment.Tabular);
            stack.Children.Add(new Viewbox
            {
                Child = _detail, StretchDirection = StretchDirection.DownOnly, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left,
                Height = 14,
            });
            _sparkline = new TrayChart { Height = 26, Margin = new Thickness(0, 4, 0, 0), UpperBound = 1 };
            stack.Children.Add(_sparkline);
            Child = stack;
        }

        public void Set(string symbol, string title, string value, string detail, IReadOnlyList<double>? history, Color tint) =>
            SetGlyph(Icons.Glyph(symbol), title, value, detail, history, tint);

        public void SetGlyph(string glyph, string title, string value, string detail, IReadOnlyList<double>? history, Color tint)
        {
            _icon.Text = glyph;
            _title.Text = title;
            _value.Text = value;
            _detail.Text = string.IsNullOrEmpty(detail) ? " " : detail;
            // Không có lịch sử (Thùng rác): giữ chỗ 26 px cho đều hàng.
            _sparkline.Values = history;
            _sparkline.Tint = tint;
        }
    }

    /// <summary>Vòng quay nhỏ khi đang tháo ổ.</summary>
    private sealed class Spinner : Grid
    {
        public Spinner()
        {
            Width = 14;
            Height = 14;
            var arc = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M7,1.25 A5.75,5.75 0 1 1 1.25,7"), Stroke = Brushes.White, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            var rotate = new RotateTransform(0, 7, 7);
            arc.RenderTransform = rotate;
            Children.Add(arc);
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    // MARK: Vị trí cạnh vùng thông báo

    private enum Edge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    private const uint MONITOR_DEFAULTTONEAREST = 2, MONITOR_DEFAULTTOPRIMARY = 1;
    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    private const uint ABM_GETTASKBARPOS = 5;

    private (RECT Work, RECT Bounds, Edge Edge, POINT Anchor) Placement()
    {
        var anchor = _anchor ?? default;
        var monitor = _anchor is { } a ? MonitorFromPoint(a, MONITOR_DEFAULTTONEAREST) : MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        var work = info.rcWork;
        var bounds = info.rcMonitor;
        Edge edge;
        if (work.Bottom < bounds.Bottom) edge = Edge.Bottom;
        else if (work.Top > bounds.Top) edge = Edge.Top;
        else if (work.Left > bounds.Left) edge = Edge.Left;
        else if (work.Right < bounds.Right) edge = Edge.Right;
        else
        {
            // Thanh tác vụ tự ẩn: hỏi vị trí thanh tác vụ và chừa chỗ cho nó.
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            edge = SHAppBarMessage(ABM_GETTASKBARPOS, ref data) != IntPtr.Zero ? (Edge)Math.Min(3, data.uEdge) : Edge.Bottom;
            var rc = data.rc;
            var onThisMonitor = rc.Right > bounds.Left && rc.Left < bounds.Right && rc.Bottom > bounds.Top && rc.Top < bounds.Bottom;
            if (onThisMonitor)
            {
                switch (edge)
                {
                    case Edge.Bottom: work.Bottom = Math.Min(work.Bottom, rc.Top); break;
                    case Edge.Top: work.Top = Math.Max(work.Top, rc.Bottom); break;
                    case Edge.Left: work.Left = Math.Max(work.Left, rc.Right); break;
                    case Edge.Right: work.Right = Math.Min(work.Right, rc.Left); break;
                }
            }
        }
        if (_anchor is null) anchor = new POINT { X = work.Right - 1, Y = work.Bottom - 1 };
        return (work, bounds, edge, anchor);
    }

    private Edge TaskbarEdge()
    {
        try { return Placement().Edge; } catch { return Edge.Bottom; }
    }

    /// <summary>Đặt popup sát mép thanh tác vụ, căn giữa theo điểm neo, không tràn khỏi vùng làm việc (tính bằng pixel thật).</summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible || !GetWindowRect(hwnd, out var rect)) return;
        var (work, _, edge, anchor) = Placement();
        var scale = GetDpiForWindow(hwnd) is var dpi and > 0 ? dpi / 96.0 : 1.0;
        var margin = (int)Math.Round(ShadowMargin * scale);
        var gap = (int)Math.Round(12 * scale);
        var cardW = rect.Right - rect.Left - 2 * margin;
        var cardH = rect.Bottom - rect.Top - 2 * margin;
        int x, y;
        switch (edge)
        {
            case Edge.Top:
                x = anchor.X - cardW / 2;
                y = work.Top + gap;
                break;
            case Edge.Left:
                x = work.Left + gap;
                y = anchor.Y - cardH / 2;
                break;
            case Edge.Right:
                x = work.Right - gap - cardW;
                y = anchor.Y - cardH / 2;
                break;
            default:
                x = anchor.X - cardW / 2;
                y = work.Bottom - gap - cardH;
                break;
        }
        x = Math.Max(work.Left + gap, Math.Min(x, work.Right - gap - cardW));
        y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - gap - cardH));
        SetWindowPos(hwnd, IntPtr.Zero, x - margin, y - margin, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
