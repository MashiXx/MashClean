import Foundation

/// Cờ của một node Space Lens (mục 11.4).
public struct DiskNodeFlags: OptionSet, Sendable, Hashable {
    public let rawValue: UInt8
    public init(rawValue: UInt8) { self.rawValue = rawValue }

    public static let directory = DiskNodeFlags(rawValue: 1 << 0)
    public static let package = DiskNodeFlags(rawValue: 1 << 1)
    public static let symlink = DiskNodeFlags(rawValue: 1 << 2)
    public static let unreadable = DiskNodeFlags(rawValue: 1 << 3)
    /// Điểm gắn volume khác: không đi vào (mục 16.3).
    public static let otherVolume = DiskNodeFlags(rawValue: 1 << 4)
    /// Hard link đã đếm ở chỗ khác nên dung lượng tính 0 (mục 6.4).
    public static let hardLinkDuplicate = DiskNodeFlags(rawValue: 1 << 5)
}

/// Node gọn nhẹ lưu trong mảng phẳng; con của một node nằm liền nhau `[firstChild, firstChild + childCount)` (mục 11.4).
/// Thứ tự trường đặt `size` lên đầu để stride còn 24 byte (thay vì 32).
public struct DiskNode: Sendable, Hashable {
    public var size: UInt64
    public var nameIndex: UInt32
    public var firstChild: UInt32
    public var childCount: UInt32
    public var flags: UInt8

    public init(nameIndex: UInt32, size: UInt64, firstChild: UInt32 = 0, childCount: UInt32 = 0, flags: DiskNodeFlags) {
        self.size = size
        self.nameIndex = nameIndex
        self.firstChild = firstChild
        self.childCount = childCount
        self.flags = flags.rawValue
    }

    public var nodeFlags: DiskNodeFlags { DiskNodeFlags(rawValue: flags) }
}

/// Cây tổng hợp dung lượng của Space Lens: mảng node phẳng + mảng cha + bảng tên chung (bytes UTF-8 liền nhau).
/// Node 0 là gốc, tên của gốc là đường dẫn tuyệt đối. Con luôn có chỉ số lớn hơn cha.
public struct DiskTree: Sendable {
    public static let noParent = UInt32.max

    public private(set) var nodes: [DiskNode]
    public private(set) var parents: [UInt32]
    private var nameBytes: [UInt8]
    /// Vị trí kết thúc của tên thứ i trong `nameBytes` (tên i bắt đầu ở `nameEnds[i-1]`).
    private var nameEnds: [UInt32]
    /// Số node không còn được tham chiếu sau khi quét lại (bỏ đi khi `compacted()`).
    public private(set) var orphanCount = 0

    public init(rootPath: String, flags: DiskNodeFlags = .directory) {
        nodes = []
        parents = []
        nameBytes = []
        nameEnds = []
        let nameIndex = appendName(rootPath)
        nodes.append(DiskNode(nameIndex: nameIndex, size: 0, flags: flags))
        parents.append(Self.noParent)
    }

    public var root: Int { 0 }
    public var count: Int { nodes.count }
    public var rootPath: String { name(of: 0) }
    public var totalSize: UInt64 { nodes[0].size }

    // MARK: Đọc

    public func name(of i: Int) -> String {
        let n = Int(nodes[i].nameIndex)
        let start = n == 0 ? 0 : Int(nameEnds[n - 1])
        let end = Int(nameEnds[n])
        return nameBytes.withUnsafeBufferPointer { String(decoding: UnsafeBufferPointer(rebasing: $0[start..<end]), as: UTF8.self) }
    }

    public func size(of i: Int) -> UInt64 { nodes[i].size }
    public func flags(of i: Int) -> DiskNodeFlags { nodes[i].nodeFlags }
    public func isDirectory(_ i: Int) -> Bool { nodes[i].nodeFlags.contains(.directory) }

    public func children(of i: Int) -> Range<Int> {
        let n = nodes[i]
        guard n.childCount > 0 else { return 0..<0 }
        return Int(n.firstChild)..<Int(n.firstChild + n.childCount)
    }

    public func parent(of i: Int) -> Int? {
        let p = parents[i]
        return p == Self.noParent ? nil : Int(p)
    }

    /// Chuỗi chỉ số từ gốc tới `i` (kể cả hai đầu).
    public func lineage(of i: Int) -> [Int] {
        var chain = [i]
        var cur = i
        while let p = parent(of: cur) {
            chain.append(p)
            cur = p
        }
        return chain.reversed()
    }

    /// Tên các thành phần từ gốc (không gồm gốc) tới `i`.
    public func components(of i: Int) -> [String] { lineage(of: i).dropFirst().map { name(of: $0) } }

    public func path(of i: Int) -> String {
        let parts = components(of: i)
        guard !parts.isEmpty else { return rootPath }
        let base = rootPath == "/" ? "" : rootPath
        return base + "/" + parts.joined(separator: "/")
    }

