import Darwin
import FileSystemKit
import Foundation
import SweepCore

/// Một file ứng viên khi tìm trùng lặp.
public struct DuplicateCandidate: Sendable, Hashable {
    public var path: String
    /// Kích thước logic (dùng để so nội dung).
    public var size: UInt64
    /// Dung lượng thực chiếm (dùng để ước tính giải phóng).
    public var allocatedSize: UInt64
    public var device: Int32
    public var fileID: UInt64
    public var modified: Date
    /// Chỉ số thư mục gốc chứa file (để đặt `allowedRoots`).
    public var rootIndex: Int

    public init(path: String, size: UInt64, allocatedSize: UInt64, device: Int32 = 0, fileID: UInt64, modified: Date = .distantPast, rootIndex: Int = 0) {
        self.path = path
        self.size = size
        self.allocatedSize = allocatedSize
        self.device = device
        self.fileID = fileID
        self.modified = modified
        self.rootIndex = rootIndex
    }

    public var name: String { (path as NSString).lastPathComponent }
}

/// Nhóm theo dung lượng / hash (hàm thuần, mục 11.8).
public enum DuplicateGrouping {
    public static let defaultMinimumSize: UInt64 = 1_000_000

    /// Bước 1: nhóm theo dung lượng chính xác; bỏ file nhỏ hơn `minimumSize`, bỏ nhóm chỉ có 1 file.
    /// Hard link cùng inode (cùng `(device, fileID)`) chỉ giữ một bản: xoá một link không giải phóng gì.
    public static func bySize(_ candidates: [DuplicateCandidate], minimumSize: UInt64 = defaultMinimumSize) -> [[DuplicateCandidate]] {
        var seen = Set<InodeKey>()
        var bySize: [UInt64: [DuplicateCandidate]] = [:]
        for c in candidates where c.size >= minimumSize {
            guard seen.insert(InodeKey(device: c.device, fileID: c.fileID)).inserted else { continue }
            bySize[c.size, default: []].append(c)
        }
        return bySize.values.filter { $0.count > 1 }.sorted { $0[0].size > $1[0].size }
    }

    /// Bước 2, 3: chia lại mỗi nhóm theo khoá (hash). Khoá `nil` (không đọc được) bị loại.
    public static func regroup<Key: Hashable>(_ groups: [[DuplicateCandidate]], key: (DuplicateCandidate) -> Key?) -> [[DuplicateCandidate]] {
        var result: [[DuplicateCandidate]] = []
        for group in groups {
            var buckets: [Key: [DuplicateCandidate]] = [:]
            var order: [Key] = []
            for c in group {
                guard let k = key(c) else { continue }
                if buckets[k] == nil { order.append(k) }
                buckets[k, default: []].append(c)
            }
            for k in order where buckets[k]!.count > 1 { result.append(buckets[k]!) }
        }
        return result
    }

    struct InodeKey: Hashable {
        let device: Int32
        let fileID: UInt64
    }
}

/// Một file trong nhóm trùng lặp cuối cùng.
public struct DuplicateFile: Sendable, Hashable {
    public var candidate: DuplicateCandidate
    public var created: Date
    /// Clone APFS: dùng chung block nên xoá không giải phóng dung lượng (mục 6.4, 11.8).
    public var isClone: Bool

    public var path: String { candidate.path }
}

public struct DuplicateGroup: Sendable, Hashable {
    public var size: UInt64
    public var hash: String
    public var files: [DuplicateFile]
}

/// Tìm file trùng lặp theo 3 bước để giảm I/O (mục 11.8).
public struct DuplicateFinder: Sendable {
    public enum Stage: Int, Sendable, CaseIterable, Comparable {
        case collecting, groupingBySize, quickHash, fullHash, checkingClones, done

        public var title: String {
            switch self {
            case .collecting: "Liệt kê file"
            case .groupingBySize: "Nhóm theo dung lượng"
            case .quickHash: "Băm nhanh đầu và cuối file"
            case .fullHash: "Băm toàn bộ (SHA-256)"
            case .checkingClones: "Kiểm tra clone APFS"
            case .done: "Xong"
            }
        }

        public var step: Int { rawValue + 1 }
        public static func < (l: Stage, r: Stage) -> Bool { l.rawValue < r.rawValue }
    }

    public struct Progress: Sendable, Equatable {
        public var stage: Stage
        public var processed: Int
        public var total: Int
        public var filesSeen: Int

