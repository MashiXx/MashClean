using CleanBoost.Core;
using CleanBoost.Core.Native;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Ipc;
using CleanBoost.Engine.Nodes;

namespace CleanBoost.Engine.Cleaning;

/// <summary>Ngữ cảnh truyền cho remover.</summary>
public sealed class RemoveContext
{
    public required FileSystemService FileSystem { get; init; }
    public IHelperClient? Helper { get; init; }
    public required PathPolicy Policy { get; init; }
    public bool DryRun { get; init; }
    /// <summary>Báo dung lượng giải phóng tăng thêm của mục đang xử lý.</summary>
    public Action<string, long> Progress { get; init; } = (_, _) => { };
    /// <summary>Ổ không có Thùng rác (ổ mạng, ổ ngoài): hỏi người dùng có muốn xoá vĩnh viễn không (mục 7.4).</summary>
    public Func<string, Task<bool>> ConfirmPermanentDelete { get; init; } = _ => Task.FromResult(false);
    public CancellationToken Cancellation { get; init; }
}

/// <summary>Interface Remover chung + remover chuyên biệt (mục 7.3).</summary>
public interface IRemover
{
    string Id { get; }
    Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context);
}

/// <summary>File và thư mục thông thường: chuyển vào Thùng rác hoặc xoá an toàn.</summary>
public sealed class FileRemover : IRemover
{
    public string Id => RemoverIDs.File;

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        var results = new List<RemoveResult>();
        foreach (var item in items)
        {
            if (context.Cancellation.IsCancellationRequested) break;
            if (item.Path is not { } path)
            {
                results.Add(RemoveResult.Failed("", Win32.ERROR_INVALID_PARAMETER, Loc.T("Mục không có đường dẫn")));
                continue;
            }
            results.Add(await RemoveOneAsync(item, path, context).ConfigureAwait(false));
        }
        return results;
    }

    internal static async Task<RemoveResult> RemoveOneAsync(CleanPlan.Item item, string path, RemoveContext context)
    {
        if (!context.FileSystem.Exists(path)) return RemoveResult.Skipped(path, SkipReason.NotFound);
        if (context.DryRun) return RemoveResult.Skipped(path, SkipReason.DryRun, item.ExpectedSize.Bytes);
        var roots = item.AllowedRoots.Count == 0 ? null : item.AllowedRoots;

        switch (item.Strategy.Kind)
        {
            case RemovalKind.MoveToTrash:
            {
                if (context.Policy.Validate(path, allowedRoots: roots) is not null) return RemoveResult.Skipped(path, SkipReason.BlockedByPolicy);
                var r = await Task.Run(() => RecycleBin.MoveToTrash(path)).ConfigureAwait(false);
                if (r.Success)
                {
                    context.Progress(path, item.ExpectedSize.Bytes);
                    return RemoveResult.Ok(path, item.ExpectedSize.Bytes, r.TrashedPath);
                }
                if (r.Error == RecycleBin.TrashError.Unavailable)
                {
                    if (!await context.ConfirmPermanentDelete(path).ConfigureAwait(false)) return RemoveResult.Skipped(path, SkipReason.UserDeclined);
                    return SecureDelete(item, path, contentsOnly: false, context);
                }
                if (r.Error == RecycleBin.TrashError.Cancelled) return RemoveResult.Skipped(path, SkipReason.UserDeclined);
                return SecureRemover.ResultFor(r.Code, path);
            }
            case RemovalKind.Delete:
                return SecureDelete(item, path, contentsOnly: false, context);
            case RemovalKind.DeleteContents:
                return SecureDelete(item, path, contentsOnly: true, context);
            default:
                return RemoveResult.Skipped(path, SkipReason.Unsupported);
        }
    }

    private static RemoveResult SecureDelete(CleanPlan.Item item, string path, bool contentsOnly, RemoveContext context)
    {
        long last = 0;
        return SecureRemover.Remove(path, context.Policy, contentsOnly, item.AllowedRoots.Count == 0 ? null : item.AllowedRoots,
            shouldContinue: () => !context.Cancellation.IsCancellationRequested,
            progress: freed =>
            {
                context.Progress(path, freed - last);
                last = freed;
            });
    }
}