    public func child(named name: String, of i: Int) -> Int? {
        children(of: i).first { self.name(of: $0) == name }
    }

    public func index(forComponents parts: [String]) -> Int? {
        var cur = 0
        for p in parts {
            guard let c = child(named: p, of: cur) else { return nil }
            cur = c
        }
        return cur
    }

    /// Thành phần tương đối so với gốc; `nil` nếu đường dẫn nằm ngoài cây.
    public func relativeComponents(of path: String) -> [String]? {
        let root = rootPath
        var p = path
        while p.count > 1 && p.hasSuffix("/") { p.removeLast() }
        if p == root { return [] }
        let prefix = root == "/" ? "/" : root + "/"
        guard p.hasPrefix(prefix) else { return nil }
        return p.dropFirst(prefix.count).split(separator: "/").map(String.init)
    }

    public func index(forPath path: String) -> Int? {
        relativeComponents(of: path).flatMap { index(forComponents: $0) }
    }

    /// Thư mục sâu nhất có trong cây chứa `path` (dùng khi FSEvents báo một thư mục mới chưa có trong cây).
    public func nearestDirectory(forPath path: String) -> Int? {
        guard let parts = relativeComponents(of: path) else { return nil }
        var cur = 0
        for p in parts {
            guard let c = child(named: p, of: cur), isDirectory(c) else { break }
            cur = c
        }
        return cur
    }

    /// Bộ nhớ ước tính của cây (byte), để đo mục tiêu ~64 byte/node.
    public var estimatedMemory: Int {
        nodes.capacity * MemoryLayout<DiskNode>.stride + parents.capacity * 4 + nameBytes.capacity + nameEnds.capacity * 4
    }

    // MARK: Dựng

    @discardableResult
    mutating func appendName(_ name: String) -> UInt32 {
        nameBytes.append(contentsOf: name.utf8)
        nameEnds.append(UInt32(nameBytes.count))
        return UInt32(nameEnds.count - 1)
    }

    mutating func setFlags(_ i: Int, insert f: DiskNodeFlags) { nodes[i].flags |= f.rawValue }

    mutating func appendNode(_ node: DiskNode, parent: Int) {
        nodes.append(node)
        parents.append(UInt32(parent))
    }

    mutating func setChildren(of i: Int, first: Int, count: Int) {
        nodes[i].firstChild = count > 0 ? UInt32(first) : 0
        nodes[i].childCount = UInt32(count)
    }

    mutating func setSize(_ i: Int, _ size: UInt64) { nodes[i].size = size }

    /// Thêm khối con cho một node chưa có con (dùng khi dựng tay, ví dụ trong test).
    @discardableResult
    public mutating func appendChildren(of parent: Int, _ items: [(name: String, size: UInt64, flags: DiskNodeFlags)]) -> Range<Int> {
        precondition(nodes[parent].childCount == 0, "Node đã có con")
        let start = nodes.count
        for item in items {
            appendNode(DiskNode(nameIndex: appendName(item.name), size: item.size, flags: item.flags), parent: parent)
        }
        setChildren(of: parent, first: start, count: items.count)
        return start..<nodes.count
    }

    /// Cộng dung lượng từ lá lên gốc. Chỉ dùng khi cây chưa có node mồ côi (sau khi dựng lần đầu).
    public mutating func finalizeSizes() {
        let dir = DiskNodeFlags.directory.rawValue
        for i in nodes.indices where nodes[i].flags & dir != 0 { nodes[i].size = 0 }
        var i = nodes.count - 1
        while i > 0 {
            let p = Int(parents[i])
            nodes[p].size &+= nodes[i].size
            i -= 1
        }
    }

    /// Cộng chênh lệch dung lượng dọc đường từ `i` lên gốc.
    mutating func propagate(from i: Int, delta: Int64) {
        guard delta != 0 else { return }
        var cur: Int? = i
        while let c = cur {
            let v = Int64(clamping: nodes[c].size) + delta
            nodes[c].size = UInt64(max(0, v))
            cur = parent(of: c)
        }
    }

    /// Số node con cháu (không kể chính nó).
    public func descendantCount(of i: Int) -> Int {
        var total = 0
        var stack = [i]
        while let n = stack.popLast() {
            let r = children(of: n)
            total += r.count
            for c in r where nodes[c].childCount > 0 { stack.append(c) }
        }
        return total
    }

