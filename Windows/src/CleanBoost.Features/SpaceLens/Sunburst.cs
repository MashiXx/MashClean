namespace CleanBoost.Features.SpaceLens;

/// <summary>Gộp các mục nhỏ hơn ngưỡng (mặc định 0,5% vùng cha) thành "Các mục nhỏ khác" (SmallItemGrouping của bản Mac).</summary>
public static class SmallItemGrouping
{
    public const double DefaultThreshold = 0.005;

    public sealed record Result(IReadOnlyList<(int Id, long Size)> Visible, long OtherSize, int OtherCount);

    /// <summary>Mục đủ lớn (≥ ngưỡng × tổng và &gt; 0) giữ riêng, sắp giảm dần; còn lại cộng vào nhóm "khác".</summary>
    public static Result Group(IEnumerable<(int Id, long Size)> items, long total, double threshold = DefaultThreshold)
    {
        var sorted = items.ToList();
        sorted.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (total <= 0) return new Result([], 0, sorted.Count);
        var minSize = total * threshold;
        var visible = new List<(int Id, long Size)>();
        long otherSize = 0;
        var otherCount = 0;
        foreach (var item in sorted)
        {
            if (item.Size >= minSize && item.Size > 0)
            {
                visible.Add(item);
            }
            else
            {
                otherSize += item.Size;
                otherCount++;
            }
        }
        return new Result(visible, otherSize, otherCount);
    }

    /// <summary>Gộp con của một node trong cây.</summary>
    public static Result Group(DiskTree tree, int node, double threshold = DefaultThreshold)
    {
        var items = new List<(int, long)>(tree.ChildCount(node));
        foreach (var c in tree.Children(node)) items.Add((c, tree.Size(c)));
        return Group(items, tree.Size(node), threshold);
    }
}

/// <summary>Đích của một cung: một node của cây, hoặc nhóm "Các mục nhỏ khác" bên dưới một node cha.</summary>
public readonly record struct SunburstTarget(int Node, bool IsOthers, int OtherCount)
{
    public static SunburstTarget ForNode(int node) => new(node, false, 0);
    public static SunburstTarget Others(int parent, int count) => new(parent, true, count);
}

/// <summary>Một cung trên biểu đồ sunburst. Góc tính bằng radian, 0 ở hướng 12 giờ, tăng theo chiều kim đồng hồ.</summary>
/// <param name="Depth">Vòng thứ mấy tính từ tâm (1 = con trực tiếp của thư mục đang xem).</param>
/// <param name="Branch">Thứ tự của nhánh cấp 1 chứa cung này, dùng để chọn màu; -1 với "Các mục nhỏ khác".</param>
public sealed record SunburstSegment(int Id, SunburstTarget Target, int Depth, double StartAngle, double EndAngle, long Size, int Branch)
{
    public double MidAngle => (StartAngle + EndAngle) / 2;
    public double Span => EndAngle - StartAngle;
    /// <summary>Chỉ số node, <c>null</c> với "Các mục nhỏ khác".</summary>
    public int? NodeIndex => Target.IsOthers ? null : Target.Node;
}

/// <summary>Kích thước vẽ: bán kính ngoài, bán kính vòng tâm, độ dày mỗi vòng (giống SunburstChart của bản Mac).</summary>
public readonly record struct SunburstGeometry(double CenterX, double CenterY, double Outer, double Inner, double RingWidth)
{
    public static SunburstGeometry For(double width, double height, int maxDepth = SunburstLayout.DefaultMaxDepth)
    {
        var side = Math.Min(width, height);
        var outer = Math.Max(0, side / 2 - 8);
        var inner = outer * 0.24;
        return new SunburstGeometry(width / 2, height / 2, outer, inner, (outer - inner) / Math.Max(1, maxDepth));
    }

    /// <summary>Bán kính trong/ngoài của một vòng (chừa 1 px mỗi bên để các vòng tách nhau).</summary>
    public (double R0, double R1) Ring(int depth)
    {
        var r0 = Inner + (depth - 1) * RingWidth + 1;
        return (r0, r0 + RingWidth - 2);
    }

    /// <summary>Điểm trên màn hình ở góc (0 = 12 giờ, theo chiều kim đồng hồ) và bán kính cho trước.</summary>
    public (double X, double Y) Point(double angle, double radius) => (CenterX + radius * Math.Sin(angle), CenterY - radius * Math.Cos(angle));
}

/// <summary>Tính bố cục sunburst và hit-test (hàm thuần, test được).</summary>
public static class SunburstLayout
{
    public const double FullCircle = 2 * Math.PI;
    public const int DefaultMaxDepth = 4;
    public const double DefaultMinAngle = 0.003;

