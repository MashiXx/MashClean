using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Security.Cryptography;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Engine.FileSystem;
using CleanBoost.Features.LargeOldFiles;

namespace CleanBoost.Features.Duplicates;

/// <summary>Một file ứng viên khi tìm trùng lặp (DuplicateCandidate của bản Mac).</summary>
public sealed record DuplicateCandidate(string Path, long Size, long AllocatedSize, DateTime ModifiedUtc, DateTime CreatedUtc, int RootIndex)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Một file trong nhóm trùng lặp cuối cùng. <see cref="IsLinked"/>: file còn hard link khác (số link &gt; 1) nên xoá đường dẫn này
/// không giải phóng gì — vai trò như clone APFS của bản Mac.
/// </summary>
public sealed record DuplicateFile(DuplicateCandidate Candidate, bool IsLinked = false)
{
    public string Path => Candidate.Path;
}

public sealed record DuplicateGroup(long Size, string Hash, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Dung lượng lãng phí: tổng các bản trừ một bản giữ lại, không tính bản có hard link (công thức của bản Mac).</summary>
    public long WastedBytes
    {
        get
        {
            var real = Files.Where(f => !f.IsLinked).Select(f => f.Candidate.AllocatedSize).ToList();
            var keep = Files.Any(f => f.IsLinked) ? 0 : real.DefaultIfEmpty(0).Max();
            return Math.Max(0, real.Sum() - keep);
        }
    }

    /// <summary>Số bản mà xoá thì giải phóng được dung lượng (các bản có hard link tính chung là một).</summary>
    public int IndependentCopies => Files.Count(f => !f.IsLinked) + (Files.Any(f => f.IsLinked) ? 1 : 0);
}

/// <summary>Các bước tìm trùng lặp (DuplicateFinder.Stage của bản Mac; bước 5 thay "clone APFS" bằng kiểm tra lại từng bản).</summary>
public enum DuplicateStage { Collecting, GroupingBySize, QuickHash, FullHash, Verifying, Done }

public static class DuplicateStages
{
    public static string Title(this DuplicateStage stage) => stage switch
    {
        DuplicateStage.Collecting => Loc.T("Liệt kê file"),
        DuplicateStage.GroupingBySize => Loc.T("Nhóm theo dung lượng"),
        DuplicateStage.QuickHash => Loc.T("Băm nhanh đầu và cuối file"),
        DuplicateStage.FullHash => Loc.T("Băm toàn bộ (SHA-256)"),
        DuplicateStage.Verifying => Loc.T("Kiểm tra lại từng bản"),
        _ => Loc.T("Xong"),
    };

    public static int Step(this DuplicateStage stage) => (int)stage + 1;
}

/// <summary>Tiến độ theo bước. Băm toàn bộ tính theo số byte đã đọc để vòng tiến độ chạy đều cả khi gặp file rất lớn.</summary>
public readonly record struct DuplicateProgress(DuplicateStage Stage, int Processed, int Total, long FilesSeen, double Within = 0)
{
    public static readonly DuplicateProgress Start = new(DuplicateStage.Collecting, 0, 0, 0);

    public double Fraction => Stage switch
    {
        DuplicateStage.Collecting => 0.02 + 0.2 * Within,
        DuplicateStage.GroupingBySize => 0.25,
        DuplicateStage.QuickHash => 0.25 + 0.25 * Within,
        DuplicateStage.FullHash => 0.5 + 0.45 * Within,
        DuplicateStage.Verifying => 0.95 + 0.05 * Within,
        _ => 1,
    };
}

/// <summary>Hộp tiến độ có khoá: task ghi, giao diện đọc định kỳ (DuplicateProgressBox của bản Mac).</summary>
public sealed class DuplicateProgressBox
{
    private readonly object _lock = new();
    private DuplicateProgress _value = DuplicateProgress.Start;

    public DuplicateProgress Current
    {
        get { lock (_lock) return _value; }
    }

    public void Set(DuplicateProgress value)
    {
        lock (_lock) _value = value;
    }
}

/// <summary>Nhóm theo dung lượng / khoá băm (hàm thuần, DuplicateGrouping của bản Mac).</summary>
public static class DuplicateGrouping
{
    public const long DefaultMinimumSize = 1_000_000;

    /// <summary>Bước 1: nhóm theo dung lượng chính xác; bỏ file nhỏ hơn ngưỡng và nhóm chỉ có một file. Nhóm lớn trước.</summary>
    public static List<List<DuplicateCandidate>> BySize(IEnumerable<DuplicateCandidate> candidates, long minimumSize = DefaultMinimumSize)
    {
        var bySize = new Dictionary<long, List<DuplicateCandidate>>();
        foreach (var c in candidates)
        {
            if (c.Size < minimumSize) continue;
            if (!bySize.TryGetValue(c.Size, out var list)) bySize[c.Size] = list = [];
            list.Add(c);
        }
        return bySize.Values.Where(g => g.Count > 1).OrderByDescending(g => g[0].Size).ToList();
    }

