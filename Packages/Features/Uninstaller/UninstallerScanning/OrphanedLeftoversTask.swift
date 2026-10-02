import AppCatalog
import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import ScanEngine
import SweepCore

extension ScanTaskID {
    public static let orphanedLeftovers: ScanTaskID = "orphanedLeftovers"
}

/// Quyết định một bundle ID lấy từ tên thư mục có phải của app đã gỡ không (hàm thuần, mục 11.3).
public struct OrphanFilter: Sendable {
    public let installedBundleIDs: Set<String>
    public let installedVendors: Set<String>
    public let knowledge: Knowledge
    public let excluded: Set<String>

    /// Không bao giờ coi là file sót dù không có app tương ứng (thành phần hệ thống không phải `com.apple`).
    public static let builtInWhitelist = ["org.cups.*", "com.openssh.*", "com.mashclean.*", "com.apple.*", "group.*"]

    public init(installedBundleIDs: [String], knowledge: Knowledge, excluded: Set<String> = []) {
        self.installedBundleIDs = Set(installedBundleIDs.map { $0.lowercased() })
        installedVendors = Set(installedBundleIDs.compactMap(ReverseDNS.vendorPrefix))
        self.knowledge = knowledge
        self.excluded = Set(excluded.map { $0.lowercased() })
    }

    public func isOrphan(_ bundleID: String) -> Bool {
        guard ReverseDNS.isReverseDNS(bundleID) else { return false }
        let id = bundleID.lowercased()
        if knowledge.isApple(bundleID) || knowledge.isWhitelistedOrphan(bundleID) { return false }
        if Self.builtInWhitelist.contains(where: { Glob.componentMatchesPublic($0, id) }) { return false }
        if excluded.contains(id) { return false }
        // App đang cài, hoặc thành phần con của nó (`<id>.helper`), hoặc app cha của nó.
        if installedBundleIDs.contains(id) { return false }
        if installedBundleIDs.contains(where: { id.hasPrefix($0 + ".") || $0.hasPrefix(id + ".") }) { return false }
        // Cùng hãng với một app đang cài (helper, updater...): không chắc đã gỡ, bỏ qua cho an toàn.
        if let vendor = ReverseDNS.vendorPrefix(id), installedVendors.contains(vendor) { return false }
        return true
    }

    /// Tên hiển thị cho nhóm: "Bar (com.foo.Bar)".
    public static func displayName(for bundleID: String) -> String {
        guard let last = bundleID.split(separator: ".").last else { return bundleID }
        return "\(last) (\(bundleID))"
    }
}

/// "File sót của app đã gỡ" (app bị kéo thẳng vào Thùng rác, mục 11.3): lấy bundle ID từ tên mục trong các thư mục chuẩn,
/// loại app đang cài, của Apple hoặc trong whitelist; phần còn lại nhóm theo bundle ID, mức `review`.
public struct OrphanedLeftoversTask: ScanTask {
    public let id: ScanTaskID = .orphanedLeftovers
    public let dependencies: [ScanTaskID] = [.installedApps]
    public let estimatedWeight: Double = 2
    public let title = "File sót của app đã gỡ"

    /// Thư mục con của `~/Library` được xét.
    public static let locations = ["Containers", "Preferences", "Caches", "Application Support", "Saved Application State", "HTTPStorages"]

    public init() {}

    public func run(context: ScanContext) async throws -> ScanOutput {
        let installed = context.installedApps
        // Không có danh sách app (quét lỗi) thì mọi thứ trông như file sót: dừng lại cho an toàn.
        guard !installed.isEmpty else { return ScanOutput(warnings: [ScanWarning(taskID: id, kind: .skipped, message: "Không đọc được danh sách app, bỏ qua tìm file sót")]) }
        let filter = OrphanFilter(installedBundleIDs: installed.map(\.bundleID), knowledge: context.rules.knowledge,
                                  excluded: context.environment.runningBundleIDs)
        let library = URL(fileURLWithPath: context.rules.resolver.resolve("~/Library"))
        let policy = context.environment.policy
        let ignore = context.environment.ignore

        var groups: [String: [Node]] = [:]
        var order: [String] = []
        for (i, sub) in Self.locations.enumerated() {
            try Task.checkCancellation()
            let dir = library.appendingPathComponent(sub)
            for entry in context.fileSystem.children(of: dir) where !entry.isSymlink {
                guard let bundleID = ReverseDNS.bundleID(fromEntryName: entry.name), filter.isOrphan(bundleID) else { continue }
                if ignore.ignores(bundleID: bundleID) || ignore.ignores(path: entry.url.path) { continue }
                if policy.isForbidden(entry.url.path) != nil { continue }
                guard let m = try? context.fileSystem.measure(entry.url, tracker: context.environment.tracker, counter: context.progress.fileCounter),
                      m.exists else { continue }
                let node = Node(kind: m.isDirectory ? .directory(entry.url, recursive: true) : .file(entry.url), title: "\(sub)/\(entry.name)",
                                size: ByteCount(m.allocatedSize), itemCount: m.itemCount, safety: .review,
                                reason: LocalizedText("Không còn app nào có bundle ID \(bundleID) trên máy"),
                                category: UninstallerCategory.orphanedLeftovers, removal: .moveToTrash,
                                allowedRoots: [dir.path], lastAccess: m.lastUse)
                let key = bundleID.lowercased()
                if groups[key] == nil { order.append(key) }
                groups[key, default: []].append(node)
                context.progress.addBytesFound(ByteCount(m.allocatedSize))
            }
            context.progress.report(Double(i + 1) / Double(Self.locations.count))
        }
        guard !order.isEmpty else { return .empty }

        var children: [Node] = []
        for key in order {
            guard let nodes = groups[key] else { continue }
            let bundleID = Self.originalCase(nodes) ?? key
            var g = Node.group(LocalizedText(OrphanFilter.displayName(for: bundleID)), icon: "app.dashed",
                               category: UninstallerCategory.orphanedLeftovers, children: nodes.sorted { $0.size > $1.size }, safety: .review)
            g.reason = "File sót của app đã gỡ"
            children.append(g)
        }
        children.sort { $0.size > $1.size }
        var root = Node.group(LocalizedText(["vi": "File sót của app đã gỡ", "en": "Leftovers of Removed Apps"]), icon: "puzzlepiece.extension",
                              category: UninstallerCategory.orphanedLeftovers, children: children, safety: .review)
        root.reason = "App đã bị xoá nhưng còn để lại dữ liệu. Hãy xem lại trước khi xoá."
        return ScanOutput(nodes: [root])
    }

    private static func originalCase(_ nodes: [Node]) -> String? {
        nodes.lazy.compactMap { $0.url.flatMap { ReverseDNS.bundleID(fromEntryName: $0.lastPathComponent) } }.first
    }
}
