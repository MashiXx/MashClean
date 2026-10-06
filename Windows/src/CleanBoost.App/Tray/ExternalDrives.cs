using System.Runtime.InteropServices;
using System.Text;
using CleanBoost.Core;
using Microsoft.Win32.SafeHandles;

namespace CleanBoost.App.Tray;

/// <summary>Ổ đĩa ngoài đang gắn (ExternalVolume của bản Mac).</summary>
public sealed record ExternalVolume(string Root, string Name, long Total, long Available)
{
    public string Id => Root;
}

/// <summary>
/// Liệt kê và tháo ổ đĩa ngoài. "Ngoài" = ổ tháo rời (USB, thẻ nhớ), ổ quang/ISO đang gắn, hoặc ổ cố định nối qua
/// USB/1394/SD/MMC hay ổ ảo (VHD). Không tính ổ hệ thống và ổ mạng.
/// Tháo: <c>CM_Request_Device_Eject</c> trên thiết bị cha của ổ (như "Safely Remove Hardware", không cần quyền quản trị,
/// cho biết lý do khi bị từ chối); ổ quang/ổ ảo hoặc khi không tìm được thiết bị thì dùng lệnh "eject" của shell.
/// </summary>
public static class ExternalDrives
{
    private const int BusType1394 = 0x4, BusTypeUsb = 0x7, BusTypeSd = 0xC, BusTypeMmc = 0xD, BusTypeFileBackedVirtual = 0xF;

    public static IReadOnlyList<ExternalVolume> List()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var result = new List<ExternalVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (string.Equals(drive.Name, system, StringComparison.OrdinalIgnoreCase)) continue;
                var external = drive.DriveType switch
                {
                    DriveType.Removable or DriveType.CDRom => true,
                    DriveType.Fixed => IsExternalBus(drive.Name),
                    _ => false,
                };
                if (!external || !drive.IsReady) continue;
                result.Add(new ExternalVolume(drive.Name, DisplayName(drive), drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch
            {
                // ổ vừa rút ra hoặc không đọc được: bỏ qua
            }
        }
        return result.OrderBy(v => v.Name, StringComparer.Create(Loc.Culture, ignoreCase: true)).ToArray();
    }

    private static string DisplayName(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd('\\');
        string label;
        try { label = drive.VolumeLabel; } catch { label = ""; }
        return string.IsNullOrWhiteSpace(label) ? Loc.F("Ổ %@", letter) : $"{label} ({letter})";
    }

