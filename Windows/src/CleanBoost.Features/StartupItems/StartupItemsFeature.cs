using System.Globalization;
using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.StartupItems;

/// <summary>
/// Mục khởi động (Login Items của bản Mac, SYSTEM_DESIGN mục 11.6). Smart Scan: thẻ "Bảo trì" với số mục hỏng
/// (chương trình đích không còn tồn tại); màn riêng liệt kê, bật/tắt và xoá mục khởi động.
/// </summary>
public sealed class StartupItemsFeature(AppScanner appScanner) : IFeatureScanProvider
{
    /// <summary>Nhóm (category) của node mục khởi động hỏng.</summary>
    public const string Category = "brokenStartupItems";

    public StartupItemScanner Scanner { get; } = new();
    public AppScanner AppScanner { get; } = appScanner;

    public string FeatureID => FeatureIDs.LoginItems;
    public bool IncludeInSmartScan => true;

    public IReadOnlyList<IScanTask> ScanTasks() => [new InstalledAppsTask(AppScanner), new StartupItemsTask(Scanner), new BrokenStartupItemsTask()];

    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var leaves = nodes.Where(n => n.Category == Category).SelectMany(n => n.RemovableLeaves()).ToList();
        var subtitle = leaves.Count switch
        {
            0 => Loc.T("Không có mục khởi động hỏng"),
            1 => Loc.T("1 mục trỏ tới chương trình không còn tồn tại"),
            _ => Loc.F("%lld mục trỏ tới chương trình không còn tồn tại", leaves.Count),
        };
        return new FeatureSummary(FeatureID, SmartScanCard.Maintenance, Loc.T("Mục khởi động hỏng"), subtitle, leaves.Sum(l => l.Size), leaves.Count);
    }

    // MARK: Node

    /// <summary>
    /// Mức an toàn khi xoá một mục hỏng: chỉ "an toàn" khi chắc chắn chương trình không còn trên ổ cứng cố định của máy
    /// (không phải ổ mạng/USB đang rút, không trong <c>%WINDIR%</c> nơi Windows có thể chuyển hướng đường dẫn).
    /// </summary>
    public static SafetyLevel BrokenSafety(StartupItem item)
    {
        if (!item.IsBroken) return SafetyLevel.Review;
        if (item.TargetPath is null) return string.IsNullOrWhiteSpace(item.Command) ? SafetyLevel.Safe : SafetyLevel.Review;
        if (!item.TargetOnFixedDrive) return SafetyLevel.Review;
        return PathPolicy.IsSameOrInside(item.TargetPath, KnownPaths.Windows) ? SafetyLevel.Review : SafetyLevel.Safe;
    }

    /// <summary>Lý do của mục hỏng: "Chương trình X không còn tồn tại".</summary>
    public static LocalizedText BrokenReason(StartupItem item) =>
        item.TargetPath is null && string.IsNullOrWhiteSpace(item.Command)
            ? Text("Lệnh khởi động trống, Windows không chạy gì")
            : Text("Chương trình %@ không còn tồn tại", item.TargetPath ?? item.Command);

    /// <summary>Node xoá một mục khởi động qua <see cref="StartupItemRemover"/> (dùng cho Smart Scan và màn riêng).</summary>
    public static Node NodeFor(StartupItem item, SafetyLevel safety, LocalizedText reason, string? badge = null)
    {
        var title = item.OwnerIsInstalledApp && item.OwnerName is { } owner && !owner.Equals(item.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? $"{owner} — {item.DisplayName}"
            : item.DisplayName;
        var node = Node.ForVirtual(new StartupItemEntry(item), title);
        node.Size = new ByteCount(item.Source == StartupSource.ScheduledTask ? 0 : item.DataSize);
        node.ItemCount = 1;
        node.Safety = safety;
        node.Reason = reason;
        node.Category = Category;
        node.Removal = RemovalStrategy.Custom(RemoverIDs.StartupItem);
        node.RequiresRoot = item.RequiresAdmin;
        if (badge is not null) node.Badges.Add(badge);
        node.Badges.Add(item.ShortTitle());
        node.Icon = "gearshape.2";
        return node;
    }

    /// <summary>Node cho mọi mục hỏng (bỏ mục trong danh sách bỏ qua, thành phần Windows và tác vụ đã tắt sẵn).</summary>
    public static List<Node> BrokenNodes(IEnumerable<StartupItem> items, IgnoreList ignore)
    {
        var result = new List<Node>();
        foreach (var item in items)
        {
            if (!item.IsBroken || item.IsSystemComponent) continue;
            if (item.Source == StartupSource.ScheduledTask && !item.IsEnabled) continue;
            if (ignore.IgnoresPath(item.Label)) continue;
            result.Add(NodeFor(item, BrokenSafety(item), BrokenReason(item), Loc.T("Hỏng")));
        }
        return result;
    }

    /// <summary>Chuỗi hai ngôn ngữ từ khoá tiếng Việt có tham số (để node hiển thị đúng khi đổi ngôn ngữ).</summary>
    public static LocalizedText Text(string key, params object?[] args) => new(
        Loc.FormatPrintf(key, args, CultureInfo.GetCultureInfo("vi-VN")),
        Loc.FormatPrintf(Loc.LookupEnglish(key), args, CultureInfo.GetCultureInfo("en-US")));
}