/// <summary>Thư mục hệ thống (<c>%WINDIR%\Temp</c>, cache Windows Update...): gửi sang helper quản trị theo batch.</summary>
public sealed class PrivilegedFileRemover : IRemover
{
    public const int BatchSize = 200;
    public string Id => RemoverIDs.PrivilegedFile;

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        if (context.DryRun) return items.Select(i => RemoveResult.Skipped(i.Path ?? "", SkipReason.DryRun, i.ExpectedSize.Bytes)).ToList();
        if (context.Helper is null) return items.Select(i => RemoveResult.Failed(i.Path ?? "", Win32.ERROR_ACCESS_DENIED, Loc.T("Chưa có helper quản trị"))).ToList();
        var results = new List<RemoveResult>();
        foreach (var group in items.GroupBy(i => i.Strategy.Kind == RemovalKind.DeleteContents ? RemoveMode.DeleteContents : RemoveMode.Delete))
        {
            foreach (var chunk in group.Chunk(BatchSize))
            {
                if (context.Cancellation.IsCancellationRequested) break;
                var paths = chunk.Select(i => i.Path ?? "").ToArray();
                var roots = chunk.Select(i => i.AllowedRoots.Count > 0 ? i.AllowedRoots.ToArray() : null).ToArray();
                try
                {
                    var r = await context.Helper.RemoveItemsAsync(paths, roots, group.Key, context.Cancellation).ConfigureAwait(false);
                    foreach (var x in r) context.Progress(x.Path, x.FreedBytes);
                    results.AddRange(r);
                }
                catch (Exception ex)
                {
                    // Huỷ hoặc mất kết nối giữa batch: helper có thể đã xoá một phần. Kiểm tra lại thay vì ghi cả batch là lỗi.
                    var message = ex is OperationCanceledException ? Loc.T("Đã dừng giữa chừng") : ex.Message;
                    results.AddRange(paths.Select(p => !string.IsNullOrEmpty(p) && !context.FileSystem.Exists(p)
                        ? RemoveResult.Skipped(p, SkipReason.NotFound)
                        : RemoveResult.Failed(p, ex is OperationCanceledException ? Win32.ERROR_CANCELLED : Win32.ERROR_IO_DEVICE, message)));
                }
            }
        }
        // Giữ thứ tự khớp với danh sách đầu vào.
        var byPath = results.GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return items.Select(i => byPath.GetValueOrDefault(i.Path ?? "") ?? RemoveResult.Failed(i.Path ?? "", Win32.ERROR_IO_DEVICE, Loc.T("Helper không trả kết quả"))).ToList();
    }
}

/// <summary>Đổ Thùng rác của từng ổ bằng <c>SHEmptyRecycleBin</c> (không đụng trực tiếp vào <c>$Recycle.Bin</c>).</summary>
public sealed class RecycleBinRemover : IRemover
{
    public string Id => RemoverIDs.RecycleBin;

    public Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        var results = new List<RemoveResult>();
        foreach (var item in items)
        {
            var drive = (item.Virtual as RecycleBinItem)?.Drive;
            var label = item.Title;
            if (context.DryRun)
            {
                results.Add(RemoveResult.Skipped(label, SkipReason.DryRun, item.ExpectedSize.Bytes));
                continue;
            }
            var before = RecycleBin.Query(drive)?.Size ?? item.ExpectedSize.Bytes;
            var hr = RecycleBin.Empty(drive);
            if (hr == 0)
            {
                context.Progress(label, before);
                results.Add(RemoveResult.Ok(label, before));
            }
            else
            {
                results.Add(RemoveResult.Failed(label, hr & 0xFFFF));
            }
        }
        return Task.FromResult<IReadOnlyList<RemoveResult>>(results);
    }
}

/// <summary>Docker: không xoá file ổ ảo trực tiếp; chạy <c>docker system prune -f</c> khi người dùng chủ động chọn.</summary>
public sealed class DockerRemover : IRemover
{
    public string Id => RemoverIDs.Docker;

    public static string? DockerPath
    {
        get
        {
            string[] candidates =
            [
                Path.Combine(KnownPaths.ProgramFiles, "Docker", "Docker", "resources", "bin", "docker.exe"),
                Path.Combine(KnownPaths.UserHome, ".docker", "bin", "docker.exe"),
                Path.Combine(KnownPaths.LocalAppData, "Programs", "Rancher Desktop", "resources", "resources", "win32", "bin", "docker.exe"),
            ];
            return candidates.FirstOrDefault(File.Exists);
        }
    }

    public async Task<IReadOnlyList<RemoveResult>> RemoveAsync(IReadOnlyList<CleanPlan.Item> items, RemoveContext context)
    {
        const string label = "docker system prune";
        if (DockerPath is not { } docker) return items.Select(_ => RemoveResult.Failed("docker", Win32.ERROR_FILE_NOT_FOUND, Loc.T("Không tìm thấy docker CLI"))).ToList();
        if (context.DryRun) return items.Select(_ => RemoveResult.Skipped(label, SkipReason.DryRun)).ToList();
        try
        {
            var output = await ProcessRunner.RunAsync(docker, ["system", "prune", "-f"], TimeSpan.FromMinutes(10), context.Cancellation).ConfigureAwait(false);
            var freed = ParseReclaimed(output.StdOut);
            return [output.Succeeded ? RemoveResult.Ok(label, freed) : RemoveResult.Failed(label, output.ExitCode, output.StdErr.Trim())];
        }
        catch (Exception ex)
        {
            return [RemoveResult.Failed(label, Win32.ERROR_IO_DEVICE, ex.Message)];
        }
    }

    /// <summary>Đọc dòng "Total reclaimed space: 1.2GB".</summary>
    public static long ParseReclaimed(string output)
    {
        var line = output.Split('\n').FirstOrDefault(l => l.Contains("reclaimed space", StringComparison.OrdinalIgnoreCase));
        var value = line?.Split(':').LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(value)) return 0;
        (string Unit, double Mult)[] units = [("TB", 1e12), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("KB", 1e3), ("B", 1)];
        foreach (var (unit, mult) in units)
        {
            if (value.EndsWith(unit, StringComparison.Ordinal) &&
                double.TryParse(value[..^unit.Length], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
                return (long)(n * mult);
        }
        return 0;
    }
}