    // MARK: Loại bus

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    private const uint FILE_SHARE_READ_WRITE = 0x1 | 0x2;
    private const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    private static SafeFileHandle OpenDevice(string path) => CreateFileW(path, 0, FILE_SHARE_READ_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    private static string VolumeDevicePath(string root) => @"\\.\" + root.TrimEnd('\\');

    /// <summary>Ổ cố định nhưng nối qua USB/1394/thẻ nhớ (ổ cứng gắn ngoài) hoặc ổ ảo VHD.</summary>
    private static bool IsExternalBus(string root) =>
        QueryBus(root) is { } d && (d.RemovableMedia || d.BusType is BusType1394 or BusTypeUsb or BusTypeSd or BusTypeMmc or BusTypeFileBackedVirtual);

    private readonly record struct BusInfo(bool RemovableMedia, int BusType);

    private static BusInfo? QueryBus(string root)
    {
        using var handle = OpenDevice(VolumeDevicePath(root));
        if (handle.IsInvalid) return null;
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
        var output = new byte[1024];
        if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out var returned, IntPtr.Zero) || returned < 32)
            return null;
        // STORAGE_DEVICE_DESCRIPTOR: RemovableMedia ở byte 10, BusType ở byte 28.
        return new BusInfo(output[10] != 0, BitConverter.ToInt32(output, 28));
    }

    // MARK: Tháo

    /// <summary>Tháo ổ; trả về lý do (đã dịch) khi không tháo được, <c>null</c> khi thành công. Chặn tới khi xong: gọi trên luồng nền.</summary>
    public static string? Eject(string root)
    {
        try
        {
            // Thiết bị USB/1394: tháo cả thiết bị như "Safely Remove Hardware". Đầu đọc thẻ gắn trong, ổ quang, ISO/VHD
            // hoặc khi không tìm được thiết bị: dùng lệnh "Eject" của Explorer (chỉ nhả đĩa/ổ ảo, không tắt bộ điều khiển).
            string? error;
            if (QueryBus(root) is { BusType: BusTypeUsb or BusType1394 }
                && DeviceNumber(VolumeDevicePath(root)) is { DeviceType: FILE_DEVICE_DISK } number
                && FindDevice(GuidDevInterfaceDisk, number) is { } devInst)
            {
                error = RequestEject(devInst);
            }
            else
            {
                error = ShellEject(root);
            }
            if (error is null && !WaitUntilGone(root)) error = Loc.T("Ổ vẫn còn gắn sau khi tháo.");
            return error;
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory.Menu, "Tháo ổ lỗi: " + ex.Message);
            return ex.Message;
        }
    }

    private static bool WaitUntilGone(string root)
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.NoRootDirectory || !drive.IsReady) return true;
            }
            catch
            {
                return true;
            }
            Thread.Sleep(200);
        }
        return false;
    }

    private const int FILE_DEVICE_DISK = 7;
    private static readonly Guid GuidDevInterfaceDisk = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    private readonly record struct StorageDeviceNumber(int DeviceType, int DeviceNumber);

    private static StorageDeviceNumber? DeviceNumber(string devicePath)
    {
        using var handle = OpenDevice(devicePath);
        if (handle.IsInvalid) return null;
        var output = new byte[12]; // STORAGE_DEVICE_NUMBER { DeviceType, DeviceNumber, PartitionNumber }
        if (!DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, output, output.Length, out var returned, IntPtr.Zero) || returned < 8)
            return null;
        return new StorageDeviceNumber(BitConverter.ToInt32(output, 0), BitConverter.ToInt32(output, 4));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int detailSize, out int required, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Request_Device_EjectW(uint devInst, out int vetoType, StringBuilder vetoName, int nameLength, uint flags);

    /// <summary>Tìm devnode của đĩa có cùng số thiết bị với ổ.</summary>
    private static uint? FindDevice(Guid interfaceGuid, StorageDeviceNumber target)
    {
        var set = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, i, ref data)) return null;
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, ref info);
                if (required <= 0) continue;
                var buffer = Marshal.AllocHGlobal(required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 trên 64-bit, 6 trên 32-bit.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, buffer, required, out _, ref info)) continue;
                    var path = Marshal.PtrToStringUni(buffer + 4);
                    if (path is not null && DeviceNumber(path) is { } n && n == target) return info.DevInst;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private const int PNP_VetoTypeUnknown = 0, PNP_VetoLegacyDevice = 1, PNP_VetoPendingClose = 2, PNP_VetoWindowsApp = 3, PNP_VetoWindowsService = 4,
        PNP_VetoOutstandingOpen = 5, PNP_VetoDevice = 6, PNP_VetoDriver = 7, PNP_VetoIllegalDeviceRequest = 8, PNP_VetoInsufficientPower = 9,
        PNP_VetoNonDisableable = 10, PNP_VetoLegacyDriver = 11, PNP_VetoInsufficientRights = 12;

    /// <summary>Yêu cầu tháo thiết bị cha của đĩa (thiết bị USB), thử lại vài lần vì Explorer có thể đang giữ ổ.</summary>
    private static string? RequestEject(uint disk)
    {
        var target = CM_Get_Parent(out var parent, disk, 0) == 0 ? parent : disk;
        string? error = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var veto = new StringBuilder(260);
            var cr = CM_Request_Device_EjectW(target, out var vetoType, veto, veto.Capacity, 0);
            if (cr == 0 && vetoType == PNP_VetoTypeUnknown) return null;
            error = vetoType switch
            {
                PNP_VetoOutstandingOpen or PNP_VetoPendingClose or PNP_VetoWindowsApp or PNP_VetoWindowsService or PNP_VetoDevice or PNP_VetoDriver
                    or PNP_VetoLegacyDevice or PNP_VetoLegacyDriver => Loc.T("Ổ đang được một chương trình khác sử dụng."),
                PNP_VetoIllegalDeviceRequest or PNP_VetoNonDisableable or PNP_VetoInsufficientPower => Loc.T("Windows không cho tháo thiết bị này."),
                PNP_VetoInsufficientRights => Loc.T("Không đủ quyền để tháo thiết bị này."),
                _ => Loc.F("Windows từ chối yêu cầu (mã %@).", cr != 0 ? cr.ToString("X", System.Globalization.CultureInfo.InvariantCulture) : vetoType.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
            Log.Info(LogCategory.Menu, $"Tháo ổ bị từ chối: cr={cr}, veto={vetoType} {veto}");
            Thread.Sleep(400);
        }
        return error;
    }

    /// <summary>Lệnh "eject" của Explorer (ổ quang, ISO/VHD đang gắn). Chạy trên luồng STA riêng vì Shell.Application là COM STA.</summary>
    private static string? ShellEject(string root)
    {
        string? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application");
                dynamic shell = Activator.CreateInstance(type)!;
                try
                {
                    dynamic? computer = shell.NameSpace(17); // ssfDRIVES
                    dynamic? item = computer?.ParseName(root);
                    if (item is null)
                    {
                        error = Loc.T("Không tìm thấy thiết bị.");
                        return;
                    }
                    item.InvokeVerb("eject");
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(15));
        return error;
    }
}