/// <summary>Task <c>loginItems</c>: đọc mọi mục khởi động, xuất artifact danh sách <see cref="StartupItem"/>.</summary>
public sealed class StartupItemsTask(StartupItemScanner scanner) : IScanTask
{
    public const string TaskID = "loginItems";
    public const string ArtifactKey = "startupItems";

    public string Id => TaskID;
    public double EstimatedWeight => 0.5;
    public string Title => Loc.T("Đọc mục khởi động");

    public async Task<ScanOutput> RunAsync(ScanContext context)
    {
        var running = context.Environment.RunningProcessPaths;
        var items = await Task.Run(() => scanner.Scan(null, running, context.Cancellation), context.Cancellation).ConfigureAwait(false);
        context.Progress.Report(1);
        return new ScanOutput { Artifacts = { [ArtifactKey] = items } };
    }
}

/// <summary>
/// Task <c>brokenLoginItems</c> (BrokenLoginItemsTask của bản Mac): mục khởi động trỏ tới chương trình không còn tồn tại,
/// tra lại app sở hữu theo danh sách app đầy đủ.
/// </summary>
public sealed class BrokenStartupItemsTask : IScanTask
{
    public const string TaskID = "brokenLoginItems";

    public string Id => TaskID;
    public IReadOnlyList<string> Dependencies => [StartupItemsTask.TaskID, InstalledAppsTask.TaskID];
    public double EstimatedWeight => 0.5;
    public string Title => Loc.T("Mục khởi động hỏng");

    public Task<ScanOutput> RunAsync(ScanContext context)
    {
        var items = context.UpstreamArtifact<List<StartupItem>>(StartupItemsTask.ArtifactKey) ?? [];
        var apps = context.InstalledApps();
        var enriched = apps.Count > 0 ? StartupItemScanner.Enrich(items, apps, context.Environment.RunningProcessPaths) : items;
        var leaves = StartupItemsFeature.BrokenNodes(enriched, context.Environment.Ignore);
        if (leaves.Count == 0) return Task.FromResult(ScanOutput.Empty);
        var root = Node.Group(new LocalizedText("Mục khởi động hỏng", "Broken Startup Items"), "exclamationmark.triangle", StartupItemsFeature.Category, leaves);
        root.Reason = LocalizedText.Source("Mục khởi động trỏ tới chương trình không còn tồn tại");
        return Task.FromResult(new ScanOutput { Nodes = [root] });
    }
}
