using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CleanBoost.Core;
using CleanBoost.Core.FileSystem;
using CleanBoost.Core.Native;

namespace CleanBoost.Engine.FileSystem;

/// <summary>Kết quả đo một đường dẫn.</summary>
public readonly record struct PathMeasurement(
    long AllocatedSize,
    int ItemCount,
    DateTime NewestModificationUtc,
    DateTime? NewestAccessUtc,
    bool IsDirectory,
    bool Exists)
{
    public static readonly PathMeasurement Missing = new(0, 0, DateTime.MinValue, null, false, false);

    /// <summary>Lần dùng gần nhất của mục (sửa hoặc truy cập).</summary>
    public DateTime LastUseUtc => NewestAccessUtc is { } a && a > NewestModificationUtc ? a : NewestModificationUtc;
}

/// <summary>Bộ đếm số mục đã duyệt, cho thanh tiến độ.</summary>
public sealed class VisitCounter
{
    private long _count;
    public void Add(int n) => Interlocked.Add(ref _count, n);
    public long Count => Interlocked.Read(ref _count);
}

/// <summary>Theo dõi file theo cặp (volume serial, file index) để chỉ đếm hard link một lần.</summary>
public sealed class HardLinkTracker
{
    private readonly ConcurrentDictionary<(uint, ulong), byte> _seen = new();
    public bool FirstSighting(uint volume, ulong fileIndex) => _seen.TryAdd((volume, fileIndex), 0);
}

public enum WalkDecision { Continue, SkipDescendants, Stop }

/// <summary>Thông tin một volume.</summary>
public sealed record VolumeInfo(string MountPoint, string Name, long Total, long Available, bool IsSystem, DriveType DriveType)
{
    public double UsedFraction => Total <= 0 ? 0 : Math.Clamp(1 - (double)Available / Total, 0, 1);
    public double FreeFraction => Total <= 0 ? 0 : Math.Clamp((double)Available / Total, 0, 1);
}

public enum StorageMedium { SolidState, Rotational, Unknown }

/// <summary>Dịch vụ hệ thống tệp dùng chung cho scan task và clean engine.</summary>
public sealed class FileSystemService
{
    public string Home { get; }

    public FileSystemService(string? home = null) => Home = home ?? KnownPaths.UserHome;

    public bool Exists(string path) => DirectoryReader.Exists(path);
    public bool IsDirectory(string path) => DirectoryReader.IsRealDirectory(path);
    public DirEntry? Entry(string path) => DirectoryReader.Stat(path);
    public List<DirEntry> Children(string path) => DirectoryReader.List(path);

    // MARK: Duyệt

    /// <summary>
    /// Duyệt cây thư mục (không theo liên kết, không qua điểm gắn volume khác). <paramref name="visit"/> nhận đường dẫn đầy đủ.
    /// Thư mục không đọc được thì bỏ qua.
    /// </summary>
    public void Walk(string root, Func<string, DirEntry, WalkDecision> visit, CancellationToken ct = default, int checkEvery = 500)
    {
        var stack = new Stack<string>();
        stack.Push(root.TrimEnd('\\'));
        var n = 0;
        var stop = false;
        while (stack.Count > 0 && !stop)
        {
            var dir = stack.Pop();
            try
            {
                DirectoryReader.Read(dir, e =>
                {
                    if (stop) return;
                    if (++n % checkEvery == 0) ct.ThrowIfCancellationRequested();
                    var full = dir + "\\" + e.Name;
                    var decision = visit(full, e);
                    if (decision == WalkDecision.Stop) { stop = true; return; }
                    if (e.IsDirectory && !e.IsLink && decision == WalkDecision.Continue) stack.Push(full);
                });
            }
            catch (IOException)
            {
                // thư mục không đọc được: bỏ qua
            }
        }
    }

    // MARK: Đo dung lượng

