using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CleanBoost.App.Services;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.App.Screens;

/// <summary>
/// Onboarding lần đầu (OnboardingView của bản Mac), 640×520, nền gradient Smart Scan.
/// Bản Windows: giới thiệu + chọn ngôn ngữ → quyền quản trị (helper qua UAC, thay cho Full Disk Access + helper của Mac)
/// → sẵn sàng (khay hệ thống, cảnh báo ổ đầy, khởi động cùng Windows, menu chuột phải) → lưu cài đặt.
/// </summary>
public sealed class OnboardingWindow : ScreenWindow
{
    public enum Step { Welcome, Admin, Done }

    private static readonly Step[] Flow = [Step.Welcome, Step.Admin, Step.Done];

    private readonly AppEnvironment _env;
    private readonly AppSettings _settings;
    private readonly AppLanguage _initialLanguage;
    private readonly StepIndicator _indicator = new(Flow.Length);
    private readonly ContentControl _content = new() { Focusable = false };
    private Step _step;
    private bool _helperBusy;
    private HelperStatus? _statusOverride;

    // Lựa chọn ở bước cuối, chỉ lưu khi bấm "Bắt đầu dùng".
    private bool _tray;
    private bool _lowDisk;
    private bool _startAtLogin;
    private bool _explorer;

    /// <summary>Đã bấm "Bắt đầu dùng" (đóng cửa sổ bằng cách khác thì lần sau hiện lại).</summary>
    public bool Completed { get; private set; }

    public OnboardingWindow(AppEnvironment env) : base(Accent.SmartScan, 640, 520)
    {
        _env = env;
        _settings = env.Settings;
        _initialLanguage = _settings.Language;
        _tray = _settings.MenuBarEnabled;
        _lowDisk = _settings.LowDiskAlerts;
        _startAtLogin = _settings.StartAtLogin;
        _explorer = _settings.ExplorerIntegration;
        Title = Loc.T("Chào mừng đến với Clean Boost");

        var root = new Grid { Margin = new Thickness(32) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _indicator.Margin = new Thickness(0, 0, 0, 22);
        root.Children.Add(_indicator);
        Grid.SetRow(_content, 2);
        root.Children.Add(_content);
        Content = root;

        // Không có nút huỷ: Esc không đóng onboarding (giống sheet của Mac); Alt+F4 đóng được và lần mở app sau hiện lại.
        env.Helper.StatusChanged += OnHelperStatus;
        Closed += (_, _) => env.Helper.StatusChanged -= OnHelperStatus;
        GoTo(Step.Welcome, animate: false);
    }

    /// <summary>Có nên mở onboarding không: chưa hoàn tất, và không ở chế độ chụp ảnh tự động (trừ khi yêu cầu riêng).</summary>
    public static bool ShouldShow(AppSettings settings)
    {
        if (settings.OnboardingCompleted) return false;
        if (Shell.DebugSnapshot.IsEnabled) return Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_ONBOARDING") == "1";
        return true;
    }

    /// <summary>Hiện onboarding dạng hộp thoại trên cửa sổ chính.</summary>
    public static void ShowModal(Window owner, AppEnvironment env)
    {
        var w = new OnboardingWindow(env);
        if (owner.IsVisible) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Log.Info(LogCategory.UI, "Mở onboarding");
        w.ShowDialog();
        Log.Info(LogCategory.UI, w.Completed ? "Onboarding hoàn tất" : "Onboarding đóng giữa chừng");
    }

    // MARK: Điều hướng

    public Step Current => _step;

    public void GoTo(Step step, bool animate = true)
    {
        _step = step;
        _indicator.Set(Array.IndexOf(Flow, step));
        _content.Content = step switch
        {
            Step.Welcome => BuildWelcome(),
            Step.Admin => BuildAdmin(),
            _ => BuildDone(),
        };
        if (animate) Kit.FadeIn(_content);
    }

    /// <summary>Chỉ dùng khi chụp ảnh kiểm thử: hiển thị bước quản trị với một trạng thái helper giả định.</summary>
    internal void PreviewHelperStatus(HelperStatus? status)
    {
        _statusOverride = status;
        if (_step == Step.Admin) GoTo(Step.Admin, animate: false);
    }

    private HelperStatus HelperStatus => _statusOverride ?? _env.Helper.Status;

    private void OnHelperStatus(HelperStatus status) => Dispatcher.InvokeAsync(() =>
    {
        if (_step == Step.Admin && !_helperBusy) GoTo(Step.Admin, animate: false);
    });

    // MARK: Bước 1: chào mừng + ngôn ngữ

    private FrameworkElement BuildWelcome()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Kit.Header("sparkles", Loc.T("Chào mừng đến với Clean Boost"),
            Loc.T("Dọn dẹp an toàn, minh bạch: mọi mục đề xuất xoá đều giải thích được, file của bạn mặc định đi qua Thùng rác.")));

