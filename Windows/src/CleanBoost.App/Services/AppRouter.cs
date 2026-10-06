using CleanBoost.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CleanBoost.App.Services;

/// <summary>Mục thanh bên (SidebarItem của bản Mac).</summary>
public enum SidebarItem
{
    SmartScan,
    SystemJunk,
    LargeOldFiles,
    Duplicates,
    Uninstaller,
    LoginItems,
    Maintenance,
    SpaceLens,
    History,
    Diagnostics,
}

public static class SidebarItems
{
    public static string Title(this SidebarItem item) => item switch
    {
        SidebarItem.SmartScan => "Smart Scan",
        SidebarItem.SystemJunk => Loc.T("Rác hệ thống"),
        SidebarItem.LargeOldFiles => Loc.T("File lớn & cũ"),
        SidebarItem.Duplicates => Loc.T("File trùng lặp"),
        SidebarItem.Uninstaller => Loc.T("Gỡ cài đặt"),
        SidebarItem.LoginItems => Loc.T("Mục khởi động"),
        SidebarItem.Maintenance => Loc.T("Bảo trì"),
        SidebarItem.SpaceLens => "Space Lens",
        SidebarItem.History => Loc.T("Lịch sử"),
        _ => Loc.T("Báo cáo lỗi"),
    };

    public static string Symbol(this SidebarItem item) => item switch
    {
        SidebarItem.SmartScan => "sparkles",
        SidebarItem.SystemJunk => "trash.circle",
        SidebarItem.LargeOldFiles => "doc.badge.clock",
        SidebarItem.Duplicates => "doc.on.doc",
        SidebarItem.Uninstaller => "xmark.app",
        SidebarItem.LoginItems => "power.circle",
        SidebarItem.Maintenance => "wrench.and.screwdriver",
        SidebarItem.SpaceLens => "circle.hexagongrid",
        SidebarItem.History => "clock.arrow.circlepath",
        _ => "ladybug",
    };

    /// <summary>Các nhóm trên thanh bên, theo thứ tự.</summary>
    public static IReadOnlyList<(string Header, SidebarItem[] Items)> Sections =>
    [
        ("", [SidebarItem.SmartScan]),
        (Loc.T("Dọn dẹp"), [SidebarItem.SystemJunk, SidebarItem.LargeOldFiles, SidebarItem.Duplicates]),
        (Loc.T("Ứng dụng"), [SidebarItem.Uninstaller, SidebarItem.LoginItems]),
        (Loc.T("Tốc độ"), [SidebarItem.Maintenance]),
        (Loc.T("Dung lượng"), [SidebarItem.SpaceLens]),
        (Loc.T("Khác"), [SidebarItem.History, SidebarItem.Diagnostics]),
    ];

    public static SidebarItem? FromFeature(string featureID) => featureID switch
    {
        FeatureIDs.SmartScan => SidebarItem.SmartScan,
        FeatureIDs.SystemJunk => SidebarItem.SystemJunk,
        FeatureIDs.Uninstaller => SidebarItem.Uninstaller,
        FeatureIDs.SpaceLens => SidebarItem.SpaceLens,
        FeatureIDs.Maintenance => SidebarItem.Maintenance,
        FeatureIDs.LoginItems => SidebarItem.LoginItems,
        FeatureIDs.LargeOldFiles => SidebarItem.LargeOldFiles,
        FeatureIDs.Duplicates => SidebarItem.Duplicates,
        _ => null,
    };
}

/// <summary>
/// Điều hướng và liên kết sâu (AppRouter của bản Mac). URL scheme <c>cleanboost://</c>:
/// <c>scan?feature=…</c>, <c>spacelens?path=…</c>, <c>uninstall?path=…</c>, <c>maintenance?task=…</c>.
/// </summary>
public sealed partial class AppRouter : ObservableObject
{
    [ObservableProperty] private SidebarItem _selection = SidebarItem.SmartScan;

    /// <summary>Yêu cầu tự bắt đầu quét ở màn đích (tăng mỗi lần).</summary>
    public event Action<SidebarItem>? AutoStartRequested;
    /// <summary>Mở Space Lens tại một thư mục.</summary>
    public event Action<string>? SpaceLensPathRequested;
    /// <summary>Mở Gỡ cài đặt cho một chương trình (đường dẫn exe hoặc lối tắt).</summary>
    public event Action<string>? UninstallPathRequested;
    /// <summary>Chạy một tác vụ bảo trì.</summary>
    public event Action<MaintenanceTaskName>? MaintenanceTaskRequested;
    /// <summary>Đưa cửa sổ chính lên trước.</summary>
    public event Action? ActivateRequested;

    public void Open(SidebarItem item, bool autoStart = false)
    {
        Selection = item;
        ActivateRequested?.Invoke();
        if (autoStart) AutoStartRequested?.Invoke(item);
    }

    /// <summary>Xử lý tham số dòng lệnh hoặc URL <c>cleanboost://…</c>.</summary>
    public void Handle(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.StartsWith(Identifiers.UrlScheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                HandleUrl(a);
                return;
            }
            switch (a)
            {
                case "--smart-scan":
                    Open(SidebarItem.SmartScan, autoStart: true);
                    return;
                case "--clean-junk":
                    Open(SidebarItem.SystemJunk, autoStart: true);
                    return;
                case "--analyze" when i + 1 < args.Count:
                    OpenSpaceLens(args[i + 1]);
                    return;
                case "--uninstall" when i + 1 < args.Count:
                    OpenUninstaller(args[i + 1]);
                    return;
            }
        }
        ActivateRequested?.Invoke();
    }

    public void HandleUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Open(SidebarItem.SmartScan);
            return;
        }
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        switch (uri.Host.ToLowerInvariant())
        {
            case "scan":
                Open(SidebarItems.FromFeature(query["feature"] ?? "") ?? SidebarItem.SmartScan, autoStart: true);
                break;
            case "spacelens":
                OpenSpaceLens(query["path"]);
                break;
            case "uninstall":
                OpenUninstaller(query["path"]);
                break;
            case "maintenance":
                Open(SidebarItem.Maintenance);
                if (MaintenanceTaskNames.Parse(query["task"]) is { } task) MaintenanceTaskRequested?.Invoke(task);
                break;
            default:
                Open(SidebarItem.SmartScan);
                break;
        }
    }

    private void OpenSpaceLens(string? path)
    {
        Open(SidebarItem.SpaceLens);
        path = NormalizeShellPath(path);
        if (!string.IsNullOrEmpty(path) && (Directory.Exists(path) || File.Exists(path))) SpaceLensPathRequested?.Invoke(path!);
    }

    /// <summary>
    /// Menu chuột phải truyền <c>--analyze "%V"</c>; với gốc ổ, <c>"C:\"</c> bị tách thành <c>C:"</c> (dấu \ trước " là ký tự thoát):
    /// bỏ dấu ngoặc thừa và chuẩn hoá.
    /// </summary>
    private static string? NormalizeShellPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += "\\";
        try { return Path.GetFullPath(trimmed); } catch { return trimmed; }
    }

    private void OpenUninstaller(string? path)
    {
        Open(SidebarItem.Uninstaller);
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) UninstallPathRequested?.Invoke(path!);
    }
}
