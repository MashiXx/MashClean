using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;
using Microsoft.Win32.SafeHandles;

namespace CleanBoost.Core;

/// <summary>
/// Remover cấp thấp dùng chung cho app và helper quản trị (SYSTEM_DESIGN mục 4.2, 9.4).
/// Chống tấn công liên kết (TOCTOU) theo cách của Windows:
/// <list type="bullet">
/// <item>không bao giờ đi theo symlink/junction: liên kết chỉ bị xoá chính nó, không đụng đích;</item>
/// <item>mỗi thư mục trên đường đi được mở bằng <c>FILE_FLAG_OPEN_REPARSE_POINT</c> và giữ handle không chia sẻ quyền xoá,
/// nên trong lúc xoá không ai đổi tên hay thay nó bằng junction được;</item>
/// <item>kiểm tra lại PathPolicy trước khi xoá.</item>
/// </list>
/// </summary>
public static class SecureRemover
{
    private sealed class Outcome
    {
        public long Freed;
        public int Removed;
        public int Counter;
        public int? FirstError;
        public string? FirstErrorPath;
        public bool Stopped;
        /// <summary>Vùng cấm có thể nằm bên trong thư mục đang xoá: mục khớp thì giữ lại.</summary>
        public IReadOnlyList<Glob> Guard = [];
        public int Protected;

        public void Record(int code, string path)
        {
            if (FirstError is not null) return;
            FirstError = code;
            FirstErrorPath = path;
        }
    }

    /// <summary>Xoá một đường dẫn (hoặc chỉ nội dung bên trong nếu <paramref name="contentsOnly"/>), có kiểm tra PathPolicy.</summary>
    /// <param name="shouldContinue">Gọi định kỳ, trả <c>false</c> để dừng (huỷ).</param>
    /// <param name="progress">Báo tổng dung lượng đã giải phóng tăng dần.</param>
    public static RemoveResult Remove(
        string path,
        PathPolicy policy,
        bool contentsOnly = false,
        IReadOnlyCollection<string>? allowedRoots = null,
        bool checkOwnership = true,
        Func<bool>? shouldContinue = null,
        Action<long>? progress = null)
    {
        string canonical;
        try
        {
            canonical = policy.Check(path, contentsOnly ? PathPolicy.Intent.RemoveContents : PathPolicy.Intent.RemoveItem, allowedRoots, checkOwnership);
        }
        catch (PathPolicy.Violation)
        {
            if (!DirectoryReader.Exists(path)) return RemoveResult.Skipped(path, SkipReason.NotFound);
            return RemoveResult.Skipped(path, SkipReason.BlockedByPolicy);
        }

        var parent = Path.GetDirectoryName(canonical);
        if (string.IsNullOrEmpty(parent)) return RemoveResult.Skipped(path, SkipReason.BlockedByPolicy);
        using var parentHandle = OpenDirectoryNoFollow(parent, out var parentError);
        if (parentHandle is null)
        {
            return parentError == -1 ? RemoveResult.Skipped(path, SkipReason.BlockedByPolicy) : ResultFor(parentError, path);
        }

        var stat = DirectoryReader.Stat(canonical);
        if (stat is null) return RemoveResult.Skipped(path, SkipReason.NotFound);
        var entry = stat.Value;
        var cluster = ClusterSize(canonical);
        var outcome = new Outcome();
        var cont = shouldContinue ?? (() => true);
        if (entry.IsDirectory && !entry.IsLink) outcome.Guard = policy.ForbiddenGlobsWithin(canonical);

        if (entry.IsReparsePoint && entry.IsLink)
        {
            // Liên kết: xoá chính nó, không bao giờ đi theo (kể cả với deleteContents).
            RemoveLink(canonical, entry.IsDirectory, outcome);
        }
        else if (entry.IsDirectory && contentsOnly)
        {
            RemoveChildren(canonical, cluster, outcome, cont, progress);
        }
        else if (entry.IsDirectory)
        {
            RemoveChildren(canonical, cluster, outcome, cont, progress);
            if (!outcome.Stopped) RemoveEmptyDirectory(canonical, outcome);
        }
        else
        {
            // deleteContents nhắm vào một file thường: xoá chính file.
            RemoveFile(canonical, entry, cluster, outcome, progress);
        }

        // Giữ lại mục thuộc vùng cấm bên trong (vd *.pst, Login Data): báo là xoá một phần.
        if (outcome.Protected > 0)
        {
            Log.Info(LogCategory.Clean, $"Giữ lại {outcome.Protected} mục thuộc vùng cấm khi xoá");
            return RemoveResult.Failed(path, Win32.ERROR_DIR_NOT_EMPTY, Loc.F("Đã giữ lại %lld mục được bảo vệ bên trong", outcome.Protected), outcome.Freed);
        }
        if (outcome.FirstError is { } code)
        {
            if (outcome.Removed > 0 && !Win32Error.IsAccessDenied(code) && !Win32Error.IsInUse(code))
            {
                return RemoveResult.Failed(path, code, Loc.F("Xoá một phần: %@ tại %@", Win32Error.Describe(code), outcome.FirstErrorPath ?? ""), outcome.Freed);
            }
            return ResultFor(code, path) with { FreedBytes = outcome.Freed };
        }
        // Bị huỷ giữa chừng: không ghi "ok" vào lịch sử, nhưng vẫn tính phần đã giải phóng.
        if (outcome.Stopped) return RemoveResult.Failed(path, Win32.ERROR_CANCELLED, Loc.T("Đã dừng giữa chừng"), outcome.Freed);
        return RemoveResult.Ok(path, outcome.Freed);
    }

