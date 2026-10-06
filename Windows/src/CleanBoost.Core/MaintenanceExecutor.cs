using System.Diagnostics;
using System.Runtime.InteropServices;
using CleanBoost.Core.Native;

namespace CleanBoost.Core;

/// <summary>
/// Thực thi tác vụ bảo trì có tên (SYSTEM_DESIGN mục 9.3). Mỗi tác vụ là chương trình hệ thống với đường dẫn tuyệt đối
/// và tham số cố định, hoặc một lời gọi API — không bao giờ nhận lệnh tuỳ ý. Tác vụ cần quyền quản trị chỉ chạy trong helper.
/// </summary>
public static class MaintenanceExecutor
{
    public static async Task<CommandResult> RunAsync(MaintenanceTaskName task, CancellationToken ct = default)
    {
        try
        {
            return task switch
            {
                MaintenanceTaskName.FlushDNS => await Exe("ipconfig.exe", ["/flushdns"], task, ct).ConfigureAwait(false),
                MaintenanceTaskName.FreeRAM => FreeRAM(),
                MaintenanceTaskName.RebuildIconCache => await Exe("ie4uinit.exe", ["-show"], task, ct).ConfigureAwait(false),
                MaintenanceTaskName.ReindexSearch => SearchIndex.Reindex(),
                MaintenanceTaskName.ComponentCleanup => await Exe("Dism.exe", ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/Quiet"], task, ct).ConfigureAwait(false),
                MaintenanceTaskName.OptimizeDrives => await Exe("defrag.exe", ["/C", "/O", "/H"], task, ct).ConfigureAwait(false),
                MaintenanceTaskName.RepairSystemFiles => await Exe("sfc.exe", ["/scannow"], task, ct).ConfigureAwait(false),
                MaintenanceTaskName.RestartExplorer => await RestartExplorerAsync(ct).ConfigureAwait(false),
                MaintenanceTaskName.ThinRestorePoints => await Exe("vssadmin.exe", ["delete", "shadows", "/for=" + KnownPaths.SystemDrive, "/oldest", "/quiet"], task, ct).ConfigureAwait(false),
                _ => CommandResult.Fail("unknown task"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory.Helper, "Tác vụ bảo trì " + task.RawValue() + " lỗi", ex);
            return CommandResult.Fail(ex.Message);
        }
    }

    private static async Task<CommandResult> Exe(string exe, string[] args, MaintenanceTaskName task, CancellationToken ct)
    {
        var path = ProcessRunner.System32(exe);
        if (!File.Exists(path)) return CommandResult.Fail(Loc.F("Không tìm thấy %@", exe));
        var output = await ProcessRunner.RunAsync(path, args, task.Timeout(), ct).ConfigureAwait(false);
        if (output.TimedOut) return CommandResult.Fail(Loc.T("Quá thời gian chờ"), output.StdOut);
        var text = (output.StdOut + output.StdErr).Trim();
        // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED (DISM): đã xong, cần khởi động lại để hoàn tất.
        if (output.ExitCode == 3010) return CommandResult.Ok(Loc.T("Đã xong; cần khởi động lại máy để hoàn tất"), text);
        return output.ExitCode == 0
            ? CommandResult.Ok(Loc.T("Đã xong"), text)
            : CommandResult.Fail(Loc.F("%@ trả về mã %@", exe, output.ExitCode.ToString()), text);
    }

    // MARK: Giải phóng RAM

    private enum SystemMemoryListCommand
    {
        MemoryEmptyWorkingSets = 2,
        MemoryFlushModifiedList = 3,
        MemoryPurgeStandbyList = 4,
        MemoryPurgeLowPriorityStandbyList = 5,
    }

    private const int SystemMemoryListInformation = 80;

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    /// <summary>
    /// Dọn working set của mọi tiến trình và danh sách standby (tương đương <c>purge</c> của macOS).
    /// Cần quyền quản trị và đặc quyền SeProfileSingleProcessPrivilege.
    /// </summary>
    public static CommandResult FreeRAM()
    {
        if (!Privileges.Enable("SeProfileSingleProcessPrivilege")) return CommandResult.Fail(Loc.T("Cần quyền quản trị"));
        Privileges.Enable("SeIncreaseQuotaPrivilege");
        var before = AvailablePhysical();
        var failures = new List<string>();
        foreach (var command in new[] { SystemMemoryListCommand.MemoryEmptyWorkingSets, SystemMemoryListCommand.MemoryFlushModifiedList,
                     SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList, SystemMemoryListCommand.MemoryPurgeStandbyList })
        {
            var c = (int)command;
            var status = NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int));
            if (status != 0) failures.Add($"{command}: 0x{status:X8}");
        }
        var freed = Math.Max(0, AvailablePhysical() - before);
        if (failures.Count == 4) return CommandResult.Fail(string.Join("; ", failures));
        return CommandResult.Ok(Loc.F("Đã giải phóng khoảng %@", ByteCount.Format(freed)), string.Join("; ", failures));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private static long AvailablePhysical()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (long)m.ullAvailPhys : 0;
    }

