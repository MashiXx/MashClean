using System.Runtime.InteropServices;
using System.Windows;
using CleanBoost.Core;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingImaging = System.Drawing.Imaging;

namespace CleanBoost.App.Tray;

/// <summary>
/// Biểu tượng khay hệ thống. Khay Windows không hiện được chữ như thanh menu của Mac nên số liệu được vẽ lên biểu tượng:
/// <list type="bullet">
/// <item>CPU và RAM: ô vuông bo góc màu gradient Smart Scan với hai cột đo (CPU xanh, RAM cam, đỏ khi quá tải).</item>
/// <item>Chỉ CPU hoặc chỉ RAM: phần trăm (2 chữ số) và thanh tiến độ nhỏ màu của chỉ số ở dưới.</item>
/// <item>Không chọn gì (hoặc chỉ mạng — tốc độ mạng nằm ở chú thích): biểu tượng app.</item>
/// </list>
/// Kích thước theo DPI của thanh tác vụ (16/20/24/32 px...). Chỉ vẽ lại khi nội dung đổi.
/// </summary>
internal sealed class TrayIconRenderer : IDisposable
{
    private static readonly Drawing.Color CpuColor = Drawing.Color.FromArgb(0x7C, 0xE7, 0xFF);
    private static readonly Drawing.Color MemoryColor = Drawing.Color.FromArgb(0xFF, 0xB8, 0x6C);
    private static readonly Drawing.Color RiskyColor = Drawing.Color.FromArgb(0xFF, 0x5E, 0x57);

    private IntPtr _handle;
    private string? _key;

    public Drawing.Icon? Icon { get; private set; }

    /// <summary>Cập nhật biểu tượng; trả về <c>true</c> khi đã đổi.</summary>
    public bool Update(TrayStatusParts style, double? cpu, double? memory, bool memoryCritical, int size)
    {
        var showCpu = style.HasFlag(TrayStatusParts.Cpu);
        var showMemory = style.HasFlag(TrayStatusParts.Memory);
        var cpuPct = showCpu ? cpu is { } c ? TrayFormat.PercentValue(c) : -1 : -2;
        var memPct = showMemory ? memory is { } m ? TrayFormat.PercentValue(m) : -1 : -2;
        var key = $"{size}|{cpuPct}|{memPct}|{memoryCritical}";
        if (key == _key && Icon is not null) return false;

        using var bitmap = showCpu || showMemory ? RenderMetrics(size, cpuPct, memPct, memoryCritical) : RenderAppIcon(size);
        if (bitmap is null) return false;
        var handle = bitmap.GetHicon();
        var old = _handle;
        var oldIcon = Icon;
        Icon = Drawing.Icon.FromHandle(handle);
        _handle = handle;
        _key = key;
        oldIcon?.Dispose();
        if (old != IntPtr.Zero) DestroyIcon(old);
        return true;
    }

    /// <summary>Buộc vẽ lại lần sau (đổi DPI).</summary>
    public void Invalidate() => _key = null;

    public void Dispose()
    {
        Icon?.Dispose();
        Icon = null;
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
        _handle = IntPtr.Zero;
    }

