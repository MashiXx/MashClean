import AppCatalog
import CleanEngine
import Foundation
import LoginItemsScanning
import NodeTree
import ScanEngine
import SweepCore
import SweepLogging

/// Feature Login Items và Background Items (mục 11.6). Smart Scan: thẻ "Bảo trì" với số mục hỏng.
public struct LoginItemsFeature: FeatureScanProvider {
    public let featureID: FeatureID = .loginItems
    public let includeInSmartScan = true
    public let appScanner: AppScanner
    public let scanner: LaunchItemScanner

    public init(appScanner: AppScanner, scanner: LaunchItemScanner = LaunchItemScanner()) {
        self.appScanner = appScanner
        self.scanner = scanner
    }

    public func scanTasks() -> [any ScanTask] {
        [InstalledAppsTask(scanner: appScanner), LoginItemsTask(scanner: scanner), BrokenLoginItemsTask()]
    }

    public var taskIDs: Set<ScanTaskID> { [.brokenLoginItems] }

    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let leaves = nodes.filter { $0.category == LoginItemsCategory.brokenLoginItems }.flatMap(\.removableLeaves)
        return FeatureSummary(featureID: featureID, card: .maintenance, title: String(localized: "Login item hỏng"),
                              subtitle: leaves.isEmpty ? String(localized: "Không có LaunchAgent/Daemon hỏng") : String(localized: "\(leaves.count) mục trỏ tới chương trình không còn tồn tại"),
                              bytes: leaves.sum(\.size), itemCount: leaves.count)
    }
}

public struct LaunchctlError: Error, Sendable, CustomStringConvertible {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var description: String { message }
}

/// Use case bật/tắt/xoá một LaunchAgent/Daemon (mục 11.6).
public struct LoginItemsController: Sendable {
    public let cleanEngine: CleanEngine
    let runner = ProcessRunner()

    public init(cleanEngine: CleanEngine) { self.cleanEngine = cleanEngine }

    /// Tắt tạm được không: agent (user và toàn hệ thống) chạy trong domain `gui/<uid>` nên người dùng tự `bootout` được.
    /// Daemon chạy trong domain `system` cần root; lệnh helper hiện có (`bootoutLaunchDaemon`) xoá luôn plist nên không dùng để tắt tạm.
    public static func canToggle(_ item: LaunchItem) -> Bool {
        item.domain != .globalDaemon && !item.isBroken
    }

    public static func toggleUnavailableReason(_ item: LaunchItem) -> String? {
        if item.isBroken { return String(localized: "Chương trình không còn tồn tại, chỉ có thể xoá") }
        if item.domain == .globalDaemon { return String(localized: "Daemon chạy với quyền root: chỉ có thể xoá hẳn") }
        return nil
    }

    /// Tắt tạm: `launchctl bootout gui/<uid>/<label>` (nạp lại ở lần đăng nhập sau nếu `RunAtLoad`).
    public func disable(_ item: LaunchItem) async throws {
        guard Self.canToggle(item) else { throw LaunchctlError(Self.toggleUnavailableReason(item) ?? String(localized: "Không tắt được")) }
        try await launchctl(["bootout", "gui/\(getuid())/\(item.label)"], ignoring: [3, 113])
        Log.info(.ui, "loginItems", "Đã tắt \(item.label)")
    }

    /// Bật lại: gỡ cờ vô hiệu (nếu có) rồi `launchctl bootstrap gui/<uid> <plist>`.
    public func enable(_ item: LaunchItem) async throws {
        guard Self.canToggle(item) else { throw LaunchctlError(Self.toggleUnavailableReason(item) ?? String(localized: "Không bật được")) }
        let domain = "gui/\(getuid())"
        if item.isDisabled { try? await launchctl(["enable", "\(domain)/\(item.label)"]) }
        // 37 / 17: đã nạp sẵn.
        try await launchctl(["bootstrap", domain, item.plistPath], ignoring: [17, 37])
        Log.info(.ui, "loginItems", "Đã bật \(item.label)")
    }

    /// Node để xoá hẳn qua CleanEngine (`LaunchItemRemover`: bootout rồi xoá plist; daemon qua helper).
    public static func node(for item: LaunchItem) -> Node {
        BrokenLoginItemsTask.node(for: item, safety: item.isBroken ? .safe : .review, badge: item.isBroken ? String(localized: "Hỏng") : nil,
                                  reason: String(localized: "Gỡ khỏi launchd và chuyển plist vào Thùng rác"))
    }

    public func plan(removing items: [LaunchItem]) -> CleanPlan {
        cleanEngine.makePlan(nodes: items.filter { !$0.isApple }.map(Self.node(for:)))
    }

    public func remove(_ items: [LaunchItem], dryRun: Bool) async -> CleanReport {
        await cleanEngine.run(plan(removing: items), dryRun: dryRun)
    }

    private func launchctl(_ args: [String], ignoring codes: Set<Int32> = []) async throws {
        let out: ProcessRunner.Output
        do {
            out = try await runner.run(LaunchItemScanner.launchctl, args, timeout: 20)
        } catch {
            throw LaunchctlError(String(describing: error))
        }
        guard !out.succeeded, !codes.contains(out.status) else { return }
        let message = (out.stderrString + out.stdoutString).trimmingCharacters(in: .whitespacesAndNewlines)
        throw LaunchctlError(message.isEmpty ? String(localized: "launchctl lỗi \(out.status)") : message)
    }
}
