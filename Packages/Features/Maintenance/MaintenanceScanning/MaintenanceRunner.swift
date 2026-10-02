import AppKit
import CleanEngineCore
import Foundation
import NodeTree
import os
import RuleEngine
import ScanEngine
import SweepCore
import SweepIPC
import SweepLogging
import SweepStorage

public enum MaintenanceError: Error, CustomStringConvertible {
    case helperUnavailable
    case mailRunning
    case failed(String)

    public var description: String {
        switch self {
        case .helperUnavailable: "Cần cài helper (quyền quản trị) để chạy tác vụ này"
        case .mailRunning: "Hãy tắt Mail trước khi tối ưu"
        case let .failed(m): m
        }
    }
}

/// Chạy tác vụ bảo trì (mục 11.5): tác vụ cần root đi qua helper; Launch Services và Mail chạy với quyền người dùng.
/// Lần chạy được lưu vào bảng `maintenance_run`.
public struct MaintenanceRunner: Sendable {
    let helper: HelperClient?
    let storage: Storage?
    let home: URL
    let dryRun: @Sendable () -> Bool

    public init(helper: HelperClient?, storage: Storage?, home: URL = .userHome, dryRun: @escaping @Sendable () -> Bool = { false }) {
        self.helper = helper
        self.storage = storage
        self.home = home
        self.dryRun = dryRun
    }

    @discardableResult
    public func run(_ task: MaintenanceTaskName) async -> MaintenanceResult {
        let start = Date()
        let result: MaintenanceResult
        if dryRun() {
            result = MaintenanceResult(task: task.rawValue, success: true, message: "Chạy thử: không thực hiện", durationSeconds: 0)
        } else {
            do {
                result = try await perform(task, start: start)
            } catch {
                result = MaintenanceResult(task: task.rawValue, success: false, message: String(describing: error), durationSeconds: Date().timeIntervalSince(start))
            }
            try? storage?.recordMaintenance(task: task.rawValue, result: result.success ? "ok" : "failed: \(result.message)")
        }
        Log.info(.clean, "maintenance", "\(task.rawValue): \(result.success ? "ok" : "lỗi") (\(String(format: "%.1f", result.durationSeconds)) giây)")
        return result
    }

    private func perform(_ task: MaintenanceTaskName, start: Date) async throws -> MaintenanceResult {
        switch task {
        case .rebuildLaunchServices:
            let (tool, args) = task.commands[0]
            let out = try await ProcessRunner().run(tool, args, timeout: 600)
            return MaintenanceResult(task: task.rawValue, success: out.succeeded,
                                     message: out.succeeded ? "Đã dựng lại cơ sở dữ liệu Launch Services" : out.stderrString,
                                     durationSeconds: Date().timeIntervalSince(start))
        case .speedUpMail:
            return try speedUpMail(start: start)
        default:
            guard let helper, HelperInstaller.status == .enabled else { throw MaintenanceError.helperUnavailable }
            return try await helper.runMaintenance(task)
        }
    }

    /// Xoá `Envelope Index` khi Mail đã tắt; Mail tự dựng lại khi mở (mục 11.5).
    private func speedUpMail(start: Date) throws -> MaintenanceResult {
        guard NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.mail").isEmpty else { throw MaintenanceError.mailRunning }
        let files = MaintenanceStatusReader.envelopeIndexFiles(home: home)
        guard !files.isEmpty else {
            return MaintenanceResult(task: MaintenanceTaskName.speedUpMail.rawValue, success: false,
                                     message: "Không tìm thấy chỉ mục Mail (có thể cần Full Disk Access)", durationSeconds: 0)
        }
        var freed: Int64 = 0
        let policy = PathPolicy.user(home: home)
        for f in files {
            let r = SecureRemover.remove(path: f.path, policy: policy)
            if case let .failed(_, message) = r.outcome { throw MaintenanceError.failed(message) }
            freed += r.freedBytes
        }
        return MaintenanceResult(task: MaintenanceTaskName.speedUpMail.rawValue, success: true,
                                 message: "Đã xoá chỉ mục Mail (\(ByteCount(freed).formatted)); Mail sẽ dựng lại khi mở", durationSeconds: Date().timeIntervalSince(start))
    }
}

extension ScanTaskID {
    public static let maintenanceStatus: ScanTaskID = "maintenanceStatus"
}

extension ArtifactKey {
    public static let maintenanceStatus: ArtifactKey = "maintenanceStatus"
}

/// Task `maintenanceStatus` trong graph Smart Scan (mục 5.5): tạo node ảo cho tác vụ nên chạy.
public struct MaintenanceStatusTask: ScanTask {
    public let id: ScanTaskID = .maintenanceStatus
    public let title = "Kiểm tra tình trạng hệ thống"
    public let estimatedWeight: Double = 0.5
    let reader: MaintenanceStatusReader

    public init(reader: MaintenanceStatusReader) { self.reader = reader }

    public func run(context: ScanContext) async throws -> ScanOutput {
        let status = await reader.read()
        context.progress.report(1)
        let nodes = status.recommendations.filter(\.recommended).map { rec in
            Node(kind: .virtual(.maintenance(task: rec.task)), title: rec.task.title, safety: rec.safety,
                 reason: LocalizedText(rec.why), category: "maintenance", removal: .custom(.maintenance),
                 requiresRoot: rec.task.requiresRoot, icon: rec.task.symbol)
        }
        guard !nodes.isEmpty else { return ScanOutput(artifacts: [.maintenanceStatus: status]) }
        let group = Node.group(["vi": "Bảo trì", "en": "Maintenance"], icon: "wrench.and.screwdriver", category: "maintenance", children: nodes)
        return ScanOutput(nodes: [group], artifacts: [.maintenanceStatus: status])
    }
}
