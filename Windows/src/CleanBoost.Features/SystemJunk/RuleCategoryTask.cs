using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Rules;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.SystemJunk;

/// <summary>Nguồn dữ liệu đặc biệt cài trong code, khi đường dẫn không đủ diễn tả (<c>match.provider</c>).</summary>
public interface IJunkProvider
{
    string Name { get; }
    Task<(List<Node> Nodes, List<ScanWarning> Warnings)> NodesAsync(Rule rule, ScanContext context);
}

/// <summary>
/// Quét một nhóm rule (Cache người dùng, Log hệ thống...): đánh giá từng rule, gom node theo app hoặc theo rule,
/// trả một node nhóm gốc cho cả category (SYSTEM_DESIGN mục 11.2).
/// </summary>
public sealed class RuleCategoryTask : IScanTask
{
    private readonly JunkCategorySpec _spec;
    private readonly IReadOnlyDictionary<string, IJunkProvider> _providers;
    private readonly bool _needsApps;

    public RuleCategoryTask(JunkCategorySpec spec, IReadOnlyDictionary<string, IJunkProvider> providers, bool needsApps)
    {
        _spec = spec;
        _providers = providers;
        _needsApps = needsApps;
    }

    public string Id => _spec.Id;
    public IReadOnlyList<string> Dependencies => _needsApps ? [InstalledAppsTask.TaskID] : [];
    public double EstimatedWeight => _spec.Weight;
    public string Title => RuleCategory.Title(_spec.Category).Resolved;

    public async Task<ScanOutput> RunAsync(ScanContext context)
    {
        var rules = context.Rules.RulesIn(_spec.Category);
        if (rules.Count == 0) return ScanOutput.Empty;
        var apps = context.InstalledApps();
        var appInfos = apps.Where(a => !a.IsSystemPublisher).Select(a => a.RuleInfo).ToList();
        var ruleContext = context.RuleContext(appInfos);
        var leaves = new List<Node>();
        var warnings = new List<ScanWarning>();
        var deniedPrefixes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignore = context.Environment.Ignore;

        for (var i = 0; i < rules.Count; i++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            var compiled = rules[i];
            var rule = compiled.Rule;
            if (rule.Match.Provider is { } providerName)
            {
                if (_providers.TryGetValue(providerName, out var provider) && !context.Rules.Disabled.Contains(rule.Id) && !ignore.IgnoresRule(rule.Id))
                {
                    var (nodes, w) = await provider.NodesAsync(rule, context).ConfigureAwait(false);
                    leaves.AddRange(nodes.Where(n => n.Url is not { } u || !ignore.IgnoresPath(u)));
                    warnings.AddRange(w);
                    foreach (var n in nodes) context.Progress.AddBytesFound(n.Size);
                }
            }
            else
            {
                // Thư mục chỉ quản trị viên đọc được: báo cảnh báo thay vì im lặng bỏ qua.
                if (!context.Environment.IsElevated)
                {
                    foreach (var glob in compiled.StaticGlobs ?? [])
                    {
                        var prefix = glob.LiteralPrefix;
                        if (AccessProbe.IsAccessDenied(prefix)) deniedPrefixes.Add(prefix);
                    }
                }
                foreach (var m in RuleEvaluator.Evaluate(compiled, context.Rules, ruleContext))
                {
                    if (IsProtectedCache(m, context)) continue;
                    leaves.Add(m.MakeNode(DisplayTitle(m, apps)));
                    context.Progress.AddBytesFound(new ByteCount(m.Measurement.AllocatedSize));
                }
            }
            context.Progress.Report((double)(i + 1) / rules.Count);
        }

        foreach (var prefix in deniedPrefixes)
        {
            warnings.Add(new ScanWarning(Id, ScanWarningKind.NeedsAdmin, Loc.F("Cần quyền quản trị để đọc %@", prefix.AbbreviatingHome())));
        }
        if (leaves.Count == 0) return new ScanOutput { Warnings = warnings };

        var children = _spec.GroupByApp ? GroupByApp(leaves, apps) : GroupByRule(leaves, rules);
        var root = Node.Group(RuleCategory.Title(_spec.Category), RuleCategory.Icon(_spec.Category), _spec.Category, children);
        if (deniedPrefixes.Count > 0) root.Badges.Add(Loc.T("Cần quyền quản trị"));
        return new ScanOutput { Nodes = [root], Warnings = warnings };
    }

