using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CleanBoost.Core;

namespace CleanBoost.App.UI;

/// <summary>
/// Vòng tiến độ (ProgressRing của bản Mac): rãnh trắng 0.15, cung tiến độ trắng bắt đầu ở 12 giờ chạy theo chiều kim đồng hồ,
/// đầu tròn; giữa là phần trăm và nhãn.
/// </summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnProgressChanged));

    private static readonly DependencyProperty DisplayedProperty = DependencyProperty.Register(
        "Displayed", typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineWidthProperty = DependencyProperty.Register(
        nameof(LineWidth), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ProgressRing), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double LineWidth { get => (double)GetValue(LineWidthProperty); set => SetValue(LineWidthProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ring = (ProgressRing)d;
        var to = Math.Clamp((double)e.NewValue, 0, 1);
        ring.BeginAnimation(DisplayedProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private static readonly Brush Track = Theme.Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - LineWidth) / 2;
        dc.DrawEllipse(null, new Pen(Track, LineWidth), center, radius, radius);

        var p = Math.Max(0.001, Math.Min((double)GetValue(DisplayedProperty), 1));
        var arcBrush = new LinearGradientBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Colors.White, new Point(0, 1), new Point(1, 0));
        var pen = new Pen(arcBrush, LineWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p >= 0.9999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
        }
        else
        {
            var angle = p * 360;
            var start = new Point(center.X, center.Y - radius);
            var rad = (angle - 90) * Math.PI / 180;
            var end = new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(radius, radius), 0, angle > 180, SweepDirection.Clockwise, true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var percent = new FormattedText($"{(int)(Math.Clamp(Progress, 0, 1) * 100)}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(AppFonts.Display, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 28, Brushes.White, dpi);
        var hasLabel = !string.IsNullOrEmpty(Label);
        var y = center.Y - percent.Height / 2 - (hasLabel ? 8 : 0);
        dc.DrawText(percent, new Point(center.X - percent.Width / 2, y));
        if (hasLabel)
        {
            var label = new FormattedText(Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(AppFonts.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11,
                Theme.Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)), dpi);
            dc.DrawText(label, new Point(center.X - label.Width / 2, y + percent.Height));
        }
    }
}

/// <summary>Biểu đồ nhỏ (sparkline): vùng tô gradient + đường, cửa sổ trượt 60 mẫu.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryValuesProperty = DependencyProperty.Register(
        nameof(SecondaryValues), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondaryTintProperty = DependencyProperty.Register(
        nameof(SecondaryTint), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Trần trục Y; 0 = tự tính theo giá trị lớn nhất.</summary>
    public static readonly DependencyProperty UpperBoundProperty = DependencyProperty.Register(
        nameof(UpperBound), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(bool), typeof(Sparkline), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? SecondaryValues { get => (IReadOnlyList<double>?)GetValue(SecondaryValuesProperty); set => SetValue(SecondaryValuesProperty, value); }
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Color SecondaryTint { get => (Color)GetValue(SecondaryTintProperty); set => SetValue(SecondaryTintProperty, value); }
    public double UpperBound { get => (double)GetValue(UpperBoundProperty); set => SetValue(UpperBoundProperty, value); }
    public bool Fill { get => (bool)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public const int Window = 60;

    protected override void OnRender(DrawingContext dc)
    {
        var max = UpperBound;
        if (max <= 0)
        {
            max = Math.Max(1, Math.Max(Values?.DefaultIfEmpty(0).Max() ?? 0, SecondaryValues?.DefaultIfEmpty(0).Max() ?? 0));
        }
        Draw(dc, Values, Tint, max, Fill);
        Draw(dc, SecondaryValues, SecondaryTint, max, false);
    }

    private void Draw(DrawingContext dc, IReadOnlyList<double>? values, Color tint, double max, bool fill)
    {
        if (values is null || values.Count < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var w = ActualWidth;
        var h = ActualHeight;
        var step = w / (Window - 1);
        var offset = (Window - values.Count) * step;
        var points = values.Select((v, i) => new Point(offset + i * step, h - Math.Clamp(v / max, 0, 1) * (h - 1.5) - 0.75)).ToList();
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();
        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, h), true, true);
                ctx.PolyLineTo(points.Append(new Point(points[^1].X, h)).ToList(), true, true);
            }
            area.Freeze();
            var brush = new LinearGradientBrush(Color.FromArgb(0x73, tint.R, tint.G, tint.B), Color.FromArgb(0x05, tint.R, tint.G, tint.B), 90);
            dc.DrawGeometry(brush, null, area);
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(tint), 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}

public static class AppFonts
{
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
}

/// <summary>Biểu tượng thật của file/thư mục từ shell (như biểu tượng Finder ở bản Mac), có cache theo đuôi file.</summary>
public static class ShellIcons
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

    private static readonly ConcurrentDictionary<string, ImageSource?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Biểu tượng cho đường dẫn. Thư mục và file thường dùng biểu tượng chung theo loại (nhanh, không đọc ổ);
    /// exe/lnk/ico đọc biểu tượng riêng của file.
    /// </summary>
    public static ImageSource? For(string? path, bool isDirectory, bool large = false)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var ext = isDirectory ? "<dir>" : Path.GetExtension(path).ToLowerInvariant();
        var specific = !isDirectory && ext is ".exe" or ".lnk" or ".ico" or ".url" or ".msi";
        var key = (specific ? path : ext) + (large ? "|L" : "|S");
        return s_cache.GetOrAdd(key, _ => Load(path, isDirectory, specific, large));
    }

    private static ImageSource? Load(string path, bool isDirectory, bool specific, bool large)
    {
        try
        {
            var info = new SHFILEINFOW();
            var flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
            if (!specific) flags |= SHGFI_USEFILEATTRIBUTES;
            var target = specific ? path : isDirectory ? "folder" : "file" + Path.GetExtension(path);
            var res = SHGetFileInfoW(target, isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), flags);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}

// MARK: Converters dùng trong XAML

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ByteCount b => b.Formatted,
        long l => ByteCount.Format(l),
        int i => ByteCount.Format(i),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value switch
        {
            bool x => x,
            int i => i != 0,
            string s => !string.IsNullOrEmpty(s),
            null => false,
            _ => true,
        };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class GlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Icons.Glyph(value as string ?? parameter as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Dịch chuỗi nguồn tiếng Việt trong XAML: <c>Text="{ui:L 'Quét lại'}"</c>.</summary>
[System.Windows.Markup.MarkupExtensionReturnType(typeof(string))]
public sealed class L(string key) : System.Windows.Markup.MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