    // MARK: Kích thước

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    /// <summary>Kích thước biểu tượng nhỏ theo DPI của thanh tác vụ (SM_CXSMICON).</summary>
    public static int PreferredSize()
    {
        try
        {
            var taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero && GetDpiForWindow(taskbar) is var dpi and > 0)
            {
                var size = GetSystemMetricsForDpi(49, dpi);
                if (size > 0) return size;
            }
        }
        catch
        {
            // Windows cũ: dùng giá trị hệ thống
        }
        return Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
    }

    // MARK: Vẽ

    /// <summary>Biểu tượng app: khung có sẵn trong AppIcon.ico nếu đúng cỡ, không thì thu nhỏ ảnh 256 px.</summary>
    internal static Drawing.Bitmap? RenderAppIcon(int size)
    {
        try
        {
            if (size is 16 or 32 or 64 or 128)
            {
                using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico"))?.Stream;
                if (stream is not null)
                {
                    using var icon = new Drawing.Icon(stream, size, size);
                    if (icon.Width == size) return icon.ToBitmap();
                }
            }
            using var png = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon256.png"))?.Stream;
            if (png is null) return null;
            using var source = new Drawing.Bitmap(png);
            var bitmap = new Drawing.Bitmap(size, size, DrawingImaging.PixelFormat.Format32bppArgb);
            using var g = Drawing.Graphics.FromImage(bitmap);
            g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;
            g.DrawImage(source, new Drawing.Rectangle(0, 0, size, size));
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Menu, "Không vẽ được biểu tượng khay: " + ex.Message);
            return null;
        }
    }

    /// <param name="cpuPct">Phần trăm CPU; -1 = chưa có số liệu; -2 = không hiện.</param>
    /// <param name="memPct">Phần trăm RAM; -1 = chưa có số liệu; -2 = không hiện.</param>
    internal static Drawing.Bitmap RenderMetrics(int s, int cpuPct, int memPct, bool memoryCritical)
    {
        var bitmap = new Drawing.Bitmap(s, s, DrawingImaging.PixelFormat.Format32bppArgb);
        using var g = Drawing.Graphics.FromImage(bitmap);
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(Drawing.Color.Transparent);

        // Nền: ô bo góc gradient Smart Scan (giống biểu tượng app).
        using (var tile = RoundedRect(new Drawing.RectangleF(0, 0, s, s), s * 0.24f))
        using (var brush = new Drawing2D.LinearGradientBrush(new Drawing.PointF(0, 0), new Drawing.PointF(s, s), Drawing.Color.Black, Drawing.Color.Black))
        {
            brush.InterpolationColors = new Drawing2D.ColorBlend
            {
                Colors = [Drawing.Color.FromArgb(0x3A, 0x1C, 0x71), Drawing.Color.FromArgb(0x6A, 0x30, 0x93), Drawing.Color.FromArgb(0xA0, 0x44, 0xFF)],
                Positions = [0f, 0.5f, 1f],
            };
            g.FillPath(brush, tile);
        }

        var cpuColor = cpuPct >= 90 ? RiskyColor : CpuColor;
        var memColor = memoryCritical || memPct >= 90 ? RiskyColor : MemoryColor;
        if (cpuPct > -2 && memPct > -2)
        {
            DrawBars(g, s, [(cpuPct, cpuColor), (memPct, memColor)]);
        }
        else
        {
            var (pct, color) = cpuPct > -2 ? (cpuPct, cpuColor) : (memPct, memColor);
            DrawDigits(g, s, pct, color);
        }
        return bitmap;
    }

    private static readonly Drawing.Color TrackColor = Drawing.Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);

    /// <summary>Hai cột đo dọc, căn theo pixel để nét ở 16 px.</summary>
    private static void DrawBars(Drawing.Graphics g, int s, (int Pct, Drawing.Color Color)[] bars)
    {
        var padY = (int)Math.Round(s * 0.17);
        var gap = Math.Max(2, (int)Math.Round(s * 0.1));
        var barW = Math.Max(2, (s - 2 * (int)Math.Round(s * 0.19) - gap) / 2);
        var x = (s - (2 * barW + gap)) / 2;
        var barH = s - 2 * padY;
        var radius = Math.Min(barW / 2f, Math.Max(1f, s * 0.07f));
        g.PixelOffsetMode = Drawing2D.PixelOffsetMode.None;
        foreach (var (pct, color) in bars)
        {
            var track = new Drawing.RectangleF(x, padY, barW, barH);
            using (var path = RoundedRect(track, radius))
            {
                using var trackBrush = new Drawing.SolidBrush(TrackColor);
                g.FillPath(trackBrush, path);
                if (pct > 0)
                {
                    var h = Math.Max(1.5f, barH * Math.Min(100, pct) / 100f);
                    var state = g.Save();
                    g.SetClip(path);
                    using var fill = new Drawing.SolidBrush(color);
                    g.FillRectangle(fill, x, padY + barH - h, barW, h);
                    g.Restore(state);
                }
            }
            x += barW + gap;
        }
    }

    /// <summary>Phần trăm (tối đa 99) căn giữa theo nét chữ thật, thanh tiến độ mảnh ở dưới.</summary>
    private static void DrawDigits(Drawing.Graphics g, int s, int pct, Drawing.Color color)
    {
        var padX = Math.Max(2, (int)Math.Round(s * 0.18));
        var stripH = Math.Max(2, (int)Math.Round(s * 0.12));
        var stripY = s - Math.Max(2, (int)Math.Round(s * 0.15)) - stripH;
        var textTop = Math.Max(1, (int)Math.Round(s * 0.14));
        var textBottom = stripY - Math.Max(1, (int)Math.Round(s * 0.09));
        var text = pct < 0 ? "–" : Math.Min(99, pct).ToString(System.Globalization.CultureInfo.InvariantCulture);

        using (var path = new Drawing2D.GraphicsPath())
        using (var family = new Drawing.FontFamily("Segoe UI"))
        {
            path.AddString(text, family, (int)Drawing.FontStyle.Bold, 100f, Drawing.PointF.Empty, Drawing.StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                var areaW = s - 2f * Math.Max(1, (int)Math.Round(s * 0.1));
                var areaH = textBottom - textTop;
                // Chữ số cao bằng vùng chữ; dấu "–" chỉ là nét ngang nên giữ tỉ lệ theo chiều rộng.
                var scale = pct < 0 ? areaW * 0.5f / bounds.Width : Math.Min(areaW / bounds.Width, areaH / bounds.Height);
                using var matrix = new Drawing2D.Matrix();
                matrix.Translate(s / 2f, textTop + areaH / 2f);
                matrix.Scale(scale, scale);
                matrix.Translate(-(bounds.X + bounds.Width / 2), -(bounds.Y + bounds.Height / 2));
                path.Transform(matrix);
                g.FillPath(Drawing.Brushes.White, path);
            }
        }

        var stripW = s - 2 * padX;
        using (var track = new Drawing.SolidBrush(TrackColor))
            g.FillRectangle(track, padX, stripY, stripW, stripH);
        if (pct > 0)
        {
            using var fill = new Drawing.SolidBrush(color);
            g.FillRectangle(fill, padX, stripY, Math.Max(1f, stripW * Math.Min(100, pct) / 100f), stripH);
        }
    }

    private static Drawing2D.GraphicsPath RoundedRect(Drawing.RectangleF r, float radius)
    {
        var path = new Drawing2D.GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.1f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
