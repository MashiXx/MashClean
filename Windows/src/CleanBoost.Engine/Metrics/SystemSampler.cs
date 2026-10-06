using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CleanBoost.Core;

namespace CleanBoost.Engine.Metrics;

public enum MemoryPressure { Normal = 1, Warning = 2, Critical = 4 }

public sealed record CpuSample(double Usage, double User, double System, int CoreCount);

public sealed record MemorySample(long Used, long Total, long Available, long Committed, long CommitLimit, MemoryPressure Pressure)
{
    public double UsedFraction => Total <= 0 ? 0 : Math.Min(1, (double)Used / Total);

    public static string PressureTitle(MemoryPressure p) => p switch
    {
        MemoryPressure.Warning => Loc.T("Cao"),
        MemoryPressure.Critical => Loc.T("Nguy cấp"),
        _ => Loc.T("Bình thường"),
    };
}

public sealed record DiskSample(long Available, long Total, string Name)
{
    public double FreeFraction => Total <= 0 ? 0 : (double)Available / Total;
    public double UsedFraction => 1 - FreeFraction;
}

public sealed record BatterySample(bool HasBattery, double Level, bool IsCharging, bool IsOnACPower, int? MinutesRemaining);

public sealed record NetworkSample(double InPerSecond, double OutPerSecond, long TotalIn, long TotalOut);

public sealed record SystemSnapshot(
    DateTime Time,
    CpuSample? Cpu,
    MemorySample Memory,
    DiskSample? Disk,
    BatterySample Battery,
    NetworkSample? Network,
    long? TrashBytes,
    TimeSpan Uptime);

/// <summary>
/// Lấy mẫu số liệu hệ thống (SYSTEM_DESIGN mục 11.9) bằng API Windows: <c>GetSystemTimes</c> (CPU),
/// <c>GlobalMemoryStatusEx</c> (RAM), <c>GetDiskFreeSpaceEx</c> (ổ), <c>GetSystemPowerStatus</c> (pin), bộ đếm byte của
/// card mạng, <c>SHQueryRecycleBin</c> (Thùng rác). Giữ mẫu trước để tính chênh lệch.
/// </summary>
public sealed class SystemSampler
{
    private (ulong Idle, ulong Kernel, ulong User)? _lastCpu;
    private (long In, long Out, DateTime At)? _lastNet;

    public SystemSnapshot Sample(long? trashBytes = null, string? diskPath = null)
    {
        return new SystemSnapshot(DateTime.Now, SampleCpu(), SampleMemory(), SampleDisk(diskPath ?? KnownPaths.SystemDrive + "\\"), SampleBattery(), SampleNetwork(),
            trashBytes, TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    // MARK: CPU

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    private CpuSample? SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var now = (idle.Value, kernel.Value, user.Value);
        var last = _lastCpu;
        _lastCpu = now;
        if (last is not { } l) return null;
        var dIdle = now.Item1 - l.Idle;
        var dKernel = now.Item2 - l.Kernel;   // kernel bao gồm cả idle
        var dUser = now.Item3 - l.User;
        var total = dKernel + dUser;
        if (total == 0) return new CpuSample(0, 0, 0, Environment.ProcessorCount);
        var system = dKernel - dIdle;
        return new CpuSample(Math.Clamp((double)(system + dUser) / total, 0, 1), (double)dUser / total, (double)system / total, Environment.ProcessorCount);
    }

    // MARK: RAM

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

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    public static MemorySample SampleMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return new MemorySample(0, 0, 0, 0, 0, MemoryPressure.Normal);
        var total = (long)m.ullTotalPhys;
        var available = (long)m.ullAvailPhys;
        var committed = (long)(m.ullTotalPageFile - m.ullAvailPageFile);
        var limit = (long)m.ullTotalPageFile;
        // Windows không có "memory pressure" như macOS: suy ra từ RAM trống và tỉ lệ commit.
        var freeFraction = total > 0 ? (double)available / total : 1;
        var commitFraction = limit > 0 ? (double)committed / limit : 0;
        var pressure = freeFraction < 0.05 || commitFraction > 0.95 ? MemoryPressure.Critical
            : freeFraction < 0.12 || commitFraction > 0.85 ? MemoryPressure.Warning
            : MemoryPressure.Normal;
        return new MemorySample(total - available, total, available, committed, limit, pressure);
    }

    // MARK: Ổ đĩa

    private static DiskSample? SampleDisk(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            if (!d.IsReady) return null;
            var label = string.IsNullOrEmpty(d.VolumeLabel) ? Loc.F("Ổ %@", d.Name.TrimEnd('\\')) : $"{d.VolumeLabel} ({d.Name.TrimEnd('\\')})";
            return new DiskSample(d.AvailableFreeSpace, d.TotalSize, label);
        }
        catch
        {
            return null;
        }
    }

    // MARK: Pin

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    public static BatterySample SampleBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return new BatterySample(false, 0, false, false, null);
        var hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255 && s.BatteryLifePercent != 255;
        if (!hasBattery) return new BatterySample(false, 0, false, s.ACLineStatus == 1, null);
        var charging = (s.BatteryFlag & 8) != 0;
        int? minutes = s.BatteryLifeTime >= 0 ? s.BatteryLifeTime / 60 : null;
        return new BatterySample(true, s.BatteryLifePercent / 100.0, charging, s.ACLineStatus == 1, charging ? null : minutes);
    }

    // MARK: Mạng

    private NetworkSample? SampleNetwork()
    {
        long totalIn = 0, totalOut = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                totalIn += stats.BytesReceived;
                totalOut += stats.BytesSent;
            }
        }
        catch
        {
            return null;
        }
        var now = DateTime.UtcNow;
        var last = _lastNet;
        _lastNet = (totalIn, totalOut, now);
        if (last is not { } l) return null;
        var seconds = Math.Max(0.001, (now - l.At).TotalSeconds);
        var dIn = Math.Max(0, totalIn - l.In);
        var dOut = Math.Max(0, totalOut - l.Out);
        return new NetworkSample(dIn / seconds, dOut / seconds, totalIn, totalOut);
    }
}
