import Foundation
import ScanEngine
import SweepCore

/// Gom scan task của mọi feature có `includeInSmartScan == true` thành một graph (mục 11.1, 23.2).
/// Task trùng id (vd `installedApps`) chỉ giữ một bản; ScanGraph tự khử trùng.
public struct SmartScanComposer: Sendable {
    public let providers: [any FeatureScanProvider]

    public init(providers: [any FeatureScanProvider]) {
        self.providers = providers.filter(\.includeInSmartScan)
    }

    public func tasks() -> [any ScanTask] {
        providers.flatMap { $0.smartScanTasks() }
    }

    /// Task id thuộc từng feature, để tách node theo thẻ khi tóm tắt.
    public func taskIDsByFeature() -> [FeatureID: Set<ScanTaskID>] {
        Dictionary(uniqueKeysWithValues: providers.map { ($0.featureID, Set($0.smartScanTasks().map(\.id))) })
    }
}