    /// <summary>Đổi mã lỗi Win32 thành kết quả theo bảng xử lý lỗi (mục 7.4).</summary>
    public static RemoveResult ResultFor(int code, string path)
    {
        if (Win32Error.IsNotFound(code)) return RemoveResult.Skipped(path, SkipReason.NotFound);
        if (Win32Error.IsInUse(code)) return RemoveResult.Skipped(path, SkipReason.InUse);
        return RemoveResult.Failed(path, code);
    }

    // MARK: Nội bộ

    /// <summary>
    /// Mở thư mục không theo liên kết, giữ không chia sẻ quyền xoá. Trả <c>null</c> và <paramref name="error"/> = -1 nếu
    /// đó là liên kết (không tin được), hoặc mã Win32 nếu không mở được.
    /// </summary>
    private static SafeFileHandle? OpenDirectoryNoFollow(string directory, out int error)
    {
        var h = Win32.CreateFileW(LongPath.Prefix(directory), Win32.FILE_LIST_DIRECTORY | Win32.FILE_READ_ATTRIBUTES | Win32.SYNCHRONIZE,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING,
            Win32.FILE_FLAG_BACKUP_SEMANTICS | Win32.FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            h.Dispose();
            return null;
        }
        if (!Win32.GetFileInformationByHandle(h, out var info)
            || (info.dwFileAttributes & Win32.FILE_ATTRIBUTE_DIRECTORY) == 0
            || (info.dwFileAttributes & Win32.FILE_ATTRIBUTE_REPARSE_POINT) != 0 && IsLinkDirectory(directory))
        {
            error = -1;
            h.Dispose();
            return null;
        }
        error = 0;
        return h;
    }

    private static bool IsLinkDirectory(string path) => DirectoryReader.Stat(path) is { IsLink: true } || DirectoryReader.Stat(path) is null;

