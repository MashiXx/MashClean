using System.Collections.Concurrent;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Engine.Scanning;

namespace CleanBoost.Features.LargeOldFiles;

/// <summary>
/// Phạm vi "nội dung của người dùng" dùng chung cho File lớn &amp; cũ và File trùng lặp: hồ sơ người dùng (trừ AppData)
/// cộng các thư mục chuẩn đã chuyển ra ngoài hồ sơ (vd Documents trên ổ D), cộng thư mục người dùng tự thêm.
/// </summary>
public static class UserContentScope
{
    /// <summary>Thư mục quét mặc định: hồ sơ người dùng và các thư mục chuẩn nằm ngoài hồ sơ.</summary>
    public static IReadOnlyList<string> DefaultRoots(string home)
    {
        var roots = new List<string> { home };
        foreach (var folder in KnownPaths.UserKnownFolders)
        {
            if (string.IsNullOrEmpty(folder) || roots.Any(r => PathPolicy.IsSameOrInside(folder, r))) continue;
            roots.Add(folder);
        }
        return roots;
    }

    /// <summary>
    /// Thư mục không bao giờ đi vào khi duyệt từ một gốc khác: AppData (thuộc Rác hệ thống) và vùng hệ thống.
    /// Người dùng vẫn chọn được chính thư mục đó (hoặc thư mục con) làm gốc quét.
    /// </summary>
    public static IReadOnlyList<string> Exclusions(string home) =>
        new[] { home + @"\AppData", KnownPaths.Windows, KnownPaths.ProgramFiles, KnownPaths.ProgramFilesX86, KnownPaths.ProgramData }
            .Where(p => p.Length > 3).Select(p => p.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsExcluded(string directory, IReadOnlyList<string> exclusions)
    {
        foreach (var e in exclusions)
        {
            if (directory.Length == e.Length && directory.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Tên ẩn kiểu Unix (<c>.gradle</c>) hoặc thuộc tính Hidden của Windows.</summary>
    public static bool IsHidden(in DirEntry entry) => entry.IsHidden || entry.Name.StartsWith('.');

    /// <summary>
    /// Chuẩn hoá danh sách gốc: bỏ trùng; bỏ gốc nằm trong gốc khác (trừ khi nằm trong vùng loại trừ, vì gốc cha sẽ không đi vào đó);
    /// bỏ ổ mạng, vùng cấm của PathPolicy, thư mục không tồn tại. Gốc bị bỏ vì lý do an toàn thì báo cảnh báo.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> roots, PathPolicy policy, IReadOnlyList<string> exclusions, string taskID,
        List<ScanWarning> warnings)
    {
        var cleaned = new List<string>();
        foreach (var raw in roots)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = KnownPaths.Long(raw.Trim());
            path = path.Length <= 3 ? path.TrimEnd('\\') + "\\" : path.TrimEnd('\\');
            if (cleaned.Any(c => c.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            if (PathPolicy.IsNetworkPath(path) || IsRemoteDrive(path))
            {
                warnings.Add(new ScanWarning(taskID, ScanWarningKind.Skipped, new PathPolicy.Violation(PathPolicy.ViolationKind.NetworkPath, path).LocalizedDescription));
                continue;
            }
            if (policy.IsForbidden(path) is { Kind: PathPolicy.ViolationKind.ForbiddenZone } v)
            {
                warnings.Add(new ScanWarning(taskID, ScanWarningKind.Skipped, v.LocalizedDescription));
                continue;
            }
            if (!DirectoryReader.IsRealDirectory(path)) continue;
            cleaned.Add(path);
        }
        // Gốc ngắn trước: gốc con bị gốc cha bao phủ thì bỏ, trừ khi nằm trong vùng loại trừ của gốc cha.
        var kept = new List<string>();
        foreach (var root in cleaned.OrderBy(r => r.Length))
        {
            var covered = kept.Any(k => PathPolicy.IsSameOrInside(root, k)
                && !exclusions.Any(e => PathPolicy.IsSameOrInside(e, k) && PathPolicy.IsSameOrInside(root, e)));
            if (!covered) kept.Add(root);
        }
        // Giữ thứ tự người dùng nhập.
        return cleaned.Where(kept.Contains).ToList();
    }

    private static bool IsRemoteDrive(string path) =>
        path.Length >= 2 && path[1] == ':' && Win32.GetDriveTypeW(path[..2] + "\\") == Win32.DRIVE_REMOTE;

    /// <summary>
    /// Số luồng duyệt: mỗi ổ HDD một luồng (tránh nhảy đầu đọc); có SSD thì 4–8 luồng (đọc metadata chủ yếu là chờ I/O).
    /// </summary>
    public static int WalkConcurrency(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return 1;
        if (roots.All(r => FileSystemService.Medium(r) == StorageMedium.Rotational))
            return Math.Max(1, roots.Select(VolumeKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        return Math.Clamp(Environment.ProcessorCount, 4, 8);
    }

    /// <summary>Khoá volume rẻ (ký tự ổ), không gọi hệ thống.</summary>
    public static string VolumeKey(string path) => path.Length >= 2 && path[1] == ':' ? path[..2].ToUpperInvariant() : path;
}

/// <summary>
/// Duyệt song song nhiều cây thư mục bằng hàng đợi dùng chung: mỗi luồng lấy một thư mục, đọc một lần bằng
/// <c>FindFirstFileEx</c> (LARGE_FETCH), đẩy thư mục con vào hàng đợi. Thư mục lớn không dồn vào một luồng như khi chia theo gốc.
/// </summary>
public static class ParallelTreeWalker
{
    /// <summary>Một thư mục cần đọc: chỉ số gốc, độ sâu (gốc = 0), đang ở trong vùng ẩn hay không.</summary>
    public readonly record struct Frame(string Path, int RootIndex, int Depth, bool Hidden);

    public sealed record Stats(long Directories, long Entries, IReadOnlyDictionary<int, int> FailedRoots);

    /// <param name="onDirectory">Quyết định đi vào thư mục con: trả về frame mới, hoặc <c>null</c> để bỏ qua.</param>
    /// <param name="onFile">Gọi cho mỗi mục không phải thư mục (song song từ nhiều luồng).</param>
    /// <param name="onVisited">Cộng dồn số mục đã duyệt (gọi theo lô).</param>
    /// <param name="onProgress">Ước lượng 0…1, không giảm: theo số thư mục của lần duyệt trước (cùng <paramref name="progressKey"/> và gốc),
    /// lần đầu thì lấy giá trị nhỏ hơn giữa "nhánh cấp 1 đã xong" và "thư mục đã đọc / đã phát hiện".</param>
    /// <summary>Số thư mục của lần duyệt trước theo bộ gốc, để lần sau báo tiến độ sát thực tế (chỉ trong phiên chạy app).</summary>
    private static readonly ConcurrentDictionary<string, long> s_lastDirectoryCount = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Item(Frame Frame, int Top);

    public static Stats Run(IReadOnlyList<Frame> starts, int workers, Func<Frame, DirEntry, string, Frame?> onDirectory,
        Action<Frame, DirEntry, string> onFile, CancellationToken ct, Action<int>? onVisited = null, Action<double>? onProgress = null,
        string progressKey = "")
    {
        if (starts.Count == 0) return new Stats(0, 0, new Dictionary<int, int>());
        workers = Math.Max(1, workers);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;
        var stack = new ConcurrentStack<Item>(starts.Reverse().Select(f => new Item(f, -1)));
        using var available = new SemaphoreSlim(starts.Count);
        long outstanding = starts.Count;
        long discovered = starts.Count;
        long directories = 0;
        long entries = 0;
        var failed = new ConcurrentDictionary<int, int>();
        // Tiến độ: mỗi thư mục con cấp 1 của gốc là một "nhánh"; đếm nhánh đã duyệt xong (ước lượng thận trọng),
        // hoặc dùng số thư mục của lần duyệt trước nếu có.
        var calibrationKey = progressKey + "|" + string.Join("|", starts.Select(f => f.Path));
        var previousTotal = s_lastDirectoryCount.GetValueOrDefault(calibrationKey);
        var topOutstanding = new ConcurrentDictionary<int, int>();
        var topCount = 0;
        var topDone = 0;
        var bestFraction = 0.0;
        var progressLock = new object();
        var lastReport = 0L;

        void Worker()
        {
            var pendingVisits = 0;
            try
            {
                while (true)
                {
                    available.Wait(token);
                    if (Interlocked.Read(ref outstanding) == 0) return;
                    if (!stack.TryPop(out var item)) continue;
                    var frame = item.Frame;
                    try
                    {
                        var dir = frame.Path.TrimEnd('\\');
                        var n = 0;
                        DirectoryReader.Read(dir, e =>
                        {
                            if ((++n & 511) == 0) token.ThrowIfCancellationRequested();
                            pendingVisits++;
                            var full = dir + "\\" + e.Name;
                            if (e.IsDirectory)
                            {
                                if (onDirectory(frame, e, full) is { } child)
                                {
                                    int top;
                                    if (item.Top < 0)
                                    {
                                        top = Interlocked.Increment(ref topCount) - 1;
                                        topOutstanding[top] = 1;
                                    }
                                    else
                                    {
                                        top = item.Top;
                                        topOutstanding.AddOrUpdate(top, 1, (_, v) => v + 1);
                                    }
                                    Interlocked.Increment(ref outstanding);
                                    Interlocked.Increment(ref discovered);
                                    stack.Push(new Item(child, top));
                                    available.Release();
                                }
                            }
                            else
                            {
                                onFile(frame, e, full);
                            }
                        });
                    }
                    catch (IOException ex)
                    {
                        // Thư mục không đọc được: bỏ qua; ghi lại nếu là chính thư mục gốc.
                        if (frame.Depth == 0) failed[frame.RootIndex] = ex is Win32IOException w ? w.Code : Win32.ERROR_ACCESS_DENIED;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (frame.Depth == 0) failed[frame.RootIndex] = Win32.ERROR_ACCESS_DENIED;
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref directories);
                        if (item.Top >= 0 && topOutstanding.AddOrUpdate(item.Top, 0, (_, v) => v - 1) == 0) Interlocked.Increment(ref topDone);
                        if (pendingVisits >= 512)
                        {
                            Interlocked.Add(ref entries, pendingVisits);
                            onVisited?.Invoke(pendingVisits);
                            pendingVisits = 0;
                        }
                        if (onProgress is not null && Environment.TickCount64 - Interlocked.Read(ref lastReport) > 100)
                        {
                            Interlocked.Exchange(ref lastReport, Environment.TickCount64);
                            double f;
                            if (previousTotal > 0)
                            {
                                f = Math.Min(0.99, (double)done / previousTotal);
                            }
                            else
                            {
                                var ratio = (double)done / Math.Max(1, Interlocked.Read(ref discovered));
                                var tops = Volatile.Read(ref topCount);
                                var branches = tops == 0 ? ratio : (double)Volatile.Read(ref topDone) / tops;
                                f = Math.Min(0.95, Math.Min(ratio, branches));
                            }
                            double report;
                            lock (progressLock)
                            {
                                bestFraction = Math.Max(bestFraction, f);
                                report = bestFraction;
                            }
                            onProgress(report);
                        }
                        // Hết việc: đánh thức mọi luồng để thoát.
                        if (Interlocked.Decrement(ref outstanding) == 0) available.Release(workers);
                    }
                }
            }
            catch
            {
                linked.Cancel();
                throw;
            }
            finally
            {
                if (pendingVisits > 0)
                {
                    Interlocked.Add(ref entries, pendingVisits);
                    onVisited?.Invoke(pendingVisits);
                }
            }
        }

        var tasks = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(Worker, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex)
        {
            ct.ThrowIfCancellationRequested();
            var first = ex.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) ?? ex.InnerException;
            if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
            throw;
        }
        ct.ThrowIfCancellationRequested();
        s_lastDirectoryCount[calibrationKey] = Interlocked.Read(ref directories);
        return new Stats(Interlocked.Read(ref directories), Interlocked.Read(ref entries), failed);
    }
}
