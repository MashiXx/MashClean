import AppCatalog
import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import ScanEngine
import SweepCore

/// Một file sót đã khớp với app, kèm mức tin cậy.
public struct Leftover: Sendable, Identifiable {
    public var id: NodeID { node.id }
    public let node: Node
    public let confidence: LeftoverConfidence

    public init(node: Node, confidence: LeftoverConfidence) {
        self.node = node
        self.confidence = confidence
    }
}

/// Kết quả tìm file sót của một app.
public struct LeftoverReport: Sendable {
    public let app: InstalledApp
    public let items: [Leftover]

    public init(app: InstalledApp, items: [Leftover]) {
        self.app = app
        self.items = items
    }

    public var totalSize: ByteCount { items.sum(\.node.size) }
    public func items(_ confidence: LeftoverConfidence) -> [Leftover] { items.filter { $0.confidence == confidence } }
    public var hasLeftovers: Bool { !items.isEmpty }
}

/// Tìm file sót của một app theo thứ tự độ tin cậy (mục 11.3, 23.5):
/// bundle ID → Team ID → tên app → LaunchAgent/Daemon → rule `appLeftovers` → pkgutil → tên gần giống.
public struct LeftoverFinder: Sendable {
    public let fileSystem: FileSystemService
    public let rules: RuleSnapshot
    public let policy: PathPolicy
    public let ignore: IgnoreList
    public let runningBundleIDs: Set<String>
    public let usePackageReceipts: Bool
    /// Tiền tố cho đường dẫn hệ thống (`/Library/...`), để test trên fixture. Rỗng khi chạy thật.
    public let systemRoot: String

    public init(fileSystem: FileSystemService, rules: RuleSnapshot, policy: PathPolicy? = nil, ignore: IgnoreList = .empty,
                runningBundleIDs: Set<String> = [], usePackageReceipts: Bool = true, systemRoot: String = "") {
        self.fileSystem = fileSystem
        self.rules = rules
        self.policy = policy ?? .user(home: fileSystem.home)
        self.ignore = ignore
        self.runningBundleIDs = runningBundleIDs
        self.usePackageReceipts = usePackageReceipts
        self.systemRoot = systemRoot
    }

    private var userLibrary: URL { fileSystem.home.appendingPathComponent("Library") }
    private func system(_ path: String) -> URL { URL(fileURLWithPath: systemRoot + path) }

    // MARK: Ứng viên

    private enum CandidateKind {
        case path
        case launch(label: String, domain: VirtualItem.LaunchDomain)
        case receipt(id: String)
    }

    private struct Candidate {
        let path: String
        let confidence: LeftoverConfidence
        let kind: CandidateKind
    }

    private struct Collector {
        var order: [String] = []
        var byKey: [String: Candidate] = [:]

        mutating func add(_ c: Candidate) {
            if let existing = byKey[c.path] {
                if c.confidence < existing.confidence { byKey[c.path] = c }
                return
            }
            order.append(c.path)
            byKey[c.path] = c
        }

        func contains(_ path: String) -> Bool { byKey[path] != nil }
        var candidates: [Candidate] { order.compactMap { byKey[$0] } }
    }

    // MARK: Tìm

