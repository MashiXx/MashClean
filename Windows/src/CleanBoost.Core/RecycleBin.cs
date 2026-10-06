using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;

namespace CleanBoost.Core;

/// <summary>
/// Thùng rác của Windows (Recycle Bin), tương đương <c>FileManager.trashItem</c> của bản Mac (mục 7.3, 15.3).
/// Sau khi chuyển vào Thùng rác, tìm lại cặp <c>$I</c>/<c>$R</c> trong <c>&lt;ổ&gt;:\$Recycle.Bin\&lt;SID&gt;</c> để biết
/// đường dẫn trong Thùng rác (<c>trashedPath</c>) — màn Lịch sử dùng nó để khôi phục.
/// </summary>
public static class RecycleBin
{
    public enum TrashError
    {
        /// <summary>Ổ không có Thùng rác (ổ mạng, ổ chỉ đọc, thiết bị tháo rời không hỗ trợ).</summary>
        Unavailable,
        /// <summary>Người dùng huỷ hộp thoại cảnh báo của Windows (ví dụ file quá lớn so với Thùng rác).</summary>
        Cancelled,
        Failed,
    }

    public sealed record TrashResult(bool Success, string? TrashedPath, TrashError? Error, int Code);

    private static readonly string s_userSid = WindowsIdentity.GetCurrent().User?.Value ?? "";

    /// <summary>Ổ chứa đường dẫn có Thùng rác dùng được không.</summary>
    public static bool IsAvailable(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var type = Win32.GetDriveTypeW(root);
        if (type is not (Win32.DRIVE_FIXED or Win32.DRIVE_REMOVABLE)) return false;
        if (PathPolicy.IsOnReadOnlyVolume(path)) return false;
        var info = new Win32.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        return Win32.SHQueryRecycleBinW(root, ref info) == 0;
    }

    /// <summary>
    /// Chuyển một mục vào Thùng rác. Dùng <c>FOF_WANTNUKEWARNING</c>: nếu Windows định xoá vĩnh viễn (mục quá lớn so với
    /// Thùng rác) thì hộp thoại cảnh báo của hệ thống hiện ra thay vì xoá âm thầm.
    /// </summary>
    public static TrashResult MoveToTrash(string path)
    {
        if (!IsAvailable(path)) return new TrashResult(false, null, TrashError.Unavailable, 0);
        // Thao tác shell nên chạy trên luồng STA (luồng thread pool là MTA).
        return RunSta(() => MoveToTrashCore(path));
    }

    private static TrashResult MoveToTrashCore(string path)
    {
        var started = DateTime.UtcNow.AddSeconds(-2);
        var op = new Win32.SHFILEOPSTRUCTW
        {
            wFunc = Win32.FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = (ushort)(Win32.FOF_ALLOWUNDO | Win32.FOF_NOCONFIRMATION | Win32.FOF_SILENT | Win32.FOF_NOERRORUI | Win32.FOF_WANTNUKEWARNING),
        };
        var rc = Win32.SHFileOperationW(ref op);
        if (op.fAnyOperationsAborted) return new TrashResult(false, null, TrashError.Cancelled, Win32.ERROR_CANCELLED);
        if (rc != 0) return new TrashResult(false, null, TrashError.Failed, MapShellError(rc));
        if (DirectoryReader.Exists(path)) return new TrashResult(false, null, TrashError.Failed, Win32.ERROR_ACCESS_DENIED);
        return new TrashResult(true, FindTrashed(path, started), null, 0);
    }

    /// <summary>Mã lỗi SHFileOperation (một số là mã riêng của shell) → mã Win32 gần nhất.</summary>
    private static int MapShellError(int rc) => rc switch
    {
        0x78 => Win32.ERROR_ACCESS_DENIED,      // DE_ACCESSDENIEDSRC
        0x7C => Win32.ERROR_PATH_NOT_FOUND,     // DE_INVALIDFILES
        0x402 => Win32.ERROR_PATH_NOT_FOUND,    // lỗi không rõ trên đường dẫn
        0x10000 => Win32.ERROR_IO_DEVICE,
        0x75 => Win32.ERROR_CANCELLED,          // DE_OPCANCELLED
        _ => rc < 0x70 ? rc : Win32.ERROR_ACCESS_DENIED,
    };

