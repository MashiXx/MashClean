import Darwin
import FileSystemKit
import Foundation
import SweepCore

/// Tiến độ quét Space Lens: số mục đã duyệt và dung lượng đã cộng (UI đọc định kỳ).
public final class DiskScanProgress: Sendable {
    public struct Snapshot: Sendable, Equatable {
        public var items: Int
        public var bytes: UInt64
    }

    private let state = Locked(Snapshot(items: 0, bytes: 0))
    public init() {}

    public func add(items: Int, bytes: UInt64) {
        state.withLock {
            $0.items += items
            $0.bytes &+= bytes
        }
    }

    public var snapshot: Snapshot { state.current }
}

/// Quét cây dung lượng cho Space Lens (mục 11.4, 23.6): `getattrlistbulk`, song song theo thư mục con cấp 1,
/// không theo symlink, không qua volume khác, đếm hard link một lần, bỏ qua file dataless.
public struct DiskScanner: Sendable {
    public let fileSystem: FileSystemService

    public init(fileSystem: FileSystemService) { self.fileSystem = fileSystem }

    /// Quét toàn bộ `root`. QoS mặc định `.userInitiated` vì người dùng đang xem (mục 16.3).
    public func scan(root: URL, priority: TaskPriority = .userInitiated, progress: DiskScanProgress? = nil) async throws -> DiskTree {
        let rootPath = PathPolicy.realPath(root.path) ?? root.path
        var st = stat()
        guard lstat(rootPath, &st) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .ENOENT) }
        guard st.st_mode & S_IFMT == S_IFDIR else {
            var single = DiskTree(rootPath: rootPath, flags: [])
            single.setSize(0, UInt64(st.st_blocks) * 512)
            return single
        }
        let scope = Self.scope(rootPath: rootPath, device: st.st_dev)
        let tracker = HardLinkTracker()
        var builder = DiskTreeBuilder(tree: DiskTree(rootPath: rootPath), scope: scope, tracker: tracker, progress: progress)

        // Mở rộng tuần tự 1–2 cấp để có đủ việc chia cho các luồng.
        let limit = max(1, fileSystem.recommendedConcurrency(for: URL(fileURLWithPath: rootPath)))
        var frontier = try builder.read(index: 0, path: rootPath)
        if frontier.count < limit * 2 {
            var next: [(Int, String)] = []
            for (i, p) in frontier { next += try builder.read(index: i, path: p) }
            frontier = next
        }
        var tree = builder.tree
        let work = frontier
        let results = try await withThrowingTaskGroup(of: (Int, DiskTree).self) { group in
            var iterator = work.makeIterator()
            var collected: [(Int, DiskTree)] = []
            func addNext() -> Bool {
                guard let (index, path) = iterator.next() else { return false }
                group.addTask(priority: priority) {
                    try Task.checkCancellation()
                    var sub = DiskTreeBuilder(tree: DiskTree(rootPath: path), scope: scope, tracker: tracker, progress: progress)
                    try sub.build()
                    return (index, sub.tree)
                }
                return true
            }
            for _ in 0..<limit where !addNext() { break }
            while let r = try await group.next() {
                collected.append(r)
                _ = addNext()
            }
            return collected
        }
        for (index, sub) in results { tree.graft(sub, at: index, propagate: false) }
        tree.finalizeSizes()
        return tree
    }

    /// Quét đồng bộ một thư mục (dùng cho quét lại). Tracker mới: hard link có thể bị đếm lại ở phần khác của cây.
    public func scanSubtree(path: String, progress: DiskScanProgress? = nil) throws -> DiskTree {
        var st = stat()
        guard lstat(path, &st) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .ENOENT) }
        let treeRoot = Self.scopeRoot(for: path)
        var rootStat = stat()
        let device = lstat(treeRoot, &rootStat) == 0 ? rootStat.st_dev : st.st_dev
        var builder = DiskTreeBuilder(tree: DiskTree(rootPath: path), scope: Self.scope(rootPath: treeRoot, device: device),
                                      tracker: HardLinkTracker(), progress: progress)
        try builder.build()
        builder.tree.finalizeSizes()
        return builder.tree
    }

    /// Đọc lại một thư mục một cấp; thư mục con có tên trong `knownDirectories` giữ cây cũ, thư mục mới quét đầy đủ.
    public func refreshListing(path: String, knownDirectories: Set<String>) throws -> ShallowListing {
        var probe = DiskTree(rootPath: path)
        let treeRoot = Self.scopeRoot(for: path)
        var rootStat = stat()
        guard lstat(treeRoot, &rootStat) == 0 else { return ShallowListing(items: [], readable: false) }
        var builder = DiskTreeBuilder(tree: probe, scope: Self.scope(rootPath: treeRoot, device: rootStat.st_dev),
                                      tracker: HardLinkTracker(), progress: nil)
        let subdirs: [(Int, String)]
        do {
            subdirs = try builder.read(index: 0, path: path)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            return ShallowListing(items: [], readable: false)
        }
        probe = builder.tree
        let descend = Dictionary(uniqueKeysWithValues: subdirs)
        var items: [ShallowListing.Item] = []
        for c in probe.children(of: 0) {
            let name = probe.name(of: c)
            let flags = probe.flags(of: c)
            if flags.contains(.directory), knownDirectories.contains(name) {
                items.append(.init(name: name, size: 0, flags: flags, reuse: true))
            } else if let subPath = descend[c] {
                try Task.checkCancellation()
                let sub = (try? scanSubtree(path: subPath)) ?? DiskTree(rootPath: subPath, flags: [.directory, .unreadable])
                items.append(.init(name: name, size: sub.totalSize, flags: flags, subtree: sub))
            } else {
                items.append(.init(name: name, size: probe.size(of: c), flags: flags))
            }
        }
        return ShallowListing(items: items, readable: !probe.flags(of: 0).contains(.unreadable))
    }

    // MARK: Phạm vi volume

    struct Scope: Sendable {
        var devices: Set<Int32>
        var excluded: Set<String>
    }

    /// Quét `/`: volume dữ liệu nối qua firmlink nên cho phép cả thiết bị của `/System/Volumes/Data`
    /// nhưng không đi vào đường dẫn thật của nó (tránh đếm hai lần).
    static func scope(rootPath: String, device: dev_t) -> Scope {
        var devices: Set<Int32> = [device]
        var excluded = Set<String>()
        if rootPath == "/" {
            var st = stat()
            if lstat("/System/Volumes/Data", &st) == 0 { devices.insert(st.st_dev) }
            excluded.insert("/System/Volumes/Data")
        }
        return Scope(devices: devices, excluded: excluded)
    }

    /// Gốc dùng để xác định volume khi quét lại: điểm gắn của volume chứa `path`.
    static func scopeRoot(for path: String) -> String {
        var fs = statfs()
        guard statfs(path, &fs) == 0 else { return path }
        let mount = withUnsafePointer(to: &fs.f_mntonname) { String(cString: UnsafeRawPointer($0).assumingMemoryBound(to: CChar.self)) }
        return mount.hasPrefix("/System/Volumes/Data") ? "/" : mount
    }
}

