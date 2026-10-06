using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace CleanBoost.Features.SpaceLens;

/// <summary>Cờ của một node Space Lens (giữ giá trị bit như bản Mac, thêm cờ riêng của Windows).</summary>
[Flags]
public enum DiskNodeFlags : byte
{
    None = 0,
    Directory = 1 << 0,
    // 1 << 1: "package" của bản Mac, Windows không có.
    /// <summary>Symlink, junction hoặc app execution alias: không bao giờ đi theo.</summary>
    Symlink = 1 << 2,
    /// <summary>Không đọc được (thiếu quyền).</summary>
    Unreadable = 1 << 3,
    /// <summary>Ổ khác gắn vào thư mục (volume mount point): không đi vào, không tính.</summary>
    OtherVolume = 1 << 4,
    /// <summary>Hard link đã đếm ở chỗ khác nên dung lượng tính 0.</summary>
    HardLinkDuplicate = 1 << 5,
    /// <summary>Thư mục chỉ đo tổng, không liệt kê bên trong (Thùng rác của ổ).</summary>
    Opaque = 1 << 6,
    /// <summary>File/thư mục đám mây chưa tải về máy (OneDrive Files On-Demand): tính 0.</summary>
    CloudOnly = 1 << 7,
}

/// <summary>
/// Node gọn nhẹ lưu trong mảng phẳng (24 byte); con của một node nằm liền nhau <c>[FirstChild, FirstChild + ChildCount)</c>.
/// Tên không lưu trong node mà là vị trí trong bảng tên chung.
/// </summary>
public struct DiskNode
{
    public long Size;
    public uint NameOffset;
    public uint FirstChild;
    public uint ChildCount;
    public DiskNodeFlags Flags;
}

/// <summary>
/// Mảng chia khối: thêm phần tử không phải chép lại cả mảng (cây vài triệu node không tạo mảng khổng lồ trên LOH),
/// truy cập theo tham chiếu. Khối đầu bắt đầu nhỏ để cây con quét lại (vài chục node) không tốn bộ nhớ.
/// </summary>
internal sealed class ChunkedArray<T> where T : struct
{
    private const int Shift = 13;
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;
    private T[][] _chunks = [];
    private int _chunkCount;

    public int Count { get; private set; }

    /// <summary>Tham chiếu tới phần tử. Không giữ tham chiếu qua lời gọi <see cref="Add"/> (khối đầu có thể được cấp lại).</summary>
    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunks[index >> Shift][index & Mask];
    }

    public int Add(in T value)
    {
        var i = Count;
        var c = i >> Shift;
        var o = i & Mask;
        if (c >= _chunkCount) AddChunk();
        else if (o >= _chunks[c].Length) Array.Resize(ref _chunks[c], Math.Min(ChunkSize, _chunks[c].Length * 2));
        _chunks[c][o] = value;
        Count = i + 1;
        return i;
    }

    private void AddChunk()
    {
        if (_chunkCount == _chunks.Length) Array.Resize(ref _chunks, Math.Max(4, _chunks.Length * 2));
        _chunks[_chunkCount] = new T[_chunkCount == 0 ? 64 : ChunkSize];
        _chunkCount++;
    }

    public long CapacityBytes
    {
        get
        {
            long total = 0;
            for (var i = 0; i < _chunkCount; i++) total += _chunks[i].Length;
            return total * Unsafe.SizeOf<T>();
        }
    }
}

/// <summary>
/// Bảng tên chung: tên mã hoá UTF-8 nối liền trong các khối 256 KB, mỗi tên có tiền tố độ dài (varint).
/// Vị trí của tên là <c>(khối &lt;&lt; 18) | vị trí trong khối</c>; một tên không bao giờ vắt qua hai khối.
/// Chỉ thêm, không xoá (tên của node mồ côi bỏ lại cho tới khi cây được dựng lại).
/// </summary>
internal sealed class NameTable
{
    private const int Shift = 18;
    private const int ChunkSize = 1 << Shift;
    private const uint Mask = ChunkSize - 1;
    private readonly List<byte[]> _chunks = [];
    private int _used;

