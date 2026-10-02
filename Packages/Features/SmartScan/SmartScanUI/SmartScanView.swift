import DesignSystem
import NodeTree
import ScanEngine
import SharedUI
import SmartScanDomain
import SweepCore
import SwiftUI

@MainActor
public final class SmartScanViewModel: ScanCleanViewModel {
    @Published public private(set) var cards: [SmartScanCardSummary] = []
    @Published public var showDetails = false
    let feature: SmartScanFeature

    public init(services: ScanServices, feature: SmartScanFeature) {
        self.feature = feature
        super.init(services: services, kind: "smartScan", tasks: { feature.tasks() })
    }

    public override func scanDidFinish(_ result: ScanResult) {
        cards = feature.summarize(result)
        showDetails = false
    }

    /// "Chạy" thực hiện các mục `safe` của cả 3 thẻ trong một Clean Plan (mục 11.1).
    public override func defaultSelection(for tree: NodeTree) -> SelectionState {
        SelectionState.defaults(for: tree)
    }

    public func run() { prepareClean() }
}

/// Màn Smart Scan (mục 11.1, 23.2).
public struct SmartScanView: View {
    @ObservedObject var model: SmartScanViewModel
    let onOpenFeature: (FeatureID) -> Void

    public static let appearance = FeatureAppearance(
        accent: .smartScan, symbol: "sparkles",
        title: "Smart Scan",
        subtitle: String(localized: "Một lần quét cho cả máy: dọn rác an toàn, gợi ý bảo trì, tìm file sót của ứng dụng."),
        scanTitle: String(localized: "Quét"), cleanTitle: String(localized: "Chạy")
    )

    public init(model: SmartScanViewModel, onOpenFeature: @escaping (FeatureID) -> Void = { _ in }) {
        self.model = model
        self.onOpenFeature = onOpenFeature
    }

    public var body: some View {
        if case .results = model.phase, !model.showDetails {
            summary
        } else {
            // Inset (không phải overlay) để nút có chỗ riêng, không đè lên tiêu đề của màn kết quả.
            FeatureFlowView(model: model, appearance: Self.appearance)
                .safeAreaInset(edge: .top, spacing: 0) {
                    if case .results = model.phase {
                        HStack {
                            Button { model.showDetails = false } label: { Label(String(localized: "Tổng hợp"), systemImage: "chevron.left") }
                                .buttonStyle(GlassButtonStyle())
                            Spacer()
                        }
                        .padding(.horizontal, 24).padding(.top, 16)
                    }
                }
        }
    }

    private var summary: some View {
        ZStack {
            Theme.background(for: .smartScan).ignoresSafeArea()
            VStack(spacing: 26) {
                Spacer()
                VStack(spacing: 6) {
                    Text(String(localized: "Quét xong")).font(Theme.Font.hero).foregroundStyle(.white)
                    Text(String(localized: "Bấm \"Chạy\" để thực hiện mọi mục an toàn. Mục cần xem lại không được chọn sẵn."))
                        .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
                }
                HStack(spacing: 18) {
                    ForEach(model.cards) { card in
                        CardView(card: card) {
                            if let first = card.features.first?.featureID { onOpenFeature(first) }
                        }
                    }
                }
                .frame(maxWidth: 860)
                HStack(spacing: 10) {
                    Text(String(localized: "Đã chọn \(model.selection.selectedBytes.formatted) · \(model.selection.selectedCount) mục"))
                        .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                    Button(String(localized: "Xem lại chi tiết")) { model.showDetails = true }.buttonStyle(GlassButtonStyle())
                }
                Spacer()
                BigActionButton(String(localized: "Chạy"), subtitle: model.selection.selectedBytes > .zero ? model.selection.selectedBytes.formatted : nil, accent: .smartScan) {
                    model.run()
                }
                .disabled(model.selection.selectedCount == 0)
                Spacer().frame(height: 20)
            }
            .padding(24)
        }
    }
}

struct CardView: View {
    let card: SmartScanCardSummary
    let onOpen: () -> Void

    var body: some View {
        Card(padding: 18) {
            VStack(alignment: .leading, spacing: 10) {
                HStack {
                    Image(systemName: card.symbol).font(.system(size: 26)).foregroundStyle(.white)
                    Spacer()
                    Button(String(localized: "Xem"), action: onOpen).buttonStyle(GlassButtonStyle())
                }
                Text(card.title).font(Theme.Font.headline).foregroundStyle(Theme.secondaryText)
                Text(card.headline).font(.system(size: 26, weight: .bold, design: .rounded)).foregroundStyle(.white).lineLimit(1).minimumScaleFactor(0.6)
                ForEach(card.features, id: \.featureID) { f in
                    Text(f.subtitle).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).lineLimit(2)
                }
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, minHeight: 170, alignment: .topLeading)
        }
    }
}
