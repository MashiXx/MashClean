using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Nodes;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.Uninstaller;

/// <summary>
/// Tính năng Gỡ cài đặt (UninstallerFeature của bản Mac, spec mục 4.6): tab "File sót của app đã gỡ" và thẻ "Ứng dụng" của
/// Smart Scan (số app đã gỡ còn để lại file).
/// </summary>
public sealed class UninstallerFeature(AppScanner appScanner) : IFeatureScanProvider
{
    public AppScanner AppScanner { get; } = appScanner;

    public string FeatureID => FeatureIDs.Uninstaller;
    public bool IncludeInSmartScan => true;

    /// <summary>Task của tab "File sót của app đã gỡ".</summary>
    public IReadOnlyList<IScanTask> ScanTasks() => [new InstalledAppsTask(AppScanner), new OrphanedLeftoversTask()];

    public IReadOnlyList<IScanTask> SmartScanTasks() => ScanTasks();

    public static IReadOnlySet<string> TaskIDs { get; } = new HashSet<string> { OrphanedLeftoversTask.TaskID };

    /// <summary>Thẻ "Ứng dụng": mỗi nhóm con của gốc "File sót của app đã gỡ" là một app đã gỡ.</summary>
    public FeatureSummary Summarize(IReadOnlyList<Node> nodes)
    {
        var groups = nodes.Where(n => n.Category == UninstallerCategory.OrphanedLeftovers)
            .SelectMany(n => n.IsContainer ? n.Children : [n]).ToList();
        var bytes = groups.Sum(g => g.Size);
        var names = groups.Take(3).Select(g => g.Title.Split(" (")[0]).ToList();
        var subtitle = groups.Count == 0
            ? Loc.T("Không có app nào để lại file sót")
            : Loc.F("%lld app: %@", groups.Count, string.Join(", ", names));
        return new FeatureSummary(FeatureID, SmartScanCard.Applications, Loc.T("File sót của app đã gỡ"), subtitle, bytes, groups.Count);
    }
}

/// <summary>Tìm app đã cài từ một đường dẫn (liên kết sâu <c>cleanboost://uninstall?path=</c>, <c>--uninstall</c>): exe hoặc lối tắt.</summary>
public static class AppLocator
{
    public static InstalledApp? Find(string path, IReadOnlyList<InstalledApp> apps)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var target = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShellLink.Target(target) is { } resolved) target = resolved;
        if (PathPolicy.NormalizeSyntax(target) is { } normalized) target = normalized;

        // 1. File biểu tượng / trình gỡ đúng bằng đường dẫn.
        foreach (var app in apps)
        {
            if (AppScanner.ExecutableFrom(app.DisplayIcon) is { } icon && icon.Equals(target, StringComparison.OrdinalIgnoreCase)) return app;
        }
        // 2. Thư mục cài đặt sâu nhất chứa đường dẫn.
        var best = apps.Where(a => a.InstallLocation is { Length: > 3 } d && PathPolicy.IsSameOrInside(target, d))
            .OrderByDescending(a => a.InstallLocation!.Length).FirstOrDefault();
        if (best is not null) return best;
        // 3. Thư mục chứa exe trùng thư mục của biểu tượng hoặc trình gỡ.
        var dir = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        if (dir is null) return null;
        foreach (var app in apps)
        {
            if (AppScanner.ExecutableFrom(app.DisplayIcon) is { } icon && Path.GetDirectoryName(icon) is { } iconDir && iconDir.Equals(dir, StringComparison.OrdinalIgnoreCase)) return app;
            if (UninstallCommand.Split(app.UninstallString ?? "") is { } u && Path.GetDirectoryName(u.Exe) is { } uDir && uDir.Equals(dir, StringComparison.OrdinalIgnoreCase)) return app;
        }
        // 4. Gói MSI không ghi thư mục cài: so tên exe và tên các thư mục chứa nó với tên app ("Program Files\Tailscale\tailscale-ipn.exe").
        var names = new List<string>();
        for (var d = dir; d is { Length: > 3 }; d = Path.GetDirectoryName(d))
        {
            var leaf = Path.GetFileName(d);
            if (leaf.Equals("Program Files", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)) break;
            names.Add(LeftoverMatcher.Normalize(leaf));
        }
        names.Add(LeftoverMatcher.Normalize(Path.GetFileNameWithoutExtension(target)));
        var candidates = apps.Where(a => !a.IsStore && LeftoverMatcher.NameKeys(a).FirstOrDefault() is { Length: >= 3 } key
                                         && names.Any(n => n == key && !LeftoverMatcher.GenericNames.Contains(n))).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