    public uint Append(ReadOnlySpan<char> name)
    {
        var count = Encoding.UTF8.GetByteCount(name);
        var dest = Reserve(count, out var offset);
        Encoding.UTF8.GetBytes(name, dest);
        return offset;
    }

    public uint Append(ReadOnlySpan<byte> utf8)
    {
        var dest = Reserve(utf8.Length, out var offset);
        utf8.CopyTo(dest);
        return offset;
    }

    private Span<byte> Reserve(int count, out uint offset)
    {
        var header = count < 0x80 ? 1 : count < 0x4000 ? 2 : 3;
        var need = header + count;
        if (need > ChunkSize) throw new ArgumentException("Tên quá dài");
        if (_chunks.Count == 0)
        {
            _chunks.Add(new byte[Math.Max(1024, (int)BitOperations.RoundUpToPowerOf2((uint)need))]);
            _used = 0;
        }
        var chunk = _chunks[^1];
        if (_used + need > chunk.Length)
        {
            if (chunk.Length < ChunkSize)
            {
                // Khối đầu còn nhỏ: nới rộng tại chỗ (vị trí các tên cũ không đổi).
                var size = chunk.Length;
                while (size < _used + need && size < ChunkSize) size *= 2;
                Array.Resize(ref chunk, Math.Min(size, ChunkSize));
                _chunks[^1] = chunk;
            }
            if (_used + need > chunk.Length)
            {
                chunk = new byte[ChunkSize];
                _chunks.Add(chunk);
                _used = 0;
            }
        }
        offset = (uint)((_chunks.Count - 1) << Shift) | (uint)_used;
        var pos = _used;
        if (header == 1)
        {
            chunk[pos++] = (byte)count;
        }
        else if (header == 2)
        {
            chunk[pos++] = (byte)(0x80 | (count & 0x7F));
            chunk[pos++] = (byte)(count >> 7);
        }
        else
        {
            chunk[pos++] = (byte)(0x80 | (count & 0x7F));
            chunk[pos++] = (byte)(0x80 | ((count >> 7) & 0x7F));
            chunk[pos++] = (byte)(count >> 14);
        }
        _used = pos + count;
        return chunk.AsSpan(pos, count);
    }

    public ReadOnlySpan<byte> Bytes(uint offset)
    {
        var chunk = _chunks[(int)(offset >> Shift)];
        var pos = (int)(offset & Mask);
        int len = chunk[pos++];
        if ((len & 0x80) != 0)
        {
            int b1 = chunk[pos++];
            len = (len & 0x7F) | ((b1 & 0x7F) << 7);
            if ((b1 & 0x80) != 0) len |= chunk[pos++] << 14;
        }
        return chunk.AsSpan(pos, len);
    }

    public string Get(uint offset) => Encoding.UTF8.GetString(Bytes(offset));

    public long CapacityBytes
    {
        get
        {
            long total = 0;
            foreach (var c in _chunks) total += c.Length;
            return total;
        }
    }
}

/// <summary>
/// Cây tổng hợp dung lượng của Space Lens (DiskTree của bản Mac): mảng node phẳng + mảng cha + bảng tên chung.
/// Node 0 là gốc, tên của gốc là đường dẫn tuyệt đối. Con luôn có chỉ số lớn hơn cha; con của một node nằm liền nhau.
/// Không an toàn đa luồng: khi quét, <see cref="DiskScanner"/> khoá cây; sau đó chỉ luồng giao diện sửa.
/// </summary>
public sealed class DiskTree
{
    public const int NoParent = -1;
    public const int Root = 0;

    private ChunkedArray<DiskNode> _nodes = new();
    private ChunkedArray<int> _parents = new();
    private readonly NameTable _names;

    /// <summary>Số node không còn được tham chiếu sau khi quét lại/xoá (bỏ đi khi <see cref="Compact"/>).</summary>
    public int OrphanCount { get; private set; }

