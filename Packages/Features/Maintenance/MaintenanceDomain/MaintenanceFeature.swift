import CleanEngine
import Foundation
import MaintenanceScanning
import NodeTree
import ScanEngine
import SweepCore
import SweepIPC
import SweepStorage

/// Feature Bảo trì (mục 11.5): thẻ "Bảo trì" trong Smart Scan = số tác vụ nên chạy.
public struct MaintenanceFeature: FeatureScanProvider {
    public let featureID: FeatureID = .maintenance
    public let includeInSmartScan = true
    public let runner: MaintenanceRunner
    public let statusReader: MaintenanceStatusReader
    public let helper: HelperClient?

    public init(helper: HelperClient?, storage: Storage?, dryRun: @escaping @Sendable () -> Bool = { AppSettings.shared.dryRun }) {
        self.helper = helper
        runner = MaintenanceRunner(helper: helper, storage: storage, dryRun: dryRun)
        statusReader = MaintenanceStatusReader(storage: storage, helper: helper)
    }

    public func scanTasks() -> [any ScanTask] { [MaintenanceStatusTask(reader: statusReader)] }

    public var taskIDs: Set<ScanTaskID> { [.maintenanceStatus] }

    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let tasks = nodes.flatMap(\.removableLeaves)
        let titles = tasks.map(\.title)
        return FeatureSummary(featureID: featureID, card: .maintenance, title: "Bảo trì",
                              subtitle: titles.isEmpty ? "Máy đang ổn, chưa cần bảo trì" : titles.prefix(3).joined(separator: ", "),
                              bytes: .zero, itemCount: tasks.count)
    }

    /// Đăng ký remover cho node bảo trì vào Clean Engine.
    public func registerRemovers(in engine: CleanEngine) {
        engine.register(MaintenanceRemover(runner: runner))
    }

    /// Xoá một snapshot cục bộ qua Clean Engine (LocalSnapshotRemover → helper `tmutil deletelocalsnapshots`).
    public func snapshotNode(_ snapshot: LocalSnapshot) -> Node {
        Node(kind: .virtual(.localSnapshot(volume: snapshot.volume, date: snapshot.date)), title: "Snapshot \(snapshot.displayDate)",
             safety: .review, reason: "Snapshot Time Machine cục bộ; Time Machine vẫn giữ bản sao lưu trên ổ sao lưu.",
             category: "maintenance", removal: .custom(.localSnapshot), requiresRoot: true)
    }
}