    private static void RemoveChildren(string directory, long cluster, Outcome outcome, Func<bool> shouldContinue, Action<long>? progress)
    {
        using var handle = OpenDirectoryNoFollow(directory, out var error);
        if (handle is null)
        {
            if (error == -1) outcome.Record(Win32.ERROR_ACCESS_DENIED, directory);
            else if (!Win32Error.IsNotFound(error)) outcome.Record(error, directory);
            return;
        }
        List<DirEntry> children = [];
        try
        {
            DirectoryReader.Read(directory, children.Add);
        }
        catch (Win32IOException ex)
        {
            if (!Win32Error.IsNotFound(ex.Code)) outcome.Record(ex.Code, directory);
            return;
        }
        foreach (var child in children)
        {
            if (outcome.Counter++ % 100 == 0 && !shouldContinue())
            {
                outcome.Stopped = true;
                return;
            }
            var childPath = directory.TrimEnd('\\') + "\\" + child.Name;
            if (outcome.Guard.Count > 0 && outcome.Guard.Any(g => g.Matches(childPath)))
            {
                outcome.Protected++;
                continue;
            }
            if (child.IsReparsePoint && child.IsLink)
            {
                RemoveLink(childPath, child.IsDirectory, outcome);
            }
            else if (child.IsDirectory)
            {
                RemoveChildren(childPath, cluster, outcome, shouldContinue, progress);
                if (outcome.Stopped) return;
                RemoveEmptyDirectory(childPath, outcome);
            }
            else
            {
                RemoveFile(childPath, child, cluster, outcome, progress);
            }
        }
    }

    private static void RemoveFile(string path, DirEntry entry, long cluster, Outcome outcome, Action<long>? progress)
    {
        var target = LongPath.Prefix(path);
        if (!Win32.DeleteFileW(target))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == Win32.ERROR_ACCESS_DENIED && entry.IsReadOnly && Win32.SetFileAttributesW(target, Win32.FILE_ATTRIBUTE_NORMAL) && Win32.DeleteFileW(target))
            {
                err = 0;
            }
            if (err != 0)
            {
                if (!Win32Error.IsNotFound(err)) outcome.Record(err, path);
                return;
            }
        }
        outcome.Freed += entry.AllocatedSize(cluster);
        outcome.Removed++;
        if (outcome.Removed % 200 == 0) progress?.Invoke(outcome.Freed);
    }

    private static void RemoveEmptyDirectory(string path, Outcome outcome)
    {
        var target = LongPath.Prefix(path);
        if (Win32.RemoveDirectoryW(target))
        {
            outcome.Removed++;
            return;
        }
        var err = Marshal.GetLastWin32Error();
        if (err == Win32.ERROR_ACCESS_DENIED && DirectoryReader.Stat(path) is { IsReadOnly: true }
            && Win32.SetFileAttributesW(target, Win32.FILE_ATTRIBUTE_DIRECTORY) && Win32.RemoveDirectoryW(target))
        {
            outcome.Removed++;
            return;
        }
        err = err == 0 ? Marshal.GetLastWin32Error() : err;
        if (!Win32Error.IsNotFound(err)) outcome.Record(err, path);
    }

    private static void RemoveLink(string path, bool isDirectory, Outcome outcome)
    {
        var target = LongPath.Prefix(path);
        var ok = isDirectory ? Win32.RemoveDirectoryW(target) : Win32.DeleteFileW(target);
        if (ok)
        {
            outcome.Removed++;
            return;
        }
        var err = Marshal.GetLastWin32Error();
        if (!Win32Error.IsNotFound(err)) outcome.Record(err, path);
    }

    private static readonly ConcurrentDictionary<string, long> s_clusters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kích thước cluster của volume chứa đường dẫn (mặc định 4096).</summary>
    public static long ClusterSize(string path)
    {
        var root = PathPolicy.VolumeRoot(path);
        if (root is null) return 4096;
        return s_clusters.GetOrAdd(root, r =>
            Win32.GetDiskFreeSpaceW(r, out var spc, out var bps, out _, out _) && spc > 0 && bps > 0 ? (long)spc * bps : 4096);
    }
}