        public static let start = Progress(stage: .collecting, processed: 0, total: 0, filesSeen: 0)

        /// Tiến độ tổng 0...1 theo trọng số từng bước.
        public var fraction: Double {
            let within = total > 0 ? Double(processed) / Double(total) : 0
            switch stage {
            case .collecting: return 0.02
            case .groupingBySize: return 0.25
            case .quickHash: return 0.25 + 0.25 * within
            case .fullHash: return 0.5 + 0.45 * within
            case .checkingClones: return 0.95 + 0.05 * within
            case .done: return 1
            }
        }
    }

    public struct Options: Sendable {
        public var roots: [URL]
        public var minimumSize: UInt64
        /// Thư mục không đi vào (mặc định `~/Library`).
        public var excludedPrefixes: [String]
        public var skipHidden: Bool
        public var policy: PathPolicy?
        public var ignore: IgnoreList
        public var concurrency: Int

        public init(roots: [URL], minimumSize: UInt64 = DuplicateGrouping.defaultMinimumSize, excludedPrefixes: [String] = [],
                    skipHidden: Bool = true, policy: PathPolicy? = nil, ignore: IgnoreList = .empty, concurrency: Int = 4) {
            self.roots = roots
            self.minimumSize = minimumSize
            self.excludedPrefixes = excludedPrefixes
            self.skipHidden = skipHidden
            self.policy = policy
            self.ignore = ignore
            self.concurrency = max(1, concurrency)
        }
    }

    public let options: Options
    public let walker = BulkWalker()

    public init(options: Options) { self.options = options }

    public func find(progress: @escaping @Sendable (Progress) -> Void = { _ in }) async throws -> [DuplicateGroup] {
        var state = Progress.start
        progress(state)

        // Bước 0: liệt kê file (không vào package, thư mục ẩn, ~/Library, vùng cấm).
        let candidates = try collect { seen in
            state.filesSeen = seen
            progress(state)
        }
        try Task.checkCancellation()

        // Bước 1
        state.stage = .groupingBySize
        progress(state)
        let sizeGroups = DuplicateGrouping.bySize(candidates, minimumSize: options.minimumSize)

        // Bước 2: xxHash3 64 KB đầu + 64 KB cuối.
        let quickTargets = sizeGroups.flatMap { $0 }
        state.stage = .quickHash
        state.processed = 0
        state.total = quickTargets.count
        progress(state)
        let quick = try await hashAll(quickTargets, stage: .quickHash, state: state, progress: progress) { c in
            try FileHasher.edgeHash(path: c.path, size: c.size).description
        }
        let quickGroups = DuplicateGrouping.regroup(sizeGroups) { quick[$0.path] }

        // Bước 3: SHA-256 toàn bộ. File ≤ 128 KB đã băm hết ở bước 2 nên không cần đọc lại.
        let fullTargets = quickGroups.flatMap { $0 }.filter { $0.size > UInt64(FileHasher.edgeChunk * 2) }
        state.stage = .fullHash
        state.processed = 0
        state.total = fullTargets.count
        progress(state)
        let full = try await hashAll(fullTargets, stage: .fullHash, state: state, progress: progress) { c in
            try FileHasher.sha256(path: c.path)
        }
        let finalGroups = DuplicateGrouping.regroup(quickGroups) { c in
            c.size > UInt64(FileHasher.edgeChunk * 2) ? full[c.path] : quick[c.path].map { "xxh3:" + $0 }
        }

        // Clone APFS.
        state.stage = .checkingClones
        state.processed = 0
        state.total = finalGroups.count
        progress(state)
        var result: [DuplicateGroup] = []
        for (i, group) in finalGroups.enumerated() {
            if i % 50 == 0 { try Task.checkCancellation() }
            if let g = Self.makeGroup(group, hash: full[group[0].path] ?? quick[group[0].path] ?? "") { result.append(g) }
            state.processed = i + 1
            if i % 20 == 0 { progress(state) }
        }
        state.stage = .done
        progress(state)
        return result.sorted { $0.wastedBytes > $1.wastedBytes }
    }

