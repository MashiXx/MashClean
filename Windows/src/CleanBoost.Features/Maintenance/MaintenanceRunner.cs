using System.Diagnostics;
using System.Text.RegularExpressions;
using CleanBoost.Core;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Persistence;

namespace CleanBoost.Features.Maintenance;

/// <summary>Kết quả chạy một tác vụ bảo trì (MaintenanceResult của bản Mac).</summary>
public sealed record MaintenanceRunResult(MaintenanceTaskName Task, bool Success, string Message, TimeSpan Duration, bool Executed = true);

/// <summary>
/// Chạy tác vụ bảo trì (MaintenanceRunner của bản Mac): tác vụ cần quyền quản trị đi qua helper (UAC), tác vụ còn lại chạy
/// ngay trong app bằng <see cref="MaintenanceExecutor"/>. Lần chạy thật được lưu vào bảng <c>maintenance_run</c>
/// ("ok" hoặc "failed: …"); chạy thử và lần không chạy được vì thiếu helper thì không lưu.
/// </summary>
public sealed class MaintenanceRunner(Storage? storage)
{
    public async Task<MaintenanceRunResult> RunAsync(MaintenanceTaskName task, IHelperClient? helper, bool dryRun, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (dryRun)
        {
            Log.Info(LogCategory.Clean, $"maintenance {task.RawValue()}: dry run");
            return new MaintenanceRunResult(task, true, Loc.T("Chạy thử: không thực hiện"), TimeSpan.Zero, Executed: false);
        }

        CommandResult result;
        if (task.RequiresAdmin())
        {
            if (HelperUnavailableMessage(helper) is { } unavailable)
                return new MaintenanceRunResult(task, false, unavailable, stopwatch.Elapsed, Executed: false);
            try
            {
                result = await helper!.RunMaintenanceAsync(task, ct).ConfigureAwait(false);
                result = result with { Message = Relocalize(result.Message) };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Không chạy được helper (từ chối UAC, mất kết nối…) hoặc helper từ chối lệnh ("busy"): tác vụ chưa chạy, không lưu.
                Log.Warning(LogCategory.Clean, $"maintenance {task.RawValue()}: helper lỗi: {ex.Message}");
                var message = ex.Message == "busy" ? Loc.T("Đang chạy một tác vụ bảo trì khác") : ex.Message;
                return new MaintenanceRunResult(task, false, message, stopwatch.Elapsed, Executed: false);
            }
        }
        else
        {
            result = await Task.Run(() => MaintenanceExecutor.RunAsync(task, ct), ct).ConfigureAwait(false);
        }
        stopwatch.Stop();

        var text = result.Success ? SuccessMessage(task, result) : FailureMessage(result);
        Record(task, result.Success, text);
        Log.Info(LogCategory.Clean, $"maintenance {task.RawValue()}: {(result.Success ? "ok" : "lỗi")} ({stopwatch.Elapsed.TotalSeconds:0.0} giây)");
        if (!result.Success && !string.IsNullOrWhiteSpace(result.Output)) Log.Warning(LogCategory.Clean, $"maintenance {task.RawValue()} output: {Truncate(result.Output, 800)}");
        return new MaintenanceRunResult(task, result.Success, text, stopwatch.Elapsed);
    }

    /// <summary>Helper chắc chắn không dùng được (đã bị từ chối UAC lần gần nhất thì không hỏi lại từng tác vụ).</summary>
    public static string? HelperUnavailableMessage(IHelperClient? helper) => helper?.Status switch
    {
        null => Loc.T("Chưa có helper quản trị"),
        HelperStatus.NotFound => Loc.T("Không tìm thấy helper quản trị (CleanBoost.Helper.exe)"),
        HelperStatus.Declined => Loc.T("Bạn đã không cấp quyền quản trị"),
        _ => null,
    };

    private void Record(MaintenanceTaskName task, bool success, string message)
    {
        try
        {
            storage?.RecordMaintenance(task.RawValue(), success ? "ok" : "failed: " + message);
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không ghi được lịch sử bảo trì", ex);
        }
    }

    private static string SuccessMessage(MaintenanceTaskName task, CommandResult result) => task switch
    {
        MaintenanceTaskName.FlushDNS => Loc.T("Đã xoá cache DNS"),
        MaintenanceTaskName.RebuildIconCache => Loc.T("Đã làm mới cache biểu tượng"),
        MaintenanceTaskName.ComponentCleanup when result.Message == Loc.T("Đã xong") => Loc.T("Đã dọn kho thành phần Windows"),
        MaintenanceTaskName.OptimizeDrives when result.Message == Loc.T("Đã xong") => Loc.T("Đã tối ưu các ổ đĩa"),
        MaintenanceTaskName.RepairSystemFiles when result.Message == Loc.T("Đã xong") => Loc.T("Đã kiểm tra file hệ thống"),
        MaintenanceTaskName.ThinRestorePoints when result.Message == Loc.T("Đã xong") => Loc.T("Đã xoá điểm khôi phục cũ nhất"),
        _ => string.IsNullOrEmpty(result.Message) ? Loc.T("Đã xong") : result.Message,
    };

    /// <summary>Thông báo lỗi kèm dòng cuối của công cụ (nếu đọc được) để người dùng biết lý do, vd "No items found…".</summary>
    private static string FailureMessage(CommandResult result)
    {
        var message = string.IsNullOrEmpty(result.Message) ? Loc.T("Đã có lỗi") : result.Message;
        var detail = (result.Output ?? "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        // Công cụ in UTF-16 hoặc bảng mã OEM thì chuỗi đọc ra bị lỗi ký tự: bỏ qua.
        if (detail is null || detail.Contains('\0') || detail.Contains('�') || detail.Length > 200) return message;
        return message + ": " + detail;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly (Regex Pattern, string Key)[] s_helperFormats =
    [
        (new Regex("^Đã giải phóng khoảng (.+)$"), "Đã giải phóng khoảng %@"),
        (new Regex("^(.+) trả về mã (.+)$"), "%@ trả về mã %@"),
        (new Regex("^Không tìm thấy (.+)$"), "Không tìm thấy %@"),
        (new Regex("^Windows Search báo lỗi 0x(.+)$"), "Windows Search báo lỗi 0x%@"),
    ];

    /// <summary>
    /// Helper không nạp bản dịch nên luôn trả chuỗi nguồn tiếng Việt (khoá của <see cref="Loc"/>): dịch lại theo ngôn ngữ của
    /// app, kể cả các chuỗi có tham số của <see cref="MaintenanceExecutor"/>.
    /// </summary>
    internal static string Relocalize(string message)
    {
        if (string.IsNullOrEmpty(message) || !Loc.IsEnglish) return message;
        var direct = Loc.T(message);
        if (direct != message) return direct;
        foreach (var (pattern, key) in s_helperFormats)
        {
            var m = pattern.Match(message);
            if (m.Success) return Loc.F(key, m.Groups.Cast<Group>().Skip(1).Select(g => (object?)g.Value).ToArray());
        }
        return message;
    }
}
