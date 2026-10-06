using CleanBoost.Core;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.SpaceLens;

/// <summary>
/// Feature Space Lens. Không chạy trong Smart Scan; màn riêng dùng <see cref="DiskScanner"/> trực tiếp để giữ cây đầy đủ.
/// </summary>
public sealed class SpaceLensFeature(FileSystemService fileSystem, string? defaultRoot = null) : IFeatureScanProvider
{
    public string FeatureID => FeatureIDs.SpaceLens;
    public bool IncludeInSmartScan => false;
    public string DefaultRoot { get; } = defaultRoot ?? KnownPaths.UserHome;
    public FileSystemService FileSystem { get; } = fileSystem;

    public IReadOnlyList<IScanTask> ScanTasks() => [new SpaceLensTask(DefaultRoot)];

    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var total = nodes.Sum(n => n.Size);
        var top = nodes.OrderByDescending(n => n.Size).Take(3).Select(n => $"{n.Title} ({n.Size.Formatted})").ToList();
        return new FeatureSummary(FeatureID, SmartScanCard.Cleanup, Loc.T("Bản đồ dung lượng"),
            top.Count == 0 ? Loc.T("Không có dữ liệu") : string.Join(", ", top), total, nodes.Count);
    }
}

/// <summary>Task quét dung lượng một thư mục cho <see cref="IFeatureScanProvider"/>: trả các mục cấp 1 sắp theo dung lượng.</summary>
public sealed class SpaceLensTask(string root) : IScanTask
{
    public string Id => "spaceLens";
    public string Title => Loc.T("Bản đồ dung lượng");
    public double EstimatedWeight => 4;
    public ScanPriority Priority => ScanPriority.Low;
    public string Root { get; } = root;

    public async Task<ScanOutput> RunAsync(ScanContext context)
    {
        var progress = new DiskScanProgress();
        var tree = await Task.Run(() => new DiskScanner(context.FileSystem).Scan(Root, progress, context.Cancellation), context.Cancellation)
            .ConfigureAwait(false);
        context.Progress.AddFilesVisited((int)Math.Min(int.MaxValue, progress.Items));
        var nodes = SpaceLensNodes.Nodes(tree.Children(DiskTree.Root).AsEnumerable(), tree)
            .Where(n => !context.Environment.Ignore.IgnoresPath(n.Url ?? ""))
            .OrderByDescending(n => n.Size)
            .ToList();
        context.Progress.Report(1);
        return new ScanOutput { Nodes = nodes };
    }
}

/// <summary>Dựng <see cref="Node"/> xoá được từ node của cây Space Lens: mức cần xem lại, mặc định chuyển vào Thùng rác, không tự chọn.</summary>
public static class SpaceLensNodes
{
    public static Node MakeNode(int index, DiskTree tree)
    {
        var isDir = tree.IsDirectory(index);
        var node = Node.ForPath(tree.Path(index), isDir, tree.Name(index));
        node.Size = new ByteCount(tree.Size(index));
        node.ItemCount = isDir ? Math.Max(1, tree.DescendantCount(index)) : 1;
        node.Safety = SafetyLevel.Review;
        node.Reason = LocalizedText.Source("Chọn từ bản đồ dung lượng");
        node.Category = "spaceLens";
        node.Removal = RemovalStrategy.MoveToTrash;
        node.AllowedRoots = [tree.RootPath];
        return node;
    }

    /// <summary>Node cho các chỉ số; <paramref name="dropEmptyDirectories"/> bỏ thư mục rỗng (như bản Mac khi đề xuất tự động).</summary>
    public static List<Node> Nodes(IEnumerable<int> indices, DiskTree tree, bool dropEmptyDirectories = true) =>
        indices.Where(i => !dropEmptyDirectories || tree.Size(i) > 0 || !tree.IsDirectory(i)).Select(i => MakeNode(i, tree)).ToList();
}

/// <summary>Điều hướng trong cây: lưu đường đi bằng tên (ổn định khi cây được cập nhật và chỉ số thay đổi).</summary>
public sealed class SpaceLensNavigator
{
    private readonly List<string> _components = [];

    public IReadOnlyList<string> Components => _components;
    public bool CanGoUp => _components.Count > 0;

    public sealed record Crumb(int Level, string Title);

    /// <summary>Node đang xem; nếu thư mục không còn thì lùi về tổ tiên gần nhất còn tồn tại.</summary>
    public int CurrentIndex(DiskTree tree)
    {
        var cur = DiskTree.Root;
        foreach (var name in _components)
        {
            var c = tree.ChildNamed(name, cur);
            if (c < 0 || !tree.IsDirectory(c)) break;
            cur = c;
        }
        return cur;
    }

    /// <param name="rootTitle">Tên hiển thị của gốc (vd tên ổ); mặc định là tên thư mục gốc.</param>
    public IReadOnlyList<Crumb> Breadcrumb(DiskTree tree, string? rootTitle = null)
    {
        var crumbs = new List<Crumb> { new(0, rootTitle ?? RootTitle(tree.RootPath)) };
        for (var i = 0; i < _components.Count; i++) crumbs.Add(new Crumb(i + 1, _components[i]));
        return crumbs;
    }

