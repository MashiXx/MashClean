using System.Runtime.InteropServices;
using CleanBoost.Core;
using CleanBoost.Engine.Metrics;
using CleanBoost.Engine.Persistence;
using Microsoft.Win32;

namespace CleanBoost.Features.Maintenance;

/// <summary>Trạng thái dịch vụ Windows Search (<c>WSearch</c>).</summary>
public enum SearchServiceState { Unknown, Running, Stopped, Disabled, Missing }

/// <summary>Lần chạy gần nhất của một tác vụ Windows tự lập lịch (đọc từ Task Scheduler).</summary>
public sealed record ScheduledRunInfo(DateTimeOffset? LastSuccess, bool? Enabled);

/// <summary>Gợi ý cho một tác vụ (Recommendation của bản Mac).</summary>
public sealed record MaintenanceRecommendation(MaintenanceTaskName Task, bool Recommended, SafetyLevel Safety, string Why);

/// <summary>
/// Trạng thái hệ thống dùng để gợi ý tác vụ nên chạy (MaintenanceStatus của bản Mac, SYSTEM_DESIGN mục 11.5).
/// Tác vụ nhẹ, vô hại thì <see cref="SafetyLevel.Safe"/> (Smart Scan chọn sẵn); tác vụ lâu hoặc làm gián đoạn thì
/// <see cref="SafetyLevel.Review"/>.
/// </summary>
public sealed record MaintenanceStatus
{
    public required MemorySample Memory { get; init; }
    /// <summary>Ổ hệ thống, vd <c>C:</c>.</summary>
    public string SystemDrive { get; init; } = KnownPaths.SystemDrive;
    public long? SystemDriveFree { get; init; }
    public long? SystemDriveTotal { get; init; }
    public TimeSpan Uptime { get; init; }
    /// <summary>Windows đang chờ khởi động lại để hoàn tất cập nhật.</summary>
    public bool RebootPending { get; init; }
    public SearchServiceState SearchService { get; init; }
    /// <summary>Bảo vệ hệ thống (System Restore) bật hay tắt; null nếu không đọc được.</summary>
    public bool? SystemProtectionEnabled { get; init; }
    /// <summary>Lần Windows tự dọn WinSxS (tác vụ lập lịch <c>StartComponentCleanup</c>).</summary>
    public ScheduledRunInfo? WindowsComponentCleanup { get; init; }
    /// <summary>Lần Windows tự tối ưu ổ (tác vụ lập lịch <c>ScheduledDefrag</c>).</summary>
    public ScheduledRunInfo? WindowsDefrag { get; init; }
    /// <summary>Lần chạy gần nhất của từng tác vụ (bảng <c>maintenance_run</c>).</summary>
    public IReadOnlyDictionary<MaintenanceTaskName, MaintenanceRun> LastRuns { get; init; } = new Dictionary<MaintenanceTaskName, MaintenanceRun>();
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;

    public int MemoryLoadPercent => (int)Math.Round(Memory.UsedFraction * 100);

    /// <summary>Ổ hệ thống sắp đầy: còn dưới 10% hoặc dưới 10 GB.</summary>
    public bool LowDiskSpace => SystemDriveFree is { } free && SystemDriveTotal is { } total && total > 0
        && (free < total / 10 || free < ByteCount.Gigabytes(10).Bytes);

    public DateTimeOffset? LastRun(MaintenanceTaskName task) => LastRuns.TryGetValue(task, out var r) ? r.RanAt : null;

    /// <summary>Lần chạy gần nhất bị lỗi.</summary>
    public bool LastRunFailed(MaintenanceTaskName task) => LastRuns.TryGetValue(task, out var r) && r.Result.StartsWith("failed", StringComparison.Ordinal);

    /// <summary>Lần chạy thành công gần nhất (lần gần nhất bị lỗi thì coi như chưa chạy).</summary>
    public DateTimeOffset? LastSuccess(MaintenanceTaskName task) => LastRunFailed(task) ? null : LastRun(task);

    public IReadOnlyList<MaintenanceRecommendation> Recommendations => MaintenanceTaskInfo.All.Select(Recommendation).ToList();

