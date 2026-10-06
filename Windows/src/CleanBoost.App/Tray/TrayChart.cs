using System.Windows;
using System.Windows.Media;

namespace CleanBoost.App.Tray;

/// <summary>
/// Biểu đồ mini của popup khay, vẽ giống Swift Charts của bản Mac: đường nội suy đơn điệu (monotone) dày 1,5 px, vùng tô
/// gradient từ màu 0.45 xuống 0.02, không trục. Trục X trải các mẫu đang có ra hết chiều rộng (miền <c>[đầu, cuối]</c> của tối đa
/// 60 mẫu), trục Y từ 0 đến <see cref="UpperBound"/> (null = giá trị lớn nhất, tối thiểu 1 — biểu đồ mạng).
/// </summary>
internal sealed class TrayChart : FrameworkElement
{
    private IReadOnlyList<double>? _values;
    private IReadOnlyList<double>? _secondary;
    private Color _tint = Colors.White;

    public IReadOnlyList<double>? Values
    {
        get => _values;
        set { _values = value; InvalidateVisual(); }
    }

    /// <summary>Đường thứ hai (không tô), vd tốc độ tải lên.</summary>
    public IReadOnlyList<double>? SecondaryValues
    {
        get => _secondary;
        set { _secondary = value; InvalidateVisual(); }
    }

    public Color Tint
    {
        get => _tint;
        set
        {
            if (_tint == value) return;
            _tint = value;
            InvalidateVisual();
        }
    }

    public Color SecondaryTint { get; set; } = Colors.White;
    public double? UpperBound { get; set; } = 1;
    public bool Fill { get; set; } = true;

    protected override void OnRender(DrawingContext dc)
    {
        var top = UpperBound ?? Math.Max(1, Math.Max(Max(_values), Max(_secondary)));
        top = Math.Max(top, 0.0001);
        Draw(dc, _values, _tint, top, Fill);
        Draw(dc, _secondary, SecondaryTint, top, false);
    }

    private static double Max(IReadOnlyList<double>? values) => values is { Count: > 0 } ? values.Max() : 0;

    private void Draw(DrawingContext dc, IReadOnlyList<double>? values, Color tint, double top, bool fill)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is not { Count: >= 2 } || w <= 0 || h <= 0) return;
        var n = values.Count;
        var points = new Point[n];
        for (var i = 0; i < n; i++)
            points[i] = new Point(w * i / (n - 1), h - Math.Clamp(values[i] / top, 0, 1) * (h - 1.5) - 0.75);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            AddMonotone(ctx, points);
        }
        line.Freeze();

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(points[0].X, h), true, true);
                ctx.LineTo(points[0], false, false);
                AddMonotone(ctx, points);
                ctx.LineTo(new Point(points[^1].X, h), false, false);
            }
            area.Freeze();
            var brush = new LinearGradientBrush(Color.FromArgb(0x73, tint.R, tint.G, tint.B), Color.FromArgb(0x05, tint.R, tint.G, tint.B), 90);
            brush.Freeze();
            dc.DrawGeometry(brush, null, area);
        }
        var pen = new Pen(new SolidColorBrush(tint), 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>Nội suy Hermite đơn điệu (Fritsch–Carlson): không vượt quá giá trị thật giữa hai mẫu, như <c>.monotone</c> của Swift Charts.</summary>
    private static void AddMonotone(StreamGeometryContext ctx, Point[] p)
    {
        var n = p.Length;
        if (n == 2)
        {
            ctx.LineTo(p[1], true, true);
            return;
        }
        var d = new double[n - 1];
        for (var i = 0; i < n - 1; i++) d[i] = (p[i + 1].Y - p[i].Y) / Math.Max(1e-9, p[i + 1].X - p[i].X);
        var m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (var i = 1; i < n - 1; i++) m[i] = d[i - 1] * d[i] <= 0 ? 0 : (d[i - 1] + d[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (d[i] == 0)
            {
                m[i] = 0;
                m[i + 1] = 0;
                continue;
            }
            var a = m[i] / d[i];
            var b = m[i + 1] / d[i];
            var s = a * a + b * b;
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                m[i] = t * a * d[i];
                m[i + 1] = t * b * d[i];
            }
        }
        for (var i = 0; i < n - 1; i++)
        {
            var dx = (p[i + 1].X - p[i].X) / 3;
            ctx.BezierTo(new Point(p[i].X + dx, p[i].Y + m[i] * dx), new Point(p[i + 1].X - dx, p[i + 1].Y - m[i + 1] * dx), p[i + 1], true, true);
        }
    }
}
