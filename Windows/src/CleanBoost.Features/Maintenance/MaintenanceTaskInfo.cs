using CleanBoost.Core;

namespace CleanBoost.Features.Maintenance;

/// <summary>
/// Thông tin hiển thị của từng tác vụ bảo trì bản Windows (title/whenToRun/symbol của <c>MaintenanceTaskName</c> bản Mac),
/// thêm mô tả việc tác vụ làm và ảnh hưởng (thời gian, gián đoạn) để người dùng quyết định trước khi chạy.
/// </summary>
public static class MaintenanceTaskInfo
{
    /// <summary>Mọi tác vụ, theo thứ tự hiển thị và thứ tự chạy (thứ tự enum, như <c>allCases</c> của bản Mac).</summary>
    public static IReadOnlyList<MaintenanceTaskName> All { get; } = Enum.GetValues<MaintenanceTaskName>();

    /// <summary>Tên biểu tượng (kiểu SF Symbol, giao diện đổi sang glyph Segoe).</summary>
    public static string Symbol(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS => "globe",
        MaintenanceTaskName.FreeRAM => "memorychip",
        MaintenanceTaskName.RebuildIconCache => "photo",
        MaintenanceTaskName.ReindexSearch => "magnifyingglass",
        MaintenanceTaskName.ComponentCleanup => "gearshape.2",
        MaintenanceTaskName.OptimizeDrives => "internaldrive",
        MaintenanceTaskName.RepairSystemFiles => "hammer",
        MaintenanceTaskName.RestartExplorer => "arrow.clockwise",
        _ => "arrow.counterclockwise",
    };

    /// <summary>Tác vụ làm gì.</summary>
    public static string Description(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS => Loc.T("Xoá bộ nhớ đệm tên miền để Windows hỏi lại máy chủ DNS."),
        MaintenanceTaskName.FreeRAM => Loc.T("Thu hồi bộ nhớ đệm (standby) và working set của các tiến trình để có thêm RAM trống."),
        MaintenanceTaskName.RebuildIconCache => Loc.T("Làm mới cache biểu tượng của File Explorer, desktop và thanh tác vụ."),
        MaintenanceTaskName.ReindexSearch => Loc.T("Xoá chỉ mục Windows Search và dựng lại từ đầu trong nền."),
        MaintenanceTaskName.ComponentCleanup => Loc.T("Xoá bản cũ của các thành phần Windows đã được bản cập nhật mới thay thế (DISM)."),
        MaintenanceTaskName.OptimizeDrives => Loc.T("TRIM cho SSD và chống phân mảnh cho ổ cứng HDD, trên mọi ổ."),
        MaintenanceTaskName.RepairSystemFiles => Loc.T("Chạy sfc /scannow: kiểm tra file hệ thống và thay file hỏng bằng bản gốc."),
        MaintenanceTaskName.RestartExplorer => Loc.T("Tắt rồi mở lại File Explorer, thanh tác vụ và menu Start."),
        _ => Loc.T("Xoá điểm khôi phục hệ thống cũ nhất trên ổ hệ thống để lấy lại dung lượng."),
    };

    /// <summary>Khi nào nên chạy.</summary>
    public static string WhenToRun(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS => Loc.T("Khi gặp lỗi mạng hoặc vừa đổi DNS."),
        MaintenanceTaskName.FreeRAM => Loc.T("Khi RAM gần đầy và máy chậm."),
        MaintenanceTaskName.RebuildIconCache => Loc.T("Khi biểu tượng hiển thị sai, bị trống hoặc không cập nhật."),
        MaintenanceTaskName.ReindexSearch => Loc.T("Khi tìm kiếm trong Start hoặc File Explorer sai, thiếu hoặc chậm."),
        MaintenanceTaskName.ComponentCleanup => Loc.T("Khi ổ hệ thống đầy dần sau nhiều bản cập nhật Windows."),
        MaintenanceTaskName.OptimizeDrives => Loc.T("Khi máy hay tắt nên Windows không kịp tự tối ưu ổ theo lịch."),
        MaintenanceTaskName.RepairSystemFiles => Loc.T("Khi Windows báo lỗi lạ, app hệ thống không mở được hoặc máy hay treo."),
        MaintenanceTaskName.RestartExplorer => Loc.T("Khi thanh tác vụ, menu Start hoặc File Explorer bị treo."),
        _ => Loc.T("Khi ổ hệ thống sắp đầy."),
    };

    /// <summary>Ảnh hưởng: mất bao lâu, có làm gián đoạn gì không.</summary>
    public static string Impact(this MaintenanceTaskName t) => t switch
    {
        MaintenanceTaskName.FlushDNS => Loc.T("Vài giây, không ảnh hưởng gì."),
        MaintenanceTaskName.FreeRAM => Loc.T("Vài giây; app đang mở có thể chậm một chút khi nạp lại dữ liệu."),
        MaintenanceTaskName.RebuildIconCache => Loc.T("Vài giây; màn hình có thể nháy nhẹ."),
        MaintenanceTaskName.ReindexSearch => Loc.T("Dựng lại trong nền, có thể mất vài giờ; trong lúc đó kết quả tìm kiếm chưa đầy đủ."),
        MaintenanceTaskName.ComponentCleanup => Loc.T("Có thể mất 10–30 phút; đừng tắt máy khi đang chạy."),
        MaintenanceTaskName.OptimizeDrives => Loc.T("Có thể mất 10–30 phút với ổ HDD; máy có thể chậm khi đang chạy."),
        MaintenanceTaskName.RepairSystemFiles => Loc.T("Có thể mất 10–30 phút; đừng tắt máy khi đang chạy."),
        MaintenanceTaskName.RestartExplorer => Loc.T("Thanh tác vụ biến mất vài giây; các cửa sổ File Explorer đang mở sẽ đóng."),
        _ => Loc.T("Không hoàn tác được: sẽ không thể đưa máy về thời điểm của điểm khôi phục đó."),
    };

    /// <summary>Tác vụ lâu hoặc làm gián đoạn: hỏi lại trước khi chạy.</summary>
    public static bool NeedsConfirmation(this MaintenanceTaskName t) => t is MaintenanceTaskName.ReindexSearch or MaintenanceTaskName.ComponentCleanup
        or MaintenanceTaskName.OptimizeDrives or MaintenanceTaskName.RepairSystemFiles or MaintenanceTaskName.RestartExplorer or MaintenanceTaskName.ThinRestorePoints;

    /// <summary>Không hoàn tác được (nút xác nhận màu đỏ).</summary>
    public static bool IsIrreversible(this MaintenanceTaskName t) => t == MaintenanceTaskName.ThinRestorePoints;
}
