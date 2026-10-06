using System.Windows;
using CleanBoost.App.Shell;
using CleanBoost.Core;
using CleanBoost.Engine.Metrics;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace CleanBoost.App.Tray;

/// <summary>
/// Chụp ảnh popup khay để kiểm tra giao diện tự động: <c>CLEANBOOST_SNAPSHOT=&lt;thư mục&gt;</c> +
/// <c>CLEANBOOST_SNAPSHOT_TRAY=1</c> (hoặc <c>demo</c> để thêm ổ ngoài, lỗi tháo ổ và nhãn RAM mẫu), chạy kèm
/// <c>--background</c>. Ghi <c>Tray.png</c> (popup sau vài lần lấy mẫu) và <c>TrayIcons.png</c> (các kiểu biểu tượng khay
/// ở 16/20/24/32 px, phóng to) rồi thoát.
/// </summary>
internal static class TraySnapshot
{
    private static string? Mode => Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_TRAY");

    public static bool IsEnabled => DebugSnapshot.IsEnabled && Mode is { Length: > 0 } m && m != "0";

    public static async void Run(TrayController tray)
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT")!;
            Directory.CreateDirectory(dir);
            var model = tray.Model;
            model.IntervalOverride = TimeSpan.FromMilliseconds(500);
            if (Mode == "demo")
            {
                model.UseDemoVolumes();
                model.DemoPressure = MemoryPressure.Warning;
            }
            model.Start();
            var popup = tray.OpenPopup(stayOpen: true);
            var seconds = int.TryParse(Environment.GetEnvironmentVariable("CLEANBOOST_SNAPSHOT_WAIT"), out var w) ? w : 8;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            popup.Refresh();
            await Task.Delay(400);
            DebugSnapshot.Save(popup.SnapshotRoot, Path.Combine(dir, "Tray.png"));
            SaveIconSheet(Path.Combine(dir, "TrayIcons.png"));
            var gear = popup.OpenGearMenu();
            await Task.Delay(500);
            DebugSnapshot.Save(gear, Path.Combine(dir, "TrayGearMenu.png"));
            gear.IsOpen = false;
            var menu = tray.BuildContextMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.PlacementTarget = popup.SnapshotRoot;
            menu.IsOpen = true;
            await Task.Delay(500);
            DebugSnapshot.Save(menu, Path.Combine(dir, "TrayMenu.png"));
            menu.IsOpen = false;
            Log.Info(LogCategory.Menu, "Đã chụp ảnh khay hệ thống vào " + dir);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Menu, "Chụp ảnh khay lỗi", ex);
        }
        Application.Current.Shutdown();
    }

    /// <summary>Bảng biểu tượng: mỗi hàng một cỡ (16/20/24/32), mỗi cột một kiểu, phóng 6 lần, trên nền tối và nền sáng.</summary>
    private static void SaveIconSheet(string path)
    {
        int[] sizes = [16, 20, 24, 32];
        (string Name, Func<int, Drawing.Bitmap?> Render)[] variants =
        [
            ("app", s => TrayIconRenderer.RenderAppIcon(s)),
            ("cpu+ram", s => TrayIconRenderer.RenderMetrics(s, 35, 72, false)),
            ("cpu+ram hot", s => TrayIconRenderer.RenderMetrics(s, 96, 91, true)),
            ("cpu", s => TrayIconRenderer.RenderMetrics(s, 48, -2, false)),
            ("ram", s => TrayIconRenderer.RenderMetrics(s, -2, 7, false)),
            ("no data", s => TrayIconRenderer.RenderMetrics(s, -1, -2, false)),
        ];
        const int zoom = 6, pad = 12;
        var cell = 32 * zoom + pad;
        using var sheet = new Drawing.Bitmap(variants.Length * cell * 2 + pad, sizes.Length * cell + pad, DrawingImaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(sheet);
        g.Clear(Drawing.Color.FromArgb(0x20, 0x20, 0x20));
        using (var light = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xEE, 0xEE, 0xEE)))
            g.FillRectangle(light, variants.Length * cell + pad / 2, 0, variants.Length * cell + pad / 2, sheet.Height);
        g.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.Half;
        for (var row = 0; row < sizes.Length; row++)
        {
            for (var col = 0; col < variants.Length; col++)
            {
                using var icon = variants[col].Render(sizes[row]);
                if (icon is null) continue;
                foreach (var half in new[] { 0, variants.Length * cell })
                    g.DrawImage(icon, new Drawing.Rectangle(pad + half + col * cell, pad + row * cell, sizes[row] * zoom, sizes[row] * zoom));
            }
        }
        sheet.Save(path, DrawingImaging.ImageFormat.Png);
    }
}
