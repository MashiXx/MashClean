import FileSystemKit
import Foundation
import NodeTree
import ScanEngine
import SweepCore

/// Nhãn gắn trên node để màn hình lọc "lớn"/"cũ".
public enum LargeOldBadges {
    public static let large = String(localized: "Lớn")
    public static let old = String(localized: "Lâu không dùng")
    public static let hidden = String(localized: "Thư mục ẩn")
    public static let category = "largeOldFiles"
}

/// Tiêu chí Large & Old (mục 11.7).
public struct LargeOldFilesCriteria: Sendable, Hashable {
    public struct Match: OptionSet, Sendable, Hashable {
        public let rawValue: UInt8
        public init(rawValue: UInt8) { self.rawValue = rawValue }
        public static let large = Match(rawValue: 1)
        public static let old = Match(rawValue: 2)
    }

    /// Ngưỡng "lớn" (byte), mặc định 100 MB.
    public var largeThreshold: UInt64
    /// "Cũ": lần dùng cuối trước `now - oldDays`.
    public var oldDays: Int
    /// File cũ phải lớn hơn ngưỡng này mới đáng đề xuất (tránh hàng trăm nghìn file nhỏ).
    public var oldMinimumSize: UInt64
    public var home: URL
    /// Thư mục Spotlight không index, quét trực tiếp. `nil` = mặc định (thư mục ẩn trong home, Containers...).
    public var unindexedRoots: [URL]?
    /// Giới hạn số mục duyệt khi quét trực tiếp.
    public var unindexedEntryLimit: Int

    public init(largeThresholdMB: Int = 100, oldDays: Int = 365, oldMinimumSizeMB: Int = 10, home: URL = .userHome,
                unindexedRoots: [URL]? = nil, unindexedEntryLimit: Int = 400_000) {
        largeThreshold = UInt64(max(1, largeThresholdMB)) * 1_000_000
        self.oldDays = max(1, oldDays)
        oldMinimumSize = UInt64(max(0, oldMinimumSizeMB)) * 1_000_000
        self.home = home
        self.unindexedRoots = unindexedRoots
        self.unindexedEntryLimit = unindexedEntryLimit
    }

    public func cutoff(now: Date) -> Date { now.addingTimeInterval(-Double(oldDays) * 86_400) }

    /// File có khớp không: lớn hơn ngưỡng, hoặc lâu không dùng và đủ lớn.
    public func match(size: UInt64, lastUsed: Date?, now: Date) -> Match {
        var m: Match = []
        if size > largeThreshold { m.insert(.large) }
        if let lastUsed, lastUsed < cutoff(now: now), size > oldMinimumSize { m.insert(.old) }
        return m
    }

    /// Thư mục ẩn trong home và vùng Library mà Spotlight không index.
    public func defaultUnindexedRoots(fileSystem: FileSystemService) -> [URL] {
        var roots: [URL] = []
        for e in fileSystem.children(of: home) where e.isDirectory && !e.isSymlink && e.name.hasPrefix(".") && e.name != ".Trash" {
            roots.append(e.url)
        }
        for sub in ["Library/Containers", "Library/Group Containers", "Library/Application Support"] {
            roots.append(home.appendingPathComponent(sub))
        }
        return roots
    }
}

/// Quét file lớn và cũ (mục 11.7): Spotlight cho phần đã index + quét trực tiếp thư mục ẩn.
/// Mọi mục là `.file`, chuyển vào Thùng rác, mức `review`, mặc định không chọn (mục 15.2).
public struct LargeOldFilesTask: ScanTask {
    public let id: ScanTaskID = "largeOldFiles"
    public let title = String(localized: "File lớn và cũ")
    public let estimatedWeight: Double = 3
    public let criteria: LargeOldFilesCriteria

    public init(criteria: LargeOldFilesCriteria) { self.criteria = criteria }