    public DiskTree(string rootPath, DiskNodeFlags flags = DiskNodeFlags.Directory)
    {
        RootPath = rootPath;
        _names = new NameTable();
        var name = _names.Append(rootPath);
        _nodes.Add(new DiskNode { NameOffset = name, Flags = flags });
        _parents.Add(NoParent);
    }

    /// <summary>Đường dẫn tuyệt đối của gốc (ổ thì có dấu <c>\</c> cuối, vd <c>C:\</c>).</summary>
    public string RootPath { get; }
    public int Count => _nodes.Count;
    public long TotalSize => _nodes[0].Size;
    /// <summary>Số node còn được tham chiếu.</summary>
    public int LiveCount => _nodes.Count - OrphanCount;

    // MARK: Đọc

    public string Name(int i) => i == Root ? RootPath : _names.Get(_nodes[i].NameOffset);
    public long Size(int i) => _nodes[i].Size;
    public DiskNodeFlags Flags(int i) => _nodes[i].Flags;
    public bool IsDirectory(int i) => (_nodes[i].Flags & DiskNodeFlags.Directory) != 0;
    public int FirstChild(int i) => (int)_nodes[i].FirstChild;
    public int ChildCount(int i) => (int)_nodes[i].ChildCount;
    public bool HasChildren(int i) => _nodes[i].ChildCount > 0;
    public int Parent(int i) => _parents[i];
    public ChildRange Children(int i) => new((int)_nodes[i].FirstChild, (int)_nodes[i].ChildCount);

    /// <summary>Con của <paramref name="i"/> sắp giảm dần theo dung lượng (bằng nhau thì theo tên).</summary>
    public int[] SortedChildren(int i)
    {
        var range = Children(i);
        var result = new int[range.Count];
        var sizes = new long[range.Count];
        for (var k = 0; k < range.Count; k++)
        {
            result[k] = range.Start + k;
            sizes[k] = -_nodes[range.Start + k].Size;
        }
        Array.Sort(sizes, result);
        return result;
    }