/// Dựng cây bằng một luồng, duyệt bằng ngăn xếp (không đệ quy).
struct DiskTreeBuilder {
    var tree: DiskTree
    let scope: DiskScanner.Scope
    let tracker: HardLinkTracker
    let progress: DiskScanProgress?
    private var interned: [String: UInt32] = [:]
    private var buffer: [BulkEntry] = []
    private var directoriesRead = 0

    init(tree: DiskTree, scope: DiskScanner.Scope, tracker: HardLinkTracker, progress: DiskScanProgress?) {
        self.tree = tree
        self.scope = scope
        self.tracker = tracker
        self.progress = progress
    }

    mutating func build() throws {
        var stack: [(Int, String)] = [(0, tree.rootPath)]
        while let (index, path) = stack.popLast() {
            stack += try read(index: index, path: path)
        }
    }

    /// Bảng tên chung: tên ngắn hay lặp (Contents, Info.plist, *.lproj...) chỉ lưu một lần.
    private mutating func nameIndex(_ name: String) -> UInt32 {
        guard name.utf8.count <= 24 else { return tree.appendName(name) }
        if let i = interned[name] { return i }
        let i = tree.appendName(name)
        if interned.count < 200_000 { interned[name] = i }
        return i
    }

    /// Đọc một thư mục, thêm khối con vào cây, trả về các thư mục con cần đi tiếp.
    mutating func read(index: Int, path: String) throws -> [(Int, String)] {
        directoriesRead += 1
        if directoriesRead % 32 == 0 { try Task.checkCancellation() }
        let fd = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else {
            tree.setFlags(index, insert: .unreadable)
            return []
        }
        defer { close(fd) }
        var st = stat()
        if fstat(fd, &st) == 0, !scope.devices.contains(st.st_dev) {
            tree.setFlags(index, insert: .otherVolume)
            return []
        }
        buffer.removeAll(keepingCapacity: true)
        do {
            try BulkReader.readDirectory(fd: fd) { buffer.append($0) }
        } catch {
            tree.setFlags(index, insert: .unreadable)
            if buffer.isEmpty { return [] }
        }

        let start = tree.count
        var subdirs: [(Int, String)] = []
        var bytes: UInt64 = 0
        let entries = buffer
        for e in entries {
            if e.isDataless && !e.isDirectory { continue }   // chưa tải về máy: xoá không giải phóng ổ (mục 6.4)
            var flags: DiskNodeFlags = []
            var size: UInt64 = 0
            var full: String?
            if e.isSymlink {
                flags.insert(.symlink)
                size = e.allocatedSize
            } else if e.isDirectory {
                flags.insert(.directory)
                if PackageExtensions.isPackage(name: e.name) { flags.insert(.package) }
                let p = path == "/" ? "/" + e.name : path + "/" + e.name
                if scope.excluded.contains(p) { flags.insert(.otherVolume) } else { full = p }
            } else {
                size = e.allocatedSize
                if e.linkCount > 1, !tracker.firstSighting(device: e.deviceID, fileID: e.fileID) {
                    size = 0
                    flags.insert(.hardLinkDuplicate)
                }
            }
            tree.appendNode(DiskNode(nameIndex: nameIndex(e.name), size: size, flags: flags), parent: index)
            if let full { subdirs.append((tree.count - 1, full)) }
            bytes &+= size
        }
        tree.setChildren(of: index, first: start, count: tree.count - start)
        progress?.add(items: tree.count - start, bytes: bytes)
        return subdirs
    }
}
