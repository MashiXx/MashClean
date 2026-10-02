import Foundation
import NodeTree
import SweepCore

/// Thẻ trên màn kết quả Smart Scan (mục 11.1).
public enum SmartScanCard: String, Sendable, CaseIterable, Hashable {
    case cleanup       // Dọn dẹp: tổng dung lượng an toàn
    case maintenance   // Bảo trì: số tác vụ nên chạy
    case applications  // Ứng dụng: số app có file sót
}

/// Tóm tắt 1 dòng + dung lượng cho màn tổng hợp.
public struct FeatureSummary: Sendable, Hashable {
    public var featureID: FeatureID
    public var card: SmartScanCard
    public var title: String
    public var subtitle: String
    public var bytes: ByteCount
    public var itemCount: Int

    public init(featureID: FeatureID, card: SmartScanCard, title: String, subtitle: String, bytes: ByteCount, itemCount: Int) {
        self.featureID = featureID
        self.card = card
        self.title = title
        self.subtitle = subtitle
        self.bytes = bytes
        self.itemCount = itemCount
    }
}

/// Feature cung cấp scan task cho Smart Scan (mục 11.1). `SmartScan` gom task qua protocol này,
/// không import UI của feature khác.
public protocol FeatureScanProvider: Sendable {
    var featureID: FeatureID { get }
    var includeInSmartScan: Bool { get }
    /// Task đầy đủ của feature (màn riêng của feature).
    func scanTasks() -> [any ScanTask]
    /// Task chạy trong Smart Scan (thường là tập con nhẹ hơn).
    func smartScanTasks() -> [any ScanTask]
    func summarize(_ nodes: [Node]) -> FeatureSummary
}

extension FeatureScanProvider {
    public func smartScanTasks() -> [any ScanTask] { scanTasks() }
}