    /// <summary>Chuỗi chỉ số từ gốc tới <paramref name="i"/> (kể cả hai đầu).</summary>
    public List<int> Lineage(int i)
    {
        var chain = new List<int>(16) { i };
        var cur = i;
        while (_parents[cur] is var p && p != NoParent)
        {
            chain.Add(p);
            cur = p;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>Tên các thành phần từ gốc (không gồm gốc) tới <paramref name="i"/>.</summary>
    public string[] Components(int i)
    {
        var lineage = Lineage(i);
        var parts = new string[lineage.Count - 1];
        for (var k = 1; k < lineage.Count; k++) parts[k - 1] = Name(lineage[k]);
        return parts;
    }

    public string Path(int i)
    {
        if (i == Root) return RootPath;
        var parts = Components(i);
        return RootPath.EndsWith('\\') ? RootPath + string.Join('\\', parts) : RootPath + "\\" + string.Join('\\', parts);
    }

    /// <summary>Con có tên <paramref name="name"/> (không phân biệt hoa thường như NTFS), <c>-1</c> nếu không có.</summary>
    public int ChildNamed(string name, int i)
    {
        var range = Children(i);
        Span<char> buffer = stackalloc char[512];
        for (var c = range.Start; c < range.End; c++)
        {
            var bytes = _names.Bytes(_nodes[c].NameOffset);
            if (bytes.Length > buffer.Length)
            {
                if (Encoding.UTF8.GetString(bytes).Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
                continue;
            }
            var n = Encoding.UTF8.GetChars(bytes, buffer);
            if (MemoryExtensions.Equals(buffer[..n], name, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return -1;
    }

    public int IndexForComponents(IEnumerable<string> parts)
    {
        var cur = Root;
        foreach (var p in parts)
        {
            cur = ChildNamed(p, cur);
            if (cur < 0) return -1;
        }
        return cur;
    }

    /// <summary>Thành phần tương đối so với gốc; <c>null</c> nếu đường dẫn nằm ngoài cây.</summary>
    public string[]? RelativeComponents(string path)
    {
        var p = NormalizePath(path);
        var root = RootPath;
        if (p.Equals(NormalizePath(root), StringComparison.OrdinalIgnoreCase)) return [];
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return p[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    public int IndexForPath(string path) => RelativeComponents(path) is { } parts ? IndexForComponents(parts) : -1;

    /// <summary>Thư mục sâu nhất có trong cây chứa <paramref name="path"/>, <c>-1</c> nếu ngoài cây.</summary>
    public int NearestDirectory(string path)
    {
        if (RelativeComponents(path) is not { } parts) return -1;
        var cur = Root;
        foreach (var p in parts)
        {
            var c = ChildNamed(p, cur);
            if (c < 0 || !IsDirectory(c)) break;
            cur = c;
        }
        return cur;
    }

    /// <summary>Số node con cháu (không kể chính nó).</summary>
    public int DescendantCount(int i)
    {
        var total = 0;
        var stack = new Stack<int>();
        stack.Push(i);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            var r = Children(n);
            total += r.Count;
            for (var c = r.Start; c < r.End; c++)
            {
                if (_nodes[c].ChildCount > 0) stack.Push(c);
            }
        }
        return total;
    }

    /// <summary>Bộ nhớ ước tính của cây (byte).</summary>
    public long EstimatedMemory => _nodes.CapacityBytes + _parents.CapacityBytes + _names.CapacityBytes;

    /// <summary>Bỏ dấu <c>\</c> cuối (trừ gốc ổ <c>C:\</c>), tiền tố <c>\\?\</c>, đổi <c>/</c> thành <c>\</c>.</summary>
    public static string NormalizePath(string path)
    {
        var p = path.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p;
    }

    // MARK: Dựng

    internal uint AppendName(ReadOnlySpan<char> name) => _names.Append(name);

    internal int AppendNode(in DiskNode node, int parent)
    {
        _parents.Add(parent);
        return _nodes.Add(node);
    }

    internal void SetChildren(int i, int first, int count)
    {
        ref var n = ref _nodes[i];
        n.FirstChild = count > 0 ? (uint)first : 0;
        n.ChildCount = (uint)count;
    }

    internal void SetSize(int i, long size) => _nodes[i].Size = size;
    internal void AddFlags(int i, DiskNodeFlags flags) => _nodes[i].Flags |= flags;
    internal void RemoveFlags(int i, DiskNodeFlags flags) => _nodes[i].Flags &= ~flags;

    /// <summary>Thêm khối con cho một node chưa có con (dựng tay, ví dụ trong test). Trả về chỉ số con đầu tiên.</summary>
    public int AppendChildren(int parent, IReadOnlyList<(string Name, long Size, DiskNodeFlags Flags)> items)
    {
        if (_nodes[parent].ChildCount != 0) throw new InvalidOperationException("Node đã có con");
        var start = Count;
        foreach (var (name, size, flags) in items)
        {
            AppendNode(new DiskNode { NameOffset = _names.Append(name), Size = size, Flags = flags }, parent);
        }
        SetChildren(parent, start, items.Count);
        return start;
    }

    /// <summary>
    /// Cộng dung lượng từ lá lên gốc trong một lượt ngược (dựa vào chỉ số con &gt; cha). Thư mục "chỉ đo tổng" giữ nguyên số đo.
    /// Chỉ dùng khi cây chưa có node mồ côi (ngay sau khi dựng).
    /// </summary>
    public void FinalizeSizes()
    {
        for (var i = 0; i < Count; i++)
        {
            ref var n = ref _nodes[i];
            if ((n.Flags & DiskNodeFlags.Directory) != 0 && (n.Flags & DiskNodeFlags.Opaque) == 0) n.Size = 0;
        }
        for (var i = Count - 1; i > 0; i--)
        {
            _nodes[_parents[i]].Size += _nodes[i].Size;
        }
    }

    /// <summary>Cộng chênh lệch dung lượng dọc đường từ <paramref name="i"/> lên gốc (không âm).</summary>
    public void Propagate(int i, long delta)
    {
        if (delta == 0) return;
        for (var cur = i; cur != NoParent; cur = _parents[cur])
        {
            ref var n = ref _nodes[cur];
            n.Size = Math.Max(0, n.Size + delta);
        }
    }

    /// <summary>
    /// Ghép cây con <paramref name="sub"/> (gốc của <paramref name="sub"/> ứng với node <paramref name="i"/>): thay toàn bộ con của
    /// <paramref name="i"/>. <paramref name="propagate"/> thì cập nhật tổng lên tới gốc.
    /// </summary>
    public void Graft(DiskTree sub, int i, bool propagate = true)
    {
        OrphanCount += DescendantCount(i);
        var offset = Count - 1;
        var names = new Dictionary<uint, uint>();
        for (var k = 1; k < sub.Count; k++)
        {
            var n = sub._nodes[k];
            if (!names.TryGetValue(n.NameOffset, out var nameOffset))
            {
                nameOffset = _names.Append(sub._names.Bytes(n.NameOffset));
                names[n.NameOffset] = nameOffset;
            }
            n.NameOffset = nameOffset;
            if (n.ChildCount > 0) n.FirstChild = (uint)(n.FirstChild + offset);
            var p = sub._parents[k];
            AppendNode(n, p == Root ? i : p + offset);
        }
        var subRoot = sub._nodes[0];
        ref var target = ref _nodes[i];
        var oldSize = target.Size;
        target.FirstChild = subRoot.ChildCount > 0 ? (uint)(subRoot.FirstChild + offset) : 0;
        target.ChildCount = subRoot.ChildCount;
        target.Flags = subRoot.Flags;
        target.Size = subRoot.Size;
        if (propagate && _parents[i] != NoParent) Propagate(_parents[i], subRoot.Size - oldSize);
    }

    /// <summary>
    /// Áp kết quả đọc lại một thư mục (một cấp): thư mục con đã biết giữ nguyên cây con, thư mục mới ghép cây vừa quét,
    /// rồi cập nhật tổng lên tới gốc. Đọc lỗi mà không có mục nào thì chỉ đánh dấu không đọc được, giữ nguyên số cũ.
    /// </summary>
    public void Apply(ShallowListing listing, int i)
    {
        if (!listing.Readable && listing.Items.Count == 0)
        {
            AddFlags(i, DiskNodeFlags.Unreadable);
            return;
        }
        var old = Children(i);
        var oldDirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = old.Start; c < old.End; c++)
        {
            if (IsDirectory(c)) oldDirs.TryAdd(Name(c), c);
        }
        var reused = new HashSet<int>();
        var start = Count;
        var grafts = new List<(int Index, DiskTree Sub)>();
        foreach (var item in listing.Items)
        {
            if (item.Reuse && oldDirs.TryGetValue(item.Name, out var oldIndex) && reused.Add(oldIndex))
            {
                var copy = _nodes[oldIndex];
                var newIndex = AppendNode(copy, i);
                var kids = Children(newIndex);
                for (var g = kids.Start; g < kids.End; g++) _parents[g] = newIndex;
            }
            else
            {
                var index = AppendNode(new DiskNode { NameOffset = _names.Append(item.Name), Size = item.Size, Flags = item.Flags }, i);
                if (item.Subtree is { } sub) grafts.Add((index, sub));
            }
        }
        OrphanCount += old.Count;
        for (var c = old.Start; c < old.End; c++)
        {
            if (!reused.Contains(c)) OrphanCount += DescendantCount(c);
        }
        SetChildren(i, start, listing.Items.Count);
        foreach (var (index, sub) in grafts) Graft(sub, index, propagate: false);

        if (listing.Readable) RemoveFlags(i, DiskNodeFlags.Unreadable);
        else AddFlags(i, DiskNodeFlags.Unreadable);
        long total = 0;
        var now = Children(i);
        for (var c = now.Start; c < now.End; c++) total += _nodes[c].Size;
        var delta = total - _nodes[i].Size;
        _nodes[i].Size = total;
        if (_parents[i] != NoParent) Propagate(_parents[i], delta);
    }

    /// <summary>
    /// Gỡ node <paramref name="i"/> (và cây con) khỏi cây sau khi xoá: anh em cuối khối chuyển vào chỗ trống để khối con
    /// vẫn liền nhau, rồi trừ dung lượng dọc đường lên gốc. Chỉ số của anh em cuối khối thay đổi.
    /// </summary>
    public void Remove(int i)
    {
        if (i == Root || i < 0 || i >= Count) return;
        var p = _parents[i];
        var first = (int)_nodes[p].FirstChild;
        var count = (int)_nodes[p].ChildCount;
        if (i < first || i >= first + count) return; // đã mồ côi
        var last = first + count - 1;
        var size = _nodes[i].Size;
        OrphanCount += 1 + DescendantCount(i);
        if (i != last)
        {
            _nodes[i] = _nodes[last];
            _parents[i] = p;
            var kids = Children(i);
            for (var g = kids.Start; g < kids.End; g++) _parents[g] = i;
        }
        SetChildren(p, first, count - 1);
        Propagate(p, -size);
    }

    /// <summary>Dựng lại mảng node chỉ gồm node còn được tham chiếu (thứ tự BFS). Bảng tên giữ nguyên.</summary>
    public void Compact()
    {
        var nodes = new ChunkedArray<DiskNode>();
        var parents = new ChunkedArray<int>();
        nodes.Add(_nodes[0]);
        parents.Add(NoParent);
        var queue = new Queue<(int Old, int New)>();
        queue.Enqueue((0, 0));
        while (queue.Count > 0)
        {
            var (o, nw) = queue.Dequeue();
            var r = Children(o);
            var first = nodes.Count;
            for (var c = r.Start; c < r.End; c++)
            {
                var index = nodes.Add(_nodes[c]);
                parents.Add(nw);
                queue.Enqueue((c, index));
            }
            ref var n = ref nodes[nw];
            n.FirstChild = r.Count > 0 ? (uint)first : 0;
            n.ChildCount = (uint)r.Count;
        }
        _nodes = nodes;
        _parents = parents;
        OrphanCount = 0;
    }

    /// <summary>Thu gọn khi node mồ côi chiếm quá nửa.</summary>
    public bool CompactIfNeeded()
    {
        if (OrphanCount <= Count / 2) return false;
        Compact();
        return true;
    }
}

/// <summary>Khoảng chỉ số con liền nhau của một node (duyệt bằng <c>foreach</c> không cấp phát).</summary>
public readonly record struct ChildRange(int Start, int Count)
{
    public int End => Start + Count;
    public bool IsEmpty => Count == 0;

    public Enumerator GetEnumerator() => new(Start, End);
    public IEnumerable<int> AsEnumerable() => Enumerable.Range(Start, Count);

    public struct Enumerator(int start, int end)
    {
        private int _current = start - 1;
        public readonly int Current => _current;
        public bool MoveNext() => ++_current < end;
    }
}

/// <summary>Kết quả đọc lại một thư mục một cấp (I/O chạy nền, áp vào cây trên luồng giao diện).</summary>
public sealed class ShallowListing(IReadOnlyList<ShallowListing.Item> items, bool readable = true)
{
    /// <param name="Reuse">Thư mục đã có trong cây: giữ nguyên cây con cũ.</param>
    /// <param name="Subtree">Thư mục mới: cây con vừa quét.</param>
    public sealed record Item(string Name, long Size, DiskNodeFlags Flags, bool Reuse = false, DiskTree? Subtree = null);

    public IReadOnlyList<Item> Items { get; } = items;
    public bool Readable { get; } = readable;

    public static readonly ShallowListing Unreadable = new([], false);
}
