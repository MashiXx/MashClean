using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CleanBoost.App.UI;

namespace CleanBoost.App.Features.StartupItems;

/// <summary>
/// Một dòng chữ bị cắt ở giữa khi quá dài (<c>C:\Program Files\…\app.exe --min</c>), như <c>lineLimit(1)</c> +
/// <c>truncationMode(.middle)</c> của SwiftUI: giữ được đầu đường dẫn lẫn tên chương trình và tham số.
/// </summary>
public sealed class MiddleEllipsisText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MiddleEllipsisText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MiddleEllipsisText), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(MiddleEllipsisText), new FrameworkPropertyMetadata(11.5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    private static readonly Typeface s_typeface = new(AppFonts.Text, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private FormattedText Format(string text) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, s_typeface, FontSize, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override Size MeasureOverride(Size availableSize)
    {
        var full = Format(Text ?? "");
        var width = double.IsInfinity(availableSize.Width) ? full.WidthIncludingTrailingWhitespace : Math.Min(full.WidthIncludingTrailingWhitespace, availableSize.Width);
        return new Size(Math.Ceiling(width), Math.Ceiling(full.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Text ?? "";
        if (text.Length == 0) return;
        var max = ActualWidth;
        var full = Format(text);
        if (full.WidthIncludingTrailingWhitespace <= max + 0.5)
        {
            dc.DrawText(full, new Point(0, 0));
            return;
        }
        // Tìm số ký tự giữ lại lớn nhất (chia đều đầu/cuối, đầu nhiều hơn một chút) mà vẫn vừa.
        int lo = 0, hi = text.Length;
        FormattedText? best = null;
        while (lo <= hi)
        {
            var keep = (lo + hi) / 2;
            var candidate = Format(Trim(text, keep));
            if (candidate.WidthIncludingTrailingWhitespace <= max)
            {
                best = candidate;
                lo = keep + 1;
            }
            else
            {
                hi = keep - 1;
            }
        }
        dc.DrawText(best ?? Format("…"), new Point(0, 0));
    }

    private static string Trim(string text, int keep)
    {
        if (keep >= text.Length) return text;
        var head = (keep + 1) / 2;
        var tail = keep - head;
        return text[..head] + "…" + text[(text.Length - tail)..];
    }
}
