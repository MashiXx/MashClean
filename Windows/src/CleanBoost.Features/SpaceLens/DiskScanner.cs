using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;
using CleanBoost.Engine.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace CleanBoost.Features.SpaceLens;

/// <summary>Tiến độ quét Space Lens: số mục đã duyệt và dung lượng đã cộng (giao diện đọc định kỳ).</summary>
public sealed class DiskScanProgress
{
    private long _items;
    private long _bytes;

    public void Add(int items, long bytes)
    {
        Interlocked.Add(ref _items, items);
        Interlocked.Add(ref _bytes, bytes);
    }

    public long Items => Interlocked.Read(ref _items);
    public long Bytes => Interlocked.Read(ref _bytes);
}

/// <summary>Thống kê một lần quét (để log và đo hiệu năng).</summary>
public sealed record DiskScanStats(int Threads, int Directories, int UnreadableDirectories, int HardLinkDuplicates, TimeSpan Duration);

/// <summary>
/// Quét cây dung lượng cho Space Lens (DiskScanner của bản Mac): đọc thư mục hàng loạt song song theo từng thư mục
/// (các luồng lấy việc từ một ngăn xếp chung nên thư mục lớn không dồn vào một luồng), không theo symlink/junction,
/// không qua ổ khác gắn vào thư mục, đếm hard link một lần (NTFS), file đám mây chưa tải tính 0,
/// Thùng rác của ổ chỉ đo tổng, thư mục không đọc được đánh dấu.
/// </summary>
public sealed class DiskScanner(FileSystemService fileSystem)
{
    /// <summary>Hard link nhỏ hơn mức này không theo dõi (sai số không đáng kể, đỡ bộ nhớ).</summary>
    internal const long HardLinkMinSize = 8 * 1024;
    private const int InternMaxLength = 24;
    private const int InternCapacity = 100_000;

    public FileSystemService FileSystem { get; } = fileSystem;

    /// <summary>Ghi đè số luồng (đo hiệu năng); 0 = tự chọn.</summary>
    public int ThreadOverride { get; init; }

    /// <summary>Thống kê lần quét gần nhất.</summary>
    public DiskScanStats? LastStats { get; private set; }

    /// <summary>
    /// Số luồng. HDD: 1 (cả ổ: 2) theo <see cref="FileSystemService.RecommendedConcurrency"/>. SSD: thời gian chủ yếu là mở thư mục
    /// trong nhân (siêu dữ liệu đã nằm trong bộ nhớ đệm) nên nhiều luồng hơn mức chung 4 vẫn nhanh lên rõ: thư mục tới 8 luồng,
    /// cả ổ tới 12 (đo trên hồ sơ người dùng 1,4 triệu mục: 4 luồng 10,4 giây, 8 luồng 7,0 giây, 12 luồng 6,1 giây).
    /// </summary>
    public int Concurrency(string root, bool isVolumeRoot)
    {
        if (ThreadOverride > 0) return ThreadOverride;
        var recommended = Math.Max(1, FileSystem.RecommendedConcurrency(root));
        if (recommended <= 1) return isVolumeRoot ? 2 : 1;
        return Math.Clamp(Environment.ProcessorCount, recommended, isVolumeRoot ? 12 : 8);
    }

    // MARK: Quét

