using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.LargeOldFiles;

namespace CleanBoost.Features.Duplicates;

/// <summary>File trùng lặp (DuplicatesFeature của bản Mac, mục 11.8). Không chạy trong Smart Scan; không chọn sẵn gì (mục 15.2).</summary>
public sealed class DuplicatesFeature(string? home = null) : IFeatureScanProvider
{
    /// <summary>Biến môi trường (chỉ bản Debug) thay thư mục quét mặc định, phân tách bằng <c>;</c> — dùng cho kiểm thử giao diện.</summary>
    public const string RootsEnvironmentVariable = "CLEANBOOST_DUPLICATES_ROOTS";

    public string FeatureID => FeatureIDs.Duplicates;
    public bool IncludeInSmartScan => false;
    public string Home { get; } = home ?? KnownPaths.UserHome;

    /// <summary>Mặc định: hồ sơ người dùng (bỏ AppData, thư mục ẩn) và các thư mục chuẩn nằm ngoài hồ sơ.</summary>
    public IReadOnlyList<string> DefaultRoots => LargeOldFilesFeature.DebugRoots(RootsEnvironmentVariable) ?? UserContentScope.DefaultRoots(Home);

    public IReadOnlyList<IScanTask> ScanTasks() => [MakeTask(DefaultRoots)];

    /// <summary>Task với thư mục do người dùng chọn (rỗng = mặc định) và hộp tiến độ theo bước.</summary>
    public DuplicatesTask MakeTask(IReadOnlyList<string> roots, DuplicateProgressBox? progress = null) =>
        new(roots.Count == 0 ? DefaultRoots : roots, Home, progress);

    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var wasted = nodes.Sum(DuplicatesTask.Wasted);
        return new FeatureSummary(FeatureID, SmartScanCard.Cleanup, Loc.T("File trùng lặp"),
            nodes.Count == 0 ? Loc.T("Không có file trùng lặp") : Loc.F("%lld nhóm trùng lặp", nodes.Count), wasted, nodes.Count);
    }
}

/// <summary>Chọn tự động và thống kê cho nhóm trùng lặp (DuplicateSelection của bản Mac).</summary>
public static class DuplicateSelection
{
    public static bool IsKeep(Node node) => node.Badges.Contains(DuplicateBadges.Keep);
    public static bool IsLinked(Node node) => node.Badges.Contains(DuplicateBadges.HardLink);

    /// <summary>"Chọn tự động": mọi bản trừ bản nên giữ và bản có hard link (xoá không giải phóng gì), chỉ ở nhóm có bản nên giữ.</summary>
    public static SelectionState AutoSelect(NodeTree tree)
    {
        var s = new SelectionState();
        var ids = new List<Guid>();
        foreach (var group in tree.Roots)
        {
            var leaves = group.RemovableLeaves().ToList();
            if (!leaves.Any(IsKeep)) continue;
            ids.AddRange(leaves.Where(l => !IsKeep(l) && !IsLinked(l)).Select(l => l.Id));
        }
        s.SetMany(ids, true, tree);
        return s;
    }

    /// <summary>Nhóm mà mọi bản đều đang được chọn (sau khi dọn sẽ không còn bản nào ngoài Thùng rác).</summary>
    public static List<Node> GroupsWithEverythingSelected(SelectionState selection, NodeTree tree) =>
        tree.Roots.Where(g => selection.State(g.Id, tree) == CheckState.On).ToList();

    public static ByteCount Wasted(Node group) => DuplicatesTask.Wasted(group);
}