        var languageRow = new DockPanel { Width = 360, Margin = new Thickness(0, 26, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var combo = new ComboBox { Style = Kit.S("DarkComboBox"), Width = 210, VerticalAlignment = VerticalAlignment.Center };
        var languages = new[] { AppLanguage.System, AppLanguage.Vietnamese, AppLanguage.English };
        foreach (var l in languages) combo.Items.Add(l.DisplayName());
        combo.SelectedIndex = Array.IndexOf(languages, _settings.Language);
        DockPanel.SetDock(combo, Dock.Right);
        languageRow.Children.Add(combo);
        languageRow.Children.Add(new TextBlock
        {
            Text = Loc.T("Ngôn ngữ · Language"), FontSize = 13, Foreground = Kit.Secondary, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        stack.Children.Add(languageRow);

        var buttonHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0), Focusable = false };
        stack.Children.Add(buttonHost);
        void UpdateButton()
        {
            Button b;
            if (_settings.Language != _initialLanguage)
            {
                // Đổi ngôn ngữ ngay ở bước đầu: mở lại app, onboarding tiếp tục bằng ngôn ngữ mới.
                b = Kit.Primary(Loc.T("Khởi động lại · Restart"));
                b.Click += (_, _) => App.Restart();
            }
            else
            {
                b = Kit.Primary(Loc.T("Bắt đầu"));
                b.Click += (_, _) => GoTo(Step.Admin);
            }
            b.IsDefault = true;
            buttonHost.Content = b;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var lang = languages[combo.SelectedIndex];
            if (_settings.Language != lang) _settings.Language = lang;
            UpdateButton();
        };
        UpdateButton();
        return stack;
    }

    // MARK: Bước 2: quyền quản trị (helper)

    private FrameworkElement BuildAdmin()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Kit.Header("lock.shield", Loc.T("Quyền quản trị"),
            Loc.T("Clean Boost chạy với quyền người dùng thường. Cache hệ thống, bản cập nhật Windows đã tải và vài tác vụ bảo trì cần quyền quản trị: khi đó app mở một helper nhỏ qua hộp thoại UAC."),
            iconSize: 46, subtitleWidth: 560));

