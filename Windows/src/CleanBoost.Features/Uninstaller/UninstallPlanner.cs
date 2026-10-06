using CleanBoost.Core;
using CleanBoost.Engine.Apps;
using CleanBoost.Engine.Cleaning;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.Features.Uninstaller;

/// <summary>
/// App nào được phép gỡ (UninstallPolicy của bản Mac, spec mục 4.7). Bản Windows chặn: mục Windows đánh dấu không gỡ được
/// (<c>NoRemove</c>, gói Store hệ thống), chính Clean Boost, chương trình nằm trong <c>%WINDIR%</c>, và chương trình không có lệnh
/// gỡ chạy được an toàn. App của nhà phát hành hệ thống (Microsoft) vẫn gỡ được như trong Cài đặt Windows.
/// </summary>
public static class UninstallPolicy
{
    /// <summary>
    /// Gói Store là thành phần của Windows dù không ký "System": Microsoft Store, Windows Security, App Installer (winget),
    /// runtime dùng chung. Cài đặt Windows cũng không cho gỡ các gói này.
    /// </summary>
    private static readonly string[] s_windowsPackages =
    [
        "Microsoft.WindowsStore_", "Microsoft.StorePurchaseApp_", "Microsoft.DesktopAppInstaller_", "Microsoft.SecHealthUI_", "Microsoft.Winget.Source_",
        "Microsoft.WindowsAppRuntime", "MicrosoftCorporationII.WinAppRuntime", "Microsoft.VCLibs", "Microsoft.UI.Xaml", "Microsoft.NET.",
        "Microsoft.Services.Store", "Microsoft.ApplicationCompatibilityEnhancements_", "Microsoft.WidgetsPlatformRuntime_", "Microsoft.StartExperiencesApp_",
    ];

