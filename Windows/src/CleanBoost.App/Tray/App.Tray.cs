using System.Windows;
using System.Windows.Threading;
using CleanBoost.App.Services;
using CleanBoost.App.Tray;
using CleanBoost.Core;

namespace CleanBoost.App;

public partial class App
{
    private TrayController? _tray;

    /// <summary>
    /// Khay hệ thống (app menu bar riêng của bản Mac): biểu tượng + popup số liệu + cảnh báo. Với <c>--background</c> app chỉ
    /// chạy ở khay; nếu biểu tượng khay đang tắt thì không còn gì để hiện nên thoát.
    /// </summary>
    partial void StartTray(AppEnvironment env, bool background)
    {
        try
        {
            _tray = new TrayController(env, Dispatcher);
            // Chế độ chụp ảnh kiểm thử: không hiện thông báo, không ghi alerts.last.* vào cài đặt thật.
            _tray.Model.AlertsEnabled = !Shell.DebugSnapshot.IsEnabled;
            if (TraySnapshot.IsEnabled)
            {
                _tray.Start();
                Dispatcher.BeginInvoke(() => TraySnapshot.Run(_tray), DispatcherPriority.ApplicationIdle);
                return;
            }
            _tray.Start();
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Menu, "Không khởi động được khay hệ thống", ex);
            KeepRunningInTray = false;
        }
        if (background && !env.Settings.MenuBarEnabled)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!Current.Windows.OfType<Window>().Any(w => w.IsVisible))
                {
                    Log.Info(LogCategory.Menu, "Chạy nền nhưng biểu tượng khay đang tắt: thoát");
                    Shutdown();
                }
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    partial void StopTray()
    {
        _tray?.Dispose();
        _tray = null;
    }
}