    /// <summary>Quy tắc gợi ý. "Chưa chạy trong N ngày" tính cả khi chưa chạy lần nào, như bản Mac.</summary>
    public MaintenanceRecommendation Recommendation(MaintenanceTaskName task)
    {
        bool OlderThan(DateTimeOffset? last, double days) => last is not { } d || (Now - d).TotalDays > days;
        MaintenanceRecommendation R(bool rec, SafetyLevel safety, string why) => new(task, rec, safety, why);
        var ours = LastSuccess(task);
        var free = SystemDriveFree is { } f ? ByteCount.Format(f) : "";

        switch (task)
        {
            case MaintenanceTaskName.FlushDNS:
            {
                var rec = OlderThan(ours, 30);
                return R(rec, SafetyLevel.Safe, rec ? Loc.T("Chưa xoá cache DNS trong 30 ngày.") : Loc.T("Đã chạy gần đây."));
            }
            case MaintenanceTaskName.FreeRAM:
            {
                var rec = Memory.Total > 0 && (Memory.Pressure >= MemoryPressure.Warning || MemoryLoadPercent >= 85);
                if (Memory.Total <= 0) return R(false, SafetyLevel.Safe, "");
                return R(rec, SafetyLevel.Safe, rec ? Loc.F("RAM đang dùng %lld%%.", MemoryLoadPercent)
                    : MemoryLoadPercent < 70 ? Loc.F("RAM đang dùng %lld%%, vẫn còn thoải mái.", MemoryLoadPercent)
                    : Loc.F("RAM đang dùng %lld%%, chưa cần giải phóng.", MemoryLoadPercent));
            }
            case MaintenanceTaskName.ReindexSearch:
                return SearchService switch
                {
                    // Dịch vụ tự khởi động nhưng không chạy (bỏ qua vài phút đầu sau khi bật máy vì dịch vụ khởi động trễ).
                    SearchServiceState.Stopped when Uptime > TimeSpan.FromMinutes(10) =>
                        R(true, SafetyLevel.Review, Loc.T("Dịch vụ Windows Search không chạy; đánh lại chỉ mục sẽ khởi động lại dịch vụ.")),
                    SearchServiceState.Disabled => R(false, SafetyLevel.Review, Loc.T("Dịch vụ Windows Search đang bị tắt nên không đánh lại chỉ mục được.")),
                    SearchServiceState.Missing => R(false, SafetyLevel.Review, Loc.T("Máy này không có dịch vụ Windows Search.")),
                    SearchServiceState.Running => R(false, SafetyLevel.Review, Loc.T("Windows Search đang chạy bình thường.")),
                    _ => R(false, SafetyLevel.Review, ""),
                };
            case MaintenanceTaskName.ComponentCleanup:
            {
                if (RebootPending) return R(false, SafetyLevel.Review, Loc.T("Windows đang chờ khởi động lại; hãy khởi động lại máy trước khi chạy."));
                var windows = WindowsComponentCleanup?.LastSuccess;
                var last = Max(ours, windows);
                if (LowDiskSpace && OlderThan(last, 30)) return R(true, SafetyLevel.Review, Loc.F("Ổ %@ chỉ còn trống %@.", SystemDrive, free));
                if (OlderThan(last, 90)) return R(true, SafetyLevel.Review, Loc.F("Chưa dọn trong %lld ngày qua.", 90));
                return R(false, SafetyLevel.Review, windows is { } w && (ours is null || w > ours)
                    ? Loc.F("Windows đã tự dọn lúc %@.", FormatDate(w))
                    : Loc.T("Đã chạy gần đây."));
            }
            case MaintenanceTaskName.OptimizeDrives:
            {
                var windows = WindowsDefrag?.LastSuccess;
                var last = Max(ours, windows);
                if (OlderThan(last, 30))
                {
                    var why = WindowsDefrag?.Enabled == false ? Loc.T("Lịch tối ưu ổ tự động của Windows đang tắt.")
                        : last is { } l ? Loc.F("Lần tối ưu gần nhất cách đây %lld ngày.", (long)(Now - l).TotalDays)
                        : Loc.F("Chưa chạy trong %lld ngày qua.", 30);
                    return R(true, SafetyLevel.Review, why);
                }
                return R(false, SafetyLevel.Review, windows is { } w && (ours is null || w > ours)
                    ? Loc.F("Windows đã tự tối ưu ổ lúc %@.", FormatDate(w))
                    : Loc.T("Đã chạy gần đây."));
            }
            case MaintenanceTaskName.ThinRestorePoints:
            {
                if (SystemProtectionEnabled == false) return R(false, SafetyLevel.Review, Loc.T("Bảo vệ hệ thống đang tắt nên có thể không có điểm khôi phục nào."));
                if (SystemDriveFree is null) return R(false, SafetyLevel.Review, "");
                var rec = LowDiskSpace && OlderThan(ours, 7);
                return R(rec, SafetyLevel.Review, LowDiskSpace
                    ? Loc.F("Ổ %@ chỉ còn trống %@.", SystemDrive, free)
                    : Loc.F("Ổ %@ còn trống %@, chưa cần xoá.", SystemDrive, free));
            }
            default:
                // Dựng lại cache biểu tượng, sfc, khởi động lại Explorer: không có dấu hiệu đo được, chỉ chạy khi gặp đúng vấn đề.
                return R(false, task == MaintenanceTaskName.RebuildIconCache ? SafetyLevel.Safe : SafetyLevel.Review, "");
        }
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>Ngày giờ ngắn theo ngôn ngữ giao diện (date abbreviated + time short của bản Mac).</summary>
    public static string FormatDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime().DateTime;
        var culture = Loc.Culture;
        var datePart = Loc.IsEnglish ? local.ToString("MMM d, yyyy", culture) : local.ToString("d 'thg' M, yyyy", culture);
        return datePart + " " + local.ToString("t", culture);
    }