    /// <summary>Quét toàn bộ <paramref name="root"/>. Ném <see cref="DirectoryNotFoundException"/> nếu không tồn tại.</summary>
    public DiskTree Scan(string root, DiskScanProgress? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var rootPath = ResolveRoot(root);
        // FindFirstFile không trả về gốc ổ mạng (\\server\share): hỏi thuộc tính thư mục thay thế.
        var entry = DirectoryReader.Stat(rootPath);
        var isDirectory = entry?.IsDirectory ?? (DirectoryReader.IsRealDirectory(rootPath) || Directory.Exists(rootPath));
        if (entry is null && !isDirectory) throw new DirectoryNotFoundException(Loc.F("Không tìm thấy %@", rootPath));
        var volume = VolumeFacts.For(rootPath);
        if (!isDirectory && entry is { } file)
        {
            var single = new DiskTree(rootPath, DiskNodeFlags.None);
            single.SetSize(0, SpecialEntries.CompressedSize(rootPath) ?? DiskSize.RoundUp(file.Size, volume.ClusterSize));
            progress?.Add(1, single.TotalSize);
            return single;
        }
        var session = new ScanSession(new DiskTree(rootPath), volume, progress, ct);
        var threads = Concurrency(rootPath, volume.IsRoot(rootPath));
        session.Run([new WorkItem(DiskTree.Root, rootPath)], threads);
        session.Tree.FinalizeSizes();
        LastStats = new DiskScanStats(threads, session.Directories, session.UnreadableDirectories, session.HardLinkDuplicates, DateTime.UtcNow - started);
        return session.Tree;
    }

    /// <summary>Quét một thư mục để ghép vào cây khi cập nhật. Bộ đếm hard link mới: hard link có thể bị đếm lại ở phần khác.</summary>
    public DiskTree ScanSubtree(string path, CancellationToken ct = default)
    {
        var volume = VolumeFacts.For(path);
        var session = new ScanSession(new DiskTree(path), volume, null, ct);
        session.Run([new WorkItem(DiskTree.Root, path)], Concurrency(path, isVolumeRoot: false));
        session.Tree.FinalizeSizes();
        return session.Tree;
    }

    /// <summary>
    /// Đọc lại một thư mục một cấp: thư mục con có tên trong <paramref name="knownDirectories"/> giữ cây cũ,
    /// thư mục mới quét đầy đủ, file lấy dung lượng mới.
    /// </summary>
    public ShallowListing RefreshListing(string path, IReadOnlySet<string> knownDirectories, CancellationToken ct = default)
    {
        var volume = VolumeFacts.For(path);
        var session = new ScanSession(new DiskTree(path), volume, null, ct);
        var subdirs = new ScanWorker(session).Process(new WorkItem(DiskTree.Root, path));
        var probe = session.Tree;
        if ((probe.Flags(0) & DiskNodeFlags.Unreadable) != 0 && !probe.HasChildren(0)) return ShallowListing.Unreadable;
        var descend = subdirs.ToDictionary(w => w.Index, w => w.Path);
        var items = new List<ShallowListing.Item>(probe.ChildCount(0));
        foreach (var c in probe.Children(0))
        {
            ct.ThrowIfCancellationRequested();
            var name = probe.Name(c);
            var flags = probe.Flags(c);
            if ((flags & DiskNodeFlags.Directory) != 0 && knownDirectories.Contains(name) && descend.ContainsKey(c))
            {
                items.Add(new ShallowListing.Item(name, 0, flags, Reuse: true));
            }
            else if (descend.TryGetValue(c, out var subPath))
            {
                DiskTree sub;
                try { sub = ScanSubtree(subPath, ct); }
                catch (OperationCanceledException) { throw; }
                catch { sub = new DiskTree(subPath, DiskNodeFlags.Directory | DiskNodeFlags.Unreadable); }
                items.Add(new ShallowListing.Item(name, sub.TotalSize, flags | sub.Flags(0), Subtree: sub));
            }
            else
            {
                items.Add(new ShallowListing.Item(name, probe.Size(c), flags));
            }
        }
        return new ShallowListing(items, (probe.Flags(0) & DiskNodeFlags.Unreadable) == 0);
    }