        var guide = new StackPanel();
        guide.Children.Add(GuideLine(1, Loc.T("Helper chỉ nhận lệnh có tên cụ thể từ Clean Boost (xoá mục đã kiểm tra, chạy tác vụ bảo trì), không bao giờ chạy lệnh tuỳ ý.")));
        guide.Children.Add(GuideLine(2, Loc.T("Mọi đường dẫn được kiểm tra lại trước khi xoá; thư mục hệ thống quan trọng luôn bị chặn.")));
        guide.Children.Add(GuideLine(3, Loc.T("Windows hỏi quyền mỗi phiên làm việc. Helper tự thoát khi bạn đóng Clean Boost hoặc sau 15 phút không dùng."), last: true));
        stack.Children.Add(new Border
        {
            Style = Kit.S("Box"), Child = guide, Width = 560, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 12, 14, 12),
        });

        var status = HelperStatus;
        var statusLine = new ContentControl { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), Focusable = false };
        if (_helperBusy)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(new Spinner { Width = 14, Height = 14, Margin = new Thickness(0, 0, 8, 0) });
            p.Children.Add(Kit.Text(Loc.T("Đang chờ Windows xác nhận…"), 13, Kit.Secondary));
            statusLine.Content = p;
        }
        else if (status == HelperStatus.Running)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(new Icon { Symbol = "checkmark.seal.fill", Size = 15, Foreground = Theme.SafeBrush, Margin = new Thickness(0, 1, 8, 0) });
            p.Children.Add(Kit.Text(Loc.T("Helper đã sẵn sàng"), 13, Kit.White, FontWeights.SemiBold));
            statusLine.Content = p;
        }
        else if (HelperProblem(status) is { } problem)
        {
            statusLine.Content = new TextBlock
            {
                Text = problem, FontSize = 12, Foreground = Theme.ReviewBrush, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 560,
            };
        }
        else
        {
            statusLine.Content = Kit.Text(Loc.T("Bạn có thể khởi động helper ngay để kiểm tra, hoặc để Clean Boost hỏi khi cần."), 12, Kit.Tertiary);
        }
        stack.Children.Add(statusLine);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        var later = Kit.Glass(Loc.T("Để sau"));
        later.Click += (_, _) => GoTo(Step.Done);
        later.Margin = new Thickness(0, 0, 12, 0);
        buttons.Children.Add(later);
        Button primary;
        if (status == HelperStatus.Running && !_helperBusy)
        {
            later.Visibility = Visibility.Collapsed;
            primary = Kit.Primary(Loc.T("Tiếp tục"));
            primary.Click += (_, _) => GoTo(Step.Done);
        }
        else
        {
            primary = Kit.Primary(Loc.T("Khởi động helper"));
            primary.IsEnabled = !_helperBusy && status != HelperStatus.NotFound;
            primary.Click += async (_, _) => await StartHelperAsync();
        }
        primary.IsDefault = true;
        buttons.Children.Add(primary);
        stack.Children.Add(buttons);
        return stack;
    }

    /// <summary>Mô tả vấn đề của helper để hiện cho người dùng (null nếu không có gì cần báo).</summary>
    internal static string? HelperProblem(HelperStatus status) => status switch
    {
        HelperStatus.Declined => Loc.T("Bạn đã không cấp quyền quản trị. Các mục cần quyền này sẽ được bỏ qua; bạn có thể thử lại trong Cài đặt."),
        HelperStatus.NotFound => Loc.T("Không tìm thấy CleanBoost.Helper.exe cạnh ứng dụng. Hãy cài lại Clean Boost."),
        HelperStatus.Unreachable => Loc.T("Không kết nối được helper quản trị"),
        _ => null,
    };

    private async Task StartHelperAsync()
    {
        if (_helperBusy) return;
        _helperBusy = true;
        _statusOverride = null;
        GoTo(Step.Admin, animate: false);
        try
        {
            await _env.Helper.EnsureRunningAsync();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Ipc, "Không khởi động được helper từ onboarding", ex);
        }
        finally
        {
            _helperBusy = false;
        }
        if (_step == Step.Admin) GoTo(Step.Admin, animate: false);
        Activate();
    }

    private static FrameworkElement GuideLine(int n, string text, bool last = false)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, last ? 0 : 7) };
        var number = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Theme.Frozen(Color.FromArgb(0x40, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock { Text = n.ToString(), FontSize = 11.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(number, Dock.Left);
        row.Children.Add(number);
        row.Children.Add(new TextBlock { Text = text, FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 1, 0, 0) });
        return row;
    }

    // MARK: Bước 3: sẵn sàng

    private FrameworkElement BuildDone()
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Kit.Header("checkmark.circle", Loc.T("Sẵn sàng quét"), Loc.T("Mọi thứ đã sẵn sàng.")));

        var toggles = new StackPanel();
        CheckBox? startup = null;
        toggles.Children.Add(Row(Kit.Toggle(Loc.T("Hiện Clean Boost trên khay hệ thống (RAM, CPU, dung lượng trống)"), _tray, v =>
        {
            _tray = v;
            if (startup is not null) startup.IsEnabled = v;
        })));
        toggles.Children.Add(Row(Kit.Toggle(Loc.T("Cảnh báo khi ổ đĩa sắp đầy"), _lowDisk, v => _lowDisk = v)));
        startup = Kit.Toggle(Loc.T("Khởi động cùng Windows"), _startAtLogin, v => _startAtLogin = v);
        startup.IsEnabled = _tray;
        startup.ToolTip = Loc.T("Clean Boost chạy nền trên khay hệ thống khi bạn đăng nhập.");
        toggles.Children.Add(Row(startup));
        toggles.Children.Add(Row(Kit.Toggle(Loc.T("Thêm \"Phân tích bằng Clean Boost\" vào menu chuột phải"), _explorer, v => _explorer = v), last: true));
        stack.Children.Add(new Border { Style = Kit.S("Box"), Child = toggles, Width = 480, Margin = new Thickness(0, 22, 0, 0), Padding = new Thickness(16, 12, 16, 12) });

        var start = Kit.Primary(Loc.T("Bắt đầu dùng"));
        start.IsDefault = true;
        start.HorizontalAlignment = HorizontalAlignment.Center;
        start.Margin = new Thickness(0, 22, 0, 0);
        start.Click += (_, _) => Finish();
        stack.Children.Add(start);
        return stack;

        static FrameworkElement Row(FrameworkElement toggle, bool last = false)
        {
            toggle.Margin = new Thickness(0, 0, 0, last ? 0 : 10);
            return toggle;
        }
    }

    /// <summary>Lưu lựa chọn (tiến trình khay/tích hợp Explorer tự phản ứng theo cài đặt) và đánh dấu đã hoàn tất.</summary>
    private void Finish()
    {
        if (_settings.MenuBarEnabled != _tray) _settings.MenuBarEnabled = _tray;
        if (_settings.LowDiskAlerts != _lowDisk) _settings.LowDiskAlerts = _lowDisk;
        if (_settings.StartAtLogin != _startAtLogin) _settings.StartAtLogin = _startAtLogin;
        if (_settings.ExplorerIntegration != _explorer) _settings.ExplorerIntegration = _explorer;
        _settings.OnboardingCompleted = true;
        Completed = true;
        Log.Info(LogCategory.UI, $"Onboarding: khay={_tray}, cảnh báo ổ đầy={_lowDisk}, khởi động cùng Windows={_startAtLogin}, menu chuột phải={_explorer}");
        Close();
    }
}
