import AppCatalog
import Foundation
import NodeTree
import ScanEngine
import SweepCore
import SystemJunkScanning

/// Feature System Junk: cung cấp task cho màn riêng và cho Smart Scan (mục 11.1, 11.2).
public struct SystemJunkFeature: FeatureScanProvider {
    public let featureID: FeatureID = .systemJunk
    public let includeInSmartScan = true
    let appScanner: AppScanner

    public init(appScanner: AppScanner) { self.appScanner = appScanner }

    public func scanTasks() -> [any ScanTask] { SystemJunkTasks.tasks(appScanner: appScanner) }

    public func smartScanTasks() -> [any ScanTask] { SystemJunkTasks.tasks(appScanner: appScanner, smartScanOnly: true) }

    public var taskIDs: Set<ScanTaskID> { SystemJunkTasks.taskIDs }

    /// Thẻ "Dọn dẹp": tổng dung lượng an toàn.
    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let leaves = nodes.flatMap(\.removableLeaves)
        let safe = leaves.filter { $0.safety == .safe }
        let bytes = safe.sum(\.size)
        let groups = nodes.filter { !$0.removableLeaves.filter { $0.safety == .safe }.isEmpty }.map(\.title)
        return FeatureSummary(featureID: featureID, card: .cleanup, title: "Rác hệ thống",
                              subtitle: groups.isEmpty ? "Không có rác an toàn để dọn" : groups.prefix(3).joined(separator: ", "),
                              bytes: bytes, itemCount: safe.count)
    }
}