    /// <summary>Đường dẫn thật của gốc (giải symlink/junction/ổ subst như <c>realpath</c> của bản Mac).</summary>
    public static string ResolveRoot(string root)
    {
        var full = DiskTree.NormalizePath(Path.GetFullPath(root));
        try
        {
            using var handle = Win32.CreateFileW(LongPath.Prefix(full), 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE | Win32.FILE_SHARE_DELETE,
                IntPtr.Zero, Win32.OPEN_EXISTING, Win32.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return full;
            var buffer = new char[32768];
            var n = Win32.GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length) return full;
            var resolved = DiskTree.NormalizePath(new string(buffer, 0, (int)n));
            return resolved.Length >= 2 && (resolved[1] == ':' || resolved.StartsWith(@"\\", StringComparison.Ordinal)) ? resolved : full;
        }
        catch
        {
            return full;
        }
    }

    // MARK: Phiên quét

    internal readonly record struct WorkItem(int Index, string Path);

    /// <summary>Thông tin volume dùng trong một lần quét.</summary>
    internal sealed record VolumeFacts(string? Root, long ClusterSize, bool TracksHardLinks)
    {
        /// <summary>Cách đọc thư mục (<see cref="DirectoryReadMode"/>), hạ dần nếu volume không hỗ trợ.</summary>
        public int ReadMode = (int)DirectoryReadMode.IdExtended;

        public bool IsRoot(string path) => Root is not null && string.Equals(DiskTree.NormalizePath(Root), path, StringComparison.OrdinalIgnoreCase);

        public static VolumeFacts For(string path)
        {
            var root = PathPolicy.VolumeRoot(path);
            var cluster = SecureRemover.ClusterSize(path);
            var ntfs = false;
            if (root is not null)
            {
                var fs = new char[64];
                if (Win32.GetVolumeInformationW(root, null, 0, out _, out _, out _, fs, (uint)fs.Length))
                    ntfs = new string(fs).TrimEnd('\0').Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            }
            return new VolumeFacts(root, cluster, ntfs);
        }
    }

    /// <summary>Theo dõi số hiệu file đã gặp (chia 64 mảnh để các luồng ít tranh khoá).</summary>
    internal sealed class FileIdTracker
    {
        private readonly HashSet<long>[] _shards = Enumerable.Range(0, 64).Select(_ => new HashSet<long>()).ToArray();

        public bool FirstSighting(long fileId)
        {
            var shard = _shards[(int)(((ulong)fileId * 0x9E3779B97F4A7C15UL) >> 58)];
            lock (shard) return shard.Add(fileId);
        }
    }

    /// <summary>
    /// Một lần quét: cây dùng chung (khoá khi thêm khối con), ngăn xếp việc dùng chung, mỗi luồng một <see cref="ScanWorker"/>.
    /// Mỗi thư mục được đọc trọn ngoài khoá rồi thêm cả khối con trong một lần khoá, nên con của một node luôn liền nhau
    /// và có chỉ số lớn hơn cha.
    /// </summary>
    internal sealed class ScanSession(DiskTree tree, VolumeFacts volume, DiskScanProgress? progress, CancellationToken ct)
    {
        public DiskTree Tree { get; } = tree;
        public object Gate { get; } = new();
        public VolumeFacts Volume { get; } = volume;
        public DiskScanProgress? Progress { get; } = progress;
        public CancellationToken Cancellation { get; } = ct;
        public FileIdTracker? HardLinks { get; } = volume.TracksHardLinks ? new FileIdTracker() : null;
        public int Directories;
        public int UnreadableDirectories;
        public int HardLinkDuplicates;