    /// <summary>Bước 2, 3: chia lại mỗi nhóm theo khoá. Khoá <c>null</c> (không đọc được) bị loại; giữ thứ tự gặp đầu tiên.</summary>
    public static List<List<DuplicateCandidate>> Regroup(IEnumerable<List<DuplicateCandidate>> groups, Func<DuplicateCandidate, string?> key)
    {
        var result = new List<List<DuplicateCandidate>>();
        foreach (var group in groups)
        {
            var buckets = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var c in group)
            {
                if (key(c) is not { } k) continue;
                if (!buckets.TryGetValue(k, out var bucket))
                {
                    buckets[k] = bucket = [];
                    order.Add(k);
                }
                bucket.Add(c);
            }
            foreach (var k in order)
            {
                if (buckets[k].Count > 1) result.Add(buckets[k]);
            }
        }
        return result;
    }
}

/// <summary>
/// Tìm file trùng lặp theo 3 bước để giảm I/O (DuplicateFinder của bản Mac, mục 11.8):
/// dung lượng chính xác → XxHash3 của 64 KB đầu + 64 KB cuối → SHA-256 toàn bộ (file &gt; 128 KB).
/// Bỏ qua hard link (cùng volume serial + file index), file đám mây chưa tải (đọc sẽ kích hoạt tải về), reparse point,
/// file hệ thống, thư mục/file ẩn và vùng cấm của PathPolicy.
/// </summary>
public sealed class DuplicateFinder(DuplicateFinder.Options options)
{
    public const int EdgeChunk = 64 * 1024;
    private const int FullChunk = 1024 * 1024;

    public sealed record Options
    {
        public required IReadOnlyList<string> Roots { get; init; }
        public long MinimumSize { get; init; } = DuplicateGrouping.DefaultMinimumSize;
        /// <summary>Thư mục không đi vào (mặc định AppData và vùng hệ thống).</summary>
        public IReadOnlyList<string> Exclusions { get; init; } = [];
        public bool SkipHidden { get; init; } = true;
        public PathPolicy? Policy { get; init; }
        public IgnoreList Ignore { get; init; } = IgnoreList.Empty;
    }

    public Options Settings => options;

