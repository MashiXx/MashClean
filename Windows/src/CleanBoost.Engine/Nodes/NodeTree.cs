using CleanBoost.Core;

namespace CleanBoost.Engine.Nodes;

/// <summary>
/// Cây kết quả bất biến (mục 6), chia sẻ được giữa các luồng. Có chỉ mục phẳng để tra cha/con, danh sách lá theo node
/// mà không duyệt lại cả cây.
/// </summary>
public sealed class NodeTree
{
    public IReadOnlyList<Node> Roots { get; }
    public IReadOnlyDictionary<Guid, Node> Index { get; }
    public IReadOnlyDictionary<Guid, Guid> Parent { get; }
    /// <summary>id node → danh sách id lá xoá được bên dưới.</summary>
    public IReadOnlyDictionary<Guid, Guid[]> LeafIDs { get; }
    public ByteCount TotalSize { get; }

    public NodeTree(IEnumerable<Node> roots)
    {
        var list = roots.ToList();
        foreach (var r in list) r.RecomputeAggregates();
        Roots = list;
        var index = new Dictionary<Guid, Node>();
        var parent = new Dictionary<Guid, Guid>();
        var leaves = new Dictionary<Guid, Guid[]>();

        List<Guid> Visit(Node node, Guid? parentID)
        {
            index[node.Id] = node;
            if (parentID is { } p) parent[node.Id] = p;
            var mine = new List<Guid>();
            if (node.IsContainer)
            {
                foreach (var c in node.Children) mine.AddRange(Visit(c, node.Id));
            }
            else
            {
                mine.Add(node.Id);
                foreach (var c in node.Children) Visit(c, node.Id); // con của mục xoá được chỉ để hiển thị
            }
            leaves[node.Id] = mine.ToArray();
            return mine;
        }

        foreach (var r in list) Visit(r, null);
        Index = index;
        Parent = parent;
        LeafIDs = leaves;
        TotalSize = list.Sum(r => r.Size);
    }

    public static readonly NodeTree Empty = new([]);

    public bool IsEmpty => Roots.Count == 0;

    public Node? Node(Guid id) => Index.TryGetValue(id, out var n) ? n : null;

    public IEnumerable<Guid> Ancestors(Guid id)
    {
        var current = id;
        while (Parent.TryGetValue(current, out var p))
        {
            yield return p;
            current = p;
        }
    }

    /// <summary>Mọi lá xoá được trong cây.</summary>
    public IEnumerable<Node> AllLeaves => Roots.SelectMany(r => r.RemovableLeaves());

    public IEnumerable<Node> Leaves(Guid under) =>
        LeafIDs.TryGetValue(under, out var ids) ? ids.Select(i => Index[i]) : [];

    public ByteCount Size(Func<Node, bool> predicate) => AllLeaves.Where(predicate).Sum(n => n.Size);

    // MARK: Gộp nhiều kết quả

    /// <summary>Thứ tự ưu tiên khi hai rule cùng trỏ vào một đường dẫn (mục 6.3): file sót của app > cache > log > còn lại.</summary>
    public static readonly string[] DefaultCategoryPriority =
    [
        "appLeftovers", "orphanedLeftovers", "userCaches", "browserCaches", "systemCaches", "devToolCaches", "windowsUpdate",
        "logs", "userLogs", "systemLogs", "crashReports",
    ];

    /// <summary>Gộp node từ nhiều task, khử trùng lặp và hạ mức an toàn của mục cache lớn bất thường.</summary>
    public static NodeTree Merge(IEnumerable<Node> roots, IReadOnlyList<string>? categoryPriority = null)
    {
        var deduped = Deduplicate(roots.ToList(), categoryPriority ?? DefaultCategoryPriority);
        foreach (var r in deduped) ApplyLargeItemGuard(r);
        return new NodeTree(deduped.Where(r => !r.IsContainer || r.Children.Count > 0));
    }

