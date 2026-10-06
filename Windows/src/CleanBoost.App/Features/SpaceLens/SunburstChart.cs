using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanBoost.App.UI;
using CleanBoost.Core;
using CleanBoost.Features.SpaceLens;

namespace CleanBoost.App.Features.SpaceLens;

/// <summary>
/// Biểu đồ sunburst (SunburstChart của bản Mac) vẽ bằng <see cref="DrawingVisual"/>: lớp nền chứa mọi cung (chỉ vẽ lại khi bố cục
/// hoặc kích thước đổi), lớp phủ chứa cung đang trỏ, nhãn giữa và chú thích đi theo chuột (vẽ lại khi di chuột, rất nhẹ).
/// Hit-test bằng toạ độ cực (<see cref="SunburstLayout.HitTest"/>). Bấm cung thư mục để đi sâu, bấm vòng tâm để lên một cấp.
/// </summary>
public sealed class SunburstChart : FrameworkElement
{
    private static readonly Pen SegmentStroke = FrozenPen(Color.FromArgb(0x40, 0, 0, 0), 0.5);
    private static readonly Brush TooltipBackground = Theme.Frozen(Color.FromArgb(0xBF, 0, 0, 0));
    private static readonly Brush CenterHoverFill = Theme.Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush SecondaryText = Theme.Frozen(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
    private static readonly Dictionary<uint, Brush> s_brushes = [];

    private readonly DrawingVisual _base = new();
    private readonly DrawingVisual _overlay = new();
    private readonly VisualCollection _visuals;

    private DiskTree? _tree;
    private IReadOnlyList<SunburstSegment> _segments = [];
    private string _title = "";
    private string _sizeText = "";
    private bool _canGoUp;
    private SunburstSegment? _hover;
    private int? _highlightNode;
    private bool _centerHover;
    private Point? _mouse;
    private SunburstGeometry _geometry;

    /// <summary>Cung đang trỏ thay đổi (<c>null</c> khi rời khỏi cung).</summary>
    public event Action<SunburstSegment?>? HoverChanged;
    /// <summary>Bấm vào cung của một node.</summary>
    public event Action<int>? SegmentClicked;
    /// <summary>Bấm vào vòng tâm.</summary>
    public event Action? CenterClicked;

    public SunburstChart()
    {
        _visuals = new VisualCollection(this) { _base, _overlay };
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);
        SnapsToDevicePixels = false;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <summary>Hiện bố cục mới. <paramref name="animate"/> khi đổi thư mục (phóng nhẹ + mờ dần như chuyển cảnh).</summary>
    public void Update(DiskTree tree, IReadOnlyList<SunburstSegment> segments, string title, long size, bool canGoUp, bool animate)
    {
        var sameShape = !animate && _tree is not null && SunburstLayout.SameShape(_segments, segments, s => IsFile(_tree, s), s => IsFile(tree, s));
        _tree = tree;
        _segments = segments;
        _title = title;
        _sizeText = ByteCount.Format(size);
        _canGoUp = canGoUp;
        var previous = _hover;
        _hover = null;
        _highlightNode = null;
        if (!sameShape) RenderBase();
        if (_mouse is { } p) UpdateHover(p, raise: true);
        else RenderOverlay();
        // Chỉ số node có thể đổi sau khi cây cập nhật: luôn báo lại cung đang trỏ (hoặc không còn cung nào).
        if (previous is not null && _hover is null) HoverChanged?.Invoke(null);
        if (animate) PlayTransition();
    }

    /// <summary>Làm nổi cung của một node (khi trỏ vào dòng tương ứng trong danh sách).</summary>
    public void HighlightNode(int? index)
    {
        if (_highlightNode == index) return;
        _highlightNode = index;
        RenderOverlay();
    }

    private void PlayTransition()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(260);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, duration) { EasingFunction = ease });
        if (RenderTransform is ScaleTransform scale && !scale.IsFrozen)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, duration) { EasingFunction = ease });
        }
    }

    private static bool IsFile(DiskTree tree, SunburstSegment s) => s.NodeIndex is { } i && i < tree.Count && !tree.IsDirectory(i);

    // MARK: Vẽ

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RenderBase();
        RenderOverlay();
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var p = hitTestParameters.HitPoint;
        return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight ? new PointHitTestResult(this, p) : null;
    }

    private void RenderBase()
    {
        _geometry = SunburstGeometry.For(ActualWidth, ActualHeight);
        using var dc = _base.RenderOpen();
        if (_tree is null || _geometry.Outer <= 4) return;
        foreach (var seg in _segments)
        {
            var geometry = SegmentGeometry(seg);
            var brush = BrushFor(seg, highlighted: false);
            dc.DrawGeometry(brush, seg.Span > 0.02 ? SegmentStroke : null, geometry);
        }
    }

    private void RenderOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_tree is null || _geometry.Outer <= 4) return;
        var g = _geometry;
        var center = new Point(g.CenterX, g.CenterY);

        // Cung nổi bật: đang trỏ trên biểu đồ, hoặc dòng đang trỏ trong danh sách.
        var highlighted = _hover ?? (_highlightNode is { } n ? _segments.FirstOrDefault(s => s.NodeIndex == n) : null);
        if (highlighted is not null) dc.DrawGeometry(BrushFor(highlighted, highlighted: true), null, SegmentGeometry(highlighted));

        if (_centerHover && _canGoUp) dc.DrawEllipse(CenterHoverFill, null, center, g.Inner - 2, g.Inner - 2);

        // Nhãn giữa: tên thư mục đang xem, dung lượng, mũi tên lên khi lên được.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var maxWidth = Math.Max(20, g.Inner * 1.75);
        var title = Text(_title, 13, FontWeights.SemiBold, Brushes.White, dpi, AppFonts.Text, maxWidth);
        var size = Text(_sizeText, Math.Clamp(g.Inner * 0.2, 14, 22), FontWeights.Bold, Brushes.White, dpi, AppFonts.Display, maxWidth);
        FormattedText? arrow = _canGoUp ? new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Icons.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 12, SecondaryText, dpi) : null;
        var total = title.Height + size.Height + (arrow is null ? 0 : arrow.Height + 2);
        var y = center.Y - total / 2;
        dc.DrawText(title, new Point(center.X - title.Width / 2, y));
        y += title.Height;
        dc.DrawText(size, new Point(center.X - size.Width / 2, y));
        y += size.Height + 2;
        if (arrow is not null) dc.DrawText(arrow, new Point(center.X - arrow.Width / 2, y));

        // Chú thích đi theo chuột: tên (hoặc "Các mục nhỏ khác") và dung lượng.
        if (_hover is { } hover && _mouse is { } m)
        {
            var name = hover.Target.IsOthers ? Loc.F("Các mục nhỏ khác (%lld)", hover.Target.OtherCount)
                : hover.NodeIndex is { } i && i < _tree.Count ? _tree.Name(i) : "";
            var t1 = Text(name, 12, FontWeights.SemiBold, Brushes.White, dpi, AppFonts.Text, 200);
            var t2 = Text(ByteCount.Format(hover.Size), 11, FontWeights.Normal, SecondaryText, dpi, AppFonts.Text, 200);
            var width = Math.Max(t1.Width, t2.Width) + 20;
            var height = t1.Height + t2.Height + 10;
            var cx = Math.Clamp(m.X, Math.Min(110, ActualWidth / 2), Math.Max(ActualWidth - 110, ActualWidth / 2));
            var cy = Math.Max(m.Y - 34, height / 2 + 4);
            var rect = new Rect(cx - width / 2, cy - height / 2, width, height);
            dc.DrawRoundedRectangle(TooltipBackground, null, rect, 8, 8);
            dc.DrawText(t1, new Point(cx - t1.Width / 2, rect.Top + 5));
            dc.DrawText(t2, new Point(cx - t2.Width / 2, rect.Top + 5 + t1.Height));
        }
    }

    private static FormattedText Text(string text, double size, FontWeight weight, Brush brush, double dpi, FontFamily family, double maxWidth)
    {
        var ft = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal),
            size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        return ft;
    }

    private Geometry SegmentGeometry(SunburstSegment seg)
    {
        var g = _geometry;
        var (r0, r1) = g.Ring(seg.Depth);
        r0 = Math.Max(0.5, r0);
        r1 = Math.Max(r0 + 0.5, r1);
        var center = new Point(g.CenterX, g.CenterY);
        if (seg.Span >= SunburstLayout.FullCircle - 1e-6)
        {
            var ring = new GeometryGroup { FillRule = FillRule.EvenOdd };
            ring.Children.Add(new EllipseGeometry(center, r1, r1));
            ring.Children.Add(new EllipseGeometry(center, r0, r0));
            ring.Freeze();
            return ring;
        }
        Point P(double angle, double r)
        {
            var (x, y) = g.Point(angle, r);
            return new Point(x, y);
        }
        var large = seg.Span > Math.PI;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(P(seg.StartAngle, r1), true, true);
            ctx.ArcTo(P(seg.EndAngle, r1), new Size(r1, r1), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(seg.EndAngle, r0), true, false);
            ctx.ArcTo(P(seg.StartAngle, r0), new Size(r0, r0), 0, large, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private Brush BrushFor(SunburstSegment seg, bool highlighted)
    {
        var isFile = _tree is not null && IsFile(_tree, seg);
        var argb = SunburstPalette.Argb(seg.Branch, seg.Depth, isFile, highlighted);
        if (s_brushes.TryGetValue(argb, out var brush)) return brush;
        brush = Theme.Frozen(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        s_brushes[argb] = brush;
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    // MARK: Chuột

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _mouse = p;
        UpdateHover(p, raise: true);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = null;
        _centerHover = false;
        if (_hover is not null)
        {
            _hover = null;
            HoverChanged?.Invoke(null);
        }
        Cursor = null;
        RenderOverlay();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_tree is null) return;
        var p = e.GetPosition(this);
        var (_, radius) = SunburstLayout.Polar(p.X - _geometry.CenterX, p.Y - _geometry.CenterY);
        if (radius < _geometry.Inner)
        {
            CenterClicked?.Invoke();
            e.Handled = true;
            return;
        }
        if (Hit(p) is { NodeIndex: { } index })
        {
            SegmentClicked?.Invoke(index);
            e.Handled = true;
        }
    }

    private SunburstSegment? Hit(Point p)
    {
        var (angle, radius) = SunburstLayout.Polar(p.X - _geometry.CenterX, p.Y - _geometry.CenterY);
        return SunburstLayout.HitTest(_segments, angle, radius, _geometry.Inner, _geometry.RingWidth);
    }

#if DEBUG
    /// <summary>Đặt con trỏ giả vào giữa cung của một node (ảnh chụp kiểm thử tooltip/hover).</summary>
    internal void SimulatePointer(int node)
    {
        if (_segments.FirstOrDefault(s => s.NodeIndex == node) is not { } seg) return;
        var (r0, r1) = _geometry.Ring(seg.Depth);
        var (x, y) = _geometry.Point(seg.MidAngle, (r0 + r1) / 2);
        _mouse = new Point(x, y);
        UpdateHover(_mouse.Value, raise: true);
    }
#endif

    private void UpdateHover(Point p, bool raise)
    {
        var hit = Hit(p);
        var (_, radius) = SunburstLayout.Polar(p.X - _geometry.CenterX, p.Y - _geometry.CenterY);
        _centerHover = radius < _geometry.Inner;
        var changed = hit?.Id != _hover?.Id;
        _hover = hit;
        var drillable = hit is { NodeIndex: { } i } && _tree is not null && i < _tree.Count && _tree.IsDirectory(i) && _tree.HasChildren(i);
        Cursor = drillable || (_centerHover && _canGoUp) ? Cursors.Hand : null;
        RenderOverlay();
        if (changed && raise) HoverChanged?.Invoke(hit);
    }
}