    /// Đánh dấu clone và bỏ nhóm mà mọi bản đều là clone của nhau (xoá không giải phóng gì).
    static func makeGroup(_ members: [DuplicateCandidate], hash: String) -> DuplicateGroup? {
        let infos = members.map { CloneInfo.read(path: $0.path) }
        var cloneIDCounts: [UInt64: Int] = [:]
        for info in infos { if let id = info?.cloneID, id != 0 { cloneIDCounts[id, default: 0] += 1 } }
        var files: [DuplicateFile] = []
        for (c, info) in zip(members, infos) {
            let sharedID = info.map { $0.cloneID != 0 && (cloneIDCounts[$0.cloneID] ?? 0) > 1 } ?? false
            let isClone = sharedID || (info?.sharesAllBlocks ?? false)
            files.append(DuplicateFile(candidate: c, created: birthDate(c.path) ?? c.modified, isClone: isClone))
        }
        let independent = files.filter { !$0.isClone }.count + (files.contains { $0.isClone } ? 1 : 0)
        guard independent >= 2 else { return nil }
        return DuplicateGroup(size: members[0].size, hash: hash, files: files)
    }

    static func birthDate(_ path: String) -> Date? {
        var st = stat()
        guard lstat(path, &st) == 0 else { return nil }
        let ts = st.st_birthtimespec
        return Date(timeIntervalSince1970: Double(ts.tv_sec) + Double(ts.tv_nsec) / 1e9)
    }

    private func collect(onProgress: (Int) -> Void) throws -> [DuplicateCandidate] {
        var result: [DuplicateCandidate] = []
        var seen = 0
        var options = WalkOptions(skipPackageContents: true, skipHidden: self.options.skipHidden)
        options.cancellationCheckInterval = 1000
        let excluded = self.options.excludedPrefixes
        let policy = self.options.policy
        let ignore = self.options.ignore
        let minimum = self.options.minimumSize
        for (rootIndex, root) in self.options.roots.enumerated() {
            guard (try? walker.walkSync(root, options: options, visit: { e in
                seen += 1
                if seen % 2000 == 0 { onProgress(seen) }
                let path = e.path.string
                if e.isDirectory {
                    if e.isPackage { return .skipDescendants }
                    if excluded.contains(where: { path == $0 || path.hasPrefix($0 + "/") }) { return .skipDescendants }
                    if let policy, policy.shouldSkipDescending(into: path) { return .skipDescendants }
                    if ignore.ignores(path: path) { return .skipDescendants }
                    return .continue
                }
                guard !e.isSymlink, !e.isDataless, e.logicalSize >= minimum, !ignore.ignores(path: path) else { return .continue }
                result.append(DuplicateCandidate(path: path, size: e.logicalSize, allocatedSize: e.allocatedSize, device: e.deviceID,
                                                 fileID: e.fileID, modified: e.modificationDate, rootIndex: rootIndex))
                return .continue
            })) != nil else {
                try Task.checkCancellation()
                continue
            }
        }
        onProgress(seen)
        return result
    }

    /// Băm song song có giới hạn, kiểm tra huỷ, báo tiến độ.
    private func hashAll(_ items: [DuplicateCandidate], stage: Stage, state: Progress, progress: @escaping @Sendable (Progress) -> Void,
                         hash: @escaping @Sendable (DuplicateCandidate) throws -> String) async throws -> [String: String] {
        guard !items.isEmpty else { return [:] }
        let limit = options.concurrency
        return try await withThrowingTaskGroup(of: (String, String?).self) { group in
            var iterator = items.makeIterator()
            var out: [String: String] = [:]
            var done = 0
            var snapshot = state
            func addNext() {
                guard let c = iterator.next() else { return }
                group.addTask(priority: .utility) {
                    try Task.checkCancellation()
                    return (c.path, try? hash(c))
                }
            }
            for _ in 0..<limit { addNext() }
            while let (path, value) = try await group.next() {
                if let value { out[path] = value }
                done += 1
                if done % 25 == 0 || done == items.count {
                    snapshot.processed = done
                    progress(snapshot)
                }
                addNext()
            }
            return out
        }
    }
}

extension DuplicateGroup {
    /// Dung lượng lãng phí: tổng các bản trừ một bản giữ lại, không tính clone (mục 11.8).
    public var wastedBytes: UInt64 {
        let real = files.filter { !$0.isClone }.map(\.candidate.allocatedSize)
        let total = real.reduce(0, +)
        let keep = files.contains { $0.isClone } ? 0 : (real.max() ?? 0)
        return total >= keep ? total - keep : 0
    }
}
