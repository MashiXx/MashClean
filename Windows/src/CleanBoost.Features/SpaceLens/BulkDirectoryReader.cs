using System.Runtime.InteropServices;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CleanBoost.Features.SpaceLens;

/// <summary>Một mục đọc được từ thư mục (tên nằm trong bộ đệm ký tự của reader).</summary>
internal struct RawEntry
{
    public int NameStart;
    public int NameLength;
    public uint Attributes;
    public uint ReparseTag;
    public long Size;
    /// <summary>Dung lượng thực chiếm trên ổ (NTFS báo sẵn trong mục thư mục: tính cả file nén, sparse, file nhỏ nằm trong MFT).</summary>
    public long Allocated;
    /// <summary>Số hiệu file trên volume (giống nhau với mọi hard link của cùng một file), 0 nếu không có.</summary>
    public long FileId;

    public readonly bool IsDirectory => (Attributes & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public readonly bool IsReparsePoint => (Attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
}

/// <summary>Cách đọc thư mục mà volume hỗ trợ (hạ dần khi gặp lỗi "không hỗ trợ").</summary>
internal enum DirectoryReadMode
{
    /// <summary><c>FileIdExtdDirectoryInfo</c>: không sinh tên 8.3, có reparse tag và số hiệu file 128 bit.</summary>
    IdExtended = 19,
    /// <summary><c>FileIdBothDirectoryInfo</c>: hỗ trợ rộng hơn (ổ mạng SMB, FAT).</summary>
    IdBoth = 10,
    /// <summary>FindFirstFileEx qua <see cref="DirectoryReader"/>; dung lượng làm tròn theo cluster.</summary>
    FindFirstFile = 0,
}

/// <summary>
/// Đọc thư mục hàng loạt bằng <c>GetFileInformationByHandleEx</c> với bộ đệm 128 KB — tương đương <c>getattrlistbulk</c>
/// của bản Mac: mỗi lời gọi trả về hàng trăm mục kèm dung lượng thực chiếm, reparse tag và số hiệu file (để đếm hard link
/// một lần). Volume không hỗ trợ thì hạ xuống lớp thông tin cũ hơn rồi tới <see cref="DirectoryReader"/> (FindFirstFileEx).
/// Mỗi luồng quét dùng một reader riêng.
/// </summary>
internal sealed unsafe class BulkDirectoryReader
{
    private const int BufferSize = 128 * 1024;
    private const int ERROR_INVALID_FUNCTION = 1;
    private const int ERROR_INVALID_LEVEL = 124;

    private readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(BufferSize, pinned: true);
    private char[] _names = new char[8192];
    private int _namesUsed;

    public List<RawEntry> Entries { get; } = new(256);

    public ReadOnlySpan<char> Name(in RawEntry e) => _names.AsSpan(e.NameStart, e.NameLength);

    /// <summary>
    /// Đọc một cấp thư mục. Trả về 0 nếu thành công, ngược lại là mã lỗi Win32 (có thể đã đọc được một phần).
    /// <paramref name="mode"/> dùng chung cho cả volume: lớp thông tin không được hỗ trợ thì hạ xuống cho mọi lần đọc sau.
    /// </summary>
    public int Read(string directory, long clusterSize, ref int mode)
    {
        Entries.Clear();
        _namesUsed = 0;
        while (true)
        {
            var current = (DirectoryReadMode)Volatile.Read(ref mode);
            if (current == DirectoryReadMode.FindFirstFile) return ReadFallback(directory, clusterSize);
            var result = ReadByHandle(directory, current);
            if (result != -1) return result;
            // Không hỗ trợ: hạ một bậc rồi thử lại.
            Interlocked.CompareExchange(ref mode, (int)(current == DirectoryReadMode.IdExtended ? DirectoryReadMode.IdBoth : DirectoryReadMode.FindFirstFile), (int)current);
            Entries.Clear();
            _namesUsed = 0;
        }
    }

    /// <summary>Trả về 0 nếu xong, mã lỗi Win32, hoặc -1 khi lớp thông tin không được hỗ trợ.</summary>
    private int ReadByHandle(string directory, DirectoryReadMode mode)
    {
        using var handle = Win32.CreateFileW(NtPath.Of(directory), Win32.FILE_LIST_DIRECTORY | Win32.SYNCHRONIZE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE, IntPtr.Zero, Win32.OPEN_EXISTING,
            Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return Marshal.GetLastWin32Error();
        var first = true;
        fixed (byte* buffer = _buffer)
        {
            while (true)
            {
                if (!GetFileInformationByHandleEx(handle, (int)mode, buffer, BufferSize))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == Win32.ERROR_NO_MORE_FILES || err == Win32.ERROR_FILE_NOT_FOUND) return 0;
                    if (first && err is Win32.ERROR_INVALID_PARAMETER or Win32.ERROR_NOT_SUPPORTED or ERROR_INVALID_LEVEL or ERROR_INVALID_FUNCTION) return -1;
                    return err;
                }
                first = false;
                if (mode == DirectoryReadMode.IdExtended) ParseIdExtended(buffer);
                else ParseIdBoth(buffer);
            }
        }
    }

    /// <summary>
    /// Bố cục FILE_ID_EXTD_DIR_INFO: NextEntryOffset@0, EndOfFile@40, AllocationSize@48, FileAttributes@56, FileNameLength@60,
    /// ReparsePointTag@68, FileId (128 bit)@72, FileName@88.
    /// </summary>
    private void ParseIdExtended(byte* buffer)
    {
        var offset = 0;
        while (true)
        {
            var e = buffer + offset;
            var next = *(uint*)e;
            var nameLength = (int)(*(uint*)(e + 60) / 2);
            var name = new ReadOnlySpan<char>(e + 88, nameLength);
            if (!(name is "." or ".."))
            {
                var attributes = *(uint*)(e + 56);
                var idLow = *(long*)(e + 72);
                var idHigh = *(long*)(e + 80);
                Entries.Add(new RawEntry
                {
                    NameStart = AppendName(name),
                    NameLength = nameLength,
                    Attributes = attributes,
                    ReparseTag = (attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? *(uint*)(e + 68) : 0,
                    Size = *(long*)(e + 40),
                    Allocated = *(long*)(e + 48),
                    // NTFS: phần cao bằng 0, phần thấp là số tham chiếu MFT. ReFS: gộp cả 128 bit.
                    FileId = idHigh == 0 ? idLow : idLow ^ (idHigh * unchecked((long)0x9E3779B97F4A7C15)),
                });
            }
            if (next == 0) return;
            offset += (int)next;
        }
    }

    /// <summary>
    /// Bố cục FILE_ID_BOTH_DIR_INFO: NextEntryOffset@0, EndOfFile@40, AllocationSize@48, FileAttributes@56,
    /// FileNameLength@60, EaSize@64 (là reparse tag khi mục là reparse point), FileId@96, FileName@104.
    /// </summary>
    private void ParseIdBoth(byte* buffer)
    {
        var offset = 0;
        while (true)
        {
            var e = buffer + offset;
            var next = *(uint*)e;
            var nameLength = (int)(*(uint*)(e + 60) / 2);
            var name = new ReadOnlySpan<char>(e + 104, nameLength);
            if (!(name is "." or ".."))
            {
                var attributes = *(uint*)(e + 56);
                Entries.Add(new RawEntry
                {
                    NameStart = AppendName(name),
                    NameLength = nameLength,
                    Attributes = attributes,
                    ReparseTag = (attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? *(uint*)(e + 64) : 0,
                    Size = *(long*)(e + 40),
                    Allocated = *(long*)(e + 48),
                    FileId = *(long*)(e + 96),
                });
            }
            if (next == 0) return;
            offset += (int)next;
        }
    }

    private int AppendName(ReadOnlySpan<char> name)
    {
        if (_namesUsed + name.Length > _names.Length) Array.Resize(ref _names, Math.Max(_names.Length * 2, _namesUsed + name.Length));
        var start = _namesUsed;
        name.CopyTo(_names.AsSpan(start));
        _namesUsed += name.Length;
        return start;
    }

    /// <summary>FindFirstFileEx (LARGE_FETCH) qua <see cref="DirectoryReader"/>; dung lượng làm tròn theo cluster.</summary>
    private int ReadFallback(string directory, long clusterSize)
    {
        try
        {
            DirectoryReader.Read(directory, d =>
            {
                Entries.Add(new RawEntry
                {
                    NameStart = AppendName(d.Name),
                    NameLength = d.Name.Length,
                    Attributes = d.Attributes,
                    ReparseTag = d.ReparseTag,
                    Size = d.Size,
                    Allocated = d.IsDirectory ? 0 : DiskSize.RoundUp(d.Size, clusterSize),
                });
            });
            return 0;
        }
        catch (Win32IOException ex)
        {
            return ex.Code;
        }
        catch (IOException)
        {
            return Win32.ERROR_IO_DEVICE;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);
}

/// <summary>Truy vấn phụ cho từng mục đặc biệt (ít gặp): dung lượng file nén/WOF, đích của mount point.</summary>
internal static unsafe class SpecialEntries
{
    public const uint IO_REPARSE_TAG_WOF = 0x80000017;
    public const uint IO_REPARSE_TAG_DEDUP = 0x80000013;
    private const uint NameSurrogateBit = 0x20000000;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

    /// <summary>Liên kết (symlink, junction, mount point, app alias, hoặc reparse tag "thay tên" bất kỳ): không đi theo.</summary>
    public static bool IsLinkTag(uint tag) =>
        tag is Win32.IO_REPARSE_TAG_SYMLINK or Win32.IO_REPARSE_TAG_MOUNT_POINT or Win32.IO_REPARSE_TAG_APPEXECLINK or Win32.IO_REPARSE_TAG_LX_SYMLINK
        || (tag & NameSurrogateBit) != 0;

    /// <summary>File nén NTFS, sparse, nén WOF (CompactOS) hoặc dedup: hỏi dung lượng thật bằng GetCompressedFileSize.</summary>
    public static bool NeedsCompressedSize(in RawEntry e) =>
        (e.Attributes & (Win32.FILE_ATTRIBUTE_COMPRESSED | Win32.FILE_ATTRIBUTE_SPARSE_FILE)) != 0
        || e.ReparseTag is IO_REPARSE_TAG_WOF or IO_REPARSE_TAG_DEDUP;

    public static long? CompressedSize(string path)
    {
        var low = GetCompressedFileSizeW(NtPath.Of(path), out var high);
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return null;
        return ((long)high << 32) | low;
    }

    /// <summary>Junction trỏ tới <c>\??\Volume{…}</c> là ổ khác gắn vào thư mục.</summary>
    public static bool IsVolumeMountPoint(string path)
    {
        using var handle = Win32.CreateFileW(NtPath.Of(path), 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
            IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) return false;
        const int size = 16 * 1024;
        var buffer = stackalloc byte[size];
        if (!DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0, buffer, size, out var returned, IntPtr.Zero) || returned < 16) return false;
        if (*(uint*)buffer != Win32.IO_REPARSE_TAG_MOUNT_POINT) return false;
        // MountPointReparseBuffer: SubstituteNameOffset@8, SubstituteNameLength@10, PathBuffer@16.
        int offset = *(ushort*)(buffer + 8);
        int length = *(ushort*)(buffer + 10);
        if (16 + offset + length > returned) return false;
        var target = new ReadOnlySpan<char>(buffer + 16 + offset, length / 2);
        return target.StartsWith(@"\??\Volume{", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, int inBufferSize, byte* outBuffer,
        int outBufferSize, out int bytesReturned, IntPtr overlapped);
}

/// <summary>
/// Đường dẫn dạng <c>\\?\</c> cho các lời gọi mở file của Space Lens: không giới hạn 260 ký tự và không để Win32 "chuẩn hoá"
/// tên kết thúc bằng dấu chấm/khoảng trắng (NTFS cho phép) thành một mục khác. Đường dẫn ở đây luôn tuyệt đối và đã chuẩn hoá.
/// </summary>
internal static class NtPath
{
    public static string Of(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return path.Length >= 3 && path[1] == ':' && path[2] == '\\' ? @"\\?\" + path : path;
    }
}
