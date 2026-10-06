using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CleanBoost.App.Services;
using CleanBoost.Core;

namespace CleanBoost.App.Shell;

/// <summary>
/// Chụp ảnh cửa sổ ra PNG để kiểm tra giao diện tự động (tương đương <c>cleanboost://debug</c> của bản Mac).
/// Biến môi trường: <c>CLEANBOOST_SNAPSHOT</c> = thư mục ra; <c>CLEANBOOST_SNAPSHOT_SCREENS</c> = danh sách mục thanh bên
/// (vd <c>SmartScan,SystemJunk</c>); <c>CLEANBOOST_SNAPSHOT_SCAN=1</c> để quét trước khi chụp; app tự thoát sau khi chụp.
/// </summary>
public static class DebugSnapshot
{
    public static bool IsEnabled => Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT") is { Length: > 0 };

    public static async void Run(MainWindow window, AppRouter router)
    {
        var dir = Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT")!;
        Directory.CreateDirectory(dir);
        var screens = (Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_SCREENS") ?? "SmartScan")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scan = Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_SCAN") == "1";
        var wait = int.TryParse(Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_WAIT"), out var w) ? w : 60;
        await Task.Delay(1200);
        foreach (var name in screens)
        {
            if (!Enum.TryParse<SidebarItem>(name, out var item)) continue;
            router.Open(item, autoStart: scan);
            if (scan)
            {
                // Chờ quét xong (hoặc hết thời gian).
                for (var i = 0; i < wait * 4; i++)
                {
                    await Task.Delay(250);
                    if (window.IsScreenIdle(item)) break;
                }
            }
            await Task.Delay(900);
            Save(window, Path.Combine(dir, $"{name}.png"));
        }
        Log.Info(LogCategory.UI, "Đã chụp ảnh giao diện vào " + dir);
        Application.Current.Shutdown();
    }

    public static void Save(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var bmp = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
