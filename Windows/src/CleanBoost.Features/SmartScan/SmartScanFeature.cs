using CleanBoost.Core;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.SmartScan;

/// <summary>Một thẻ trên màn tổng hợp Smart Scan.</summary>
public sealed record SmartScanCardSummary(SmartScanCard Card, IReadOnlyList<FeatureSummary> Features, IReadOnlyList<Node> Nodes)
{
    public ByteCount Bytes => Features.Sum(f => f.Bytes);
    public int ItemCount => Features.Sum(f => f.ItemCount);

    public string Title => Card switch
    {
        SmartScanCard.Cleanup => Loc.T("Dọn dẹp"),
        SmartScanCard.Maintenance => Loc.T("Bảo trì"),
        _ => Loc.T("Ứng dụng"),
    };

    public string Icon => Card switch
    {
        SmartScanCard.Cleanup => "trash",
        SmartScanCard.Maintenance => "wrench.and.screwdriver",
        _ => "square.grid.2x2",
    };

    public string Headline => Card switch
    {
        SmartScanCard.Cleanup => Bytes.Bytes > 0 ? Bytes.Formatted : Loc.T("Sạch sẽ"),
        SmartScanCard.Maintenance => ItemCount > 0 ? Loc.F("%lld tác vụ nên chạy", ItemCount) : Loc.T("Ổn định"),
        _ => ItemCount > 0 ? Loc.F("%lld mục cần xem", ItemCount) : Loc.T("Gọn gàng"),
    };

    public string? PrimaryFeatureID => Features.FirstOrDefault()?.FeatureID;
}

/// <summary>Smart Scan (SYSTEM_DESIGN mục 11.1): gom scan task của các feature có <c>IncludeInSmartScan</c>.</summary>
public sealed class SmartScanFeature(IReadOnlyList<IFeatureScanProvider> allProviders)
{
    public IReadOnlyList<IFeatureScanProvider> Providers { get; } = allProviders.Where(p => p.IncludeInSmartScan).ToList();

    /// <summary>Task của mọi feature; ScanGraph tự bỏ task trùng id (vd installedApps).</summary>
    public IReadOnlyList<IScanTask> Tasks() => Providers.SelectMany(p => p.SmartScanTasks()).ToList();

    public Dictionary<string, HashSet<string>> TaskIDsByFeature() =>
        Providers.ToDictionary(p => p.FeatureID, p => p.SmartScanTasks().Select(t => t.Id).ToHashSet());

    /// <summary>Chỉ hiện thẻ có feature đóng góp.</summary>
    public List<SmartScanCardSummary> Summarize(ScanResult result)
    {
        var ids = TaskIDsByFeature();
        var byCard = new Dictionary<SmartScanCard, (List<FeatureSummary> Features, List<Node> Nodes)>();
        foreach (var provider in Providers)
        {
            // installedApps dùng chung không phải của riêng feature nào: bỏ ra để node không bị tính hai lần.
            var own = ids.GetValueOrDefault(provider.FeatureID) ?? [];
            var nodes = result.NodesOf(own);
            var summary = provider.Summarize(nodes);
            if (!byCard.TryGetValue(summary.Card, out var entry)) byCard[summary.Card] = entry = ([], []);
            entry.Features.Add(summary);
            entry.Nodes.AddRange(nodes);
        }
        return Enum.GetValues<SmartScanCard>().Where(byCard.ContainsKey)
            .Select(c => new SmartScanCardSummary(c, byCard[c].Features, byCard[c].Nodes.DistinctBy(n => n.Id).ToList()))
            .ToList();
    }
}
