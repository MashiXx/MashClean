using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Rules;

namespace CleanBoost.App.Screens;

/// <summary>
/// Cửa sổ Cài đặt (SettingsView của bản Mac), nền gradient trung tính, bốn tab: Chung, Quyền quản trị, Bỏ qua, Nâng cao.
/// Chỉ ghi <see cref="AppSettings"/>; khay hệ thống, khởi động cùng Windows và menu chuột phải tự phản ứng theo cài đặt.
/// </summary>
public sealed class SettingsWindow : ScreenWindow
{
    public enum Tab { General, Admin, Ignore, Advanced }

    private static SettingsWindow? s_current;

    private readonly AppEnvironment _env;
    private readonly AppSettings _s;
    /// <summary>Database cho danh sách bỏ qua (khi chụp ảnh kiểm thử có thể là database mẫu).</summary>
    private readonly Storage? _storage;
    private readonly AppLanguage _initialLanguage;
    private readonly Dictionary<Tab, RadioButton> _tabButtons = [];
    private readonly Dictionary<Tab, FrameworkElement> _pages = [];
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Dictionary<string, Action> _pendingCommits = [];
    /// <summary>Công tắc gắn với khoá cài đặt, để cập nhật khi tiến trình/màn khác đổi cùng khoá.</summary>
    private readonly List<(string Key, Func<bool> Get, CheckBox Box)> _bound = [];
    private Tab _tab;
    private bool _syncingTabs;

    // Tab Quyền quản trị
    private System.Windows.Shapes.Ellipse? _helperDot;
    private TextBlock? _helperText;
    private TextBlock? _helperDetail;
    private Button? _helperStart;
    private Button? _helperStop;
    private TextBlock? _adminMessage;
    private bool _helperBusy;

    // Tab Bỏ qua
    private StackPanel? _ignoreRows;

    // Tab Nâng cao
    private TextBlock? _rulesVersion;
    private TextBlock? _rulesSource;
    private TextBlock? _rulesResult;
    private Spinner? _rulesSpinner;
    private Button? _rulesCheck;
    private static string? s_lastRuleCheck;