    /// <summary>Khoá so sánh đường dẫn: chuẩn hoá cú pháp, không phân biệt hoa thường.</summary>
    public static string PathKey(string path) => (PathPolicy.NormalizeSyntax(path) ?? path).ToUpperInvariant();

    /// <summary>
    /// Một đường dẫn chỉ thuộc một node (mục 6.3):
    /// 1. Chỉ mục theo đường dẫn chuẩn hoá. 2. Trùng đường dẫn: giữ node có category ưu tiên cao hơn.
    /// 3. Node là thư mục cha của node khác: giữ node cha, bỏ node con.
    /// </summary>
    public static List<Node> Deduplicate(List<Node> roots, IReadOnlyList<string> categoryPriority)
    {
        int Rank(string? category)
        {
            if (category is null) return categoryPriority.Count;
            var i = -1;
            for (var k = 0; k < categoryPriority.Count; k++)
            {
                if (categoryPriority[k] == category) { i = k; break; }
            }
            return i < 0 ? categoryPriority.Count : i;
        }

        var candidates = new List<(Guid Id, string Path, int Rank, int Order)>();
        var order = 0;

        void Collect(Node n, string? inherited)
        {
            var cat = n.Category ?? inherited;
            if (!n.IsContainer)
            {
                if (n.Url is { } url) candidates.Add((n.Id, PathKey(url), Rank(cat), order++));
                return;
            }
            foreach (var c in n.Children) Collect(c, cat);
        }

        foreach (var r in roots) Collect(r, null);

        var winnerByPath = new Dictionary<string, (Guid Id, string Path, int Rank, int Order)>();
        foreach (var c in candidates)
        {
            if (winnerByPath.TryGetValue(c.Path, out var existing))
            {
                if ((c.Rank, c.Order).CompareTo((existing.Rank, existing.Order)) < 0) winnerByPath[c.Path] = c;
            }
            else
            {
                winnerByPath[c.Path] = c;
            }
        }
        var removed = candidates.Where(c => winnerByPath[c.Path].Id != c.Id).Select(c => c.Id).ToHashSet();

        // Node là con cháu của một node khác → bỏ node con. Sắp theo đường dẫn để kiểm tra tiền tố; '\' xếp nhỏ nhất để con cháu
        // luôn đứng liền sau tổ tiên (nếu không, "CACHEX" < "CACHE\SUB" vì 'X' < '\' và con bị sót).
        var survivors = winnerByPath.Values.OrderBy(c => c.Path.Replace('\\', '\0'), StringComparer.Ordinal).ToList();
        var stack = new List<string>();
        foreach (var c in survivors)
        {
            while (stack.Count > 0 && !c.Path.StartsWith(stack[^1].TrimEnd('\\') + "\\", StringComparison.Ordinal)) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0) removed.Add(c.Id);
            else stack.Add(c.Path);
        }

        if (removed.Count == 0) return roots;

        Node? Prune(Node n)
        {
            if (removed.Contains(n.Id)) return null;
            if (!n.IsContainer) return n;
            var copy = n.CloneShallow();
            copy.Children = n.Children.Select(Prune).Where(c => c is not null).Cast<Node>().ToList();
            if (copy.Children.Count == 0) return null;
            copy.RecomputeAggregates();
            return copy;
        }

        return roots.Select(Prune).Where(r => r is not null).Cast<Node>().ToList();
    }

    /// <summary>Mục lớn bất thường (thư mục > 50 GB mà rule loại cache) thì hạ xuống review (mục 15.1).</summary>
    internal static void ApplyLargeItemGuard(Node node)
    {
        string[] cacheCategories = ["userCaches", "browserCaches", "systemCaches", "devToolCaches"];
        if (!node.IsContainer && node.Category is { } cat && cacheCategories.Contains(cat) && node.Size > ByteCount.Gigabytes(50) && node.Safety == SafetyLevel.Safe)
        {
            node.Safety = SafetyLevel.Review;
            node.Badges.Add(Loc.T("Lớn bất thường"));
        }
        foreach (var c in node.Children) ApplyLargeItemGuard(c);
    }
}
