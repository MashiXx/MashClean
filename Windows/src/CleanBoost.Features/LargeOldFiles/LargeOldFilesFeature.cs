using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.LargeOldFiles;

/// <summary>
/// File lớn &amp; cũ (LargeOldFilesFeature của bản Mac, mục 11.7). Không chạy trong Smart Scan; không chọn sẵn gì (mục 15.2).
/// Ngưỡng đọc từ cài đặt mỗi lần quét (<c>largeFiles.thresholdMB</c>, <c>largeFiles.oldDays</c>).
/// </summary>
public sealed class LargeOldFilesFeature(AppSettings settings, string? home = null) : IFeatureScanProvider
{
    /// <summary>Biến môi trường (chỉ bản Debug) thay thư mục quét mặc định, phân tách bằng <c>;</c> — dùng cho kiểm thử giao diện.</summary>
    public const string RootsEnvironmentVariable = "CLEANBOOST_LARGEOLD_ROOTS";

    public string FeatureID => FeatureIDs.LargeOldFiles;
    public bool IncludeInSmartScan => false;
    public string Home { get; } = home ?? KnownPaths.UserHome;

    public LargeOldFilesCriteria Criteria => LargeOldFilesCriteria.From(settings.LargeFileThresholdMB, settings.OldFileDays);

    public IReadOnlyList<string> DefaultRoots => DebugRoots(RootsEnvironmentVariable) ?? UserContentScope.DefaultRoots(Home);

    public IReadOnlyList<IScanTask> ScanTasks() => [MakeTask(DefaultRoots)];

    /// <summary>Task với thư mục do người dùng chọn (rỗng = mặc định).</summary>
    public LargeOldFilesTask MakeTask(IReadOnlyList<string> roots) => new(Criteria, roots.Count == 0 ? DefaultRoots : roots, Home);

    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var leaves = nodes.SelectMany(n => n.RemovableLeaves()).ToList();
        return new FeatureSummary(FeatureID, SmartScanCard.Cleanup, Loc.T("File lớn và cũ"),
            leaves.Count == 0 ? Loc.T("Không có file lớn hoặc cũ") : Loc.F("%lld file cần xem lại", leaves.Count),
            leaves.Sum(l => l.Size), leaves.Count);
    }

    internal static IReadOnlyList<string>? DebugRoots(string variable)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            return value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
#endif
        return null;
    }
}

/// <summary>Một dòng trong bảng File lớn &amp; cũ, dựng từ lá của cây kết quả.</summary>
public sealed class LargeOldItem
{
    public required Guid Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    /// <summary>Thư mục chứa (đường dẫn đầy đủ).</summary>
    public required string Folder { get; init; }
    public ByteCount Size { get; init; }
    public DateTime? LastUsedUtc { get; init; }
    public FileKind Kind { get; init; }
    public bool IsLarge { get; init; }
    public bool IsOld { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>Khoá sắp xếp không null cho cột "Lần dùng cuối".</summary>
    public DateTime LastUsedSortKey => LastUsedUtc ?? DateTime.MinValue;
    public string KindTitle => Kind.Title();

    public static LargeOldItem From(Node node)
    {
        var path = node.Url ?? "";
        return new LargeOldItem
        {
            Id = node.Id,
            Path = path,
            Name = node.Title,
            Folder = System.IO.Path.GetDirectoryName(path) ?? "",
            Size = node.Size,
            LastUsedUtc = node.LastAccessUtc,
            Kind = FileKinds.Classify(path),
            IsLarge = node.Badges.Contains(LargeOldBadges.Large),
            IsOld = node.Badges.Contains(LargeOldBadges.Old),
            IsHidden = node.Badges.Contains(LargeOldBadges.Hidden),
        };
    }

    public static List<LargeOldItem> Items(NodeTree tree) => tree.AllLeaves.Where(l => l.Url is not null).Select(From).ToList();
}

/// <summary>Bộ lọc: loại file, khoảng dung lượng, khoảng thời gian, thư mục, tên (LargeOldFilter của bản Mac).</summary>
public sealed class LargeOldFilter
{
    public enum SizeRange { Any, Under100MB, From100MBTo1GB, From1GBTo5GB, Over5GB }

    public enum AgeRange { Any, Month, ThreeMonths, SixMonths, Year, TwoYears }

