using System.Diagnostics;
using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;
using CleanBoost.Features.LargeOldFiles;

namespace CleanBoost.Features.Duplicates;

/// <summary>Nhãn trên node file trùng lặp.</summary>
public static class DuplicateBadges
{
    public static string Keep => Loc.T("Nên giữ");
    /// <summary>File còn hard link khác: xoá đường dẫn này không giải phóng dung lượng (như "Clone APFS" của bản Mac).</summary>
    public static string HardLink => Loc.T("Hard link: xoá không giải phóng dung lượng");
    public const string Category = "duplicates";
}

/// <summary>
/// Task tìm trùng lặp (DuplicatesTask của bản Mac, mục 11.8). Mỗi nhóm là node group; mỗi file là node file, mức review,
/// chuyển vào Thùng rác, không chọn sẵn (mục 15.2). Bỏ AppData và vùng hệ thống trừ khi người dùng chủ động chọn chính thư mục đó.
/// </summary>
public sealed class DuplicatesTask(IReadOnlyList<string> roots, string home, DuplicateProgressBox? progressBox = null) : IScanTask
{
    public const string TaskID = "duplicates";

    public string Id => TaskID;
    public string Title => Loc.T("Tìm file trùng lặp");
    public double EstimatedWeight => 5;
    /// <summary>Băm toàn bộ có thể mất nhiều phút trên ổ lớn; người dùng có nút Dừng nên không giới hạn thời gian.</summary>
    public TimeSpan? Timeout => System.Threading.Timeout.InfiniteTimeSpan;

    public IReadOnlyList<string> Roots => roots;

    public Task<ScanOutput> RunAsync(ScanContext context) => Task.FromResult(Run(context));

    private ScanOutput Run(ScanContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var env = context.Environment;
        var warnings = new List<ScanWarning>();
        var exclusions = UserContentScope.Exclusions(home);
        var scanRoots = UserContentScope.Normalize(roots, env.Policy, exclusions, Id, warnings);
        var finder = new DuplicateFinder(new DuplicateFinder.Options
        {
            Roots = scanRoots,
            Exclusions = exclusions,
            Policy = env.Policy,
            Ignore = env.Ignore,
        });
        var reporter = context.Progress;
        var gate = new object();
        long reportedSeen = 0;
        var last = DuplicateProgress.Start;
        // Gọi từ nhiều luồng: giữ tiến độ không giảm, chỉ chuyển phần chênh lệch số mục đã duyệt sang reporter của phiên quét.
        var groups = finder.Find(p =>
        {
            long delta;
            DuplicateProgress current;
            lock (gate)
            {
                if (p.Stage > last.Stage || (p.Stage == last.Stage && p.Fraction >= last.Fraction)) last = p;
                current = last;
                delta = Math.Max(0, p.FilesSeen - reportedSeen);
                reportedSeen = Math.Max(reportedSeen, p.FilesSeen);
            }
            progressBox?.Set(current);
            reporter.Report(current.Fraction);
            if (delta > 0) reporter.AddFilesVisited((int)Math.Min(delta, int.MaxValue));
        }, context.Cancellation);
        var nodes = groups.Select(g => MakeNode(g, scanRoots)).OrderByDescending(n => Wasted(n).Bytes).ToList();
        foreach (var n in nodes) reporter.AddBytesFound(Wasted(n));
        stopwatch.Stop();
        Log.Info(LogCategory.Scan, $"File trùng lặp: {scanRoots.Count} gốc, {last.FilesSeen} mục, {nodes.Count} nhóm, " +
            $"lãng phí {nodes.Sum(Wasted).Bytes} byte trong {stopwatch.Elapsed.TotalSeconds:F2} giây");
        return new ScanOutput { Nodes = nodes, Warnings = warnings };
    }

    /// <summary>Node nhóm: bản nên giữ lên đầu, rồi theo tên.</summary>
    public static Node MakeNode(DuplicateGroup group, IReadOnlyList<string> roots, KeepAdvisor.Folders? folders = null)
    {
        var keepIndex = KeepAdvisor.SuggestKeep(group.Files.Select(f => new KeepAdvisor.Candidate(f.Path, f.Candidate.CreatedUtc)).ToList(), folders);
        var others = group.Files.Count - 1;
        var children = new List<Node>();
        for (var i = 0; i < group.Files.Count; i++)
        {
            var f = group.Files[i];
            var node = Node.ForPath(f.Path, isDirectory: false);
            node.Size = f.IsLinked ? ByteCount.Zero : new ByteCount(f.Candidate.AllocatedSize);
            node.ItemCount = 1;
            node.Safety = SafetyLevel.Review;
            node.Reason = LocalizedText.Source(Loc.F("Trùng nội dung (SHA-256) với %lld file khác", others));
            node.Category = DuplicateBadges.Category;
            node.Removal = RemovalStrategy.MoveToTrash;
            node.AllowedRoots = roots.Count > f.Candidate.RootIndex ? [roots[f.Candidate.RootIndex]] : [];
            node.LastAccessUtc = f.Candidate.ModifiedUtc;
            if (i == keepIndex) node.Badges.Add(DuplicateBadges.Keep);
            if (f.IsLinked) node.Badges.Add(DuplicateBadges.HardLink);
            children.Add(node);
        }
        children.Sort((a, b) =>
        {
            var ka = DuplicateSelection.IsKeep(a);
            var kb = DuplicateSelection.IsKeep(b);
            if (ka != kb) return ka ? -1 : 1;
            var byTitle = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            return byTitle != 0 ? byTitle : string.CompareOrdinal(a.Path, b.Path);
        });
        var name = children.Count > 0 ? children[0].Title : "?";
        var group1 = Node.Group(LocalizedText.Source(Loc.F("%@ — %lld bản", name, group.Files.Count)), "doc.on.doc", DuplicateBadges.Category,
            children, SafetyLevel.Review);
        group1.Reason = LocalizedText.Source(Loc.F("Mỗi bản %@", new ByteCount(group.Size).Formatted));
        return group1;
    }

    /// <summary>Dung lượng giải phóng được nếu xoá mọi bản trừ bản nên giữ.</summary>
    public static ByteCount Wasted(Node group) => group.Children.Where(c => !DuplicateSelection.IsKeep(c)).Sum(c => c.Size);
}
