using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Persistence;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.Maintenance;

/// <summary>Feature Bảo trì (SYSTEM_DESIGN mục 11.5): thẻ "Bảo trì" trong Smart Scan = số tác vụ nên chạy.</summary>
public sealed class MaintenanceFeature : IFeatureScanProvider
{
    public const string StatusTaskID = "maintenanceStatus";
    public const string StatusArtifact = "maintenanceStatus";

    public MaintenanceRunner Runner { get; }
    public MaintenanceStatusReader StatusReader { get; }
    public IHelperClient? Helper { get; }

    public MaintenanceFeature(IHelperClient? helper, Storage? storage)
    {
        Helper = helper;
        Runner = new MaintenanceRunner(storage);
        StatusReader = new MaintenanceStatusReader(storage);
    }

    public string FeatureID => FeatureIDs.Maintenance;
    public bool IncludeInSmartScan => true;

    public IReadOnlyList<IScanTask> ScanTasks() => [new MaintenanceStatusTask(StatusReader)];

    /// <summary>Remover cho node ảo <see cref="MaintenanceItem"/>, đăng ký vào Clean Engine.</summary>
    public IRemover CreateRemover() => new MaintenanceRemover(Runner);

    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var tasks = nodes.SelectMany(n => n.RemovableLeaves()).ToList();
        var subtitle = tasks.Count == 0 ? Loc.T("Máy đang ổn, chưa cần bảo trì") : string.Join(", ", tasks.Take(3).Select(t => t.Title));
        return new FeatureSummary(FeatureID, SmartScanCard.Maintenance, Loc.T("Bảo trì"), subtitle, ByteCount.Zero, tasks.Count);
    }

    /// <summary>Node ảo cho một tác vụ nên chạy.</summary>
    public static Node MakeNode(MaintenanceRecommendation rec)
    {
        var node = Node.ForVirtual(new MaintenanceItem(rec.Task), rec.Task.Title());
        node.Safety = rec.Safety;
        node.Reason = new LocalizedText(new Dictionary<string, string> { [Loc.Active] = string.IsNullOrEmpty(rec.Why) ? rec.Task.Description() : rec.Why });
        node.Category = "maintenance";
        node.Removal = RemovalStrategy.Custom(RemoverIDs.Maintenance);
        node.RequiresRoot = rec.Task.RequiresAdmin();
        node.Icon = rec.Task.Symbol();
        node.ItemCount = 1;
        return node;
    }
}

/// <summary>Task <c>maintenanceStatus</c> trong graph Smart Scan: tạo node ảo cho tác vụ nên chạy, gom trong nhóm "Bảo trì".</summary>
public sealed class MaintenanceStatusTask(MaintenanceStatusReader reader) : IScanTask
{
    public string Id => MaintenanceFeature.StatusTaskID;
    public double EstimatedWeight => 0.5;
    public string Title => Loc.T("Kiểm tra tình trạng hệ thống");

    public async Task<ScanOutput> RunAsync(ScanContext context)
    {
        var status = await reader.ReadAsync(context.Cancellation).ConfigureAwait(false);
        context.Progress.Report(1);
        var nodes = status.Recommendations.Where(r => r.Recommended).Select(MaintenanceFeature.MakeNode).ToList();
        var output = new ScanOutput { Artifacts = { [MaintenanceFeature.StatusArtifact] = status } };
        if (nodes.Count > 0) output.Nodes.Add(Node.Group(new LocalizedText("Bảo trì", "Maintenance"), "wrench.and.screwdriver", "maintenance", nodes));
        return output;
    }
}

/// <summary>
/// Remover cho node ảo <see cref="MaintenanceItem"/> (id <c>maintenance</c>): nút "Chạy" của Smart Scan thực hiện cả tác vụ bảo
/// trì trong cùng một Clean Plan. Nhãn <c>maintenance:&lt;tên&gt;</c>, giải phóng 0 byte.
/// </summary>
public sealed class MaintenanceRemover(MaintenanceRunner runner) : IRemover
{
    public string Id => RemoverIDs.Maintenance;

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        var results = new List<RemoveResult>();
        foreach (var item in items)
        {
            if (item.Virtual is not MaintenanceItem m)
            {
                results.Add(RemoveResult.Skipped(item.Title, SkipReason.Unsupported));
                continue;
            }
            var label = "maintenance:" + m.Task.RawValue();
            if (context.DryRun)
            {
                results.Add(RemoveResult.Skipped(label, SkipReason.DryRun));
                continue;
            }
            if (context.Cancellation.IsCancellationRequested)
            {
                results.Add(RemoveResult.Skipped(label, SkipReason.UserDeclined));
                continue;
            }
            try
            {
                var r = await runner.RunAsync(m.Task, context.Helper, dryRun: false, context.Cancellation).ConfigureAwait(false);
                context.Progress(label, 0);
                results.Add(r.Success ? RemoveResult.Ok(label, 0) : RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, r.Message));
            }
            catch (OperationCanceledException)
            {
                results.Add(RemoveResult.Skipped(label, SkipReason.UserDeclined));
            }
        }
        return results;
    }
}
