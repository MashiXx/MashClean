using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CleanBoost.App.UI;

/// <summary>
/// Ánh xạ tên biểu tượng kiểu SF Symbols (dùng trong node, rule, màn hình của bản Mac) sang glyph của
/// Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10).
/// </summary>
public static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Dictionary<string, char> s_map = new(StringComparer.Ordinal)
    {
        ["sparkles"] = '',
        ["sparkle.magnifyingglass"] = '',
        ["trash"] = '',
        ["trash.circle"] = '',
        ["doc.badge.clock"] = '',
        ["doc.on.doc"] = '',
        ["xmark.app"] = '',
        ["power.circle"] = '',
        ["power"] = '',
        ["wrench.and.screwdriver"] = '',
        ["circle.hexagongrid"] = '',
        ["chart.pie"] = '',
        ["clock.arrow.circlepath"] = '',
        ["ladybug"] = '',
        ["archivebox"] = '',
        ["shippingbox"] = '',
        ["gearshape"] = '',
        ["gearshape.fill"] = '',
        ["gearshape.2"] = '',
        ["doc.text"] = '',
        ["doc"] = '',
        ["exclamationmark.triangle"] = '',
        ["exclamationmark.triangle.fill"] = '',
        ["chevron.left.forwardslash.chevron.right"] = '',
        ["globe"] = '',
        ["iphone"] = '',
        ["arrow.down.app"] = '',
        ["arrow.down.circle"] = '',
        ["paperclip"] = '',
        ["puzzlepiece.extension"] = '',
        ["clock"] = '',
        ["folder"] = '',
        ["folder.fill"] = '',
        ["folder.badge.plus"] = '',
        ["app"] = '',
        ["square.grid.2x2"] = '',
        ["checkmark"] = '',
        ["checkmark.circle"] = '',
        ["checkmark.circle.fill"] = '',
        ["checkmark.seal.fill"] = '',
        ["xmark"] = '',
        ["xmark.octagon.fill"] = '',
        ["exclamationmark.circle.fill"] = '',
        ["exclamationmark.shield"] = '',
        ["lock.fill"] = '',
        ["lock.open"] = '',
        ["lock.shield"] = '',
        ["eye"] = '',
        ["eye.slash"] = '',
        ["hand.raised.fill"] = '',
        ["internaldrive"] = '',
        ["externaldrive"] = '',
        ["externaldrive.fill"] = '',
        ["checklist"] = '',
        ["arrow.clockwise"] = '',
        ["chevron.left"] = '',
        ["chevron.right"] = '',
        ["chevron.down"] = '',
        ["chevron.up"] = '',
        ["cpu"] = '',
        ["memorychip"] = '',
        ["network"] = '',
        ["arrow.down"] = '',
        ["arrow.up"] = '',
        ["battery.100"] = '',
        ["battery.100.bolt"] = '',
        ["macwindow"] = '',
        ["slider.horizontal.3"] = '',
        ["magnifyingglass"] = '',
        ["arrow.uturn.backward"] = '',
        ["square.and.arrow.up"] = '',
        ["envelope"] = '',
        ["info.circle"] = '',
        ["questionmark.circle"] = '',
        ["link"] = '',
        ["calendar"] = '',
        ["play"] = '',
        ["stop"] = '',
        ["bolt"] = '',
        ["photo"] = '',
        ["music.note"] = '',
        ["film"] = '',
        ["doc.zipper"] = '',
        ["opticaldisc"] = '',
        ["hammer"] = '',
        ["desktopcomputer"] = '',
        ["person"] = '',
        ["bag"] = '',
        ["books.vertical"] = '',
        ["arrow.counterclockwise"] = '',
        ["eject.fill"] = '',
        ["testtube.2"] = '',
        ["minus.circle"] = '',
        ["plus.circle"] = '',
        ["ellipsis"] = '',
        ["filter"] = '',
        ["list"] = '',
        ["window"] = '',
        ["copy"] = '',
        ["open"] = '',
        ["shield"] = '',
        ["key"] = '',
        ["minimize"] = '',
        ["maximize"] = '',
        ["restore"] = '',
        ["close"] = '',
    };

    /// <summary>Glyph cho tên biểu tượng; không biết thì trả về thư mục.</summary>
    public static string Glyph(string? name) => name is not null && s_map.TryGetValue(name, out var c) ? c.ToString() : "";
}

/// <summary>Một biểu tượng: <c>&lt;ui:Icon Symbol="sparkles" Size="16"/&gt;</c>.</summary>
public sealed class Icon : TextBlock
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(Icon), new PropertyMetadata(null, (d, e) => ((Icon)d).Text = Icons.Glyph((string?)e.NewValue)));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(16.0, (d, e) => ((Icon)d).FontSize = (double)e.NewValue));

    public string? Symbol
    {
        get => (string?)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public Icon()
    {
        FontFamily = Icons.Font;
        FontSize = 16;
        VerticalAlignment = VerticalAlignment.Center;
        TextAlignment = TextAlignment.Center;
        Text = Icons.Glyph(null);
    }
}
