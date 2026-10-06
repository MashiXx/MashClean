using CleanBoost.Core;

namespace CleanBoost.Engine.Nodes;

/// <summary>
/// Mục ảo, không phải file thông thường (SYSTEM_DESIGN mục 6.1): mục khởi động, tác vụ bảo trì, Thùng rác, khoá registry...
/// Feature định nghĩa loại riêng bằng cách kế thừa.
/// </summary>
public abstract record VirtualItem
{
    /// <summary>Đường dẫn file gắn với mục ảo (nếu có), để gộp trùng và hiển thị.</summary>
    public virtual string? AssociatedPath => null;
    public abstract string DefaultTitle { get; }
}

/// <summary>Tác vụ bảo trì đưa vào Smart Scan.</summary>
public sealed record MaintenanceItem(MaintenanceTaskName Task) : VirtualItem
{
    public override string DefaultTitle => Task.Title();
}

/// <summary>Thùng rác của một ổ (hoặc mọi ổ khi <see cref="Drive"/> là null).</summary>
public sealed record RecycleBinItem(string? Drive) : VirtualItem
{
    public override string DefaultTitle => Drive is null ? Loc.T("Thùng rác") : Loc.F("Thùng rác (%@)", Drive.TrimEnd('\\'));
}

/// <summary><c>docker system prune</c> — không xoá file ổ ảo của Docker trực tiếp.</summary>
public sealed record DockerPruneItem : VirtualItem
{
    public override string DefaultTitle => "docker system prune";
}

/// <summary>Khoá registry (file sót của app trong <c>HKCU\Software</c>), xuất bản sao .reg trước khi xoá.</summary>
public sealed record RegistryKeyItem(string Hive, string KeyPath) : VirtualItem
{
    public string DisplayPath => Hive + "\\" + KeyPath;
    public override string DefaultTitle => DisplayPath;
}

public enum NodeKind
{
    /// <summary>Nhóm hiển thị ("Cache người dùng").</summary>
    Group,
    /// <summary>Nhóm theo ứng dụng.</summary>
    Application,
    File,
    Directory,
    /// <summary>Mục ảo (mục khởi động, tác vụ bảo trì...).</summary>
    Virtual,
}

