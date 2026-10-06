using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.SystemJunk;

/// <summary>Mô tả một nhóm rác: id task, category, trọng số tiến độ, có gom theo app không, có trong Smart Scan không.</summary>
public sealed record JunkCategorySpec(string Id, string Category, double Weight, bool NeedsApps = false, bool GroupByApp = false, bool InSmartScan = true);

/// <summary>Rác hệ thống (SYSTEM_DESIGN mục 11.2), bản Windows.</summary>
public sealed class SystemJunkFeature : IFeatureScanProvider
{
    public static readonly IReadOnlyList<JunkCategorySpec> Specs =
    [
        new("userTemp", RuleCategory.UserTemp, 2),
        new("systemTemp", RuleCategory.SystemTemp, 1),
        new("userCaches", RuleCategory.UserCaches, 4, NeedsApps: true, GroupByApp: true),
        new("browserCaches", RuleCategory.BrowserCaches, 2),
        new("systemCaches", RuleCategory.SystemCaches, 2),
        new("windowsUpdate", RuleCategory.WindowsUpdate, 1),
        new("userLogs", RuleCategory.UserLogs, 1),
        new("systemLogs", RuleCategory.SystemLogs, 1),
        new("crashReports", RuleCategory.CrashReports, 0.5),
        new("devToolCaches", RuleCategory.DevToolCaches, 3),
        new("iosBackups", RuleCategory.IosBackups, 1),
        new("iosSoftwareUpdates", RuleCategory.IosSoftwareUpdates, 0.5),
        new("trash", RuleCategory.Trash, 1),
        new("oldDownloads", RuleCategory.OldDownloads, 0.5),
    ];

    public static IReadOnlySet<string> TaskIDs { get; } = Specs.Select(s => s.Id).ToHashSet();

    private readonly AppScanner _appScanner;
    private readonly RuleStore _rules;
    private readonly IReadOnlyDictionary<string, IJunkProvider> _providers;

    public SystemJunkFeature(AppScanner appScanner, RuleStore rules)
    {
        _appScanner = appScanner;
        _rules = rules;
        _providers = new IJunkProvider[] { new RecycleBinProvider(), new DockerProvider(), new IosBackupsProvider() }.ToDictionary(p => p.Name);
    }

    public string FeatureID => FeatureIDs.SystemJunk;
    public bool IncludeInSmartScan => true;

    public IReadOnlyList<IScanTask> ScanTasks() => Tasks(smartScanOnly: false);
    public IReadOnlyList<IScanTask> SmartScanTasks() => Tasks(smartScanOnly: true);

    private List<IScanTask> Tasks(bool smartScanOnly)
    {
        var snapshot = _rules.Snapshot;
        var tasks = new List<IScanTask> { new InstalledAppsTask(_appScanner) };
        foreach (var spec in Specs)
        {
            if (smartScanOnly && !spec.InSmartScan) continue;
            // Nhóm nào có rule dùng biến theo app thì phụ thuộc installedApps (sửa lỗi tiềm ẩn của bản Mac với userLogs).
            var needsApps = spec.NeedsApps || snapshot.RulesIn(spec.Category).Any(r => r.Rule.UsesAppVariables);
            tasks.Add(new RuleCategoryTask(spec, _providers, needsApps));
        }
        return tasks;
    }

    /// <summary>Thẻ Dọn dẹp của Smart Scan: tổng các lá an toàn, phụ đề là 3 nhóm đầu có mục an toàn.</summary>
    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var safe = nodes.SelectMany(n => n.RemovableLeaves()).Where(l => l.Safety == SafetyLevel.Safe).ToList();
        var groups = nodes.Where(n => n.RemovableLeaves().Any(l => l.Safety == SafetyLevel.Safe)).Take(3).Select(n => n.Title).ToList();
        var subtitle = groups.Count > 0 ? string.Join(", ", groups) : Loc.T("Không có rác an toàn để dọn");
        return new FeatureSummary(FeatureID, SmartScanCard.Cleanup, Loc.T("Rác hệ thống"), subtitle, safe.Sum(l => l.Size), safe.Count);
    }
}