    // MARK: Explorer

    private static async Task<CommandResult> RestartExplorerAsync(CancellationToken ct)
    {
        var explorer = Path.Combine(KnownPaths.Windows, "explorer.exe");
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    p.Kill();
                    await p.WaitForExitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // tiến trình đã thoát
                }
            }
        }
        await Task.Delay(800, ct).ConfigureAwait(false);
        // Explorer thường tự khởi động lại; nếu chưa thì mở lại.
        if (Process.GetProcessesByName("explorer").All(p => { using (p) return p.SessionId != session; }))
        {
            Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = true })?.Dispose();
        }
        return CommandResult.Ok(Loc.T("Đã khởi động lại Windows Explorer"));
    }
}

/// <summary>Bật đặc quyền trong token của tiến trình hiện tại.</summary>
public static class Privileges
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool Enable(string privilege)
    {
        const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValueW(null, privilege, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == Win32.ERROR_SUCCESS;
        }
        finally
        {
            CloseHandle(token);
        }
    }
}

/// <summary>
/// Đánh lại chỉ mục Windows Search qua <c>ISearchCatalogManager::Reindex</c> (Microsoft.Search.Interop, khai báo tay).
/// Cần quyền quản trị.
/// </summary>
public static class SearchIndex
{
    [ComImport, Guid("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39")]
    private class CSearchManager;

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchManager
    {
        void GetIndexerVersionStr([MarshalAs(UnmanagedType.LPWStr)] out string version);
        void GetIndexerVersion(out uint major, out uint minor);
        void GetParameter([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr value);
        void SetParameter([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr value);
        void get_ProxyName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void get_BypassList([MarshalAs(UnmanagedType.LPWStr)] out string list);
        void SetProxy(int usage, int local, uint port, [MarshalAs(UnmanagedType.LPWStr)] string proxy, [MarshalAs(UnmanagedType.LPWStr)] string bypass);
        [return: MarshalAs(UnmanagedType.Interface)]
        ISearchCatalogManager GetCatalog([MarshalAs(UnmanagedType.LPWStr)] string catalog);
    }

    [ComImport, Guid("AB310581-AC80-11D1-8DF3-00C04FB6EF50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISearchCatalogManager
    {
        void get_Name([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetParameter([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr value);
        void SetParameter([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr value);
        void GetCatalogStatus(out int status, out int pausedReason);
        void Reset();
        void Reindex();
    }

    public static CommandResult Reindex()
    {
        try
        {
            var manager = (ISearchManager)new CSearchManager();
            var catalog = manager.GetCatalog("SystemIndex");
            catalog.Reindex();
            Marshal.ReleaseComObject(catalog);
            Marshal.ReleaseComObject(manager);
            return CommandResult.Ok(Loc.T("Windows Search sẽ đánh lại chỉ mục trong nền"));
        }
        catch (COMException ex)
        {
            return CommandResult.Fail(Loc.F("Windows Search báo lỗi 0x%@", ex.HResult.ToString("X8")));
        }
        catch (Exception ex)
        {
            return CommandResult.Fail(ex.Message);
        }
    }
}