/// <summary>
/// Một node trong cây kết quả (mục 6.1). Sau khi <see cref="NodeTree"/> được dựng, cây coi như bất biến; trạng thái chọn
/// nằm ngoài (<see cref="SelectionState"/>).
/// </summary>
public sealed class Node
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public NodeKind Kind { get; init; }
    /// <summary>Đường dẫn của file/thư mục, hoặc thư mục cài đặt với node ứng dụng.</summary>
    public string? Path { get; init; }
    /// <summary>Định danh ứng dụng với node <see cref="NodeKind.Application"/>.</summary>
    public string? AppID { get; init; }
    public LocalizedText? GroupTitle { get; init; }
    public VirtualItem? Virtual { get; init; }

    public string Title { get; set; } = "";
    public ByteCount Size { get; set; }
    public int ItemCount { get; set; }
    public SafetyLevel Safety { get; set; } = SafetyLevel.Review;
    public LocalizedText Reason { get; set; } = LocalizedText.Empty;
    public string? RuleID { get; set; }
    /// <summary>Nhóm rule (userCaches, appLeftovers...), dùng khi gộp trùng.</summary>
    public string? Category { get; set; }
    public RemovalStrategy Removal { get; set; } = RemovalStrategy.MoveToTrash;
    public bool RequiresRoot { get; set; }
    /// <summary>Vùng rule cho phép: sau khi chuẩn hoá, đường dẫn phải vẫn nằm trong đây (mục 15.1).</summary>
    public IReadOnlyList<string> AllowedRoots { get; set; } = [];
    public List<Node> Children { get; set; } = [];
    public DateTime? LastAccessUtc { get; set; }
    /// <summary>Nhãn phụ hiển thị ("đang chạy", "hỏng"...).</summary>
    public List<string> Badges { get; set; } = [];
    /// <summary>Khoá biểu tượng (tên kiểu SF Symbol của bản Mac), giao diện đổi sang glyph Segoe.</summary>
    public string? Icon { get; set; }

    public bool IsContainer => Kind is NodeKind.Group or NodeKind.Application;

    /// <summary>Đường dẫn đại diện: file/thư mục, thư mục app, hoặc file gắn với mục ảo.</summary>
    public string? Url => Kind switch
    {
        NodeKind.File or NodeKind.Directory or NodeKind.Application => Path,
        NodeKind.Virtual => Virtual?.AssociatedPath,
        _ => null,
    };

    public static string DefaultTitle(NodeKind kind, string? path, LocalizedText? groupTitle, VirtualItem? item, string? appID) => kind switch
    {
        NodeKind.Group => groupTitle?.Resolved ?? "",
        NodeKind.Application => path is null ? appID ?? "" : System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } n ? n : appID ?? path,
        NodeKind.File or NodeKind.Directory => path is null ? "" : System.IO.Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } f ? f : path,
        _ => item?.DefaultTitle ?? "",
    };

    // MARK: Tạo node

    public static Node Group(LocalizedText title, string icon, string? category, IEnumerable<Node> children, SafetyLevel? safety = null)
    {
        var kids = children.ToList();
        var n = new Node
        {
            Kind = NodeKind.Group,
            GroupTitle = title,
            Title = title.Resolved,
            Category = category,
            Children = kids,
            Icon = icon,
        };
        n.Safety = safety ?? (kids.Count == 0 ? SafetyLevel.Review : kids.Min(c => c.Safety));
        n.RecomputeAggregates();
        return n;
    }

    public static Node Application(string appID, string? path, string title, IEnumerable<Node> children, string? category = null, string icon = "app")
    {
        var kids = children.ToList();
        var n = new Node
        {
            Kind = NodeKind.Application,
            AppID = appID,
            Path = path,
            Title = title,
            Category = category,
            Children = kids,
            Icon = icon,
        };
        n.Safety = kids.Count == 0 ? SafetyLevel.Review : kids.Min(c => c.Safety);
        n.RecomputeAggregates();
        return n;
    }

    public static Node ForPath(string path, bool isDirectory, string? title = null) => new()
    {
        Kind = isDirectory ? NodeKind.Directory : NodeKind.File,
        Path = path,
        Title = title ?? DefaultTitle(isDirectory ? NodeKind.Directory : NodeKind.File, path, null, null, null),
    };

    public static Node ForVirtual(VirtualItem item, string? title = null) => new()
    {
        Kind = NodeKind.Virtual,
        Virtual = item,
        Title = title ?? item.DefaultTitle,
    };

    /// <summary>Bản sao nông (danh sách con mới, các con dùng chung), dùng khi cắt tỉa cây.</summary>
    public Node CloneShallow() => new()
    {
        Id = Id,
        Kind = Kind,
        Path = Path,
        AppID = AppID,
        GroupTitle = GroupTitle,
        Virtual = Virtual,
        Title = Title,
        Size = Size,
        ItemCount = ItemCount,
        Safety = Safety,
        Reason = Reason,
        RuleID = RuleID,
        Category = Category,
        Removal = Removal,
        RequiresRoot = RequiresRoot,
        AllowedRoots = AllowedRoots,
        Children = [.. Children],
        LastAccessUtc = LastAccessUtc,
        Badges = [.. Badges],
        Icon = Icon,
    };

    /// <summary>Tổng dung lượng và số mục của node chứa = tổng của con (cộng từ lá lên gốc).</summary>
    public void RecomputeAggregates()
    {
        if (Children.Count == 0) return;
        foreach (var c in Children) c.RecomputeAggregates();
        if (!IsContainer) return;
        Size = Children.Sum(c => c.Size);
        ItemCount = Children.Sum(c => Math.Max(c.ItemCount, 1));
        LastAccessUtc = Children.Select(c => c.LastAccessUtc).Where(d => d is not null).DefaultIfEmpty(null).Max();
    }

    /// <summary>Duyệt mọi node con cháu (kể cả chính nó), theo chiều sâu.</summary>
    public IEnumerable<Node> Descendants()
    {
        yield return this;
        foreach (var c in Children)
        {
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    /// <summary>Các node xoá được (không phải container) bên dưới, kể cả chính nó.</summary>
    public IEnumerable<Node> RemovableLeaves()
    {
        if (!IsContainer)
        {
            yield return this;
            yield break;
        }
        foreach (var c in Children)
        {
            foreach (var l in c.RemovableLeaves()) yield return l;
        }
    }

    public override string ToString() => $"{Kind} {Title} {Size}";
}