    /// Ghép cây con `sub` (gốc của `sub` ứng với node `i`): thay toàn bộ con của `i`.
    /// `propagate == true` thì cập nhật tổng lên tới gốc (quét lại một thư mục, mục 23.6).
    public mutating func graft(_ sub: DiskTree, at i: Int, propagate: Bool = true) {
        orphanCount += descendantCount(of: i)
        let nameBase = UInt32(nameEnds.count)
        let byteBase = UInt32(nameBytes.count)
        nameBytes.append(contentsOf: sub.nameBytes)
        nameEnds.append(contentsOf: sub.nameEnds.map { $0 + byteBase })

        let offset = UInt32(nodes.count) - 1
        nodes.reserveCapacity(nodes.count + sub.nodes.count)
        parents.reserveCapacity(parents.count + sub.nodes.count)
        for k in 1..<max(1, sub.nodes.count) {
            var n = sub.nodes[k]
            n.nameIndex += nameBase
            if n.childCount > 0 { n.firstChild += offset }
            nodes.append(n)
            let p = sub.parents[k]
            parents.append(p == 0 ? UInt32(i) : p + offset)
        }
        let subRoot = sub.nodes[0]
        let oldSize = nodes[i].size
        nodes[i].firstChild = subRoot.childCount > 0 ? subRoot.firstChild + offset : 0
        nodes[i].childCount = subRoot.childCount
        nodes[i].flags = (nodes[i].flags & DiskNodeFlags.package.rawValue) | subRoot.flags
        nodes[i].size = subRoot.size
        if propagate, let p = parent(of: i) {
            self.propagate(from: p, delta: Int64(clamping: subRoot.size) - Int64(clamping: oldSize))
        }
    }

    /// Áp kết quả đọc lại một thư mục (chỉ một cấp): thư mục con đã biết giữ nguyên cây con,
    /// thư mục mới ghép cây con vừa quét, rồi cập nhật tổng lên tới gốc.
    public mutating func apply(_ listing: ShallowListing, at i: Int) {
        let old = children(of: i)
        var oldDirs: [String: Int] = [:]
        for c in old where isDirectory(c) { oldDirs[name(of: c)] = c }
        var reused = Set<Int>()

        let start = nodes.count
        var grafts: [(Int, DiskTree)] = []
        for item in listing.items {
            if item.reuse, let oldIndex = oldDirs[item.name] {
                appendNode(nodes[oldIndex], parent: i)
                reused.insert(oldIndex)
                let newIndex = nodes.count - 1
                for g in children(of: newIndex) { parents[g] = UInt32(newIndex) }
            } else {
                appendNode(DiskNode(nameIndex: appendName(item.name), size: item.size, flags: item.flags), parent: i)
                if let sub = item.subtree { grafts.append((nodes.count - 1, sub)) }
            }
        }
        orphanCount += old.count
        for c in old where !reused.contains(c) { orphanCount += descendantCount(of: c) }
        setChildren(of: i, first: start, count: listing.items.count)
        for (index, sub) in grafts { graft(sub, at: index, propagate: false) }

        if !listing.readable { setFlags(i, insert: .unreadable) }
        var total: UInt64 = 0
        for c in children(of: i) { total &+= nodes[c].size }
        let delta = Int64(clamping: total) - Int64(clamping: nodes[i].size)
        nodes[i].size = total
        if let p = parent(of: i) { propagate(from: p, delta: delta) }
    }

    /// Dựng lại cây chỉ gồm node còn được tham chiếu (theo thứ tự BFS).
    public func compacted() -> DiskTree {
        var copy = self
        copy.nodes = [nodes[0]]
        copy.parents = [Self.noParent]
        copy.nodes.reserveCapacity(nodes.count - orphanCount)
        copy.parents.reserveCapacity(nodes.count - orphanCount)
        copy.orphanCount = 0
        var queue: [(old: Int, new: Int)] = [(0, 0)]
        var head = 0
        while head < queue.count {
            let (o, nw) = queue[head]
            head += 1
            let r = children(of: o)
            let first = copy.nodes.count
            for c in r {
                copy.nodes.append(nodes[c])
                copy.parents.append(UInt32(nw))
                queue.append((c, copy.nodes.count - 1))
            }
            copy.setChildren(of: nw, first: first, count: r.count)
        }
        return copy
    }

    /// Thu gọn khi node mồ côi chiếm quá nửa.
    public mutating func compactIfNeeded() {
        if orphanCount > nodes.count / 2 { self = compacted() }
    }
}

/// Kết quả đọc lại một thư mục một cấp (I/O chạy nền, áp vào cây trên main actor).
public struct ShallowListing: Sendable {
    public struct Item: Sendable {
        public var name: String
        public var size: UInt64
        public var flags: DiskNodeFlags
        /// Thư mục đã có trong cây: giữ nguyên cây con cũ.
        public var reuse: Bool
        /// Thư mục mới: cây con vừa quét.
        public var subtree: DiskTree?

        public init(name: String, size: UInt64, flags: DiskNodeFlags, reuse: Bool = false, subtree: DiskTree? = nil) {
            self.name = name
            self.size = size
            self.flags = flags
            self.reuse = reuse
            self.subtree = subtree
        }
    }

    public var items: [Item]
    public var readable: Bool

    public init(items: [Item], readable: Bool = true) {
        self.items = items
        self.readable = readable
    }
}
