using System.Collections.Concurrent;
using System.Diagnostics;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.LargeOldFiles;

/// <summary>Nhãn gắn trên node để màn hình lọc "lớn"/"cũ"/"thư mục ẩn" (LargeOldBadges của bản Mac).</summary>
public static class LargeOldBadges
{
    public static string Large => Loc.T("Lớn");
    public static string Old => Loc.T("Lâu không dùng");
    public static string Hidden => Loc.T("Thư mục ẩn");
    public const string Category = "largeOldFiles";
}

[Flags]
public enum LargeOldMatch { None = 0, Large = 1, Old = 2 }

/// <summary>Tiêu chí File lớn &amp; cũ (LargeOldFilesCriteria của bản Mac). Ngưỡng thập phân: 1 MB = 1.000.000 byte.</summary>
public sealed record LargeOldFilesCriteria
{
    /// <summary>"Lớn": dung lượng trên đĩa vượt ngưỡng (mặc định 100 MB).</summary>
    public long LargeThreshold { get; init; } = 100_000_000;
    /// <summary>"Cũ": lần dùng cuối trước <c>now - OldDays</c>.</summary>
    public int OldDays { get; init; } = 365;
    /// <summary>File cũ phải lớn hơn ngưỡng này mới đáng đề xuất (tránh hàng trăm nghìn file nhỏ).</summary>
    public long OldMinimumSize { get; init; } = 10_000_000;

    public static LargeOldFilesCriteria From(int largeThresholdMB, int oldDays) => new()
    {
        LargeThreshold = Math.Max(1, largeThresholdMB > 0 ? largeThresholdMB : 100) * 1_000_000L,
        OldDays = Math.Max(1, oldDays > 0 ? oldDays : 365),
    };

    public DateTime CutoffUtc(DateTime nowUtc) => nowUtc.AddDays(-OldDays);

    public LargeOldMatch Match(long size, DateTime? lastUsedUtc, DateTime nowUtc)
    {
        var m = LargeOldMatch.None;
        if (size > LargeThreshold) m |= LargeOldMatch.Large;
        if (lastUsedUtc is { } used && used < CutoffUtc(nowUtc) && size > OldMinimumSize) m |= LargeOldMatch.Old;
        return m;
    }
}

/// <summary>
/// Quét file lớn và cũ (LargeOldFilesTask của bản Mac, mục 11.7). Windows không có Spotlight nên duyệt trực tiếp
/// hồ sơ người dùng song song nhiều luồng; bỏ AppData, vùng cấm, liên kết, file đám mây chưa tải về.
/// Như bản Mac: chỉ đi vào thư mục ẩn nằm ngay trong hồ sơ (<c>~\.gradle</c>…) và gắn nhãn "Thư mục ẩn";
/// thư mục ẩn lồng sâu (<c>.git</c>, <c>.vs</c>) bỏ qua. Mọi mục là file, mức review, chuyển vào Thùng rác, không chọn sẵn.
/// </summary>
public sealed class LargeOldFilesTask(LargeOldFilesCriteria criteria, IReadOnlyList<string> roots, string home) : IScanTask
{
    public const string TaskID = "largeOldFiles";

    public string Id => TaskID;
    public string Title => Loc.T("File lớn và cũ");
    public double EstimatedWeight => 3;
    /// <summary>Quét do người dùng bấm và có nút Dừng: không giới hạn thời gian.</summary>
    public TimeSpan? Timeout => System.Threading.Timeout.InfiniteTimeSpan;

    public LargeOldFilesCriteria Criteria => criteria;
    public IReadOnlyList<string> Roots => roots;

    private sealed record Hit(string Path, DirEntry Entry, long Size, int RootIndex, bool Hidden, LargeOldMatch Match);

    public Task<ScanOutput> RunAsync(ScanContext context) => Task.FromResult(Run(context));

