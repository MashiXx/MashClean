namespace CleanBoost.Core;

/// <summary>Tác vụ bảo trì có tên (SYSTEM_DESIGN mục 9.3, 11.5) — bản Windows. Helper chỉ nhận đúng các tên này.</summary>
public enum MaintenanceTaskName
{
    /// <summary>Xoá cache DNS: <c>ipconfig /flushdns</c>.</summary>
    FlushDNS,
    /// <summary>Giải phóng RAM: dọn working set và standby list (NtSetSystemInformation).</summary>
    FreeRAM,
    /// <summary>Dựng lại cache biểu tượng: <c>ie4uinit.exe -show</c>.</summary>
    RebuildIconCache,
    /// <summary>Đánh lại chỉ mục Windows Search (ISearchCatalogManager::Reindex).</summary>
    ReindexSearch,
    /// <summary>Dọn kho thành phần WinSxS: <c>Dism /Online /Cleanup-Image /StartComponentCleanup</c>.</summary>
    ComponentCleanup,
    /// <summary>Tối ưu ổ đĩa (TRIM cho SSD, chống phân mảnh cho HDD): <c>defrag /C /O /H</c>.</summary>
    OptimizeDrives,
    /// <summary>Kiểm tra và sửa file hệ thống: <c>sfc /scannow</c>.</summary>
    RepairSystemFiles,
    /// <summary>Khởi động lại Windows Explorer khi thanh tác vụ hoặc File Explorer bị treo.</summary>
    RestartExplorer,
    /// <summary>Xoá điểm khôi phục cũ nhất: <c>vssadmin delete shadows /for=C: /oldest</c>.</summary>
    ThinRestorePoints,
}

public static class MaintenanceTaskNames
{
    public static string RawValue(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS => "flushDNS",
        MaintenanceTaskName.FreeRAM => "freeRAM",
        MaintenanceTaskName.RebuildIconCache => "rebuildIconCache",
        MaintenanceTaskName.ReindexSearch => "reindexSearch",
        MaintenanceTaskName.ComponentCleanup => "componentCleanup",
        MaintenanceTaskName.OptimizeDrives => "optimizeDrives",
        MaintenanceTaskName.RepairSystemFiles => "repairSystemFiles",
        MaintenanceTaskName.RestartExplorer => "restartExplorer",
        _ => "thinRestorePoints",
    };

    public static MaintenanceTaskName? Parse(string? raw) =>
        Enum.GetValues<MaintenanceTaskName>().Cast<MaintenanceTaskName?>().FirstOrDefault(t => t!.Value.RawValue() == raw);

    /// <summary>Tác vụ cần helper quản trị.</summary>
    public static bool RequiresAdmin(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS or MaintenanceTaskName.RebuildIconCache or MaintenanceTaskName.RestartExplorer => false,
        _ => true,
    };

    /// <summary>Thời gian chờ tối đa của từng tác vụ.</summary>
    public static TimeSpan Timeout(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.ComponentCleanup => TimeSpan.FromMinutes(60),
        MaintenanceTaskName.RepairSystemFiles => TimeSpan.FromMinutes(60),
        MaintenanceTaskName.OptimizeDrives => TimeSpan.FromMinutes(60),
        MaintenanceTaskName.ReindexSearch => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromMinutes(3),
    };

    public static string Title(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS => Loc.T("Xoá cache DNS"),
        MaintenanceTaskName.FreeRAM => Loc.T("Giải phóng RAM"),
        MaintenanceTaskName.RebuildIconCache => Loc.T("Dựng lại cache biểu tượng"),
        MaintenanceTaskName.ReindexSearch => Loc.T("Đánh lại chỉ mục Windows Search"),
        MaintenanceTaskName.ComponentCleanup => Loc.T("Dọn kho thành phần Windows (WinSxS)"),
        MaintenanceTaskName.OptimizeDrives => Loc.T("Tối ưu ổ đĩa"),
        MaintenanceTaskName.RepairSystemFiles => Loc.T("Kiểm tra và sửa file hệ thống"),
        MaintenanceTaskName.RestartExplorer => Loc.T("Khởi động lại Windows Explorer"),
        _ => Loc.T("Xoá điểm khôi phục cũ nhất"),
    };
}

/// <summary>Kết quả một lệnh của helper.</summary>
public sealed record CommandResult(bool Success, string Message, string? Output = null)
{
    public static CommandResult Ok(string message = "", string? output = null) => new(true, message, output);
    public static CommandResult Fail(string message, string? output = null) => new(false, message, output);
}