    /// <summary>Rỗng = mọi loại.</summary>
    public HashSet<FileKind> Kinds { get; } = [];
    public SizeRange Size { get; set; } = SizeRange.Any;
    public AgeRange Age { get; set; } = AgeRange.Any;
    /// <summary>Chỉ hiện file nằm trong thư mục này (đường dẫn đầy đủ). <c>null</c> = mọi thư mục.</summary>
    public string? Folder { get; set; }
    public string Search { get; set; } = "";

    public static string Title(SizeRange range) => range switch
    {
        SizeRange.Any => Loc.T("Mọi dung lượng"),
        SizeRange.Under100MB => Loc.T("Dưới 100 MB"),
        SizeRange.From100MBTo1GB => "100 MB – 1 GB",
        SizeRange.From1GBTo5GB => "1 – 5 GB",
        _ => Loc.T("Trên 5 GB"),
    };

    public static string Title(AgeRange range) => range switch
    {
        AgeRange.Any => Loc.T("Mọi thời điểm"),
        AgeRange.Month => Loc.T("Không dùng > 1 tháng"),
        AgeRange.ThreeMonths => Loc.T("Không dùng > 3 tháng"),
        AgeRange.SixMonths => Loc.T("Không dùng > 6 tháng"),
        AgeRange.Year => Loc.T("Không dùng > 1 năm"),
        _ => Loc.T("Không dùng > 2 năm"),
    };

    /// <summary>Khoảng nửa mở <c>[lo, hi)</c>, đơn vị thập phân.</summary>
    public static bool Contains(SizeRange range, ByteCount size)
    {
        var b = size.Bytes;
        return range switch
        {
            SizeRange.Any => true,
            SizeRange.Under100MB => b < 100_000_000,
            SizeRange.From100MBTo1GB => b is >= 100_000_000 and < 1_000_000_000,
            SizeRange.From1GBTo5GB => b is >= 1_000_000_000 and < 5_000_000_000,
            _ => b >= 5_000_000_000,
        };
    }

    public static int? Days(AgeRange range) => range switch
    {
        AgeRange.Month => 30,
        AgeRange.ThreeMonths => 90,
        AgeRange.SixMonths => 180,
        AgeRange.Year => 365,
        AgeRange.TwoYears => 730,
        _ => null,
    };

    /// <summary>Không biết lần dùng cuối thì vẫn hiện (như bản Mac).</summary>
    public static bool Contains(AgeRange range, DateTime? lastUsedUtc, DateTime nowUtc) =>
        Days(range) is not { } days || lastUsedUtc is not { } used || used < nowUtc.AddDays(-days);

    public bool IsDefault => Kinds.Count == 0 && Size == SizeRange.Any && Age == AgeRange.Any && Folder is null && string.IsNullOrEmpty(Search);

    public bool Matches(LargeOldItem item, DateTime nowUtc)
    {
        if (Kinds.Count > 0 && !Kinds.Contains(item.Kind)) return false;
        if (!Contains(Size, item.Size)) return false;
        if (!Contains(Age, item.LastUsedUtc, nowUtc)) return false;
        if (Folder is { } folder && !PathPolicy.IsSameOrInside(item.Folder, folder)) return false;
        if (Search.Length > 0 && item.Name.IndexOf(Search, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
        return true;
    }

    public List<LargeOldItem> Apply(IEnumerable<LargeOldItem> items, DateTime nowUtc) => items.Where(i => Matches(i, nowUtc)).ToList();

    /// <summary>
    /// Thư mục cấp 1 trong hồ sơ có chứa kết quả (Downloads, Videos…), file nằm thẳng trong hồ sơ tính là chính hồ sơ;
    /// file ở gốc quét ngoài hồ sơ tính theo gốc đó. Sắp theo tổng dung lượng giảm dần.
    /// </summary>
    public static List<string> FolderOptions(IEnumerable<LargeOldItem> items, string home, IReadOnlyList<string> roots)
    {
        home = home.TrimEnd('\\');
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string key;
            if (PathPolicy.IsSameOrInside(item.Folder, home))
            {
                var rest = item.Folder.Length > home.Length ? item.Folder[(home.Length + 1)..] : "";
                var first = rest.Split('\\', 2)[0];
                key = first.Length == 0 ? home : home + "\\" + first;
            }
            else
            {
                key = roots.Where(r => PathPolicy.IsSameOrInside(item.Folder, r)).OrderByDescending(r => r.Length).FirstOrDefault()?.TrimEnd('\\')
                    ?? item.Folder;
            }
            totals[key] = totals.GetValueOrDefault(key) + item.Size.Bytes;
        }
        return totals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }
}
