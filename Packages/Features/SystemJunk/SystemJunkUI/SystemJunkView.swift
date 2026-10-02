import DesignSystem
import SharedUI
import SwiftUI
import SystemJunkDomain
import SystemJunkScanning

/// Màn System Junk.
public struct SystemJunkView: View {
    @StateObject private var model: ScanCleanViewModel
    private let autoStart: Bool

    public static let appearance = FeatureAppearance(
        accent: .cleanup, symbol: "trash.circle",
        title: "Rác hệ thống",
        subtitle: "Dọn cache, log, rác Xcode và công cụ lập trình, bản sao lưu cũ. Mọi mục đều giải thích được: thuộc app nào, vì sao an toàn."
    )

    public init(services: ScanServices, feature: SystemJunkFeature, autoStart: Bool = false) {
        _model = StateObject(wrappedValue: ScanCleanViewModel(services: services, kind: "systemJunk", tasks: { feature.scanTasks() }))
        self.autoStart = autoStart
    }

    public var body: some View {
        FeatureFlowView(model: model, appearance: Self.appearance) {
            CategoryChips()
        }
        .onAppear {
            if autoStart, case .idle = model.phase { model.startScan() }
        }
    }
}

struct CategoryChips: View {
    var body: some View {
        let titles = SystemJunkTasks.specs.map(\.title)
        VStack(spacing: 8) {
            ForEach(Array(stride(from: 0, to: titles.count, by: 5)), id: \.self) { start in
                HStack(spacing: 8) {
                    ForEach(titles[start..<min(start + 5, titles.count)], id: \.self) { Pill($0) }
                }
            }
        }
    }
}