    /// <param name="center">Node đang xem (ở tâm).</param>
    /// <param name="maxDepth">Số vòng tối đa.</param>
    /// <param name="threshold">Tỉ lệ tối thiểu so với vùng cha để vẽ riêng.</param>
    /// <param name="minAngle">Cung hẹp hơn (radian) không vẽ riêng mà gộp vào "Các mục nhỏ khác".</param>
    public static List<SunburstSegment> Layout(DiskTree tree, int center, int maxDepth = DefaultMaxDepth,
        double threshold = SmallItemGrouping.DefaultThreshold, double minAngle = DefaultMinAngle)
    {
        var segments = new List<SunburstSegment>();
        if (tree.Size(center) <= 0) return segments;
        var nextId = 0;

        void Visit(int node, int depth, double start, double span, int branch)
        {
            var parentSize = tree.Size(node);
            if (parentSize <= 0 || depth > maxDepth) return;
            var grouped = SmallItemGrouping.Group(tree, node, threshold);
            var angle = start;
            var otherSize = grouped.OtherSize;
            var otherCount = grouped.OtherCount;
            for (var i = 0; i < grouped.Visible.Count; i++)
            {
                var (id, size) = grouped.Visible[i];
                var width = span * size / parentSize;
                if (width < minAngle)
                {
                    otherSize += size;
                    otherCount++;
                    continue;
                }
                var b = depth == 1 ? i : branch;
                segments.Add(new SunburstSegment(nextId++, SunburstTarget.ForNode(id), depth, angle, angle + width, size, b));
                if (tree.IsDirectory(id)) Visit(id, depth + 1, angle, width, b);
                angle += width;
            }
            if (otherSize > 0)
            {
                var width = span * otherSize / parentSize;
                if (width >= minAngle / 4)
                {
                    segments.Add(new SunburstSegment(nextId++, SunburstTarget.Others(node, otherCount), depth, angle, angle + width, otherSize, -1));
                }
            }
        }

        Visit(center, 1, 0, FullCircle, 0);
        return segments;
    }

    /// <summary>Toạ độ cực của điểm (dx, dy) so với tâm, trục y hướng xuống (toạ độ màn hình).</summary>
    public static (double Angle, double Radius) Polar(double dx, double dy)
    {
        var angle = Math.Atan2(dx, -dy);
        if (angle < 0) angle += FullCircle;
        return (angle, Math.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>Tìm cung chứa điểm. <c>null</c> nếu ở vòng tâm hoặc ngoài biểu đồ.</summary>
    public static SunburstSegment? HitTest(IReadOnlyList<SunburstSegment> segments, double angle, double radius, double innerRadius, double ringWidth)
    {
        if (radius < innerRadius || ringWidth <= 0) return null;
        var ring = (int)((radius - innerRadius) / ringWidth) + 1;
        foreach (var s in segments)
        {
            if (s.Depth == ring && angle >= s.StartAngle && angle < s.EndAngle) return s;
        }
        return null;
    }

    /// <summary>Hai bố cục vẽ ra giống hệt nhau (chỉ số node có thể khác sau khi cây được cập nhật).</summary>
    public static bool SameShape(IReadOnlyList<SunburstSegment> a, IReadOnlyList<SunburstSegment> b, Func<SunburstSegment, bool> isFileA, Func<SunburstSegment, bool> isFileB)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Depth != y.Depth || x.Branch != y.Branch || Math.Abs(x.StartAngle - y.StartAngle) > 1e-9 || Math.Abs(x.EndAngle - y.EndAngle) > 1e-9
                || x.Target.IsOthers != y.Target.IsOthers || isFileA(x) != isFileB(y)) return false;
        }
        return true;
    }
}

/// <summary>Bảng màu sunburst (giống bản Mac): mỗi nhánh cấp 1 một sắc độ, càng xa tâm càng nhạt, file nhạt hơn thư mục.</summary>
public static class SunburstPalette
{
    /// <summary>Màu HSB + độ mờ.</summary>
    public static (double Hue, double Saturation, double Brightness, double Alpha) Hsb(int branch, int depth, bool isFile, bool highlighted)
    {
        if (branch < 0) return (0, 0, 1, highlighted ? 0.45 : 0.22);
        var hue = (0.08 + branch * 0.137) % 1.0;
        var depthFade = (depth - 1) * 0.12;
        var saturation = Math.Max(0.25, 0.75 - depthFade) * (isFile ? 0.6 : 1);
        var brightness = highlighted ? 1 : 0.92 - depthFade / 2;
        return (hue, saturation, brightness, 1);
    }

    /// <summary>Màu ARGB 32 bit của một cung.</summary>
    public static uint Argb(int branch, int depth, bool isFile, bool highlighted)
    {
        var (h, s, v, a) = Hsb(branch, depth, isFile, highlighted);
        var (r, g, b) = HsvToRgb(h, s, v);
        return ((uint)Math.Round(a * 255) << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = (h % 1 + 1) % 1 * 6;
        var i = (int)Math.Floor(h);
        var f = h - i;
        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var t = v * (1 - s * (1 - f));
        var (r, g, b) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        static byte B(double x) => (byte)Math.Clamp(Math.Round(x * 255), 0, 255);
        return (B(r), B(g), B(b));
    }
}
