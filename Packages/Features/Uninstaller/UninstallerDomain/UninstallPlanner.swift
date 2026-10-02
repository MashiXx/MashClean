import AppCatalog
import AppKit
import CleanEngine
import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import ScanEngine
import SweepCore
import SweepLogging
import SweepStorage
import UninstallerScanning

/// App nào được phép gỡ (mục 11.3, 15): không gỡ app hệ thống (/System), app Apple mặc định, hay chính MashClean.
public enum UninstallPolicy {
    public static func reasonCannotUninstall(_ app: InstalledApp) -> String? {
        if app.isSystemApp { return "App hệ thống của macOS" }
        if app.isApple && !app.isAppStore { return "App Apple cài sẵn cùng macOS" }
        if app.bundleID.hasPrefix("com.mashclean.") { return "Chính MashClean" }
        if app.url.path.hasPrefix("/Library/Apple/") { return "Thành phần hệ thống" }
        return nil
    }

    public static func canUninstall(_ app: InstalledApp) -> Bool { reasonCannotUninstall(app) == nil }
}

/// Use case lập kế hoạch gỡ app (mục 23.5): tìm file sót, dựng cây (app + file sót theo mức tin cậy), cập nhật cache sau khi gỡ.
public struct UninstallPlanner: Sendable {
    public let fileSystem: FileSystemService
    public let storage: Storage?

    public init(fileSystem: FileSystemService, storage: Storage?) {
        self.fileSystem = fileSystem
        self.storage = storage
    }

    public func findLeftovers(for app: InstalledApp, installedApps: [InstalledApp], rules: RuleSnapshot, environment: ScanEnvironment) async -> LeftoverReport {
        let finder = LeftoverFinder(fileSystem: fileSystem, rules: rules, policy: environment.policy, ignore: environment.ignore,
                                    runningBundleIDs: environment.runningBundleIDs)
        return await finder.find(for: app, installedApps: installedApps)
    }

    /// Node của bundle app: `.custom(.app)` → AppRemover (tắt app, chuyển vào Thùng rác, mục 7.3).
    public static func appBundleNode(_ app: InstalledApp) -> Node {
        Node(kind: .directory(app.url, recursive: true), title: app.url.lastPathComponent, size: app.size, itemCount: 1, safety: .safe,
             reason: "Chuyển ứng dụng vào Thùng rác", category: "applications", removal: .custom(.app),
             allowedRoots: [app.url.deletingLastPathComponent().path], lastAccess: app.lastUsed, icon: "app")
    }

    /// Cây của một app: bundle app (nếu gỡ hẳn) rồi các nhóm file sót theo mức tin cậy.
    /// - Parameter includeApp: `false` cho "Đặt lại app" (chỉ xoá dữ liệu, giữ app).
    public static func appNode(for report: LeftoverReport, includeApp: Bool = true) -> Node {
        var children: [Node] = []
        if includeApp { children.append(appBundleNode(report.app)) }
        for confidence in LeftoverConfidence.allCases {
            let items = report.items(confidence)
            guard !items.isEmpty else { continue }
            var g = Node.group(LocalizedText("Độ tin cậy: \(confidence.title)"), icon: confidence.symbol, category: UninstallerCategory.appLeftovers,
                               children: items.map(\.node), safety: confidence.safety)
            g.reason = LocalizedText(confidence.explanation)
            children.append(g)
        }
        var n = Node(kind: .application(bundleID: report.app.bundleID, url: report.app.url), title: report.app.name, safety: .safe,
                     category: UninstallerCategory.appLeftovers, children: children)
        n.recomputeAggregates()
        return n
    }

    public static func tree(for reports: [LeftoverReport], includeApp: Bool = true) -> NodeTree {
        NodeTree(roots: reports.map { appNode(for: $0, includeApp: includeApp) })
    }

    /// Chọn sẵn bundle app và file sót mức Chắc chắn/Cao (mục 23.5).
    public static func defaultSelection(for tree: NodeTree) -> SelectionState {
        SelectionState.defaults(for: tree)
    }

    /// App đã gỡ thành công (bundle app xoá được) theo báo cáo dọn.
    public static func uninstalledApps(_ apps: [InstalledApp], report: CleanReport) -> [InstalledApp] {
        let ok = Set(report.succeeded.filter { $0.item.strategy == .custom(.app) }.compactMap { $0.item.url?.standardizedFileURL.path })
        return apps.filter { ok.contains($0.url.standardizedFileURL.path) }
    }

    /// Sau khi gỡ: xoá app khỏi `app_usage_cache` (mục 23.5).
    public func didUninstall(_ apps: [InstalledApp]) {
        for app in apps {
            do { try storage?.removeApp(bundleID: app.bundleID) } catch { Log.error(.storage, "uninstaller", "Không xoá được \(app.bundleID) khỏi cache: \(error)") }
        }
    }

    /// "Đã gỡ Foo, giải phóng 1,2 GB".
    public static func summary(uninstalled: [InstalledApp], report: CleanReport, resetOnly: Bool = false) -> String {
        let freed = report.measuredFreed.map { max($0, report.estimatedFreed) } ?? report.estimatedFreed
        if report.dryRun { return "Chạy thử: sẽ giải phóng \(report.estimatedFreed.formatted)" }
        if resetOnly { return "Đã đặt lại app, giải phóng \(freed.formatted)" }
        guard !uninstalled.isEmpty else { return "Đã xoá file sót, giải phóng \(freed.formatted)" }
        let names = uninstalled.count <= 3 ? uninstalled.map(\.name).joined(separator: ", ") : "\(uninstalled.count) ứng dụng"
        return "Đã gỡ \(names), giải phóng \(freed.formatted)"
    }
}

/// Tắt app trước khi gỡ (mục 11.3 bước 1): `terminate()`, chờ 10 giây; chưa tắt thì UI hỏi `forceTerminate()`.
@MainActor
public enum AppTerminator {
    public static func instances(of app: InstalledApp) -> [NSRunningApplication] {
        let target = app.url.standardizedFileURL.path
        return NSRunningApplication.runningApplications(withBundleIdentifier: app.bundleID)
            .filter { $0.bundleURL?.standardizedFileURL.path == target }
    }

    public static func isRunning(_ app: InstalledApp) -> Bool { !instances(of: app).isEmpty }

    /// Gửi yêu cầu thoát, trả về `true` nếu app đã tắt trong thời gian chờ.
    public static func terminate(_ app: InstalledApp, timeout: TimeInterval = 10) async -> Bool {
        let running = instances(of: app)
        guard !running.isEmpty else { return true }
        for r in running { r.terminate() }
        return await waitUntilQuit(app, timeout: timeout)
    }

    public static func forceTerminate(_ app: InstalledApp, timeout: TimeInterval = 5) async -> Bool {
        for r in instances(of: app) { r.forceTerminate() }
        return await waitUntilQuit(app, timeout: timeout)
    }

    private static func waitUntilQuit(_ app: InstalledApp, timeout: TimeInterval) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if instances(of: app).allSatisfy(\.isTerminated) { return true }
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
        return instances(of: app).allSatisfy(\.isTerminated)
    }
}