    public func find(for app: InstalledApp, installedApps: [InstalledApp]) async -> LeftoverReport {
        let bundleID = app.bundleID
        guard RuleEvaluator.isSafeComponent(bundleID), !ignore.ignores(bundleID: bundleID) else { return LeftoverReport(app: app, items: []) }
        let others = installedApps.filter { $0.url.standardizedFileURL != app.url.standardizedFileURL }
        let otherIDs = others.map(\.bundleID).filter { $0.caseInsensitiveCompare(bundleID) != .orderedSame }
        let appNames = Array(Set([app.name, app.url.deletingPathExtension().lastPathComponent]))
            .filter { name in RuleEvaluator.isSafeComponent(name) && !others.contains { $0.name.caseInsensitiveCompare(name) == .orderedSame } }
        var collector = Collector()

        func scan(_ dir: URL, _ confidence: LeftoverConfidence, _ match: (String) -> Bool) {
            for entry in fileSystem.children(of: dir) where match(entry.name) {
                if LeftoverMatcher.belongsToOtherApp(entry.name, bundleID: bundleID, otherBundleIDs: otherIDs) { continue }
                collector.add(Candidate(path: entry.url.path, confidence: confidence, kind: .path))
            }
        }
        let byBundle: (String) -> Bool = { LeftoverMatcher.matchesBundleID($0, bundleID: bundleID) }

        // 1. Chắc chắn: khớp bundle ID
        let lib = userLibrary
        for sub in ["Containers", "Application Scripts", "Caches", "HTTPStorages", "WebKit", "Saved Application State",
                    "Preferences", "Preferences/ByHost", "Application Support", "Logs"] {
            scan(lib.appendingPathComponent(sub), .certain, byBundle)
        }
        scan(lib.appendingPathComponent("Cookies"), .certain) { $0.hasSuffix(".binarycookies") && byBundle($0) }
        for sys in ["/Library/Preferences", "/Library/Caches", "/Library/Application Support", "/Library/Logs", "/Library/PrivilegedHelperTools"] {
            scan(system(sys), .certain, byBundle)
        }

        // 1b. Chắc chắn: Group Containers theo Team ID (chỉ khi không còn app nào khác cùng Team ID)
        let teamShared = app.teamID.map { team in others.contains { $0.teamID == team } } ?? true
        for entry in fileSystem.children(of: lib.appendingPathComponent("Group Containers")) {
            if LeftoverMatcher.groupContainerMatchesBundle(entry.name, bundleID: bundleID, teamID: app.teamID) {
                collector.add(Candidate(path: entry.url.path, confidence: .certain, kind: .path))
            } else if LeftoverMatcher.groupContainerMatchesTeam(entry.name, teamID: app.teamID) {
                collector.add(Candidate(path: entry.url.path, confidence: teamShared ? .low : .certain, kind: .path))
            }
        }

        // 2. Cao: tên app trong thư mục chuẩn
        let byName: (String) -> Bool = { LeftoverMatcher.matchesAppName($0, appNames: appNames) }
        for sub in ["Application Support", "Logs", "Caches"] { scan(lib.appendingPathComponent(sub), .high, byName) }
        scan(system("/Library/Application Support"), .high, byName)

        // 3. LaunchAgent/Daemon: label khớp bundle ID (Chắc chắn) hoặc chạy file trong app (Cao)
        let launchDirs: [(URL, VirtualItem.LaunchDomain)] = [
            (lib.appendingPathComponent("LaunchAgents"), .userAgent),
            (system("/Library/LaunchAgents"), .globalAgent),
            (system("/Library/LaunchDaemons"), .globalDaemon),
        ]
        for (dir, domain) in launchDirs {
            for entry in fileSystem.children(of: dir) where entry.name.hasSuffix(".plist") && !entry.isDirectory {
                guard let data = try? Data(contentsOf: entry.url), let info = LaunchPlistInfo.parse(data),
                      let confidence = info.confidence(fileName: entry.name, bundleID: bundleID, appPath: app.url.path) else { continue }
                collector.add(Candidate(path: entry.url.path, confidence: confidence, kind: .launch(label: info.label, domain: domain)))
                // Helper đặc quyền mà daemon chạy cũng thuộc app.
                if let exe = info.executable, exe.hasPrefix("/Library/PrivilegedHelperTools/") {
                    let helper = system(exe)
                    if fileSystem.exists(helper) { collector.add(Candidate(path: helper.path, confidence: confidence, kind: .path)) }
                }
            }
        }

        // 4. Mức Trung bình: package receipt
        if usePackageReceipts, systemRoot.isEmpty {
            for receipt in await PackageReceipts.receipts(appPath: app.url.path, bundleID: bundleID) {
                for path in receipt.ownedPaths where !collector.contains(path) {
                    let url = URL(fileURLWithPath: path)
                    let parent = url.deletingLastPathComponent().path
                    if path.hasSuffix(".plist"), parent == "/Library/LaunchDaemons" || parent == "/Library/LaunchAgents",
                       let data = try? Data(contentsOf: url), let info = LaunchPlistInfo.parse(data) {
                        collector.add(Candidate(path: path, confidence: .medium,
                                                kind: .launch(label: info.label, domain: parent == "/Library/LaunchDaemons" ? .globalDaemon : .globalAgent)))
                    } else if fileSystem.exists(url) {
                        collector.add(Candidate(path: path, confidence: .medium, kind: .path))
                    }
                }
                collector.add(Candidate(path: "pkg:" + receipt.info.id, confidence: .medium, kind: .receipt(id: receipt.info.id)))
            }
        }

        // 5. Thấp: tên gần giống (chỉ gợi ý, không tự chọn)
        let otherNames = others.map { LeftoverMatcher.normalized($0.name) }.filter { $0.count >= 4 }
        for sub in ["Application Support", "Caches", "Preferences", "Logs", "Saved Application State", "HTTPStorages", "WebKit", "Containers"] {
            for entry in fileSystem.children(of: lib.appendingPathComponent(sub)) where !collector.contains(entry.url.path) {
                guard LeftoverMatcher.isSimilarName(entry.name, appName: app.name, bundleID: bundleID) else { continue }
                let n = LeftoverMatcher.normalized(entry.name)
                if otherNames.contains(where: { n.contains($0) }) { continue }
                if LeftoverMatcher.belongsToOtherApp(entry.name, bundleID: bundleID, otherBundleIDs: otherIDs) { continue }
                if otherIDs.contains(where: { LeftoverMatcher.matchesBundleID(entry.name, bundleID: $0) }) { continue }
                if let id = ReverseDNS.bundleID(fromEntryName: entry.name), rules.knowledge.isApple(id) { continue }
                collector.add(Candidate(path: entry.url.path, confidence: .low, kind: .path))
            }
        }

        var items = collector.candidates.compactMap { makeLeftover($0, app: app) }

        // 6. Rule `app.<bundleID>.leftovers` (JetBrains, Adobe, Office, game launcher...): mức Cao (mục 8.2).
        items += ruleLeftovers(for: app, excluding: Set(items.compactMap { $0.node.url?.path }))

        items.sort { ($0.confidence, $1.node.size) < ($1.confidence, $0.node.size) }
        return LeftoverReport(app: app, items: items)
    }