    public func run(context: ScanContext) async throws -> ScanOutput {
        let now = context.environment.now
        let cutoff = criteria.cutoff(now: now) as NSDate
        let predicate = NSPredicate(
            format: "(kMDItemFSSize > %lld) OR ((kMDItemLastUsedDate < %@ OR kMDItemFSContentChangeDate < %@) AND kMDItemFSSize > %lld)",
            Int64(criteria.largeThreshold), cutoff, cutoff, Int64(criteria.oldMinimumSize)
        )
        context.progress.report(0.05)
        let home = criteria.home
        let found = await SpotlightQuery.run(predicate: predicate, scopes: [home], timeout: 30)
        try Task.checkCancellation()
        context.progress.report(0.5)

        var seen = Set<String>()
        var leaves: [Node] = []
        for (i, item) in found.enumerated() {
            if i % 200 == 0 { try Task.checkCancellation() }
            let lastUsed = item.lastUsed ?? item.modified
            if let node = makeNode(item.url, lastUsed: lastUsed, hidden: false, context: context, now: now), seen.insert(item.url.path).inserted {
                leaves.append(node)
            }
        }
        context.progress.addFilesVisited(found.count)
        context.progress.report(0.6)

        var warnings: [ScanWarning] = []
        let roots = criteria.unindexedRoots ?? criteria.defaultUnindexedRoots(fileSystem: context.fileSystem)
        var budget = criteria.unindexedEntryLimit
        for (i, root) in roots.enumerated() {
            try Task.checkCancellation()
            guard budget > 0 else {
                warnings.append(ScanWarning(taskID: id, kind: .skipped, message: String(localized: "Dừng quét thư mục ẩn sau \(criteria.unindexedEntryLimit.formatted()) mục")))
                break
            }
            if AccessProbe.isBlockedByTCC(root.path) {
                warnings.append(ScanWarning(taskID: id, kind: .needsFullDiskAccess, message: String(localized: "Cần Full Disk Access để đọc \(root.path.abbreviatingHome)")))
                continue
            }
            leaves += try scanUnindexed(root, budget: &budget, seen: &seen, context: context, now: now)
            context.progress.report(0.6 + 0.4 * Double(i + 1) / Double(max(roots.count, 1)))
        }

        guard !leaves.isEmpty else { return ScanOutput(warnings: warnings) }
        for leaf in leaves { context.progress.addBytesFound(leaf.size) }
        let byKind = Dictionary(grouping: leaves) { FileKind.classify($0.url ?? URL(fileURLWithPath: "/")) }
        let groups = FileKind.allCases.compactMap { kind -> Node? in
            guard let nodes = byKind[kind], !nodes.isEmpty else { return nil }
            return Node.group(LocalizedText(kind.title), icon: kind.symbol, category: LargeOldBadges.category,
                              children: nodes.sorted { $0.size > $1.size }, safety: .review)
        }
        return ScanOutput(nodes: groups.sorted { $0.size > $1.size }, warnings: warnings)
    }

    /// Duyệt trực tiếp (BulkWalker), không vào package, bỏ vùng cấm, có giới hạn số mục.
    private func scanUnindexed(_ root: URL, budget: inout Int, seen: inout Set<String>, context: ScanContext, now: Date) throws -> [Node] {
        var candidates: [(URL, Date)] = []
        var remaining = budget
        let policy = context.environment.policy
        let tracker = context.environment.tracker
        let criteria = criteria
        var options = WalkOptions(skipPackageContents: true)
        options.cancellationCheckInterval = 1000
        try context.fileSystem.walker.walkSync(root, options: options) { e in
            remaining -= 1
            if remaining <= 0 { return .stop }
            if e.isDirectory {
                return policy.shouldSkipDescending(into: e.path.string) ? .skipDescendants : .continue
            }
            guard !e.isSymlink, !e.isDataless else { return .continue }
            let match = criteria.match(size: e.allocatedSize, lastUsed: e.modificationDate, now: now)
            guard !match.isEmpty else { return .continue }
            if e.linkCount > 1, !tracker.firstSighting(device: e.deviceID, fileID: e.fileID) { return .continue }
            candidates.append((e.url, e.modificationDate))
            return .continue
        }
        context.progress.addFilesVisited(budget - max(remaining, 0))
        budget = max(remaining, 0)
        return candidates.compactMap { url, date in
            guard seen.insert(url.path).inserted else { return nil }
            return makeNode(url, lastUsed: date, hidden: true, context: context, now: now)
        }
    }

    private func makeNode(_ url: URL, lastUsed: Date?, hidden: Bool, context: ScanContext, now: Date) -> Node? {
        let path = url.path
        guard Self.isEligible(path: path, home: criteria.home.path),
              context.environment.policy.isForbidden(path) == nil,
              !context.environment.ignore.ignores(path: path),
              let entry = context.fileSystem.entry(at: url),
              !entry.isDirectory, !entry.isSymlink, !entry.isDataless,
              !context.fileSystem.isDataless(url) else { return nil }
        let match = criteria.match(size: entry.allocatedSize, lastUsed: lastUsed ?? entry.modificationDate, now: now)
        guard !match.isEmpty else { return nil }
        var badges: [String] = []
        if match.contains(.large) { badges.append(LargeOldBadges.large) }
        if match.contains(.old) { badges.append(LargeOldBadges.old) }
        if hidden { badges.append(LargeOldBadges.hidden) }
        let reason: LocalizedText = match.contains(.large)
            ? LocalizedText(String(localized: "Lớn hơn \(ByteCount(criteria.largeThreshold).formatted)"))
            : LocalizedText(String(localized: "Không dùng hơn \(criteria.oldDays) ngày"))
        return Node(kind: .file(url), title: url.lastPathComponent, size: ByteCount(entry.allocatedSize), itemCount: 1,
                    safety: .review, reason: reason, category: LargeOldBadges.category, removal: .moveToTrash,
                    allowedRoots: [criteria.home.path], lastAccess: lastUsed ?? entry.modificationDate, badges: badges)
    }

    /// Nằm trong home, không ở trong package (.app, .photoslibrary...) và không ở Thùng rác.
    public static func isEligible(path: String, home: String) -> Bool {
        guard path.hasPrefix(home.hasSuffix("/") ? home : home + "/") else { return false }
        let relative = path.dropFirst(home.count).split(separator: "/")
        guard let first = relative.first, first != ".Trash" else { return false }
        for component in relative.dropLast() where PackageExtensions.isPackage(name: String(component)) { return false }
        return true
    }
}