    /// <summary>"3 ngày 4 giờ", "5 giờ 12 phút"…</summary>
    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1) return Loc.F("%lld ngày %lld giờ", (long)span.TotalDays, (long)span.Hours);
        if (span.TotalHours >= 1) return Loc.F("%lld giờ %lld phút", (long)span.TotalHours, (long)span.Minutes);
        return Loc.F("%lld phút", (long)Math.Max(0, span.TotalMinutes));
    }
}

/// <summary>
/// Thu thập trạng thái hệ thống (MaintenanceStatusReader của bản Mac). Không cần quyền quản trị: RAM
/// (<c>GlobalMemoryStatusEx</c>), dung lượng ổ hệ thống, thời gian hoạt động, cờ chờ khởi động lại trong registry, trạng thái
/// dịch vụ WSearch (SCM), lần chạy tự động của Windows (Task Scheduler) và lịch sử trong bảng <c>maintenance_run</c>.
/// </summary>
public sealed class MaintenanceStatusReader(Storage? storage)
{
    public async Task<MaintenanceStatus> ReadAsync(CancellationToken ct = default)
    {
        // Task Scheduler là COM ngoài tiến trình: chạy song song và có giới hạn thời gian để không chặn cả màn hình.
        var scheduled = Task.Run(() => (Cleanup: ScheduledTask(@"\Microsoft\Windows\Servicing", "StartComponentCleanup"),
            Defrag: ScheduledTask(@"\Microsoft\Windows\Defrag", "ScheduledDefrag")), ct);
        var lastRuns = new Dictionary<MaintenanceTaskName, MaintenanceRun>();
        try
        {
            foreach (var (raw, run) in storage?.LastMaintenanceRuns() ?? [])
            {
                if (MaintenanceTaskNames.Parse(raw) is { } task) lastRuns[task] = run;
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Storage, "Không đọc được lịch sử bảo trì", ex);
        }
        long? free = null, total = null;
        try
        {
            var drive = new DriveInfo(KnownPaths.SystemDrive + "\\");
            if (drive.IsReady)
            {
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
        }
        catch
        {
            // không đọc được ổ
        }
        var memory = SystemSampler.SampleMemory();
        var search = SearchService();
        var reboot = IsRebootPending();
        var protection = IsSystemProtectionEnabled();
        ScheduledRunInfo? cleanup = null, defrag = null;
        if (await Task.WhenAny(scheduled, Task.Delay(TimeSpan.FromSeconds(5), ct)).ConfigureAwait(false) == scheduled && scheduled.IsCompletedSuccessfully)
        {
            (cleanup, defrag) = scheduled.Result;
        }
        ct.ThrowIfCancellationRequested();
        return new MaintenanceStatus
        {
            Memory = memory,
            SystemDriveFree = free,
            SystemDriveTotal = total,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            RebootPending = reboot,
            SearchService = search,
            SystemProtectionEnabled = protection,
            WindowsComponentCleanup = cleanup,
            WindowsDefrag = defrag,
            LastRuns = lastRuns,
            Now = DateTimeOffset.Now,
        };
    }

    // MARK: Chờ khởi động lại

    /// <summary>Cờ của Component Based Servicing và Windows Update (không dùng PendingFileRenameOperations vì hay báo nhầm).</summary>
    public static bool IsRebootPending()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cbs = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbs is not null) return true;
            using var wu = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return wu is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Bảo vệ hệ thống: chính sách <c>DisableSR</c> hoặc giá trị <c>RPSessionInterval</c> (0 khi tắt trên mọi ổ).
    /// Đếm điểm khôi phục thật cần quyền quản trị (<c>vssadmin list shadows</c>) nên không làm ở đây.
    /// </summary>
    public static bool? IsSystemProtectionEnabled()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var policy = hklm.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"))
            {
                if (policy?.GetValue("DisableSR") is int d && d == 1) return false;
            }
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            return key?.GetValue("RPSessionInterval") is int v ? v != 0 : null;
        }
        catch
        {
            return null;
        }
    }

    // MARK: Task Scheduler

    /// <summary>Lần chạy thành công gần nhất và trạng thái bật/tắt của một tác vụ lập lịch (người dùng thường đọc được).</summary>
    public static ScheduledRunInfo? ScheduledTask(string folder, string name)
    {
        object? service = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            service = Activator.CreateInstance(type);
            if (service is null) return null;
            dynamic s = service;
            s.Connect();
            dynamic task = s.GetFolder(folder).GetTask(name);
            bool enabled = task.Enabled;
            DateTime last = task.LastRunTime;
            int result = task.LastTaskResult;
            // Chưa chạy lần nào: Task Scheduler trả về 30/12/1899.
            DateTimeOffset? success = result == 0 && last.Year > 2000 ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Local)) : null;
            return new ScheduledRunInfo(success, enabled);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Scan, $"Không đọc được tác vụ lập lịch {folder}\\{name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
    }

    // MARK: Dịch vụ Windows Search

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, int size, out int needed);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private const uint SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_CONFIG = 0x1, SERVICE_QUERY_STATUS = 0x4;
    private const uint SERVICE_RUNNING = 4, SERVICE_START_PENDING = 2, SERVICE_DISABLED = 4;
    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;

    public static SearchServiceState SearchService()
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return SearchServiceState.Unknown;
        try
        {
            var svc = OpenServiceW(scm, "WSearch", SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == ERROR_SERVICE_DOES_NOT_EXIST ? SearchServiceState.Missing : SearchServiceState.Unknown;
            try
            {
                if (!QueryServiceStatus(svc, out var status)) return SearchServiceState.Unknown;
                if (status.CurrentState is SERVICE_RUNNING or SERVICE_START_PENDING) return SearchServiceState.Running;
                // QUERY_SERVICE_CONFIG: dwServiceType, dwStartType, ... — chỉ cần dwStartType ở offset 4.
                QueryServiceConfigW(svc, IntPtr.Zero, 0, out var needed);
                if (needed <= 0) return SearchServiceState.Stopped;
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!QueryServiceConfigW(svc, buffer, needed, out _)) return SearchServiceState.Stopped;
                    return (uint)Marshal.ReadInt32(buffer, 4) == SERVICE_DISABLED ? SearchServiceState.Disabled : SearchServiceState.Stopped;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }
}