    public List<DuplicateGroup> Find(Action<DuplicateProgress> progress, CancellationToken ct)
    {
        var filesSeen = 0L;

        // Bước 0: liệt kê file.
        progress(DuplicateProgress.Start);
        var candidates = Collect(seen =>
        {
            filesSeen = seen.Seen;
            progress(new DuplicateProgress(DuplicateStage.Collecting, 0, 0, seen.Seen, seen.Fraction));
        }, ct);
        ct.ThrowIfCancellationRequested();

        // Bước 1: theo dung lượng.
        progress(new DuplicateProgress(DuplicateStage.GroupingBySize, 0, 0, filesSeen));
        var sizeGroups = DuplicateGrouping.BySize(candidates, options.MinimumSize);

        // Bước 2: XxHash3 của 64 KB đầu + 64 KB cuối; cùng lúc lấy định danh file để bỏ hard link.
        var quickTargets = sizeGroups.SelectMany(g => g).ToList();
        var quick = HashAll(quickTargets, DuplicateStage.QuickHash, filesSeen, progress, EdgeHash, c => Math.Min(c.Size, 2L * EdgeChunk), ct);
        var seenIdentity = new HashSet<(uint, ulong)>();
        var quickGroups = DuplicateGrouping.Regroup(sizeGroups, c =>
        {
            if (!quick.TryGetValue(c.Path, out var r)) return null;
            // Hard link: cùng (volume serial, file index) chỉ giữ một đường dẫn — xoá một link không giải phóng gì.
            if (r.Identity is { } id && !seenIdentity.Add((id.VolumeSerial, id.FileIndex))) return null;
            return r.Hash;
        });

        // Bước 3: SHA-256 toàn bộ. File ≤ 128 KB đã được băm hết ở bước 2.
        var fullTargets = quickGroups.SelectMany(g => g).Where(c => c.Size > 2L * EdgeChunk).ToList();
        var full = HashAll(fullTargets, DuplicateStage.FullHash, filesSeen, progress, FullHash, c => c.Size, ct);
        var finalGroups = DuplicateGrouping.Regroup(quickGroups, c =>
            c.Size > 2L * EdgeChunk
                ? full.TryGetValue(c.Path, out var f) ? f.Hash : null
                : quick.TryGetValue(c.Path, out var q) ? "xxh3:" + q.Hash : null);

        // Bước 5: kiểm tra lại từng bản (vẫn tồn tại, không đổi dung lượng, chưa thành file đám mây/liên kết).
        var result = new List<DuplicateGroup>();
        for (var i = 0; i < finalGroups.Count; i++)
        {
            if ((i & 31) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress(new DuplicateProgress(DuplicateStage.Verifying, i, finalGroups.Count, filesSeen, (double)i / Math.Max(1, finalGroups.Count)));
            }
            var members = finalGroups[i].Where(StillValid).ToList();
            if (members.Count < 2) continue;
            var first = members[0];
            var hash = full.TryGetValue(first.Path, out var fh) ? fh.Hash : quick.TryGetValue(first.Path, out var qh) ? qh.Hash : "";
            var files = members.Select(m => new DuplicateFile(m, quick.TryGetValue(m.Path, out var r) && r.Identity is { LinkCount: > 1 })).ToList();
            var group = new DuplicateGroup(first.Size, hash, files);
            // Mọi bản đều là hard link (xoá không giải phóng gì) thì bỏ nhóm.
            if (group.IndependentCopies >= 2) result.Add(group);
        }
        progress(new DuplicateProgress(DuplicateStage.Done, finalGroups.Count, finalGroups.Count, filesSeen, 1));
        return result.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static bool StillValid(DuplicateCandidate c)
    {
        if (DirectoryReader.Stat(c.Path) is not { } e) return false;
        return !e.IsDirectory && !e.IsReparsePoint && !e.IsDataless && e.Size == c.Size;
    }

    // MARK: Liệt kê

    private readonly record struct CollectProgress(long Seen, double Fraction);

    private List<DuplicateCandidate> Collect(Action<CollectProgress> onProgress, CancellationToken ct)
    {
        var roots = options.Roots;
        var clusters = roots.Select(SecureRemover.ClusterSize).ToArray();
        var policy = options.Policy;
        var ignore = options.Ignore;
        var exclusions = options.Exclusions;
        var minimum = options.MinimumSize;
        var skipHidden = options.SkipHidden;
        var result = new ConcurrentBag<DuplicateCandidate>();
        long seen = 0;
        var fraction = 0.0;
        var lastReport = 0L;

        void Report()
        {
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref lastReport) < 100) return;
            Interlocked.Exchange(ref lastReport, now);
            onProgress(new CollectProgress(Interlocked.Read(ref seen), Volatile.Read(ref fraction)));
        }

        var starts = roots.Select((r, i) => new ParallelTreeWalker.Frame(r, i, 0, false)).ToList();
        ParallelTreeWalker.Run(starts, UserContentScope.WalkConcurrency(roots),
            onDirectory: (parent, e, full) =>
            {
                if (e.IsReparsePoint || e.IsDataless) return null;
                if (skipHidden && UserContentScope.IsHidden(e)) return null;
                if (UserContentScope.IsExcluded(full, exclusions)) return null;
                if (policy is not null && policy.ShouldSkipDescending(full)) return null;
                if (ignore.Paths.Count > 0 && ignore.IgnoresPath(full)) return null;
                return new ParallelTreeWalker.Frame(full, parent.RootIndex, parent.Depth + 1, false);
            },
            onFile: (parent, e, full) =>
            {
                if (e.Size < minimum || e.IsReparsePoint || e.IsDataless || e.IsSystem) return;
                if (skipHidden && UserContentScope.IsHidden(e)) return;
                if (ignore.Paths.Count > 0 && ignore.IgnoresPath(full)) return;
                result.Add(new DuplicateCandidate(full, e.Size, e.AllocatedSize(clusters[parent.RootIndex]), e.LastWriteUtc, e.CreationUtc, parent.RootIndex));
            },
            ct,
            onVisited: n =>
            {
                Interlocked.Add(ref seen, n);
                Report();
            },
            onProgress: f =>
            {
                Volatile.Write(ref fraction, f);
                Report();
            },
            progressKey: "duplicates" + (skipHidden ? "" : "+hidden"));
        onProgress(new CollectProgress(Interlocked.Read(ref seen), 1));
        // Thứ tự ổn định (đường dẫn) để kết quả không phụ thuộc thứ tự luồng.
        return result.OrderBy(c => c.RootIndex).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // MARK: Băm

    private readonly record struct HashResult(string Hash, FileIdentity? Identity);