    /// <summary>Cache người dùng: bỏ thư mục trùng tên tiến trình đang chạy hoặc khớp <c>knowledge.protectedCacheNames</c>.</summary>
    private bool IsProtectedCache(RuleMatch m, ScanContext context)
    {
        if (_spec.Category != RuleCategory.UserCaches) return false;
        var name = Path.GetFileName(m.Path.TrimEnd('\\'));
        if (context.Environment.RunningProcesses.Contains(name.ToLowerInvariant())) return true;
        return context.Rules.Knowledge.ProtectedCacheNames.Any(p => Glob.ComponentMatches(p, name));
    }

    /// <summary>Tên hiển thị: "App — thư mục" với rule theo app; tên app nếu thư mục trùng; còn lại là tên thư mục.</summary>
    internal static string DisplayTitle(RuleMatch m, IReadOnlyList<InstalledApp> apps)
    {
        var folder = Path.GetFileName(m.Path.TrimEnd('\\'));
        if (m.App is { } app)
        {
            return folder.Equals(app.AppName, StringComparison.OrdinalIgnoreCase) ? app.AppName : $"{app.AppName} — {folder}";
        }
        var owner = apps.FirstOrDefault(a => a.AppID.Equals(folder, StringComparison.OrdinalIgnoreCase) || a.Name.Equals(folder, StringComparison.OrdinalIgnoreCase));
        return owner?.Name ?? folder;
    }

    /// <summary>Gom lá theo app sở hữu (Cache người dùng); lá không rõ app để phẳng ở cấp trên.</summary>
    internal static List<Node> GroupByApp(List<Node> leaves, IReadOnlyList<InstalledApp> apps)
    {
        var groups = new Dictionary<string, (InstalledApp App, List<Node> Nodes)>(StringComparer.OrdinalIgnoreCase);
        var flat = new List<Node>();
        foreach (var leaf in leaves)
        {
            var name = Path.GetFileName((leaf.Url ?? "").TrimEnd('\\'));
            var app = apps.FirstOrDefault(a => a.AppID.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? apps.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                      ?? apps.FirstOrDefault(a => leaf.Title.StartsWith(a.Name + " — ", StringComparison.OrdinalIgnoreCase))
                      ?? apps.FirstOrDefault(a => a.InstallLocation is { } dir && leaf.Url is { } u && PathPolicy.IsSameOrInside(u, dir));
            if (app is null)
            {
                flat.Add(leaf);
                continue;
            }
            if (!groups.TryGetValue(app.Id, out var g)) groups[app.Id] = g = (app, []);
            g.Nodes.Add(leaf);
        }
        var result = new List<Node>(flat);
        foreach (var (app, nodes) in groups.Values)
        {
            if (nodes.Count == 1)
            {
                result.Add(nodes[0]);
                continue;
            }
            result.Add(Node.Application(app.AppID, app.InstallLocation, app.Name, nodes.OrderByDescending(n => n.Size.Bytes)));
        }
        return result.OrderByDescending(n => n.Size.Bytes).ToList();
    }

    /// <summary>Một rule: danh sách phẳng; nhiều rule: mỗi rule có ≥2 lá thành nhóm con mang tên và lý do của rule.</summary>
    internal static List<Node> GroupByRule(List<Node> leaves, IReadOnlyList<CompiledRule> rules)
    {
        var byRule = leaves.GroupBy(l => l.RuleID ?? "").ToDictionary(g => g.Key, g => g.ToList());
        if (byRule.Count <= 1) return leaves.OrderByDescending(n => n.Size.Bytes).ToList();
        var result = new List<Node>();
        foreach (var compiled in rules)
        {
            if (!byRule.TryGetValue(compiled.Id, out var nodes) || nodes.Count == 0) continue;
            var rule = compiled.Rule;
            if (nodes.Count == 1)
            {
                var single = nodes[0];
                if (single.Url is { } u && single.Title == Path.GetFileName(u.TrimEnd('\\'))) single.Title = rule.DisplayTitle;
                result.Add(single);
                continue;
            }
            var group = Node.Group(rule.Title ?? new LocalizedText(rule.Id, rule.Id), RuleCategory.Icon(rule.Category), null,
                nodes.OrderByDescending(n => n.Size.Bytes));
            group.Reason = rule.Reason ?? LocalizedText.Empty;
            result.Add(group);
        }
        // Lá đến từ provider không thuộc rule nào trong danh sách.
        if (byRule.TryGetValue("", out var orphans)) result.AddRange(orphans);
        return result.OrderByDescending(n => n.Size.Bytes).ToList();
    }
}