    /// <summary>Đo dung lượng thực chiếm của file hoặc thư mục, đệ quy, không theo liên kết.</summary>
    public PathMeasurement Measure(string path, VisitCounter? counter = null, Func<string, bool>? skip = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Entry(path) is not { } root) return PathMeasurement.Missing;
        var cluster = SecureRemover.ClusterSize(path);
        if (!root.IsDirectory || root.IsLink)
        {
            counter?.Add(1);
            return new PathMeasurement(root.AllocatedSize(cluster), 1, root.LastWriteUtc, root.LastAccessUtc, root.IsDirectory && !root.IsLink, true);
        }
        long total = 0;
        var count = 0;
        var newestMod = root.LastWriteUtc;
        DateTime? newestAccess = null;
        var pending = 0;
        Walk(path, (full, e) =>
        {
            if (e.IsDirectory && !e.IsLink && skip is not null && skip(full)) return WalkDecision.SkipDescendants;
            count++;
            if (++pending >= 500)
            {
                counter?.Add(pending);
                pending = 0;
            }
            if (e.LastWriteUtc > newestMod) newestMod = e.LastWriteUtc;
            if (!e.IsDirectory)
            {
                if (newestAccess is null || e.LastAccessUtc > newestAccess) newestAccess = e.LastAccessUtc;
                total += e.AllocatedSize(cluster);
            }
            return WalkDecision.Continue;
        }, ct);
        counter?.Add(pending);
        return new PathMeasurement(total, count, newestMod, newestAccess, true, true);
    }

    /// <summary>Đo song song theo thư mục con cấp 1 để một thư mục rất lớn không chạy một luồng (mục 16.3).</summary>
    public PathMeasurement MeasureConcurrently(string path, VisitCounter? counter = null, Func<string, bool>? skip = null, CancellationToken ct = default)
    {
        if (Entry(path) is not { IsDirectory: true, IsLink: false } root) return Measure(path, counter, skip, ct);
        var kids = Children(path).Where(k => !(k.IsDirectory && skip is not null && skip(path.TrimEnd('\\') + "\\" + k.Name))).ToList();
        if (kids.Count <= 1) return Measure(path, counter, skip, ct);
        var cluster = SecureRemover.ClusterSize(path);
        long total = 0;
        var items = kids.Count;
        var newestMod = root.LastWriteUtc;
        DateTime? newestAccess = null;
        var gate = new object();
        Parallel.ForEach(kids, new ParallelOptions { MaxDegreeOfParallelism = RecommendedConcurrency(path), CancellationToken = ct }, kid =>
        {
            var full = path.TrimEnd('\\') + "\\" + kid.Name;
            PathMeasurement m;
            if (kid.IsDirectory && !kid.IsLink)
            {
                m = Measure(full, counter, skip, ct);
            }
            else
            {
                counter?.Add(1);
                m = new PathMeasurement(kid.AllocatedSize(cluster), 1, kid.LastWriteUtc, kid.LastAccessUtc, false, true);
            }
            lock (gate)
            {
                total += m.AllocatedSize;
                if (m.IsDirectory) items += m.ItemCount;
                if (m.NewestModificationUtc > newestMod) newestMod = m.NewestModificationUtc;
                if (m.NewestAccessUtc is { } a && (newestAccess is null || a > newestAccess)) newestAccess = a;
            }
        });
        return new PathMeasurement(total, items, newestMod, newestAccess, true, true);
    }

    // MARK: Volume

    public VolumeInfo? Volume(string path)
    {
        var root = PathPolicy.VolumeRoot(path);
        if (root is null) return null;
        try
        {
            if (!Win32.GetDiskFreeSpaceExW(root, out var available, out var total, out _)) return null;
            var label = "";
            var buffer = new char[261];
            if (Win32.GetVolumeInformationW(root, buffer, (uint)buffer.Length, out _, out _, out _, null, 0)) label = new string(buffer).TrimEnd('\0');
            var drive = root.TrimEnd('\\');
            var name = string.IsNullOrEmpty(label) ? Loc.F("Ổ %@", drive) : $"{label} ({drive})";
            var type = (DriveType)Win32.GetDriveTypeW(root);
            var isSystem = root.StartsWith(KnownPaths.SystemDrive, StringComparison.OrdinalIgnoreCase);
            return new VolumeInfo(root, name, (long)total, (long)available, isSystem, type);
        }
        catch
        {
            return null;
        }
    }

    public VolumeInfo? SystemVolume => Volume(KnownPaths.SystemDrive + "\\");

    private static readonly ConcurrentDictionary<string, StorageMedium> s_media = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>4 luồng trên SSD, 1 trên HDD (mục 5.3).</summary>
    public int RecommendedConcurrency(string path) => Medium(path) == StorageMedium.Rotational ? 1 : 4;

    public static StorageMedium Medium(string path)
    {
        var root = PathPolicy.VolumeRoot(path);
        if (root is null || root.Length < 2 || root[1] != ':') return StorageMedium.Unknown;
        return s_media.GetOrAdd(root[..2].ToUpperInvariant(), drive =>
        {
            using var h = Win32.CreateFileW(@"\\.\" + drive, 0, Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return StorageMedium.Unknown;
            var query = new Win32.STORAGE_PROPERTY_QUERY { PropertyId = 7, QueryType = 0 };
            if (!Win32.DeviceIoControl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY, ref query, Marshal.SizeOf<Win32.STORAGE_PROPERTY_QUERY>(),
                    out var desc, Marshal.SizeOf<Win32.DEVICE_SEEK_PENALTY_DESCRIPTOR>(), out _, IntPtr.Zero))
                return StorageMedium.Unknown;
            return desc.IncursSeekPenalty ? StorageMedium.Rotational : StorageMedium.SolidState;
        });
    }
}
