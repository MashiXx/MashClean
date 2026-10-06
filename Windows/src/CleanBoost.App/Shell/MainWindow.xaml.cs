using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;

namespace CleanBoost.App.Shell;

/// <summary>
/// Cửa sổ chính (RootView của bản Mac): thanh bên nổi bên trái, vùng chi tiết nền gradient theo tính năng chạy tới mép
/// trên. Mọi màn đã mở được giữ sống để kết quả quét không mất khi chuyển màn.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppEnvironment _env;
    private readonly AppRouter _router;
    private readonly ScreenFactory _screens;
    private readonly Dictionary<SidebarItem, FrameworkElement> _visited = [];
    private readonly Dictionary<SidebarItem, RadioButton> _buttons = [];
    private bool _backgroundAOnTop = true;
    private Accent? _currentAccent;

    /// <summary>Đóng cửa sổ: thu xuống khay thay vì thoát (nếu bật biểu tượng khay).</summary>
    public Func<bool>? ShouldHideOnClose { get; set; }

    public MainWindow(AppEnvironment env, AppRouter router)
    {
        _env = env;
        _router = router;
        _screens = new ScreenFactory(env, router);
        InitializeComponent();
        ApplySidebarTheme();
        BuildSidebar();
        _router.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppRouter.Selection)) Show(_router.Selection);
        };
        _router.AutoStartRequested += item => Dispatcher.InvokeAsync(() =>
        {
            Show(item);
            if (_visited.TryGetValue(item, out var view) && view is IAutoStartScreen auto) auto.AutoStart();
        });
        Show(_router.Selection);
        UpdateFooter();
        CleanEngine.DidClean += () => Dispatcher.InvokeAsync(UpdateFooter);
        Activated += (_, _) => UpdateFooter();
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        SourceInitialized += (_, _) => EnableRoundedCorners();
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _router.Open(SidebarItem.SmartScan, autoStart: true)), Key.D1, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _router.Open(SidebarItem.SystemJunk, autoStart: true)), Key.D2, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _router.Open(SidebarItem.SpaceLens)), Key.D3, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => _router.Open(SidebarItem.Uninstaller)), Key.D4, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => OnSettings(this, new RoutedEventArgs())), Key.OemComma, ModifierKeys.Control));
    }

    /// <summary>Mở cửa sổ Cài đặt (được App cài khi có màn Cài đặt).</summary>
    public static Action<Window>? OpenSettings { get; set; }

    // MARK: Thanh bên

    private void ApplySidebarTheme()
    {
        var light = Theme.SystemUsesLightTheme;
        var accent = Theme.SystemAccent;
        Resources["SidebarBackground"] = Theme.Frozen(light ? Color.FromArgb(0xE0, 0xF6, 0xF2, 0xFC) : Color.FromArgb(0xD9, 0x1E, 0x1C, 0x2A));
        Resources["SidebarText"] = Theme.Frozen(light ? Color.FromRgb(0x1D, 0x1B, 0x26) : Colors.White);
        Resources["SidebarSecondaryText"] = Theme.Frozen(light ? Color.FromArgb(0x99, 0x1D, 0x1B, 0x26) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        Resources["SidebarDivider"] = Theme.Frozen(light ? Color.FromArgb(0x1F, 0, 0, 0) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        Resources["SidebarHover"] = Theme.Frozen(light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        Resources["SidebarSelected"] = Theme.Frozen(accent);
        Sidebar.Background = (Brush)Resources["SidebarBackground"];
    }

    private void BuildSidebar()
    {
        NavItems.Children.Clear();
        foreach (var (header, items) in Services.SidebarItems.Sections)
        {
            if (!string.IsNullOrEmpty(header))
            {
                NavItems.Children.Add(new TextBlock
                {
                    Text = header, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 14, 0, 4),
                    Foreground = (Brush)Resources["SidebarSecondaryText"],
                });
            }
            foreach (var item in items)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new Icon { Symbol = item.Symbol(), Size = 16, Width = 22, Margin = new Thickness(0, 0, 10, 0) });
                content.Children.Add(new TextBlock { Text = item.Title(), VerticalAlignment = VerticalAlignment.Center });
                var button = new RadioButton { Content = content, Style = (Style)FindResource("SidebarItem"), GroupName = "nav", Tag = item };
                button.Checked += (_, _) => _router.Selection = item;
                _buttons[item] = button;
                NavItems.Children.Add(button);
            }
        }
    }

    private void UpdateFooter()
    {
        LanguageText.Text = _env.Settings.Language.DisplayName();
        var dry = _env.Settings.DryRun;
        DryRunNotice.Visibility = dry ? Visibility.Visible : Visibility.Collapsed;
        DryRunText.Text = Loc.T("Đang ở chế độ thử");
        if (_env.FileSystem.SystemVolume is { } vol)
        {
            VolumeName.Text = vol.Name;
            VolumeBar.Value = vol.UsedFraction;
            VolumeBar.Foreground = vol.UsedFraction > 0.9 ? Theme.RiskyBrush : (Brush)Resources["SidebarSelected"];
            VolumeText.Text = Loc.F("Còn trống %@ / %@", ByteCount.Format(vol.Available), ByteCount.Format(vol.Total));
        }
    }

    private void OnLanguage(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var lang in new[] { AppLanguage.System, AppLanguage.Vietnamese, AppLanguage.English })
        {
            var item = new MenuItem { Header = lang.DisplayName(), IsChecked = _env.Settings.Language == lang };
            item.Click += (_, _) =>
            {
                if (_env.Settings.Language == lang) return;
                if (!MessageDialog.Show(this, "Khởi động lại Clean Boost để đổi ngôn ngữ? · Restart Clean Boost to change the language?", "",
                        "Khởi động lại · Restart", "Huỷ · Cancel")) return;
                _env.Settings.Language = lang;
                App.Restart();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = LanguageButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings?.Invoke(this);

    // MARK: Điều hướng

    public void Show(SidebarItem item)
    {
        if (_buttons.TryGetValue(item, out var b) && b.IsChecked != true) b.IsChecked = true;
        if (!_visited.TryGetValue(item, out var view))
        {
            view = _screens.Create(item);
            _visited[item] = view;
            DetailHost.Children.Add(view);
        }
        foreach (var (key, v) in _visited)
        {
            v.Visibility = key == item ? Visibility.Visible : Visibility.Collapsed;
            v.IsHitTestVisible = key == item;
        }
        SetAccent(view is IAccentScreen a ? a.Accent : Accent.Neutral);
    }

    /// <summary>Đổi nền gradient với hiệu ứng mờ dần (màn có thể gọi khi đổi màu chủ đạo).</summary>
    public void SetAccent(Accent accent)
    {
        if (_currentAccent == accent) return;
        var first = _currentAccent is null;
        _currentAccent = accent;
        Theme.Apply(this, accent);
        var top = _backgroundAOnTop ? BackgroundB : BackgroundA;
        var bottom = _backgroundAOnTop ? BackgroundA : BackgroundB;
        top.Background = Theme.Background(accent);
        Panel.SetZIndex(top, 1);
        Panel.SetZIndex(bottom, 0);
        if (first)
        {
            top.Opacity = 1;
        }
        else
        {
            top.Opacity = 0;
            top.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        }
        _backgroundAOnTop = !_backgroundAOnTop;
    }

    // MARK: Nút tiêu đề

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (ShouldHideOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // Khi phóng to, WindowChrome tràn ra ngoài màn hình vài pixel: bù lại.
        Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Bo góc cửa sổ trên Windows 11.</summary>
    private void EnableRoundedCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Windows 10: không hỗ trợ, bỏ qua.
        }
    }

    /// <summary>Màn đã mở không còn quét/dọn.</summary>
    public bool IsScreenIdle(SidebarItem item) => !_visited.TryGetValue(item, out var v) || v is not IBusyScreen b || !b.IsBusy;

    /// <summary>Đưa cửa sổ lên trước.</summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
