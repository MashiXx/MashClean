using System.Runtime.InteropServices;
using CleanBoost.Core;
using CleanBoost.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CleanBoost.Features.LargeOldFiles;

/// <summary>Định danh một file trên volume (tương đương <c>(st_dev, st_ino)</c> của bản Mac) và số hard link.</summary>
public readonly record struct FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount, long Size, uint Attributes);

/// <summary>
/// Mở file để đọc mà không đi theo reparse point và không làm thay đổi "lần truy cập cuối" (để File lớn &amp; cũ không
/// coi file vừa được băm là "mới dùng"). Dùng chung cho File trùng lặp.
/// </summary>
public static class FileOpener
{
    private const uint FILE_WRITE_ATTRIBUTES = 0x100;
    private const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    private const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(SafeFileHandle hFile, IntPtr lpCreationTime, ref long lpLastAccessTime, IntPtr lpLastWriteTime);

    /// <summary>
    /// Mở file chỉ để đọc. Nếu được cấp quyền ghi thuộc tính thì đặt thời gian truy cập = 0xFFFFFFFF'FFFFFFFF
    /// ("không cập nhật cho các thao tác qua handle này"); không được thì vẫn đọc bình thường.
    /// </summary>
    public static SafeFileHandle? OpenForRead(string path, bool sequential, out int error)
    {
        var flags = Win32.FILE_FLAG_OPEN_REPARSE_POINT | (sequential ? FILE_FLAG_SEQUENTIAL_SCAN : FILE_FLAG_RANDOM_ACCESS);
        const uint share = Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE;
        var name = LongPath.Prefix(path);
        var h = Win32.CreateFileW(name, Win32.GENERIC_READ | FILE_WRITE_ATTRIBUTES, share, IntPtr.Zero, Win32.OPEN_EXISTING, flags, IntPtr.Zero);
        if (!h.IsInvalid)
        {
            var keep = -1L;
            SetFileTime(h, IntPtr.Zero, ref keep, IntPtr.Zero);
            error = 0;
            return h;
        }
        h.Dispose();
        h = Win32.CreateFileW(name, Win32.GENERIC_READ, share, IntPtr.Zero, Win32.OPEN_EXISTING, flags, IntPtr.Zero);
        if (h.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            h.Dispose();
            return null;
        }
        error = 0;
        return h;
    }

    /// <summary>Định danh của file từ handle đang mở.</summary>
    public static FileIdentity? Identity(SafeFileHandle handle)
    {
        if (!Win32.GetFileInformationByHandle(handle, out var info)) return null;
        return new FileIdentity(info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow, info.nNumberOfLinks,
            ((long)info.nFileSizeHigh << 32) | info.nFileSizeLow, info.dwFileAttributes);
    }

    /// <summary>Định danh của file theo đường dẫn (chỉ xin quyền đọc thuộc tính, không đọc nội dung).</summary>
    public static FileIdentity? Identity(string path)
    {
        using var h = Win32.CreateFileW(LongPath.Prefix(path), Win32.FILE_READ_ATTRIBUTES,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING,
            Win32.FILE_FLAG_OPEN_REPARSE_POINT | Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return h.IsInvalid ? null : Identity(h);
    }
}