    // MARK: Rule

    private func ruleLeftovers(for app: InstalledApp, excluding: Set<String>) -> [Leftover] {
        let info = app.ruleInfo
        let context = RuleEvaluationContext(fileSystem: fileSystem, policy: policy, apps: [info], runningBundleIDs: runningBundleIDs, ignore: ignore)
        var result: [Leftover] = []
        var seen = excluding
        for compiled in rules.rules(in: UninstallerCategory.appLeftovers) {
            // Chỉ rule gắn với app: có `match.bundleIDs` khớp, hoặc dùng biến theo app.
            guard compiled.staticGlobs == nil || compiled.bundleIDFilter != nil, compiled.applies(toBundleID: app.bundleID) else { continue }
            guard let matches = try? RuleEvaluator.evaluate(compiled, snapshot: rules, context: context) else { continue }
            for m in matches where seen.insert(m.url.path).inserted {
                var node = m.makeNode(badges: [LeftoverConfidence.high.title])
                node.category = UninstallerCategory.appLeftovers
                result.append(Leftover(node: node, confidence: .high))
            }
        }
        return result
    }

    // MARK: Dựng node

    private func makeLeftover(_ c: Candidate, app: InstalledApp) -> Leftover? {
        let reason = LocalizedText("\(c.confidence.explanation) (\(app.name))")
        switch c.kind {
        case let .receipt(id):
            let node = Node(kind: .virtual(.packageReceipt(id: id)), title: String(localized: "Biên nhận cài đặt \(id)"), itemCount: 1, safety: c.confidence.safety,
                            reason: LocalizedText(String(localized: "Xoá biên nhận pkg khỏi hệ thống (pkgutil --forget)")), category: UninstallerCategory.appLeftovers,
                            removal: .custom(.pkgForget), requiresRoot: true, badges: [c.confidence.title], icon: "shippingbox")
            return Leftover(node: node, confidence: c.confidence)
        case let .launch(label, domain):
            guard !ignore.ignores(path: c.path), policy.isForbidden(c.path) == nil, let entry = fileSystem.entry(at: URL(fileURLWithPath: c.path)) else { return nil }
            let node = Node(kind: .virtual(.launchItem(label: label, plistPath: c.path, domain: domain)), title: label,
                            size: ByteCount(entry.allocatedSize), itemCount: 1, safety: c.confidence.safety, reason: reason,
                            category: UninstallerCategory.appLeftovers, removal: .custom(.launchItem), requiresRoot: domain != .userAgent,
                            lastAccess: entry.lastUse, badges: [c.confidence.title, domain == .globalDaemon ? "Daemon" : "LaunchAgent"],
                            icon: "gearshape.2")
            return Leftover(node: node, confidence: c.confidence)
        case .path:
            let url = URL(fileURLWithPath: c.path)
            guard !ignore.ignores(path: c.path), policy.isForbidden(c.path) == nil, !policy.shouldSkipDescending(into: c.path),
                  let m = try? fileSystem.measure(url), m.exists else { return nil }
            let owner = PathPolicy.owner(of: c.path)
            let requiresRoot = owner.map { $0 != getuid() } ?? false
            let node = Node(kind: m.isDirectory ? .directory(url, recursive: true) : .file(url),
                            title: url.lastPathComponent, size: ByteCount(m.allocatedSize), itemCount: m.itemCount,
                            safety: c.confidence.safety, reason: reason, category: UninstallerCategory.appLeftovers,
                            removal: requiresRoot ? .delete : .moveToTrash, requiresRoot: requiresRoot,
                            allowedRoots: [url.deletingLastPathComponent().path], lastAccess: m.lastUse,
                            badges: [c.confidence.title] + (requiresRoot ? [String(localized: "Cần quyền quản trị")] : []))
            return Leftover(node: node, confidence: c.confidence)
        }
    }
}
