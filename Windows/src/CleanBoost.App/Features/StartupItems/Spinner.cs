using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CleanBoost.App.Features.StartupItems;

/// <summary>Vòng quay chờ không xác định (ProgressView của SwiftUI): cung trắng 3/4 vòng quay đều.</summary>
public sealed class Spinner : FrameworkElement
{
    private static readonly Brush s_track = UI.Theme.Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private readonly RotateTransform _rotation = new();

    public double Thickness { get; set; } = 2;

    public Spinner()
    {
        RenderTransform = _rotation;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) => Animate();
        Loaded += (_, _) => Animate();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Animate()
    {
        if (IsVisible && IsLoaded)
            _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        else
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = (size - Thickness) / 2;
        dc.DrawEllipse(null, new Pen(s_track, Thickness), center, r, r);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(center.X, center.Y - r), false, false);
            ctx.ArcTo(new Point(center.X + r, center.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(Brushes.White, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
    }
}
