import AppCatalog
import Foundation
import NodeTree
import ScanEngine
import SweepCore

extension ScanTaskID {
    public static let loginItems: ScanTaskID = "loginItems"
    public static let brokenLoginItems: ScanTaskID = "brokenLoginItems"
}

extension ArtifactKey {
    /// `[LaunchItem]` do task `loginItems` tạo ra.
    public static let loginItems: ArtifactKey = "loginItems"
}

extension RemovalStrategy {
    public static let launchItem: RemovalStrategy = .custom(.launchItem)
}

public enum LoginItemsCategory {
    public static let brokenLoginItems = "brokenLoginItems"
}

/// Đọc mọi LaunchAgent/Daemon và trạng thái (mục 11.6).
public struct LoginItemsTask: ScanTask {
    public let id: ScanTaskID = .loginItems
    public let title = String(localized: "Đọc Login Items")
    public let estimatedWeight: Double = 0.5
    let scanner: LaunchItemScanner

    public init(scanner: LaunchItemScanner = LaunchItemScanner()) { self.scanner = scanner }

    public func run(context: ScanContext) async throws -> ScanOutput {
        let items = await scanner.scan()
        try Task.checkCancellation()
        return ScanOutput(artifacts: [.loginItems: items])
    }
}

/// Mục hỏng (binary không còn) hoặc thuộc app đã gỡ, cho Smart Scan (mục 11.6, 7.3 `LaunchItemRemover`).
public struct BrokenLoginItemsTask: ScanTask {
    public let id: ScanTaskID = .brokenLoginItems
    public let dependencies: [ScanTaskID] = [.loginItems, .installedApps]
    public let title = String(localized: "Login item hỏng")
    public let estimatedWeight: Double = 0.5

    public init() {}

    public func run(context: ScanContext) async throws -> ScanOutput {
        let items = context.upstreamArtifact(.loginItems, as: [LaunchItem].self) ?? []
        let apps = context.installedApps
        let leaves = Self.nodes(for: items, installedApps: apps, ignore: context.environment.ignore)
        guard !leaves.isEmpty else { return .empty }
        var root = Node.group(LocalizedText(["vi": "Login item hỏng", "en": "Broken Login Items"]), icon: "exclamationmark.triangle",
                              category: LoginItemsCategory.brokenLoginItems, children: leaves)
        root.reason = LocalizedText(String(localized: "LaunchAgent/Daemon trỏ tới chương trình không còn tồn tại hoặc thuộc app đã gỡ"))
        return ScanOutput(nodes: [root])
    }

    /// Hàm thuần: chọn mục hỏng/thuộc app đã gỡ và dựng node.
    public static func nodes(for items: [LaunchItem], installedApps: [InstalledApp], ignore: IgnoreList = .empty) -> [Node] {
        let installedIDs = Set(installedApps.map(\.bundleID))
        var result: [Node] = []
        for var item in items where !item.isApple && !ignore.ignores(path: item.plistPath) {
            // Danh sách app đầy đủ chỉ có ở đây: tra lại app sở hữu.
            if !installedApps.isEmpty, let owner = LaunchItemScanner.resolveOwner(job: item.job, installedApps: installedApps) { item.owner = owner }
            if item.isBroken {
                result.append(node(for: item, safety: .safe, badge: String(localized: "Hỏng"), reason: String(localized: "Chương trình \(item.job.executablePath ?? "") không còn tồn tại")))
            } else if !installedApps.isEmpty, item.belongsToRemovedApp(installedBundleIDs: installedIDs) {
                result.append(node(for: item, safety: .review, badge: String(localized: "Thuộc app đã gỡ"), reason: String(localized: "App sở hữu không còn trên máy")))
            }
        }
        return result
    }

    public static func node(for item: LaunchItem, safety: SafetyLevel, badge: String? = nil, reason: String = "") -> Node {
        Node(kind: .virtual(item.virtualItem), title: item.owner.map { "\($0.name) — \(item.label)" } ?? item.label, size: item.plistSize, itemCount: 1,
             safety: safety, reason: LocalizedText(reason), category: LoginItemsCategory.brokenLoginItems, removal: .launchItem,
             requiresRoot: item.domain != .userAgent, badges: [badge, item.domain.title].compactMap { $0 }, icon: "gearshape.2")
    }
}