        public void Run(IReadOnlyList<WorkItem> initial, int threads)
        {
            var stack = new ConcurrentStack<WorkItem>(initial);
            var pending = initial.Count;
            if (pending == 0) return;
            using var signal = new SemaphoreSlim(pending);
            var done = 0;
            Exception? failure = null;

            void Finish()
            {
                if (Interlocked.Exchange(ref done, 1) == 0) signal.Release(threads);
            }

            void Loop()
            {
                var worker = new ScanWorker(this);
                try
                {
                    while (true)
                    {
                        signal.Wait(Cancellation);
                        if (Volatile.Read(ref done) != 0) return;
                        if (!stack.TryPop(out var item)) continue;
                        var children = worker.Process(item);
                        if (children.Count > 0)
                        {
                            Interlocked.Add(ref pending, children.Count);
                            stack.PushRange(children.ToArray());
                            signal.Release(children.Count);
                        }
                        if (Interlocked.Decrement(ref pending) == 0)
                        {
                            Finish();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Finish();
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    Finish();
                }
            }

            if (threads <= 1)
            {
                Loop();
            }
            else
            {
                var workers = new Thread[threads];
                for (var i = 0; i < threads; i++)
                {
                    workers[i] = new Thread(Loop) { IsBackground = true, Name = "SpaceLens scan " + i };
                    workers[i].Start();
                }
                foreach (var t in workers) t.Join();
            }
            Cancellation.ThrowIfCancellationRequested();
            if (failure is not null) throw new IOException(failure.Message, failure);
        }
    }

    /// <summary>Phần việc của một luồng: bộ đọc, bộ đệm phân loại và bảng tên ngắn hay lặp (Contents, index.js…) riêng.</summary>
    internal sealed class ScanWorker
    {
        private readonly ScanSession _session;
        private readonly BulkDirectoryReader _reader = new();
        private BulkDirectoryReader? _opaqueReader;
        private readonly Dictionary<string, uint> _interned = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint>.AlternateLookup<ReadOnlySpan<char>> _lookup;
        private readonly List<(int Entry, long Size, DiskNodeFlags Flags, uint Name, string? Path)> _items = new(256);
        private readonly List<WorkItem> _subdirs = new(64);

        public ScanWorker(ScanSession session)
        {
            _session = session;
            _lookup = _interned.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        /// <summary>Đọc một thư mục, thêm khối con vào cây, trả về các thư mục con cần đi tiếp.</summary>
        public List<WorkItem> Process(WorkItem work)
        {
            _session.Cancellation.ThrowIfCancellationRequested();
            _subdirs.Clear();
            _items.Clear();
            var volume = _session.Volume;
            var error = _reader.Read(work.Path, volume.ClusterSize, ref volume.ReadMode);
            Interlocked.Increment(ref _session.Directories);
            var tree = _session.Tree;
            if (error != 0 && _reader.Entries.Count == 0)
            {
                Interlocked.Increment(ref _session.UnreadableDirectories);
                lock (_session.Gate) tree.AddFlags(work.Index, DiskNodeFlags.Unreadable);
                return _subdirs;
            }

            var entries = _reader.Entries;
            var basePath = work.Path.EndsWith('\\') ? work.Path : work.Path + "\\";
            long bytes = 0;
            for (var k = 0; k < entries.Count; k++)
            {
                var e = entries[k];
                var name = _reader.Name(e);
                var flags = DiskNodeFlags.None;
                long size = 0;
                string? descend = null;
                var isLink = e.IsReparsePoint && SpecialEntries.IsLinkTag(e.ReparseTag);
                if (e.IsDirectory)
                {
                    flags |= DiskNodeFlags.Directory;
                    if (isLink)
                    {
                        var full = string.Concat(basePath, name);
                        flags |= e.ReparseTag == Win32.IO_REPARSE_TAG_MOUNT_POINT && SpecialEntries.IsVolumeMountPoint(full)
                            ? DiskNodeFlags.OtherVolume : DiskNodeFlags.Symlink;
                    }
                    else if ((e.Attributes & (Win32.FILE_ATTRIBUTE_RECALL_ON_OPEN | Win32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Win32.FILE_ATTRIBUTE_OFFLINE)) != 0)
                    {
                        // Thư mục đám mây chưa tải danh sách: mở ra sẽ kích hoạt tải về, và chưa có gì nằm trên máy.
                        flags |= DiskNodeFlags.CloudOnly;
                    }
                    else if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                    {
                        flags |= DiskNodeFlags.Unreadable;
                    }
                    else if (name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase))
                    {
                        flags |= DiskNodeFlags.Opaque;
                        size = MeasureOpaque(string.Concat(basePath, name), out var readable);
                        if (!readable) flags |= DiskNodeFlags.Unreadable;
                    }
                    else
                    {
                        descend = string.Concat(basePath, name);
                    }
                }
                else if (isLink)
                {
                    flags |= DiskNodeFlags.Symlink;
                }
                else if ((e.Attributes & (Win32.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | Win32.FILE_ATTRIBUTE_RECALL_ON_OPEN | Win32.FILE_ATTRIBUTE_OFFLINE)) != 0)
                {
                    // File đám mây chưa tải về: xoá không giải phóng ổ, đọc sẽ kích hoạt tải về.
                    flags |= DiskNodeFlags.CloudOnly;
                }
                else
                {
                    size = e.Allocated;
                    if (SpecialEntries.NeedsCompressedSize(e)) size = SpecialEntries.CompressedSize(string.Concat(basePath, name)) ?? size;
                    if (_session.HardLinks is { } tracker && size >= HardLinkMinSize && e.FileId != 0 && !tracker.FirstSighting(e.FileId))
                    {
                        size = 0;
                        flags |= DiskNodeFlags.HardLinkDuplicate;
                        Interlocked.Increment(ref _session.HardLinkDuplicates);
                    }
                }
                var nameOffset = name.Length <= InternMaxLength && _lookup.TryGetValue(name, out var o) ? o : uint.MaxValue;
                _items.Add((k, size, flags, nameOffset, descend));
                bytes += size;
            }

            int start;
            lock (_session.Gate)
            {
                start = tree.Count;
                for (var k = 0; k < _items.Count; k++)
                {
                    var item = _items[k];
                    var nameOffset = item.Name;
                    if (nameOffset == uint.MaxValue)
                    {
                        nameOffset = tree.AppendName(_reader.Name(entries[item.Entry]));
                        _items[k] = item with { Name = nameOffset };
                    }
                    tree.AppendNode(new DiskNode { NameOffset = nameOffset, Size = item.Size, Flags = item.Flags }, work.Index);
                }
                tree.SetChildren(work.Index, start, _items.Count);
                if (error != 0) tree.AddFlags(work.Index, DiskNodeFlags.Unreadable);
            }

            for (var k = 0; k < _items.Count; k++)
            {
                var item = _items[k];
                if (item.Path is not null) _subdirs.Add(new WorkItem(start + k, item.Path));
                var name = _reader.Name(entries[item.Entry]);
                if (name.Length <= InternMaxLength && _interned.Count < InternCapacity) _lookup.TryAdd(name, item.Name);
            }
            if (error != 0) Interlocked.Increment(ref _session.UnreadableDirectories);
            _session.Progress?.Add(_items.Count, bytes);
            return _subdirs;
        }

        /// <summary>Tổng dung lượng một thư mục mà không thêm node (Thùng rác của ổ: chỉ dọn qua API Thùng rác).</summary>
        private long MeasureOpaque(string path, out bool readable)
        {
            _opaqueReader ??= new BulkDirectoryReader();
            var reader = _opaqueReader;
            readable = true;
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            var first = true;
            while (stack.Count > 0)
            {
                _session.Cancellation.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                var error = reader.Read(dir, _session.Volume.ClusterSize, ref _session.Volume.ReadMode);
                if (error != 0 && first) readable = false;
                first = false;
                var basePath = dir.EndsWith('\\') ? dir : dir + "\\";
                foreach (var e in reader.Entries)
                {
                    if (e.IsReparsePoint && SpecialEntries.IsLinkTag(e.ReparseTag)) continue;
                    if (e.IsDirectory) stack.Push(string.Concat(basePath, reader.Name(e)));
                    else total += e.Allocated;
                }
            }
            return total;
        }
    }
}