    public static string RootTitle(string rootPath)
    {
        var name = Path.GetFileName(rootPath.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? rootPath : name;
    }

    /// <summary>Đi sâu vào một thư mục con bất kỳ trong cây (chỉ thư mục có con).</summary>
    public bool Drill(int index, DiskTree tree)
    {
        if (!tree.IsDirectory(index) || !tree.HasChildren(index)) return false;
        _components.Clear();
        _components.AddRange(tree.Components(index));
        return true;
    }

    public void GoUp()
    {
        if (_components.Count > 0) _components.RemoveAt(_components.Count - 1);
    }

    public void GoTo(int level)
    {
        level = Math.Max(0, level);
        if (level < _components.Count) _components.RemoveRange(level, _components.Count - level);
    }

    public void Reset() => _components.Clear();

    /// <summary>Cắt lại đường đi theo thư mục thực sự còn tồn tại.</summary>
    public void Normalize(DiskTree tree)
    {
        var parts = tree.Components(CurrentIndex(tree));
        _components.Clear();
        _components.AddRange(parts);
    }
}

/// <summary>Xoá mục chọn trên bản đồ qua Clean Engine (mặc định chuyển vào Thùng rác; PathPolicy chặn vùng cấm).</summary>
public sealed class SpaceLensCleaner(CleanEngine cleanEngine)
{
    public CleanEngine CleanEngine { get; } = cleanEngine;

    /// <summary>Node cho các đường dẫn đang chọn (bỏ gốc và mục nằm trong mục khác cũng đang chọn). Chạy trên luồng sở hữu cây.</summary>
    public List<Node> NodesForPaths(IEnumerable<string> paths, DiskTree tree) => NodesForIndices(ResolvePaths(paths, tree).Values, tree);

    /// <summary>Node cho các chỉ số đang chọn (bỏ gốc và mục nằm trong mục khác cũng đang chọn).</summary>
    public List<Node> NodesForIndices(IEnumerable<int> indices, DiskTree tree)
    {
        var list = indices.Where(i => i > DiskTree.Root && i < tree.Count).Distinct().ToList();
        return SpaceLensNodes.Nodes(RemovingNested(list, tree), tree, dropEmptyDirectories: false);
    }

    /// <summary>
    /// Tìm chỉ số cho nhiều đường dẫn một lượt: gom theo thư mục cha, mỗi thư mục cha dựng bảng tên con một lần
    /// (chọn hàng chục nghìn file trong một thư mục không thành O(n²)). Đường dẫn không còn trong cây bị bỏ.
    /// </summary>
    public static Dictionary<string, int> ResolvePaths(IEnumerable<string> paths, DiskTree tree)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in paths.GroupBy(p => Path.GetDirectoryName(DiskTree.NormalizePath(p)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                var i = tree.IndexForPath(items[0]);
                if (i >= 0) result[items[0]] = i;
                continue;
            }
            var dir = tree.IndexForPath(group.Key);
            if (dir < 0) continue;
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in tree.Children(dir)) names.TryAdd(tree.Name(c), c);
            foreach (var p in items)
            {
                if (names.TryGetValue(Path.GetFileName(DiskTree.NormalizePath(p)), out var i)) result[p] = i;
            }
        }
        return result;
    }

    /// <summary>Kiểm tra PathPolicy và lập kế hoạch; mục bị chặn nằm trong <see cref="CleanPlan.BlockedItems"/>.</summary>
    public CleanPlan Plan(IReadOnlyList<Node> nodes) => CleanEngine.MakePlan(nodes);

    /// <summary>Thư mục cha của các mục đã xử lý: cần đọc lại để cập nhật tổng.</summary>
    public static List<string> AffectedDirectories(CleanReport report) =>
        report.Entries.Select(e => e.Item.Path).Where(p => p is not null).Select(p => Path.GetDirectoryName(p!.TrimEnd('\\')))
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Bỏ mục nằm bên trong một mục khác cũng đang chọn.</summary>
    public static List<int> RemovingNested(IReadOnlyList<int> indices, DiskTree tree)
    {
        var set = indices.ToHashSet();
        return indices.Where(i =>
        {
            for (var p = tree.Parent(i); p != DiskTree.NoParent; p = tree.Parent(p))
            {
                if (set.Contains(p)) return false;
            }
            return true;
        }).ToList();
    }

    /// <summary>Tổng dung lượng các mục chọn (không đếm mục lồng trong mục khác).</summary>
    public static long SelectedBytes(IEnumerable<int> indices, DiskTree tree)
    {
        var list = indices.Where(i => i >= 0 && i < tree.Count).Distinct().ToList();
        return RemovingNested(list, tree).Sum(tree.Size);
    }
}

/// <summary>Gom đường dẫn mà bộ theo dõi thay đổi báo về thành danh sách thư mục cần đọc lại.</summary>
public static class SpaceLensRefreshPlanner
{
    /// <summary>Chuẩn hoá, bỏ trùng (không phân biệt hoa thường), bỏ đường dẫn ngoài cây.</summary>
    public static List<string> Directories(IEnumerable<string> changedPaths, string rootPath)
    {
        var root = DiskTree.NormalizePath(rootPath);
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in changedPaths)
        {
            var p = DiskTree.NormalizePath(raw);
            if (!p.Equals(root, StringComparison.OrdinalIgnoreCase) && !p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(p)) result.Add(p);
        }
        return result;
    }
}