    private SettingsWindow(AppEnvironment env) : base(Accent.Neutral, 640, 560)
    {
        _env = env;
        _s = env.Settings;
        _storage = ScreensSnapshot.DemoStorage() ?? env.Storage;
        _initialLanguage = _s.Language;
        Title = Loc.T("Cài đặt");
        ShowInTaskbar = false;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 12) };
        AddTab(tabs, Tab.General, "gearshape", Loc.T("Chung"));
        AddTab(tabs, Tab.Admin, "lock.shield", Loc.T("Quyền quản trị"));
        AddTab(tabs, Tab.Ignore, "eye.slash", Loc.T("Danh sách bỏ qua"));
        AddTab(tabs, Tab.Advanced, "slider.horizontal.3", Loc.T("Nâng cao"));
        root.Children.Add(tabs);
        root.Children.Add(CloseButton());

        var line = Kit.HLine(0x1F);
        Grid.SetRow(line, 1);
        root.Children.Add(line);
        Grid.SetRow(_host, 2);
        root.Children.Add(_host);
        Content = root;

        _commitTimer.Tick += (_, _) => FlushCommits();
        _s.Changed += OnSettingChanged;
        env.Helper.StatusChanged += OnHelperStatus;
        env.Rules.SnapshotChanged += OnRulesChanged;
        Closed += (_, _) =>
        {
            FlushCommits();
            _s.Changed -= OnSettingChanged;
            env.Helper.StatusChanged -= OnHelperStatus;
            env.Rules.SnapshotChanged -= OnRulesChanged;
            if (s_current == this) s_current = null;
        };
        // Esc đóng cửa sổ (sự kiện nổi bọt: ComboBox đang mở danh sách thì nó đóng danh sách trước).
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                var all = Enum.GetValues<Tab>();
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? all.Length - 1 : 1;
                Select(all[((int)_tab + step) % all.Length]);
                e.Handled = true;
            }
        };
        Select(Tab.General);
    }

    /// <summary>Mở (hoặc đưa lên trước) cửa sổ Cài đặt.</summary>
    public static SettingsWindow ShowFor(Window? owner, AppEnvironment env, Tab? tab = null)
    {
        if (s_current is { } existing)
        {
            if (tab is { } t) existing.Select(t);
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }
        var w = new SettingsWindow(env);
        if (tab is { } first) w.Select(first);
        if (owner is { IsVisible: true })
        {
            w.Owner = owner;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowInTaskbar = true;
        }
        s_current = w;
        w.Show();
        w.Activate();
        return w;
    }

    // MARK: Tab

    private void AddTab(Panel bar, Tab tab, string symbol, string title)
    {
        var content = new StackPanel();
        content.Children.Add(new Icon { Symbol = symbol, Size = 18, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = title, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        var rb = new RadioButton { Style = Kit.S("SettingsTab"), Content = content, GroupName = "settings-tabs" };
        System.Windows.Automation.AutomationProperties.SetName(rb, title);
        rb.Checked += (_, _) =>
        {
            if (!_syncingTabs) Select(tab);
        };
        _tabButtons[tab] = rb;
        bar.Children.Add(rb);
    }

    public Tab Current => _tab;

    public void Select(Tab tab)
    {
        var changed = _tab != tab || _host.Content is null;
        _tab = tab;
        if (_tabButtons[tab].IsChecked != true)
        {
            _syncingTabs = true;
            _tabButtons[tab].IsChecked = true;
            _syncingTabs = false;
        }
        if (!_pages.TryGetValue(tab, out var page))
        {
            page = tab switch
            {
                Tab.General => BuildGeneral(),
                Tab.Admin => BuildAdmin(),
                Tab.Ignore => BuildIgnore(),
                _ => BuildAdvanced(),
            };
            _pages[tab] = page;
        }
        switch (tab)
        {
            case Tab.Admin:
                UpdateHelper();
                break;
            case Tab.Ignore:
                ReloadIgnore();
                break;
        }
        if (!changed) return;
        _host.Content = page;
        Kit.FadeIn(page);
    }

    private static ScrollViewer Page(StackPanel content)
    {
        content.Margin = new Thickness(24, 2, 24, 24);
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    }

    // MARK: Khối dựng form

    private static TextBlock Section(string title) => new() { Text = title, Style = Kit.S("FormSectionHeader") };

    private static TextBlock Footnote(string text) => new() { Text = text, Style = Kit.S("FormFootnote") };

    /// <summary>Nhóm các hàng có đường kẻ mảnh ở giữa (Form .grouped của macOS).</summary>
    private static Border Group(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Kit.Divider, Margin = new Thickness(14, 0, 0, 0) });
            // Mọi hàng cao bằng nhau (công tắc, stepper, nút) như Form của macOS.
            if (rows[i].MinHeight < 30) rows[i].MinHeight = 30;
            stack.Children.Add(new Border { Padding = new Thickness(14, 8, 14, 8), Child = rows[i] });
        }
        return new Border { Style = Kit.S("FormGroup"), Child = stack };
    }

    /// <summary>Hàng: nhãn bên trái, điều khiển bên phải.</summary>
    private static DockPanel Row(FrameworkElement left, FrameworkElement? right = null)
    {
        var d = new DockPanel { LastChildFill = true, MinHeight = 28 };
        if (right is not null)
        {
            DockPanel.SetDock(right, Dock.Right);
            right.Margin = new Thickness(14, 0, 0, 0);
            right.VerticalAlignment = VerticalAlignment.Center;
            d.Children.Add(right);
        }
        left.VerticalAlignment = VerticalAlignment.Center;
        d.Children.Add(left);
        return d;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 13, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };

    private CheckBox BoundToggle(string key, string text, Func<bool> get, Action<bool> set)
    {
        var box = Kit.Toggle(text, get(), v =>
        {
            if (get() != v) set(v);
        });
        _bound.Add((key, get, box));
        return box;
    }

    /// <summary>Hàng có Stepper: nhãn cập nhật ngay, ghi cài đặt sau khi ngừng bấm 0,4 s.</summary>
    private FrameworkElement StepperRow(string key, Func<int, string> label, int value, int min, int max, int step, Action<int> commit)
    {
        var text = Label(label(value));
        var stepper = new Stepper(value, min, max, step);
        stepper.ValueChanged += v =>
        {
            text.Text = label(v);
            _pendingCommits[key] = () => commit(v);
            _commitTimer.Stop();
            _commitTimer.Start();
        };
        return Row(text, stepper);
    }

    private void FlushCommits()
    {
        _commitTimer.Stop();
        if (_pendingCommits.Count == 0) return;
        var commits = _pendingCommits.Values.ToList();
        _pendingCommits.Clear();
        foreach (var c in commits) c();
    }

    private void OnSettingChanged(string key) => Dispatcher.InvokeAsync(() =>
    {
        foreach (var (k, get, box) in _bound)
        {
            if (key != "*" && key != k) continue;
            var v = get();
            if (box.IsChecked != v) box.IsChecked = v;
        }
    });

    // MARK: Chung

    private FrameworkElement BuildGeneral()
    {
        var page = new StackPanel();

        // Ngôn ngữ (tên luôn song ngữ để nhận ra ở bất kỳ ngôn ngữ nào)
        page.Children.Add(Section(Loc.T("Ngôn ngữ · Language")));
        var languages = new[] { AppLanguage.System, AppLanguage.Vietnamese, AppLanguage.English };
        var combo = new ComboBox { Style = Kit.S("DarkComboBox"), Width = 210 };
        foreach (var l in languages) combo.Items.Add(l.DisplayName());
        combo.SelectedIndex = Array.IndexOf(languages, _s.Language);
        var restartText = new TextBlock { Text = Loc.T("Khởi động lại Clean Boost để áp dụng ngôn ngữ mới."), Style = Kit.S("CaptionText"), TextWrapping = TextWrapping.Wrap };
        var restart = Kit.Glass(Loc.T("Khởi động lại"), "arrow.clockwise", small: true);
        restart.Click += (_, _) =>
        {
            FlushCommits();
            App.Restart();
        };
        var restartRow = Row(restartText, restart);
        restartRow.Margin = new Thickness(0, 8, 0, 0);
        restartRow.Visibility = Visibility.Collapsed;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var lang = languages[combo.SelectedIndex];
            if (_s.Language != lang) _s.Language = lang;
            restartRow.Visibility = lang != _initialLanguage ? Visibility.Visible : Visibility.Collapsed;
        };
        var languageBlock = new StackPanel();
        languageBlock.Children.Add(Row(Label(Loc.T("Ngôn ngữ · Language")), combo));
        languageBlock.Children.Add(restartRow);
        page.Children.Add(Group(languageBlock));

        // Khay hệ thống (thanh menu của bản Mac)
        page.Children.Add(Section(Loc.T("Khay hệ thống")));
        var closeToTray = BoundToggle("tray.closeToTray", Loc.T("Đóng cửa sổ thì thu xuống khay thay vì thoát"), () => _s.CloseToTray, v => _s.CloseToTray = v);
        var startAtLogin = BoundToggle("startup.atLogin", Loc.T("Khởi động cùng Windows"), () => _s.StartAtLogin, v => _s.StartAtLogin = v);
        var tray = BoundToggle("menuBar.enabled", Loc.T("Hiện Clean Boost trên khay hệ thống"), () => _s.MenuBarEnabled, v => _s.MenuBarEnabled = v);
        void SyncTrayDependents()
        {
            closeToTray.IsEnabled = tray.IsChecked == true;
            startAtLogin.IsEnabled = tray.IsChecked == true;
        }
        tray.Checked += (_, _) => SyncTrayDependents();
        tray.Unchecked += (_, _) => SyncTrayDependents();
        SyncTrayDependents();
        page.Children.Add(Group(
            tray,
            closeToTray,
            startAtLogin,
            BoundToggle("alerts.lowDisk", Loc.T("Cảnh báo khi ổ đĩa sắp đầy (dưới 10% hoặc 10 GB)"), () => _s.LowDiskAlerts, v => _s.LowDiskAlerts = v),
            StepperRow("alerts.trashGB", v => Loc.F("Cảnh báo khi Thùng rác lớn hơn %lld GB", v), _s.TrashAlertGB, 1, 200, 1, v => _s.TrashAlertGB = v)));
        page.Children.Add(Footnote(Loc.T("Biểu tượng khay hiện RAM, CPU, dung lượng trống và cảnh báo bằng thông báo của Windows. Khởi động cùng Windows chỉ chạy biểu tượng khay, không mở cửa sổ.")));

        // Quét
        page.Children.Add(Section(Loc.T("Quét")));
        page.Children.Add(Group(
            StepperRow("largeFiles.thresholdMB", v => Loc.F("File lớn: từ %lld MB", v), _s.LargeFileThresholdMB, 10, 10_000, 50, v => _s.LargeFileThresholdMB = v),
            StepperRow("largeFiles.oldDays", v => Loc.F("File cũ: không dùng %lld ngày", v), _s.OldFileDays, 30, 3650, 30, v => _s.OldFileDays = v),
            BoundToggle("scan.showRisky", Loc.T("Hiện mục rủi ro (ví dụ gói ngôn ngữ)"), () => _s.ShowRiskyItems, v => _s.ShowRiskyItems = v)));

        // File Explorer (Finder Sync của bản Mac)
        page.Children.Add(Section("File Explorer"));
        page.Children.Add(Group(
            BoundToggle("explorer.contextMenu", Loc.T("Thêm \"Phân tích bằng Clean Boost\" vào menu chuột phải"), () => _s.ExplorerIntegration, v => _s.ExplorerIntegration = v)));

        // Cập nhật
        page.Children.Add(Section(Loc.T("Cập nhật")));
        var channel = new SegmentedPicker([("stable", Loc.T("Ổn định")), ("beta", "Beta")], _s.UpdateChannel == "beta" ? "beta" : "stable");
        channel.SelectionChanged += v =>
        {
            if (_s.UpdateChannel != v) _s.UpdateChannel = v;
        };
        page.Children.Add(Group(Row(Label(Loc.T("Kênh cập nhật")), channel)));
        return Page(page);
    }

    // MARK: Quyền quản trị

    private FrameworkElement BuildAdmin()
    {
        var page = new StackPanel();
        page.Children.Add(Section(Loc.T("Helper quản trị")));

        _helperDot = Kit.Dot(false);
        _helperDot.Margin = new Thickness(0, 6, 10, 0);
        _helperDot.VerticalAlignment = VerticalAlignment.Top;
        _helperText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        _helperDetail = new TextBlock { Style = Kit.S("CaptionText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel();
        text.Children.Add(_helperText);
        text.Children.Add(_helperDetail);
        var left = new DockPanel();
        DockPanel.SetDock(_helperDot, Dock.Left);
        left.Children.Add(_helperDot);
        left.Children.Add(text);

        _helperStart = Kit.Glass(Loc.T("Khởi động helper"), small: true);
        _helperStart.Click += async (_, _) => await StartHelperAsync();
        _helperStop = Kit.Glass(Loc.T("Dừng helper"), small: true);
        _helperStop.Margin = new Thickness(8, 0, 0, 0);
        _helperStop.Click += async (_, _) => await StopHelperAsync();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_helperStart);
        buttons.Children.Add(_helperStop);
        page.Children.Add(Group(Row(left, buttons)));
        page.Children.Add(Footnote(Loc.T("Helper chỉ nhận lệnh có tên từ Clean Boost cùng thư mục cài đặt, kiểm tra lại mọi đường dẫn trước khi xoá và ghi log mọi lệnh. Windows hỏi quyền (UAC) mỗi phiên làm việc; helper tự thoát khi bạn đóng Clean Boost hoặc sau 15 phút không dùng.")));

        page.Children.Add(Section("Clean Boost"));
        var elevated = HelperClient.IsProcessElevated;
        var dot = Kit.Dot(!elevated);
        dot.Margin = new Thickness(0, 0, 10, 0);
        var elevation = new StackPanel { Orientation = Orientation.Horizontal };
        elevation.Children.Add(dot);
        elevation.Children.Add(new TextBlock
        {
            Text = elevated ? Loc.T("Đang chạy với quyền quản trị") : Loc.T("Đang chạy với quyền người dùng thường"),
            FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
        });
        page.Children.Add(Group(Row(elevation)));
        page.Children.Add(Footnote(elevated
            ? Loc.T("Helper khởi động không cần hỏi UAC vì Clean Boost đã có quyền quản trị. Nên chạy Clean Boost với quyền thường: chỉ helper được nâng quyền, và chỉ khi cần.")
            : Loc.T("Đây là chế độ khuyến nghị: chỉ helper được nâng quyền, và chỉ khi cần.")));

        _adminMessage = new TextBlock { FontSize = 12, Foreground = Kit.Orange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 14, 4, 0), Visibility = Visibility.Collapsed };
        page.Children.Add(_adminMessage);
        UpdateHelper();
        return Page(page);
    }

    internal static string HelperStatusTitle(HelperStatus status) => status switch
    {
        HelperStatus.Running => Loc.T("Đang hoạt động"),
        HelperStatus.NotRunning => Loc.T("Chưa chạy"),
        HelperStatus.Declined => Loc.T("Đã từ chối"),
        HelperStatus.NotFound => Loc.T("Không tìm thấy"),
        _ => Loc.T("Không kết nối được"),
    };

    private static string HelperStatusDetail(HelperStatus status) => status switch
    {
        HelperStatus.Running => Loc.T("Helper đang chạy với quyền quản trị trong phiên này."),
        HelperStatus.NotRunning => Loc.T("Clean Boost sẽ hỏi quyền quản trị (UAC) khi cần."),
        HelperStatus.Declined => Loc.T("Lần gần nhất bạn đã từ chối hộp thoại UAC."),
        HelperStatus.NotFound => Loc.T("Không tìm thấy CleanBoost.Helper.exe cạnh ứng dụng. Hãy cài lại Clean Boost."),
        _ => Loc.T("Helper không phản hồi hoặc không qua kiểm tra an toàn."),
    };

    private void OnHelperStatus(HelperStatus _) => Dispatcher.InvokeAsync(UpdateHelper);

    private void UpdateHelper()
    {
        if (_helperText is null) return;
        var status = _env.Helper.Status;
        _helperDot!.Fill = status == HelperStatus.Running ? Theme.SafeBrush : Kit.Orange;
        _helperText.Text = _helperBusy ? Loc.T("Đang chờ Windows xác nhận…") : HelperStatusTitle(status);
        _helperDetail!.Text = HelperStatusDetail(status);
        _helperStart!.IsEnabled = !_helperBusy && status != HelperStatus.Running && status != HelperStatus.NotFound;
        _helperStop!.IsEnabled = !_helperBusy && status == HelperStatus.Running;
    }

    private void ShowAdminMessage(string? message)
    {
        if (_adminMessage is null) return;
        _adminMessage.Text = message ?? "";
        _adminMessage.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task StartHelperAsync()
    {
        if (_helperBusy) return;
        _helperBusy = true;
        ShowAdminMessage(null);
        UpdateHelper();
        try
        {
            var ok = await _env.Helper.EnsureRunningAsync();
            if (!ok) ShowAdminMessage(OnboardingWindow.HelperProblem(_env.Helper.Status) ?? Loc.T("Không kết nối được helper quản trị"));
        }
        catch (Exception ex)
        {
            ShowAdminMessage(ex.Message);
        }
        finally
        {
            _helperBusy = false;
            UpdateHelper();
            Activate();
        }
    }

    private async Task StopHelperAsync()
    {
        if (_helperBusy) return;
        _helperBusy = true;
        ShowAdminMessage(null);
        UpdateHelper();
        try
        {
            await Task.Run(_env.Helper.Shutdown);
        }
        catch (Exception ex)
        {
            ShowAdminMessage(ex.Message);
        }
        finally
        {
            _helperBusy = false;
            UpdateHelper();
        }
    }

    // MARK: Bỏ qua

    private FrameworkElement BuildIgnore()
    {
        var page = new StackPanel();
        page.Children.Add(new TextBlock
        {
            Text = Loc.T("Các mục bạn đã chọn \"Không bao giờ đề xuất\". Xoá khỏi danh sách để Clean Boost đề xuất lại."),
            Style = Kit.S("SecondaryBodyText"), Margin = new Thickness(4, 18, 4, 12), LineHeight = 19,
        });
        _ignoreRows = new StackPanel();
        page.Children.Add(new Border { Style = Kit.S("FormGroup"), Child = _ignoreRows });
        return Page(page);
    }

    private static Grid IgnoreGrid()
    {
        var g = new Grid { MinHeight = 30 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return g;
    }

    internal static string IgnoreKindTitle(string kind) => kind switch
    {
        IgnoreEntry.KindPath => Loc.T("Đường dẫn"),
        IgnoreEntry.KindRule => "Rule",
        IgnoreEntry.KindApp => Loc.T("Ứng dụng"),
        _ => kind,
    };

    private void ReloadIgnore()
    {
        if (_ignoreRows is null) return;
        _ignoreRows.Children.Clear();
        var header = IgnoreGrid();
        header.Margin = new Thickness(14, 6, 14, 4);
        header.Children.Add(new TextBlock { Text = Loc.T("Loại"), Style = Kit.S("TableHeaderText") });
        var valueHeader = new TextBlock { Text = Loc.T("Giá trị"), Style = Kit.S("TableHeaderText") };
        Grid.SetColumn(valueHeader, 1);
        header.Children.Add(valueHeader);
        _ignoreRows.Children.Add(header);

        List<IgnoreEntry> entries;
        string? error = null;
        try
        {
            entries = _storage?.IgnoreEntries() ?? [];
            if (_storage is null) error = Loc.T("Không mở được database");
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không đọc được danh sách bỏ qua", ex);
            entries = [];
            error = ex.Message;
        }

        if (entries.Count == 0)
        {
            _ignoreRows.Children.Add(Kit.HLine());
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 28) };
            empty.Children.Add(new Icon { Symbol = "eye.slash", Size = 26, Foreground = Kit.Tertiary, HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock
            {
                Text = error ?? Loc.T("Chưa có mục nào bị bỏ qua"), FontSize = 13, Foreground = Kit.Secondary, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
            if (error is null)
            {
                empty.Children.Add(new TextBlock
                {
                    Text = Loc.T("Bấm chuột phải vào một mục trong kết quả quét và chọn \"Không bao giờ đề xuất mục này\"."),
                    Style = Kit.S("TertiaryCaptionText"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
            _ignoreRows.Children.Add(empty);
            return;
        }

        foreach (var entry in entries)
        {
            _ignoreRows.Children.Add(Kit.HLine());
            var row = IgnoreGrid();
            row.Margin = new Thickness(14, 5, 10, 5);
            row.Children.Add(new TextBlock { Text = IgnoreKindTitle(entry.Kind), FontSize = 12.5, Foreground = Kit.Secondary, VerticalAlignment = VerticalAlignment.Center });
            var shown = entry.Kind == IgnoreEntry.KindPath ? entry.Value.AbbreviatingHome() : entry.Value;
            var value = new MiddleTrimmedText { Text = shown, ToolTip = entry.Value, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            var remove = Kit.Glass(Loc.T("Xoá"), small: true);
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.ToolTip = Loc.T("Xoá khỏi danh sách bỏ qua (không xoá file nào)");
            remove.Click += (_, _) =>
            {
                try
                {
                    _storage?.RemoveIgnore(entry.Id);
                    Log.Info(LogCategory.Storage, "Bỏ khỏi danh sách bỏ qua: " + entry.Kind);
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.Storage, "Không xoá được mục bỏ qua", ex);
                }
                ReloadIgnore();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            _ignoreRows.Children.Add(row);
        }
    }

    // MARK: Nâng cao

    private FrameworkElement BuildAdvanced()
    {
        var page = new StackPanel();

        page.Children.Add(Section(Loc.T("Bộ rule")));
        _rulesVersion = new TextBlock { FontSize = 13, Foreground = Kit.Secondary, Typography = { NumeralAlignment = FontNumeralAlignment.Tabular } };
        _rulesSource = new TextBlock { FontSize = 13, Foreground = Kit.Secondary };
        _rulesCheck = Kit.Glass(Loc.T("Kiểm tra rule mới"), "arrow.clockwise", small: true);
        _rulesCheck.Click += async (_, _) => await CheckRulesAsync();
        _rulesSpinner = new Spinner { Width = 14, Height = 14, Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        _rulesResult = new TextBlock { Style = Kit.S("CaptionText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Text = s_lastRuleCheck ?? "" };
        var checkRow = new DockPanel { MinHeight = 28 };
        DockPanel.SetDock(_rulesCheck, Dock.Left);
        DockPanel.SetDock(_rulesSpinner, Dock.Left);
        checkRow.Children.Add(_rulesCheck);
        checkRow.Children.Add(_rulesSpinner);
        checkRow.Children.Add(_rulesResult);
        page.Children.Add(Group(
            Row(Label(Loc.T("Phiên bản rule")), _rulesVersion),
            Row(Label(Loc.T("Nguồn")), _rulesSource),
            checkRow));
        UpdateRules();

        page.Children.Add(Section(Loc.T("Quyền riêng tư")));
        page.Children.Add(Group(
            BoundToggle("analytics.enabled", Loc.T("Gửi thống kê ẩn danh"), () => _s.AnalyticsEnabled, v => _s.AnalyticsEnabled = v)));
        page.Children.Add(Footnote(Loc.T("Chỉ số liệu tổng hợp (dung lượng dọn theo nhóm, thời gian quét, rule hay lỗi). Không bao giờ gửi đường dẫn hay tên file. Tắt mặc định.")));

        page.Children.Add(Section(Loc.T("Chế độ thử")));
        var dry = BoundToggle("debug.dryRun", Loc.T("Dry run: chạy toàn bộ luồng nhưng không xoá gì"), () => _s.DryRun, v => _s.DryRunSetting = v);
        if (DryRun.IsEnabledByEnvironment)
        {
            dry.IsEnabled = false;
            dry.ToolTip = Loc.T("Đang bật bằng biến môi trường hoặc tham số dòng lệnh");
        }
        page.Children.Add(Group(dry));
        page.Children.Add(Footnote(Loc.T("Cũng bật được bằng biến môi trường MASHCLEAN_DRY_RUN=1 hoặc tham số --dry-run.")));

        page.Children.Add(Section(Loc.T("Dữ liệu và log")));
        var dataFolder = _env.Storage is { } st ? Path.GetDirectoryName(st.DatabasePath) ?? KnownPaths.AppData : KnownPaths.AppData;
        var openData = Kit.Glass(Loc.T("Mở thư mục dữ liệu"), "folder", small: true);
        openData.Click += (_, _) => Kit.OpenFolder(dataFolder);
        var openLogs = Kit.Glass(Loc.T("Mở thư mục log"), "doc.text", small: true);
        openLogs.Margin = new Thickness(8, 0, 0, 0);
        openLogs.Click += (_, _) => Kit.OpenFolder(Log.Directory);
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal };
        folderButtons.Children.Add(openData);
        folderButtons.Children.Add(openLogs);
        page.Children.Add(Group(folderButtons));
        page.Children.Add(Footnote(Loc.F("Cài đặt, lịch sử dọn dẹp và bộ rule đã tải nằm trong %@.", dataFolder.AbbreviatingHome())));
        return Page(page);
    }

    private void OnRulesChanged(RuleSnapshot _) => Dispatcher.InvokeAsync(UpdateRules);

    private void UpdateRules()
    {
        if (_rulesVersion is null) return;
        var snap = _env.Rules.Snapshot;
        _rulesVersion.Text = snap.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _rulesSource!.Text = snap.SourceTitle;
    }

    private async Task CheckRulesAsync()
    {
        if (_rulesCheck is null) return;
        _rulesCheck.IsEnabled = false;
        _rulesSpinner!.Visibility = Visibility.Visible;
        _rulesResult!.Text = Loc.T("Đang kiểm tra…");
        try
        {
            var outcome = await Task.Run(() => _env.RuleUpdater.CheckIfNeededAsync(force: true));
            s_lastRuleCheck = outcome.Description;
            Log.Info(LogCategory.Rules, outcome.Description);
        }
        catch (Exception ex)
        {
            s_lastRuleCheck = Loc.F("Lỗi cập nhật rule: %@", ex.Message);
        }
        _rulesResult.Text = s_lastRuleCheck;
        _rulesSpinner.Visibility = Visibility.Collapsed;
        _rulesCheck.IsEnabled = true;
        UpdateRules();
    }

    // MARK: Chụp ảnh kiểm thử

    /// <summary>Chỉ dùng khi chụp ảnh: phần nội dung để lưu PNG.</summary>
    internal FrameworkElement SnapshotRoot => (FrameworkElement)Content;
}
