import AppCatalog
import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import ScanEngine
import SweepCore

/// Nguồn dữ liệu đặc biệt cho rule có `match.provider` (khi đường dẫn không đủ diễn tả).
public protocol JunkProvider: Sendable {
    var name: String { get }
    func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning])
}

/// Task quét một nhóm rác theo rule (mục 11.2): đánh giá mọi rule thuộc `category`,
/// gọi provider cho rule đặc biệt, rồi dựng node nhóm.
public struct RuleCategoryTask: ScanTask {
    public let id: ScanTaskID
    public let category: String
    public let title: String
    public let estimatedWeight: Double
    public let priority: ScanPriority
    public let dependencies: [ScanTaskID]
    /// Nhóm các mục theo app sở hữu (Cache người dùng: "Node nhóm theo app").
    public let groupByApp: Bool
    let providers: [String: any JunkProvider]

    public init(id: ScanTaskID, category: String, title: String, weight: Double = 1, priority: ScanPriority = .normal,
                needsInstalledApps: Bool = false, groupByApp: Bool = false, providers: [any JunkProvider] = []) {
        self.id = id
        self.category = category
        self.title = title
        estimatedWeight = weight
        self.priority = priority
        dependencies = needsInstalledApps ? [.installedApps] : []
        self.groupByApp = groupByApp
        self.providers = Dictionary(uniqueKeysWithValues: providers.map { ($0.name, $0) })
    }

    public func run(context: ScanContext) async throws -> ScanOutput {
        let rules = context.rules.rules(in: category)
        guard !rules.isEmpty else { return .empty }
        let apps = context.installedApps
        let ruleContext = context.ruleContext(apps: apps.map(\.ruleInfo))
        var leaves: [Node] = []
        var warnings: [ScanWarning] = []
        var blockedPrefixes = Set<String>()

        for (i, compiled) in rules.enumerated() {
            try Task.checkCancellation()
            let rule = compiled.rule
            if let providerName = rule.match.provider {
                guard let provider = providers[providerName] else { continue }
                let (nodes, w) = try await provider.nodes(for: rule, context: context)
                leaves += nodes.filter { !context.environment.ignore.ignores(path: $0.url?.path ?? "") }
                warnings += w
            } else {
                // Thư mục bị TCC chặn → nhãn "Cần Full Disk Access" thay vì im lặng bỏ qua (mục 10.2).
                for glob in compiled.staticGlobs ?? [] {
                    let prefix = glob.literalPrefix
                    if AccessProbe.isBlockedByTCC(prefix) { blockedPrefixes.insert(prefix) }
                }
                let matches = try RuleEvaluator.evaluate(compiled, snapshot: context.rules, context: ruleContext)
                for m in matches {
                    if isProtectedCache(m, context: context) { continue }
                    leaves.append(m.makeNode(title: displayTitle(for: m, apps: apps)))
                    context.progress.addBytesFound(ByteCount(m.measurement.allocatedSize))
                }
            }
            context.progress.report(Double(i + 1) / Double(rules.count))
        }

        for prefix in blockedPrefixes.sorted() {
            warnings.append(ScanWarning(taskID: id, kind: .needsFullDiskAccess, message: String(localized: "Cần Full Disk Access để đọc \(prefix.abbreviatingHome)")))
        }
        // Nhóm rỗng (kể cả vì bị chặn): chỉ trả cảnh báo; UI hiện nhãn "Cần Full Disk Access" từ cảnh báo.
        guard !leaves.isEmpty else { return ScanOutput(warnings: warnings) }

        let children = groupByApp ? group(leaves, by: apps) : groupByRule(leaves, rules: rules.map(\.rule))
        var root = Node.group(RuleCategory.title(category), icon: RuleCategory.icon(category), category: category, children: children)
        if !blockedPrefixes.isEmpty { root.badges.append(String(localized: "Cần Full Disk Access")) }
        return ScanOutput(nodes: [root], warnings: warnings)
    }

    /// Cache của app đang chạy (thư mục đặt tên theo bundle ID) và tên trong `knowledge.protectedCacheNames`
    /// không được đề xuất, kể cả khi khớp rule chung `~/Library/Caches/*` (mục 8.3 `appNotRunning`).
    private func isProtectedCache(_ match: RuleMatch, context: ScanContext) -> Bool {
        guard category == RuleCategory.userCaches else { return false }
        let name = match.url.lastPathComponent
        if context.environment.runningBundleIDs.contains(name) { return true }
        return context.rules.knowledge.protectedCacheNames.contains { Glob.componentMatchesPublic($0, name) }
    }

    private func displayTitle(for match: RuleMatch, apps: [InstalledApp]) -> String {
        let name = match.url.lastPathComponent
        if let app = match.app { return "\(app.appName) — \(name)" }
        if let app = apps.first(where: { $0.bundleID == name }) { return app.name }
        return FileManager.default.displayName(atPath: match.url.path)
    }

    /// Nhóm theo app sở hữu: tên thư mục khớp bundle ID hoặc tên app.
    private func group(_ leaves: [Node], by apps: [InstalledApp]) -> [Node] {
        let byBundle = Dictionary(apps.map { ($0.bundleID.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
        let byName = Dictionary(apps.map { ($0.name.lowercased(), $0) }, uniquingKeysWith: { a, _ in a })
        var groups: [String: (InstalledApp?, [Node])] = [:]
        var order: [String] = []
        for leaf in leaves {
            let name = leaf.url?.lastPathComponent.lowercased() ?? ""
            let app = byBundle[name] ?? byName[name] ?? apps.first { name.hasPrefix($0.bundleID.lowercased() + ".") }
            let key = app?.bundleID ?? "_other"
            if groups[key] == nil { order.append(key) }
            groups[key, default: (app, [])].1.append(leaf)
        }
        var result: [Node] = []
        for key in order {
            guard let (app, nodes) = groups[key] else { continue }
            if let app {
                var n = Node(kind: .application(bundleID: app.bundleID, url: app.url), title: app.name, safety: nodes.map(\.safety).min() ?? .safe,
                             category: category, children: nodes)
                n.recomputeAggregates()
                result.append(n)
            } else {
                result += nodes
            }
        }
        return result.sorted { $0.size > $1.size }
    }

    /// Nhiều rule trong một nhóm: mỗi rule một nhóm con (ví dụ npm, Homebrew, pip).
    private func groupByRule(_ leaves: [Node], rules: [Rule]) -> [Node] {
        let byRule = Dictionary(grouping: leaves) { $0.ruleID?.rawValue ?? "" }
        guard byRule.count > 1 else { return leaves.sorted { $0.size > $1.size } }
        var result: [Node] = []
        for rule in rules {
            guard let nodes = byRule[rule.id.rawValue], !nodes.isEmpty else { continue }
            if nodes.count == 1 {
                var single = nodes[0]
                if single.title == single.url?.lastPathComponent { single.title = rule.displayTitle }
                result.append(single)
            } else {
                var g = Node.group(rule.title ?? LocalizedText(rule.id.rawValue), icon: RuleCategory.icon(category), category: category, children: nodes.sorted { $0.size > $1.size })
                g.reason = rule.reason ?? ""
                result.append(g)
            }
        }
        return result.sorted { $0.size > $1.size }
    }
}
