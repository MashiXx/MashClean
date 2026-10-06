using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CleanBoost.App.UI;

/// <summary>Màu chủ đạo theo tính năng (Theme.Accent của bản Mac).</summary>
public enum Accent
{
    SmartScan,
    Cleanup,
    Applications,
    SpaceLens,
    Maintenance,
    Protection,
    Files,
    Neutral,
}

/// <summary>
/// Design system (DesignSystem/Theme.swift): gradient 3 màu theo tính năng đi từ góc trên trái xuống dưới phải,
/// chữ trắng, thẻ trắng mờ, màu an toàn/xem lại/rủi ro.
/// </summary>
public static class Theme
{
    private static Color Hex(uint rgb, byte a = 0xFF) => Color.FromArgb(a, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static Color[] GradientColors(Accent accent) => accent switch
    {
        Accent.SmartScan => [Hex(0x3A1C71), Hex(0x6A3093), Hex(0xA044FF)],
        Accent.Cleanup => [Hex(0x0F3D3E), Hex(0x11706A), Hex(0x2BB39B)],
        Accent.Applications => [Hex(0x1A2A6C), Hex(0x2B5DC5), Hex(0x46A3FF)],
        Accent.SpaceLens => [Hex(0x23074D), Hex(0x6B1D9C), Hex(0xCC5333)],
        Accent.Maintenance => [Hex(0x3E1E0E), Hex(0x9A4512), Hex(0xF09819)],
        Accent.Protection => [Hex(0x0B3D20), Hex(0x16753E), Hex(0x56AB2F)],
        Accent.Files => [Hex(0x13213A), Hex(0x24477A), Hex(0x4E7BB8)],
        _ => [Hex(0x1E1E2A), Hex(0x2A2A3C), Hex(0x3A3A52)],
    };

    private static readonly Dictionary<Accent, LinearGradientBrush> s_backgrounds = [];

    public static LinearGradientBrush Background(Accent accent)
    {
        if (s_backgrounds.TryGetValue(accent, out var b)) return b;
        var c = GradientColors(accent);
        b = new LinearGradientBrush(new GradientStopCollection { new(c[0], 0), new(c[1], 0.5), new(c[2], 1) }, new Point(0, 0), new Point(1, 1));
        b.Freeze();
        s_backgrounds[accent] = b;
        return b;
    }

    /// <summary>Màu cuối gradient (tint) — dùng cho bóng nút tròn lớn, biểu đồ.</summary>
    public static Color Tint(Accent accent) => GradientColors(accent)[2];

    /// <summary>Màu giữa gradient — chữ của nút chính (nền trắng).</summary>
    public static SolidColorBrush Mid(Accent accent)
    {
        var b = new SolidColorBrush(GradientColors(accent)[1]);
        b.Freeze();
        return b;
    }

    public static readonly Color SafeColor = Hex(0x4CD964);
    public static readonly Color ReviewColor = Hex(0xFFCC00);
    public static readonly Color RiskyColor = Hex(0xFF5E57);

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static readonly SolidColorBrush SafeBrush = Frozen(SafeColor);
    public static readonly SolidColorBrush ReviewBrush = Frozen(ReviewColor);
    public static readonly SolidColorBrush RiskyBrush = Frozen(RiskyColor);

    public static SolidColorBrush SafetyBrush(Core.SafetyLevel level) => level switch
    {
        Core.SafetyLevel.Safe => SafeBrush,
        Core.SafetyLevel.Review => ReviewBrush,
        _ => RiskyBrush,
    };

    /// <summary>Màu nhấn của Windows (thanh bên đang chọn), mặc định xanh giống macOS.</summary>
    public static Color SystemAccent
    {
        get
        {
            try
            {
                if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                {
                    var u = unchecked((uint)abgr);
                    return Color.FromRgb((byte)u, (byte)(u >> 8), (byte)(u >> 16));
                }
            }
            catch
            {
                // dùng màu mặc định
            }
            return Hex(0x2F6FE8);
        }
    }

    /// <summary>Windows đang dùng giao diện sáng cho app.</summary>
    public static bool SystemUsesLightTheme
    {
        get
        {
            try
            {
                return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not int v || v != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    // MARK: Thuộc tính đính kèm kế thừa — màu nhấn của màn hiện tại cho nút chính, checkbox...

    public static readonly DependencyProperty AccentMidProperty = DependencyProperty.RegisterAttached(
        "AccentMid", typeof(Brush), typeof(Theme),
        new FrameworkPropertyMetadata(Mid(Accent.SmartScan), FrameworkPropertyMetadataOptions.Inherits));

    public static Brush GetAccentMid(DependencyObject d) => (Brush)d.GetValue(AccentMidProperty);
    public static void SetAccentMid(DependencyObject d, Brush value) => d.SetValue(AccentMidProperty, value);

    public static readonly DependencyProperty AccentTintProperty = DependencyProperty.RegisterAttached(
        "AccentTint", typeof(Color), typeof(Theme),
        new FrameworkPropertyMetadata(Tint(Accent.SmartScan), FrameworkPropertyMetadataOptions.Inherits));

    public static Color GetAccentTint(DependencyObject d) => (Color)d.GetValue(AccentTintProperty);
    public static void SetAccentTint(DependencyObject d, Color value) => d.SetValue(AccentTintProperty, value);

    /// <summary>Đặt accent cho một cây giao diện: nền gradient + màu nhấn kế thừa.</summary>
    public static void Apply(FrameworkElement element, Accent accent)
    {
        SetAccentMid(element, Mid(accent));
        SetAccentTint(element, Tint(accent));
    }
}