    public static string? ReasonCannotUninstall(InstalledApp app)
    {
        if (app.IsNonRemovable) return Loc.T("Windows không cho gỡ mục này");
        if (IsCleanBoost(app)) return Loc.T("Chính Clean Boost");
        if (app.IsStore && app.PackageFamilyName is { } pfn
            && (pfn.EndsWith("_cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase) || s_windowsPackages.Any(p => pfn.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            return Loc.T("Thành phần hệ thống");
        if (!app.IsStore && app.InstallLocation is { } dir && PathPolicy.IsSameOrInside(dir, KnownPaths.Windows)) return Loc.T("Thành phần hệ thống");
        if (UninstallCommand.For(app, out var problem) is null) return problem ?? Loc.T("Không đọc được lệnh gỡ cài đặt");
        return null;
    }

    public static bool CanUninstall(InstalledApp app) => ReasonCannotUninstall(app) is null;

    /// <summary>Lời nhắc (không chặn) cho thành phần dùng chung: runtime, thư viện, driver mà chương trình khác có thể cần.</summary>
    public static string? Caution(InstalledApp app)
    {
        string[] shared = ["Redistributable", "Runtime", "Visual C++", ".NET", "WebView2", "Driver", "SDK", "Framework", "Desktop Runtime"];
        return shared.Any(s => app.Name.Contains(s, StringComparison.OrdinalIgnoreCase))
            ? Loc.T("Đây là thành phần dùng chung (runtime, thư viện, driver). Chương trình khác có thể cần nó, chỉ gỡ khi bạn chắc chắn.")
            : null;
    }

    private static bool IsCleanBoost(InstalledApp app)
    {
        if (app.Name.StartsWith("Clean Boost", StringComparison.OrdinalIgnoreCase) || app.AppID.StartsWith("CleanBoost", StringComparison.OrdinalIgnoreCase)) return true;
        var self = AppContext.BaseDirectory.TrimEnd('\\');
        return app.InstallLocation is { Length: > 3 } dir && (PathPolicy.IsSameOrInside(self, dir) || PathPolicy.IsSameOrInside(dir, self));
    }
}

/// <summary>
/// Dựng cây gỡ cài đặt (UninstallPlanner của bản Mac, spec mục 4.7): mỗi app là một node ứng dụng gồm mục "chạy trình gỡ"
/// rồi các nhóm file sót theo mức tin cậy.
/// </summary>
public static class UninstallPlanner
{
    /// <summary>Node "chạy trình gỡ của app": mục ảo, remover <c>app</c>, an toàn (chọn sẵn).</summary>
    public static Node AppItemNode(InstalledApp app)
    {
        string? location = null;
        if (!app.IsStore && app.InstallLocation is { Length: > 3 } dir && PathPolicy.CurrentUser.IsForbidden(dir) is null) location = dir;
        var command = UninstallCommand.For(app, out _);
        var reason = command?.Kind switch
        {
            UninstallKind.Store => LocalizedText.Source("Gỡ gói Microsoft Store của app"),
            UninstallKind.WindowsInstaller => LocalizedText.Source("Gỡ bằng Windows Installer (msiexec)"),
            _ => LocalizedText.Source("Chạy trình gỡ cài đặt của app"),
        };
        var node = Node.ForVirtual(new InstalledAppItem(app, location), app.Name);
        node.Size = new ByteCount(app.Size);
        node.ItemCount = 1;
        node.Safety = SafetyLevel.Safe;
        node.Reason = reason;
        node.Category = UninstallerCategory.Applications;
        node.Removal = RemovalStrategy.Custom(RemoverIDs.App);
        node.LastAccessUtc = app.LastUsed?.ToUniversalTime();
        node.Icon = "app";
        if (app.RequiresAdminToUninstall) node.Badges.Add(Loc.T("Có thể hỏi quyền quản trị"));
        return node;
    }

    /// <summary>Cây của một app. <paramref name="includeApp"/> = false cho "Đặt lại app" và bước dọn file sót sau khi gỡ.</summary>
    public static Node AppNode(LeftoverReport report, bool includeApp = true)
    {
        var children = new List<Node>();
        if (includeApp) children.Add(AppItemNode(report.App));
        foreach (var confidence in LeftoverConfidenceExtensions.All)
        {
            var items = report.ItemsWith(confidence).Select(i => i.Node).ToList();
            if (items.Count == 0) continue;
            var title = new LocalizedText(
                "Độ tin cậy: " + confidence.TitleKey(),
                Loc.LookupEnglish("Độ tin cậy: %@").Replace("%@", Loc.LookupEnglish(confidence.TitleKey()), StringComparison.Ordinal));
            var group = Node.Group(title, confidence.Symbol(), UninstallerCategory.AppLeftovers, items, confidence.Safety());
            group.Reason = LocalizedText.Source(confidence.ExplanationKey());
            children.Add(group);
        }
        var root = Node.Application(report.App.Id, report.App.InstallLocation, report.App.Name, children, UninstallerCategory.AppLeftovers);
        root.Safety = SafetyLevel.Safe;
        return root;
    }

    public static NodeTree Tree(IEnumerable<LeftoverReport> reports, bool includeApp = true) =>
        new(reports.Select(r => AppNode(r, includeApp)).Where(n => n.Children.Count > 0));

    /// <summary>Lá "chạy trình gỡ" trong cây.</summary>
    public static bool IsAppItem(Node node) => node.Virtual is InstalledAppItem;

    /// <summary>App gỡ thành công theo báo cáo dọn (mục <c>app</c> thành công).</summary>
    public static List<InstalledApp> UninstalledApps(CleanReport report) =>
        report.Entries.Where(e => e.Item.Virtual is InstalledAppItem && e.Result.Outcome == RemoveOutcomeKind.Ok)
            .Select(e => ((InstalledAppItem)e.Item.Virtual!).App).ToList();

    /// <summary>"Đã gỡ Foo, giải phóng 1,2 GB" (spec mục 4.7).</summary>
    public static string Summary(IReadOnlyList<InstalledApp> uninstalled, ByteCount freed, bool dryRun, bool resetOnly)
    {
        if (dryRun) return Loc.F("Chạy thử: sẽ giải phóng %@", freed.Formatted);
        if (resetOnly) return Loc.F("Đã đặt lại app, giải phóng %@", freed.Formatted);
        if (uninstalled.Count == 0) return Loc.T("Chưa gỡ được ứng dụng nào");
        var names = uninstalled.Count <= 3 ? string.Join(", ", uninstalled.Select(a => a.Name)) : Loc.F("%lld ứng dụng", uninstalled.Count);
        return Loc.F("Đã gỡ %@, giải phóng %@", names, freed.Formatted);
    }
}