    /// <summary>Thư mục Thùng rác của người dùng hiện tại trên ổ chứa đường dẫn.</summary>
    public static string? BinDirectory(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || s_userSid.Length == 0) return null;
        return Path.Combine(root, "$Recycle.Bin", s_userSid);
    }

    /// <summary>Một mục trong Thùng rác, đọc từ file <c>$I</c>.</summary>
    public sealed record Entry(string InfoPath, string DataPath, string OriginalPath, long Size, DateTime DeletedUtc);

    /// <summary>Tìm mục vừa chuyển vào Thùng rác: file <c>$I</c> mới nhất có đường dẫn gốc trùng.</summary>
    public static string? FindTrashed(string originalPath, DateTime notBeforeUtc)
    {
        var bin = BinDirectory(originalPath);
        if (bin is null) return null;
        Entry? best = null;
        foreach (var e in Entries(bin))
        {
            if (!e.OriginalPath.Equals(originalPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.DeletedUtc < notBeforeUtc) continue;
            if (best is null || e.DeletedUtc > best.DeletedUtc) best = e;
        }
        return best?.DataPath;
    }

    public static IEnumerable<Entry> Entries(string binDirectory)
    {
        List<DirEntry> files;
        try
        {
            files = [];
            DirectoryReader.Read(binDirectory, files.Add);
        }
        catch (IOException)
        {
            yield break;
        }
        foreach (var f in files)
        {
            if (f.IsDirectory || !f.Name.StartsWith("$I", StringComparison.OrdinalIgnoreCase)) continue;
            var info = Path.Combine(binDirectory, f.Name);
            var parsed = ParseInfo(info);
            if (parsed is null) continue;
            var data = Path.Combine(binDirectory, "$R" + f.Name[2..]);
            yield return parsed with { DataPath = data };
        }
    }

    /// <summary>
    /// Đọc file <c>$I</c>. Phiên bản 1 (Vista–8.1): header 8 + size 8 + FILETIME 8 + đường dẫn 520 byte.
    /// Phiên bản 2 (Windows 10+): header 8 + size 8 + FILETIME 8 + độ dài 4 (ký tự, gồm NUL) + đường dẫn UTF-16.
    /// </summary>
    public static Entry? ParseInfo(string infoPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(infoPath);
            return ParseInfo(bytes, infoPath);
        }
        catch
        {
            return null;
        }
    }

    public static Entry? ParseInfo(byte[] bytes, string infoPath)
    {
        if (bytes.Length < 24) return null;
        var version = BitConverter.ToInt64(bytes, 0);
        var size = BitConverter.ToInt64(bytes, 8);
        var deleted = DateTime.FromFileTimeUtc(Math.Max(0, BitConverter.ToInt64(bytes, 16)));
        string original;
        if (version == 2)
        {
            if (bytes.Length < 28) return null;
            var chars = BitConverter.ToInt32(bytes, 24);
            if (chars <= 0 || 28 + chars * 2 > bytes.Length) return null;
            original = Encoding.Unicode.GetString(bytes, 28, chars * 2).TrimEnd('\0');
        }
        else if (version == 1)
        {
            var len = Math.Min(520, bytes.Length - 24);
            original = Encoding.Unicode.GetString(bytes, 24, len);
            var nul = original.IndexOf('\0');
            if (nul >= 0) original = original[..nul];
        }
        else
        {
            return null;
        }
        return new Entry(infoPath, "", original, size, deleted);
    }

    public enum RestoreError { NotRestorable, TrashedItemMissing, DestinationExists, Failed }

    /// <summary>Chuyển mục từ Thùng rác về vị trí cũ, xoá file <c>$I</c> đi kèm.</summary>
    public static RestoreError? Restore(string trashedPath, string originalPath, out string? message)
    {
        message = null;
        var name = Path.GetFileName(trashedPath);
        if (!name.StartsWith("$R", StringComparison.OrdinalIgnoreCase)) return RestoreError.NotRestorable;
        if (!DirectoryReader.Exists(trashedPath)) return RestoreError.TrashedItemMissing;
        if (DirectoryReader.Exists(originalPath)) return RestoreError.DestinationExists;
        try
        {
            var dir = Path.GetDirectoryName(originalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (DirectoryReader.IsRealDirectory(trashedPath)) Directory.Move(trashedPath, originalPath);
            else File.Move(trashedPath, originalPath);
            var info = Path.Combine(Path.GetDirectoryName(trashedPath)!, "$I" + name[2..]);
            try { File.Delete(info); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return RestoreError.Failed;
        }
    }

    /// <summary>Tổng dung lượng và số mục trong Thùng rác (mọi ổ khi <paramref name="root"/> là <c>null</c>).</summary>
    public static (long Size, long Count)? Query(string? root = null)
    {
        var info = new Win32.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        return Win32.SHQueryRecycleBinW(root, ref info) == 0 ? (info.i64Size, info.i64NumItems) : null;
    }

    /// <summary>Đổ Thùng rác của một ổ (hoặc mọi ổ) không hỏi lại, không hiện tiến độ.</summary>
    public static int Empty(string? root = null)
    {
        var hr = RunSta(() => Win32.SHEmptyRecycleBinW(IntPtr.Zero, root, Win32.SHERB_NOCONFIRMATION | Win32.SHERB_NOPROGRESSUI | Win32.SHERB_NOSOUND));
        // E_UNEXPECTED (0x8000FFFF) khi Thùng rác đã rỗng: coi như thành công.
        return hr == 0 || hr == unchecked((int)0x8000FFFF) ? 0 : hr;
    }

    /// <summary>Chạy trên luồng STA riêng nếu luồng hiện tại không phải STA (API shell).</summary>
    private static T RunSta<T>(Func<T> body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return body();
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); } catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "RecycleBin" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result;
    }

    /// <summary>Các ổ có Thùng rác dùng được (ổ cố định và ổ tháo rời).</summary>
    public static IReadOnlyList<string> Drives() =>
        DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => d.RootDirectory.FullName)
            .Where(r => Query(r) is not null)
            .ToArray();
}
