import Foundation
import NodeTree
import ScanEngine
import SmartScanScanning
import SweepCore

/// Tóm tắt một thẻ trên màn kết quả Smart Scan (Dọn dẹp / Bảo trì / Ứng dụng).
public struct SmartScanCardSummary: Sendable, Identifiable {
    public var id: SmartScanCard { card }
    public let card: SmartScanCard
    public let features: [FeatureSummary]
    public let nodes: [Node]

    public var bytes: ByteCount { features.sum(\.bytes) }
    public var itemCount: Int { features.reduce(0) { $0 + $1.itemCount } }

    public var title: String {
        switch card {
        case .cleanup: String(localized: "Dọn dẹp")
        case .maintenance: String(localized: "Bảo trì")
        case .applications: String(localized: "Ứng dụng")
        }
    }

    public var symbol: String {
        switch card {
        case .cleanup: "trash"
        case .maintenance: "wrench.and.screwdriver"
        case .applications: "square.grid.2x2"
        }
    }

    /// Dòng chính của thẻ (mục 11.1): dung lượng an toàn / số tác vụ nên chạy / số app có file sót.
    public var headline: String {
        switch card {
        case .cleanup: bytes > .zero ? bytes.formatted : String(localized: "Sạch sẽ")
        case .maintenance: itemCount > 0 ? String(localized: "\(itemCount) tác vụ nên chạy") : String(localized: "Ổn định")
        case .applications: itemCount > 0 ? String(localized: "\(itemCount) mục cần xem") : String(localized: "Gọn gàng")
        }
    }
}

/// Smart Scan: quét tổng hợp mọi feature (mục 11.1). Ngoại lệ duy nhất được gom task từ feature khác,
/// qua protocol `FeatureScanProvider`, không import UI của chúng (mục 4.2).
public struct SmartScanFeature: Sendable {
    public let composer: SmartScanComposer

    public init(providers: [any FeatureScanProvider]) {
        composer = SmartScanComposer(providers: providers)
    }

    public func tasks() -> [any ScanTask] { composer.tasks() }

    /// Tách node theo feature rồi nhóm theo thẻ.
    public func summarize(_ result: ScanResult) -> [SmartScanCardSummary] {
        let ids = composer.taskIDsByFeature()
        var byCard: [SmartScanCard: ([FeatureSummary], [Node])] = [:]
        for provider in composer.providers {
            let nodes = result.nodes(of: ids[provider.featureID] ?? [])
            let summary = provider.summarize(nodes)
            byCard[summary.card, default: ([], [])].0.append(summary)
            byCard[summary.card, default: ([], [])].1 += nodes
        }
        // Chỉ thẻ có feature đóng góp: bản App Store không có Bảo trì/Login Items nên không hiện thẻ Bảo trì.
        return SmartScanCard.allCases.filter { byCard[$0] != nil }.map { card in
            SmartScanCardSummary(card: card, features: byCard[card]?.0 ?? [], nodes: byCard[card]?.1 ?? [])
        }
    }
}
