using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.App.Shared;
using CleanBoost.App.Shell;
using CleanBoost.Core;
using CleanBoost.Engine.Metrics;
using CleanBoost.Engine.Persistence;
using Forms = System.Windows.Forms;

namespace CleanBoost.App.Tray;

/// <summary>
/// Biểu tượng khay hệ thống (MenuBarController + AlertNotifier của bản Mac): bấm trái mở/đóng popup số liệu, bấm phải mở menu,
/// biểu tượng vẽ số liệu theo <c>menuBar.statusStyle</c>, chú thích kiểu "Clean Boost — CPU 12% · RAM 48% · …",
/// cảnh báo bằng thông báo Windows (bấm vào mở Smart Scan). Bật/tắt và đổi kiểu ngay khi cài đặt đổi.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly TrayModel _model;
    private readonly TrayIconRenderer _renderer = new();
    private Forms.NotifyIcon? _notifyIcon;
    private TrayPopupWindow? _popup;
    private HwndSource? _messageWindow;
    private DispatcherTimer? _volumeDebounce;
    private bool _started;

    public TrayController(AppEnvironment env, Dispatcher dispatcher)
    {
        _settings = env.Settings;
        _dispatcher = dispatcher;
        _model = new TrayModel(_settings, env.Storage, dispatcher);
        _model.Changed += UpdateIcon;
        _model.AlertRaised += ShowAlert;
    }

    public TrayModel Model => _model;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _settings.Changed += OnSettingsChanged;
        ApplySettings();
    }

    private void OnSettingsChanged(string key)
    {
        if (key is "menuBar.enabled" or "menuBar.statusStyle" or "tray.closeToTray" or "*")
            _dispatcher.InvokeAsync(ApplySettings);
    }

    /// <summary>Áp dụng cài đặt: hiện/ẩn biểu tượng, kiểu biểu tượng, đóng cửa sổ thì thu xuống khay.</summary>
    private void ApplySettings()
    {
        if (!_started) return;
        var enabled = _settings.MenuBarEnabled;
        App.KeepRunningInTray = enabled && _settings.CloseToTray;
        _model.Style = TrayStatusStyle.Parse(_settings.MenuBarStatusStyle);
        if (enabled && _notifyIcon is null) Enable();
        else if (!enabled && _notifyIcon is not null) Disable();
        UpdateIcon();
    }

    private void Enable()
    {
        _renderer.Invalidate();
        _notifyIcon = new Forms.NotifyIcon { Text = "Clean Boost" };
        _notifyIcon.MouseClick += OnIconClick;
        _notifyIcon.BalloonTipClicked += (_, _) => Perform(TrayAction.SmartScan);
        UpdateIcon();
        _notifyIcon.Visible = true;
        EnsureMessageWindow();
        _model.Start();
        Log.Info(LogCategory.Menu, "Đã bật biểu tượng khay hệ thống");
    }

    private void Disable()
    {
        _popup?.HidePopup();
        _model.Stop();
        if (_notifyIcon is { } icon)
        {
            icon.Visible = false;
            icon.Dispose();
        }
        _notifyIcon = null;
        Log.Info(LogCategory.Menu, "Đã tắt biểu tượng khay hệ thống");
        // Đang chạy nền (không cửa sổ nào hiện) mà tắt biểu tượng thì không còn cách mở lại: thoát.
        if (!Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible)) Application.Current.Shutdown();
    }

    // MARK: Biểu tượng và chú thích

    private void UpdateIcon()
    {
        if (_notifyIcon is not { } icon) return;
        var snap = _model.Snapshot;
        var memory = snap is { Memory.Total: > 0 } ? snap.Memory.UsedFraction : (double?)null;
        var changed = _renderer.Update(_model.Style, snap?.Cpu?.Usage, memory, snap?.Memory.Pressure == MemoryPressure.Critical,
            TrayIconRenderer.PreferredSize());
        if ((changed || icon.Icon is null) && _renderer.Icon is { } rendered) icon.Icon = rendered;
        var text = _model.StatusText;
        if (text.Length > 127) text = text[..127];
        if (icon.Text != text) icon.Text = text;
    }

    private void ShowAlert(TrayAlertKind kind, string body)
    {
        if (_notifyIcon is not { } icon) return;
        // Windows 10/11 hiện balloon dưới dạng thông báo (toast); bấm vào → BalloonTipClicked → Smart Scan.
        icon.ShowBalloonTip(10_000, kind.Title(), body, kind == TrayAlertKind.LargeTrash ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
    }

    // MARK: Bấm biểu tượng

    private void OnIconClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) TogglePopup(Forms.Cursor.Position);
        else if (e.Button == Forms.MouseButtons.Right) ShowContextMenu(Forms.Cursor.Position);
    }

    private TrayPopupWindow EnsurePopup()
    {
        if (_popup is not null) return _popup;
        _popup = new TrayPopupWindow(_model, _settings);
        _popup.ActionRequested += Perform;
        _popup.Closed += (_, _) => _popup = null;
        return _popup;
    }

    private void TogglePopup(System.Drawing.Point anchor)
    {
        var popup = EnsurePopup();
        if (popup.IsVisible)
        {
            popup.HidePopup();
            return;
        }
        // Bấm vào biểu tượng lúc popup đang mở: popup đã tự ẩn vì mất focus ngay trước cú bấm này — không mở lại.
        if (DateTime.UtcNow - popup.HiddenAt < TimeSpan.FromMilliseconds(400)) return;
        popup.ShowAt(anchor);
    }

    /// <summary>Mở popup không cần bấm (chụp ảnh kiểm thử).</summary>
    internal TrayPopupWindow OpenPopup(bool stayOpen)
    {
        var popup = EnsurePopup();
        popup.StayOpen = stayOpen;
        popup.ShowAt(null);
        return popup;
    }

    // MARK: Menu chuột phải

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>Menu chuột phải của biểu tượng khay.</summary>
    internal ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu { StaysOpen = false };
        MenuItem Item(string header, TrayAction action, bool bold = false)
        {
            var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            item.Click += (_, _) => Perform(action);
            return item;
        }
        menu.Items.Add(Item(Loc.T("Mở Clean Boost"), TrayAction.OpenApp, bold: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Smart Scan", TrayAction.SmartScan));
        menu.Items.Add(Item(Loc.T("Dọn rác"), TrayAction.CleanJunk));
        menu.Items.Add(Item(Loc.T("Giải phóng RAM"), TrayAction.FreeRam));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Cài đặt…"), TrayAction.Settings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Loc.T("Thoát"), TrayAction.Quit));
        return menu;
    }

    private void ShowContextMenu(System.Drawing.Point cursor)
    {
        _popup?.HidePopup();
        var menu = BuildContextMenu();

        // Vị trí con trỏ (pixel) → DIP theo DPI của màn hình chứa con trỏ; mở về phía trong màn hình so với thanh tác vụ.
        var scale = 1.0;
        try
        {
            if (GetDpiForMonitor(MonitorFromPoint(cursor, 2), 0, out var dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
        }
        catch
        {
            // Windows cũ: coi như 96 DPI
        }
        var screen = Forms.Screen.FromPoint(cursor);
        menu.Placement = screen.WorkingArea.Bottom < screen.Bounds.Bottom ? PlacementMode.Top
            : screen.WorkingArea.Top > screen.Bounds.Top ? PlacementMode.Bottom
            : screen.WorkingArea.Left > screen.Bounds.Left ? PlacementMode.Right
            : screen.WorkingArea.Right < screen.Bounds.Right ? PlacementMode.Left
            : PlacementMode.Top;
        menu.PlacementRectangle = new Rect(cursor.X / scale, cursor.Y / scale, 0, 0);
        menu.Opened += (_, _) =>
        {
            // Menu không có cửa sổ cha: đưa nó lên trước để bấm ra ngoài thì menu đóng.
            if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
        };
        menu.IsOpen = true;
    }

    // MARK: Hành động

    private void Perform(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.OpenApp:
                App.ShowMainWindow();
                break;
            case TrayAction.SmartScan:
                App.ShowMainWindow();
                App.Router.Open(SidebarItem.SmartScan, autoStart: true);
                break;
            case TrayAction.CleanJunk:
                App.ShowMainWindow();
                App.Router.Open(SidebarItem.SystemJunk, autoStart: true);
                break;
            case TrayAction.FreeRam:
                App.ShowMainWindow();
                App.Router.HandleUrl(Identifiers.UrlScheme + "://maintenance?task=" + MaintenanceTaskName.FreeRAM.RawValue());
                break;
            case TrayAction.Settings:
                App.ShowMainWindow();
                if (App.MainView is { } window) MainWindow.OpenSettings?.Invoke(window);
                break;
            case TrayAction.Quit:
                Quit();
                break;
        }
    }

    /// <summary>Thoát hẳn app; hỏi lại nếu đang quét/dọn.</summary>
    private void Quit()
    {
        var busy = App.MainView is { } window && Enum.GetValues<SidebarItem>().Any(item => !window.IsScreenIdle(item));
        if (busy)
        {
            _popup?.HidePopup();
            App.ShowMainWindow();
            if (!MessageDialog.Show(App.MainView, Loc.T("Clean Boost đang làm việc"),
                    Loc.T("Đang quét hoặc dọn dẹp. Thoát bây giờ sẽ dừng việc đang làm."), Loc.T("Thoát"), Loc.T("Huỷ"), destructive: true))
                return;
        }
        Application.Current.Shutdown();
    }

    // MARK: Ổ đĩa cắm/rút

    /// <summary>Cửa sổ ẩn nhận WM_DEVICECHANGE để cập nhật danh sách ổ ngoài khi cắm/rút (như didMount/didUnmount của Mac).</summary>
    private void EnsureMessageWindow()
    {
        if (_messageWindow is not null) return;
        try
        {
            _messageWindow = new HwndSource(new HwndSourceParameters("CleanBoostTray") { Width = 0, Height = 0, WindowStyle = 0 });
            _messageWindow.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Menu, "Không tạo được cửa sổ nhận sự kiện ổ đĩa: " + ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
        if (msg == WM_DEVICECHANGE && (wParam.ToInt64() is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero
            && Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME)
        {
            _volumeDebounce ??= CreateDebounce();
            _volumeDebounce.Stop();
            _volumeDebounce.Start();
        }
        return IntPtr.Zero;
    }

    private DispatcherTimer CreateDebounce()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _model.RefreshVolumes();
        };
        return timer;
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _volumeDebounce?.Stop();
        if (_popup is { } popup)
        {
            popup.StayOpen = true;
            popup.Close();
        }
        _popup = null;
        _model.Dispose();
        if (_notifyIcon is { } icon)
        {
            icon.Visible = false;
            icon.Dispose();
        }
        _notifyIcon = null;
        _renderer.Dispose();
        _messageWindow?.Dispose();
        _messageWindow = null;
    }
}