    /// <summary>
    /// Băm song song: mỗi volume một nhóm luồng riêng (4 trên SSD, 1 trên HDD — RecommendedConcurrency), các volume chạy cùng lúc.
    /// Lỗi đọc thì bỏ file đó. Kiểm tra huỷ trong từng file.
    /// </summary>
    private static Dictionary<string, HashResult> HashAll(List<DuplicateCandidate> items, DuplicateStage stage, long filesSeen,
        Action<DuplicateProgress> progress, Func<DuplicateCandidate, Action<long>, CancellationToken, HashResult?> hash,
        Func<DuplicateCandidate, long> workOf, CancellationToken ct)
    {
        var output = new ConcurrentDictionary<string, HashResult>(StringComparer.Ordinal);
        progress(new DuplicateProgress(stage, 0, items.Count, filesSeen));
        if (items.Count == 0) return [];
        var totalWork = Math.Max(1, items.Sum(workOf));
        long doneWork = 0;
        var done = 0;
        var lastReport = 0L;

        void Report(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - Interlocked.Read(ref lastReport) < 100) return;
            Interlocked.Exchange(ref lastReport, now);
            progress(new DuplicateProgress(stage, Volatile.Read(ref done), items.Count, filesSeen,
                Math.Min(1, (double)Interlocked.Read(ref doneWork) / totalWork)));
        }

        var byVolume = items.GroupBy(c => UserContentScope.VolumeKey(c.Path), StringComparer.OrdinalIgnoreCase).ToList();
        var tasks = byVolume.Select(volume => Task.Run(() =>
        {
            var list = volume.ToList();
            var degree = FileSystemService.Medium(list[0].Path) == StorageMedium.Rotational ? 1 : 4;
            Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct }, c =>
            {
                var accounted = 0L;
                var r = hash(c, bytes =>
                {
                    accounted += bytes;
                    Interlocked.Add(ref doneWork, bytes);
                    Report();
                }, ct);
                if (r is { } value) output[c.Path] = value;
                // File lỗi giữa chừng: cộng phần còn lại để tiến độ vẫn tới 100%.
                var rest = workOf(c) - accounted;
                if (rest > 0) Interlocked.Add(ref doneWork, rest);
                Interlocked.Increment(ref done);
                Report();
            });
        }, ct)).ToArray();
        try
        {
            Task.WaitAll(tasks, ct);
        }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
        {
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();
        Report(force: true);
        return new Dictionary<string, HashResult>(output, StringComparer.Ordinal);
    }

    /// <summary>XxHash3-64 của 64 KB đầu + 64 KB cuối (file 64–128 KB: phần sau đọc tiếp từ 64 KB, không chồng lấn).</summary>
    private static HashResult? EdgeHash(DuplicateCandidate c, Action<long> advance, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var handle = FileOpener.OpenForRead(c.Path, sequential: false, out _);
        if (handle is null) return null;
        var identity = FileOpener.Identity(handle);
        if (identity is { } id && id.Size != c.Size) return null; // file vừa đổi
        var buffer = ArrayPool<byte>.Shared.Rent(2 * EdgeChunk);
        try
        {
            var first = ReadFully(handle, buffer.AsSpan(0, (int)Math.Min(EdgeChunk, c.Size)), 0);
            var total = first;
            if (c.Size > EdgeChunk)
            {
                var offset = Math.Max(EdgeChunk, c.Size - EdgeChunk);
                total += ReadFully(handle, buffer.AsSpan(first, (int)Math.Min(EdgeChunk, c.Size - offset)), offset);
            }
            advance(total);
            return new HashResult(XxHash3.HashToUInt64(buffer.AsSpan(0, total)).ToString("x16"), identity);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>SHA-256 toàn bộ nội dung, đọc tuần tự từng khối 1 MB, kiểm tra huỷ mỗi khối; trả về hex chữ thường.</summary>
    private static HashResult? FullHash(DuplicateCandidate c, Action<long> advance, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var handle = FileOpener.OpenForRead(c.Path, sequential: true, out _);
        if (handle is null) return null;
        var buffer = ArrayPool<byte>.Shared.Rent(FullChunk);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long offset = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var n = RandomAccess.Read(handle, buffer.AsSpan(0, FullChunk), offset);
                if (n <= 0) break;
                sha.AppendData(buffer, 0, n);
                offset += n;
                advance(n);
            }
            if (offset != c.Size) return null; // file đổi trong lúc đọc
            return new HashResult(Convert.ToHexStringLower(sha.GetHashAndReset()), null);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> span, long offset)
    {
        var total = 0;
        while (total < span.Length)
        {
            var n = RandomAccess.Read(handle, span[total..], offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }
}
