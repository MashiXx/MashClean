using CleanBoost.Core.Native;

namespace CleanBoost.Core.FileSystem;

/// <summary>
/// Một mục đọc từ thư mục bằng <c>FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)</c> — tương đương
/// <c>getattrlistbulk</c> của bản Mac: tên, loại, dung lượng, thời gian của nhiều mục trong ít lời gọi hệ thống.
/// </summary>
public readonly struct DirEntry
{
    public string Name { get; init; }
    public uint Attributes { get; init; }
    public uint ReparseTag { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public DateTime LastAccessUtc { get; init; }
    public DateTime CreationUtc { get; init; }

    public bool IsDirectory => (Attributes & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
    public bool IsReparsePoint => (Attributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    /// <summary>Symlink, junction hoặc app execution alias: không bao giờ đi theo khi duyệt hay xoá.</summary>
    public bool IsLink => IsReparsePoint && ReparseTag is Win32.IO_REPARSE_TAG_SYMLINK or Win32.IO_REPARSE_TAG_MOUNT_POINT
        or Win32.IO_REPARSE_TAG_APPEXECLINK or Win32.IO_REPARSE_TAG_LX_SYMLINK;

    /// <summary>File đám mây chưa tải về (OneDrive Files On-Demand…): xoá không giải phóng ổ, đọc sẽ kích hoạt tải về.</summary>
    public bool IsDataless => (Attributes & (Win32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Win32.FILE_ATTRIBUTE_RECALL_ON_OPEN | Win32.FILE_ATTRIBUTE_OFFLINE)) != 0;

    public bool IsHidden => (Attributes & Win32.FILE_ATTRIBUTE_HIDDEN) != 0;
    public bool IsSystem => (Attributes & Win32.FILE_ATTRIBUTE_SYSTEM) != 0;
    public bool IsReadOnly => (Attributes & Win32.FILE_ATTRIBUTE_READONLY) != 0;

    /// <summary>Dung lượng ước tính trên đĩa: làm tròn lên theo cluster; file đám mây chưa tải tính 0.</summary>
    public long AllocatedSize(long clusterSize) => IsDirectory || IsDataless || IsLink ? 0 : DiskSize.RoundUp(Size, clusterSize);

    /// <summary>Lần dùng gần nhất (sửa hoặc truy cập).</summary>
    public DateTime LastUseUtc => LastWriteUtc > LastAccessUtc ? LastWriteUtc : LastAccessUtc;
}

public static class DiskSize
{
    public static long RoundUp(long size, long cluster)
    {
        if (size <= 0) return 0;
        if (cluster <= 0) return size;
        return (size + cluster - 1) / cluster * cluster;
    }
}

public static class DirectoryReader
{
    /// <summary>Đọc một cấp thư mục. Ném <see cref="IOException"/> mang mã Win32 khi không mở được thư mục.</summary>
    public static void Read(string directory, Action<DirEntry> visit)
    {
        var pattern = LongPath.Prefix(directory.TrimEnd('\\') + "\\*");
        using var handle = Win32.FindFirstFileExW(pattern, Win32.FINDEX_INFO_LEVELS.FindExInfoBasic, out var data,
            Win32.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, Win32.FIND_FIRST_EX_LARGE_FETCH);
        if (handle.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (err == Win32.ERROR_FILE_NOT_FOUND || err == Win32.ERROR_NO_MORE_FILES) return;
            throw new Win32IOException(err, directory);
        }
        do
        {
            var name = data.cFileName;
            if (name is "." or "..") continue;
            visit(new DirEntry
            {
                Name = name,
                Attributes = data.dwFileAttributes,
                ReparseTag = (data.dwFileAttributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? data.dwReserved0 : 0,
                Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                LastWriteUtc = data.ftLastWriteTime.ToDateTimeUtc(),
                LastAccessUtc = data.ftLastAccessTime.ToDateTimeUtc(),
                CreationUtc = data.ftCreationTime.ToDateTimeUtc(),
            });
        }
        while (Win32.FindNextFileW(handle, out data));
    }

    public static List<DirEntry> List(string directory)
    {
        var list = new List<DirEntry>();
        try { Read(directory, list.Add); } catch (IOException) { }
        return list;
    }

    /// <summary>Thông tin một mục (không theo liên kết), <c>null</c> nếu không tồn tại.</summary>
    public static DirEntry? Stat(string path)
    {
        var normalized = path.TrimEnd('\\');
        if (normalized.Length <= 2)
        {
            // Gốc ổ đĩa: FindFirstFile không trả về chính nó.
            return Directory.Exists(normalized + "\\") ? new DirEntry { Name = normalized, Attributes = Win32.FILE_ATTRIBUTE_DIRECTORY } : null;
        }
        using var handle = Win32.FindFirstFileExW(LongPath.Prefix(normalized), Win32.FINDEX_INFO_LEVELS.FindExInfoBasic, out var data,
            Win32.FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, 0);
        if (handle.IsInvalid) return null;
        return new DirEntry
        {
            Name = data.cFileName,
            Attributes = data.dwFileAttributes,
            ReparseTag = (data.dwFileAttributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ? data.dwReserved0 : 0,
            Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
            LastWriteUtc = data.ftLastWriteTime.ToDateTimeUtc(),
            LastAccessUtc = data.ftLastAccessTime.ToDateTimeUtc(),
            CreationUtc = data.ftCreationTime.ToDateTimeUtc(),
        };
    }

    public static bool Exists(string path) =>
        Win32.GetFileAttributesExW(LongPath.Prefix(path), 0, out _);

    public static bool IsRealDirectory(string path)
    {
        if (!Win32.GetFileAttributesExW(LongPath.Prefix(path), 0, out var data)) return false;
        return (data.dwFileAttributes & Win32.FILE_ATTRIBUTE_DIRECTORY) != 0;
    }
}

/// <summary>Lỗi IO mang mã Win32 gốc.</summary>
public sealed class Win32IOException(int code, string path) : IOException(Win32Error.Describe(code) + ": " + path)
{
    public int Code { get; } = code;
    public string Path { get; } = path;
}