    private ScanOutput Run(ScanContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var ct = context.Cancellation;
        var env = context.Environment;
        var policy = env.Policy;
        var now = env.NowUtc;
        var warnings = new List<ScanWarning>();
        var exclusions = UserContentScope.Exclusions(home);
        var scanRoots = UserContentScope.Normalize(roots, policy, exclusions, Id, warnings);
        var clusters = scanRoots.Select(SecureRemover.ClusterSize).ToArray();
        var homeRootIndex = scanRoots.ToList().FindIndex(r => r.Equals(home.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        var ignore = env.Ignore;
        var hits = new ConcurrentBag<Hit>();
        context.Progress.Report(0.02);

        var starts = scanRoots.Select((r, i) => new ParallelTreeWalker.Frame(r, i, 0, false)).ToList();
        var stats = ParallelTreeWalker.Run(starts, UserContentScope.WalkConcurrency(scanRoots),
            onDirectory: (parent, e, full) =>
            {
                if (e.IsReparsePoint || e.IsDataless) return null; // liên kết/junction, thư mục đám mây
                if (UserContentScope.IsExcluded(full, exclusions) || policy.ShouldSkipDescending(full)) return null;
                if (ignore.Paths.Count > 0 && ignore.IgnoresPath(full)) return null;
                var hidden = parent.Hidden;
                if (!hidden && UserContentScope.IsHidden(e))
                {
                    // Giống bản Mac: chỉ quét thư mục ẩn ngay trong hồ sơ người dùng.
                    if (parent.Depth != 0 || parent.RootIndex != homeRootIndex) return null;
                    hidden = true;
                }
                return new ParallelTreeWalker.Frame(full, parent.RootIndex, parent.Depth + 1, hidden);
            },
            onFile: (parent, e, full) =>
            {
                if (e.IsReparsePoint || e.IsDataless || e.IsSystem) return;
                if (!parent.Hidden && UserContentScope.IsHidden(e)) return;
                var size = e.AllocatedSize(clusters[parent.RootIndex]);
                var match = criteria.Match(size, e.LastUseUtc, now);
                if (match != LargeOldMatch.None) hits.Add(new Hit(full, e, size, parent.RootIndex, parent.Hidden, match));
            },
            ct, context.Progress.AddFilesVisited, f => context.Progress.Report(0.02 + 0.88 * f), progressKey: TaskID);

        foreach (var (index, code) in stats.FailedRoots)
        {
            var path = scanRoots[index];
            warnings.Add(Win32Error.IsAccessDenied(code)
                ? new ScanWarning(Id, ScanWarningKind.NeedsAdmin, Loc.F("Cần quyền quản trị để đọc %@", path.AbbreviatingHome()))
                : new ScanWarning(Id, ScanWarningKind.Failed, Loc.F("Không đọc được %@", path.AbbreviatingHome())));
        }
        context.Progress.Report(0.9);

        // Kiểm tra lại từng kết quả: vùng cấm (NTUSER, .pst…), danh sách bỏ qua, hard link (chỉ giữ một đường dẫn).
        var tracker = new HardLinkTracker();
        var leaves = new List<(Node Node, FileKind Kind)>();
        var ordered = hits.OrderByDescending(h => h.Size).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            if ((i & 127) == 0) ct.ThrowIfCancellationRequested();
            var hit = ordered[i];
            if (policy.IsForbidden(hit.Path) is not null) continue;
            if (ignore.Paths.Count > 0 && ignore.IgnoresPath(hit.Path)) continue;
            if (FileOpener.Identity(hit.Path) is { } id && id.LinkCount > 1 && !tracker.FirstSighting(id.VolumeSerial, id.FileIndex)) continue;
            leaves.Add((MakeNode(hit, scanRoots[hit.RootIndex]), FileKinds.Classify(hit.Path)));
        }
        context.Progress.Report(0.97);

        foreach (var (node, _) in leaves) context.Progress.AddBytesFound(node.Size);
        var groups = leaves.GroupBy(l => l.Kind)
            .Select(g => Node.Group(LocalizedText.Source(g.Key.Title()), g.Key.Symbol(), LargeOldBadges.Category,
                g.Select(l => l.Node).OrderByDescending(n => n.Size.Bytes), SafetyLevel.Review))
            .OrderByDescending(g => g.Size.Bytes)
            .ToList();
        stopwatch.Stop();
        Log.Info(LogCategory.Scan, $"File lớn & cũ: {scanRoots.Count} gốc, {stats.Directories} thư mục, {stats.Entries} mục, " +
            $"{leaves.Count} kết quả trong {stopwatch.Elapsed.TotalSeconds:F2} giây");
        return new ScanOutput { Nodes = groups, Warnings = warnings };
    }

    private Node MakeNode(Hit hit, string root)
    {
        var badges = new List<string>();
        if (hit.Match.HasFlag(LargeOldMatch.Large)) badges.Add(LargeOldBadges.Large);
        if (hit.Match.HasFlag(LargeOldMatch.Old)) badges.Add(LargeOldBadges.Old);
        if (hit.Hidden) badges.Add(LargeOldBadges.Hidden);
        var reason = hit.Match.HasFlag(LargeOldMatch.Large)
            ? LocalizedText.Source(Loc.F("Lớn hơn %@", new ByteCount(criteria.LargeThreshold).Formatted))
            : LocalizedText.Source(Loc.F("Không dùng hơn %lld ngày", criteria.OldDays));
        var node = Node.ForPath(hit.Path, isDirectory: false);
        node.Size = new ByteCount(hit.Size);
        node.ItemCount = 1;
        node.Safety = SafetyLevel.Review;
        node.Reason = reason;
        node.Category = LargeOldBadges.Category;
        node.Removal = RemovalStrategy.MoveToTrash;
        node.AllowedRoots = [root];
        node.LastAccessUtc = hit.Entry.LastUseUtc;
        node.Badges = badges;
        return node;
    }
}
