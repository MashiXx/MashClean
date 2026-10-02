import Darwin
import Foundation
import SweepCore
import System

/// Kết quả đo một đường dẫn.
public struct PathMeasurement: Sendable, Hashable {
    public var allocatedSize: UInt64
    public var itemCount: Int
    public var newestModification: Date
    public var newestAccess: Date?
    public var isDirectory: Bool
    public var exists: Bool

    public static let missing = PathMeasurement(allocatedSize: 0, itemCount: 0, newestModification: .distantPast, newestAccess: nil, isDirectory: false, exists: false)

    /// Lần dùng gần nhất của mục (sửa hoặc truy cập).
    public var lastUse: Date { max(newestModification, newestAccess ?? .distantPast) }
}

/// Theo dõi hard link theo cặp `(volume, fileID)` để chỉ đếm một lần (mục 6.4).
public final class HardLinkTracker: @unchecked Sendable {
    private struct Key: Hashable { let device: Int32; let fileID: UInt64 }
    private var seen = Set<Key>()
    private let lock = NSLock()

    public init() {}

    /// Trả về `true` nếu đây là lần đầu gặp inode này.
    public func firstSighting(device: Int32, fileID: UInt64) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return seen.insert(Key(device: device, fileID: fileID)).inserted
    }
}

/// Bộ đếm số file đã duyệt, dùng cho thanh tiến độ ("số file đã duyệt", mục 5.4).
public final class VisitCounter: @unchecked Sendable {
    private let value = Locked<Int>(0)
    public init() {}
    public func add(_ n: Int) { value.withLock { $0 += n } }
    public var count: Int { value.current }
}

/// Dịch vụ hệ thống tệp dùng chung cho scan task và clean engine.
public final class FileSystemService: Sendable {
    public let home: URL
    public let walker: BulkWalker

    public init(home: URL = .userHome) {
        self.home = home
        walker = BulkWalker()
    }

    // MARK: Thông tin một mục

    public func exists(_ url: URL) -> Bool {
        var st = stat()
        return lstat(url.path, &st) == 0
    }

    public func isDirectory(_ url: URL) -> Bool {
        var st = stat()
        return lstat(url.path, &st) == 0 && st.st_mode & S_IFMT == S_IFDIR
    }

    /// Đọc một mục bằng `lstat` (không theo symlink).
    public func entry(at url: URL) -> Entry? {
        var st = stat()
        guard lstat(url.path, &st) == 0 else { return nil }
        let isDir = st.st_mode & S_IFMT == S_IFDIR
        let dataless = st.st_flags & UInt32(SF_DATALESS) != 0
        return Entry(
            path: FilePath(url.path), isDirectory: isDir, isSymlink: st.st_mode & S_IFMT == S_IFLNK,
            allocatedSize: dataless ? 0 : UInt64(st.st_blocks) * 512, logicalSize: UInt64(max(0, st.st_size)),
            modificationDate: Date(timeIntervalSince1970: Double(st.st_mtimespec.tv_sec)),
            accessDate: Date(timeIntervalSince1970: Double(st.st_atimespec.tv_sec)),
            fileID: st.st_ino, deviceID: st.st_dev, linkCount: UInt32(st.st_nlink),
            isPackage: isDir && PackageExtensions.isPackage(name: url.lastPathComponent),
            isDataless: dataless, isHidden: url.lastPathComponent.hasPrefix(".")
        )
    }

    /// Nội dung trực tiếp của một thư mục (một cấp).
    public func children(of url: URL) -> [Entry] {
        var result: [Entry] = []
        try? BulkReader.readDirectory(url.path) { raw in
            let full = url.path == "/" ? "/" + raw.name : url.path + "/" + raw.name
            result.append(Entry(
                path: FilePath(full), isDirectory: raw.isDirectory, isSymlink: raw.isSymlink,
                allocatedSize: raw.isDataless ? 0 : raw.allocatedSize, logicalSize: raw.logicalSize,
                modificationDate: raw.modificationDate, accessDate: raw.accessDate, fileID: raw.fileID,
                deviceID: raw.deviceID, linkCount: raw.linkCount,
                isPackage: raw.isDirectory && PackageExtensions.isPackage(name: raw.name),
                isDataless: raw.isDataless, isHidden: raw.isHidden
            ))
        }
        return result
    }

    // MARK: Đo dung lượng

    /// Đo dung lượng thực chiếm (allocated) của file hoặc thư mục, đệ quy, không theo symlink, không qua volume khác.
    /// - Parameters:
    ///   - tracker: dùng chung trong một phiên quét để không đếm hard link hai lần.
    ///   - counter: cộng dồn số mục đã duyệt cho thanh tiến độ.
    public func measure(_ url: URL, tracker: HardLinkTracker? = nil, counter: VisitCounter? = nil, skip: (@Sendable (String) -> Bool)? = nil) throws -> PathMeasurement {
        guard let root = entry(at: url) else { return .missing }
        guard root.isDirectory else {
            if root.linkCount > 1, let tracker, !tracker.firstSighting(device: root.deviceID, fileID: root.fileID) {
                return PathMeasurement(allocatedSize: 0, itemCount: 1, newestModification: root.modificationDate, newestAccess: root.accessDate, isDirectory: false, exists: true)
            }
            counter?.add(1)
            return PathMeasurement(allocatedSize: root.allocatedSize, itemCount: 1, newestModification: root.modificationDate,
                               newestAccess: root.accessDate, isDirectory: false, exists: true)
        }
        var total: UInt64 = 0
        var count = 0
        var newestMod = root.modificationDate
        var newestAccess: Date? = nil
        var pending = 0
        try walker.walkSync(url, options: .default) { e in
            if e.isDirectory, let skip, skip(e.path.string) { return .skipDescendants }
            count += 1
            pending += 1
            if pending >= 500 { counter?.add(pending); pending = 0 }
            if e.modificationDate > newestMod { newestMod = e.modificationDate }
            if let a = e.accessDate, !e.isDirectory, a > (newestAccess ?? .distantPast) { newestAccess = a }
            if !e.isDirectory {
                if e.linkCount > 1, let tracker, !tracker.firstSighting(device: e.deviceID, fileID: e.fileID) { return .continue }
                total += e.allocatedSize
            }
            return .continue
        }
        counter?.add(pending)
        return PathMeasurement(allocatedSize: total, itemCount: count, newestModification: newestMod, newestAccess: newestAccess, isDirectory: true, exists: true)
    }

    /// Phiên bản đồng bộ của `measureParallel`, chia việc theo thư mục con cấp 1 trên `concurrentPerform`.
    /// Dùng trong code đồng bộ (RuleEvaluator) để một thư mục rất lớn (vd DerivedData) không chạy một luồng.
    public func measureConcurrently(_ url: URL, tracker: HardLinkTracker? = nil, counter: VisitCounter? = nil,
                                    skip: (@Sendable (String) -> Bool)? = nil) throws -> PathMeasurement {
        guard let root = entry(at: url), root.isDirectory else { return try measure(url, tracker: tracker, counter: counter, skip: skip) }
        let kids = children(of: url).filter { !(skip?($0.path.string) ?? false) }
        guard kids.count > 1 else { return try measure(url, tracker: tracker, counter: counter, skip: skip) }
        let limit = recommendedConcurrency(for: url)
        let next = Locked(0)
        let parts = Locked<[PathMeasurement]>([])
        let failure = Locked<(any Error)?>(nil)
        DispatchQueue.concurrentPerform(iterations: min(limit, kids.count)) { _ in
            while failure.current == nil {
                let i = next.withLock { v -> Int in defer { v += 1 }; return v }
                guard i < kids.count else { return }
                do {
                    let m = try measure(kids[i].url, tracker: tracker, counter: counter, skip: skip)
                    parts.withLock { $0.append(m) }
                } catch is CancellationError {
                    failure.withLock { $0 = CancellationError() }
                } catch {
                    continue   // thư mục con không đọc được: bỏ qua
                }
            }
        }
        if let error = failure.current { throw error }
        var total = PathMeasurement(allocatedSize: 0, itemCount: kids.count, newestModification: root.modificationDate, newestAccess: nil, isDirectory: true, exists: true)
        for m in parts.current {
            total.allocatedSize += m.allocatedSize
            if m.isDirectory { total.itemCount += m.itemCount }
            if m.newestModification > total.newestModification { total.newestModification = m.newestModification }
            if let a = m.newestAccess, a > (total.newestAccess ?? .distantPast) { total.newestAccess = a }
        }
        return total
    }

    /// Đo song song theo thư mục con cấp 1, giới hạn đồng thời theo loại ổ (mục 16.3).
    public func measureParallel(_ url: URL, tracker: HardLinkTracker? = nil, counter: VisitCounter? = nil) async throws -> PathMeasurement {
        guard let root = entry(at: url), root.isDirectory else { return try measure(url, tracker: tracker, counter: counter) }
        let kids = children(of: url)
        let limit = recommendedConcurrency(for: url)
        var total = PathMeasurement(allocatedSize: 0, itemCount: kids.count, newestModification: root.modificationDate, newestAccess: nil, isDirectory: true, exists: true)
        try await withThrowingTaskGroup(of: PathMeasurement.self) { group in
            var iterator = kids.makeIterator()
            func addNext() {
                guard let kid = iterator.next() else { return }
                group.addTask(priority: .utility) { [self] in
                    try Task.checkCancellation()
                    return try measure(kid.url, tracker: tracker, counter: counter)
                }
            }
            for _ in 0..<limit { addNext() }
            while let m = try await group.next() {
                total.allocatedSize += m.allocatedSize
                total.itemCount += max(0, m.itemCount - 1) + (m.isDirectory ? 1 : 0)
                if m.newestModification > total.newestModification { total.newestModification = m.newestModification }
                if let a = m.newestAccess, a > (total.newestAccess ?? .distantPast) { total.newestAccess = a }
                addNext()
            }
        }
        return total
    }

    // MARK: Volume

    public func volume(for url: URL) -> VolumeInfo? { VolumeInfo(url: url) }

    /// 4 trên SSD, 1 trên HDD (mục 5.3).
    public func recommendedConcurrency(for url: URL) -> Int {
        switch StorageMedium.detect(for: url) {
        case .rotational: 1
        case .solidState, .unknown: 4
        }
    }

    // MARK: iCloud

    /// File dataless (chưa tải về) không giải phóng ổ khi xoá (mục 6.4).
    public func isDataless(_ url: URL) -> Bool {
        if let e = entry(at: url), e.isDataless { return true }
        let status = try? url.resourceValues(forKeys: [.ubiquitousItemDownloadingStatusKey]).ubiquitousItemDownloadingStatus
        return status == .notDownloaded
    }
}
