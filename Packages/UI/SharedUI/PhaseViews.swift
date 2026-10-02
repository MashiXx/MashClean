import AppKit
import CleanEngine
import DesignSystem
import NodeTree
import ScanEngine
import SweepCore
import SwiftUI

/// Mô tả hiển thị của một màn tính năng.
public struct FeatureAppearance: Sendable {
    public var accent: Theme.Accent
    public var symbol: String
    public var title: String
    public var subtitle: String
    public var scanTitle: String
    public var cleanTitle: String

    public init(accent: Theme.Accent, symbol: String, title: String, subtitle: String, scanTitle: String = "Quét", cleanTitle: String = "Dọn dẹp") {
        self.accent = accent
        self.symbol = symbol
        self.title = title
        self.subtitle = subtitle
        self.scanTitle = scanTitle
        self.cleanTitle = cleanTitle
    }
}

/// Màn tính năng theo máy trạng thái chung (mục 22.3).
public struct FeatureFlowView<Idle: View>: View {
    @ObservedObject var model: ScanCleanViewModel
    let appearance: FeatureAppearance
    let idleAccessory: Idle

    public init(model: ScanCleanViewModel, appearance: FeatureAppearance, @ViewBuilder idleAccessory: () -> Idle = { EmptyView() }) {
        self.model = model
        self.appearance = appearance
        self.idleAccessory = idleAccessory()
    }

    public var body: some View {
        ZStack {
            Theme.background(for: appearance.accent).ignoresSafeArea()
            content
                .padding(24)
                .transition(.opacity)
        }
        .animation(.easeInOut(duration: 0.25), value: phaseKey)
        .sheet(item: $model.permanentDeletePrompt) { prompt in
            PermanentDeleteSheet(prompt: prompt)
        }
    }

    private var phaseKey: Int {
        switch model.phase {
        case .idle: 0
        case .scanning: 1
        case .results: 2
        case .confirming: 3
        case .cleaning: 4
        case .done: 5
        case .error: 6
        }
    }

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .idle:
            IdleView(appearance: appearance, accessory: idleAccessory, onScan: model.startScan)
        case let .scanning(progress):
            ScanningView(appearance: appearance, progress: progress, onCancel: model.cancel)
        case let .results(tree):
            ResultsView(model: model, tree: tree, appearance: appearance)
        case let .confirming(plan, _):
            ConfirmView(plan: plan, appearance: appearance, onCancel: model.cancelConfirmation, onConfirm: { model.execute(plan) })
        case let .cleaning(progress, item, freed):
            CleaningView(appearance: appearance, progress: progress, currentItem: item, freed: freed)
        case let .done(report):
            DoneView(report: report, appearance: appearance, onRescan: model.startScan, onClose: model.reset)
        case let .error(message):
            ErrorView(message: message, onRetry: model.startScan)
        }
    }
}

public struct IdleView<Accessory: View>: View {
    let appearance: FeatureAppearance
    let accessory: Accessory
    let onScan: () -> Void

    public init(appearance: FeatureAppearance, accessory: Accessory, onScan: @escaping () -> Void) {
        self.appearance = appearance
        self.accessory = accessory
        self.onScan = onScan
    }

    public var body: some View {
        VStack(spacing: 28) {
            Spacer()
            FeatureHeader(symbol: appearance.symbol, title: appearance.title, subtitle: appearance.subtitle)
            accessory
            Spacer()
            BigActionButton(appearance.scanTitle, accent: appearance.accent, action: onScan)
            Spacer().frame(height: 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

public struct ScanningView: View {
    let appearance: FeatureAppearance
    let progress: ScanProgress
    let onCancel: () -> Void

    public init(appearance: FeatureAppearance, progress: ScanProgress, onCancel: @escaping () -> Void) {
        self.appearance = appearance
        self.progress = progress
        self.onCancel = onCancel
    }

    public var body: some View {
        VStack(spacing: 24) {
            Spacer()
            ProgressRing(progress: progress.fraction, lineWidth: 12, label: "đang quét")
                .frame(width: 170, height: 170)
            VStack(spacing: 6) {
                Text(progress.currentTask.isEmpty ? "Đang chuẩn bị…" : progress.currentTask)
                    .font(Theme.Font.headline).foregroundStyle(.white)
                HStack(spacing: 18) {
                    Label("\(progress.filesVisited.formatted()) mục đã duyệt", systemImage: "doc.on.doc")
                    if progress.bytesFound > .zero { Label("Tìm thấy \(progress.bytesFound.formatted)", systemImage: "externaldrive") }
                    Label("\(progress.completedTasks)/\(progress.totalTasks) bước", systemImage: "checklist")
                }
                .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            Button("Dừng", action: onCancel).buttonStyle(GlassButtonStyle()).keyboardShortcut(.cancelAction)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Màn kết quả: cột trái là các nhóm, cột phải là cây chi tiết (NSOutlineView).
public struct ResultsView: View {
    @ObservedObject var model: ScanCleanViewModel
    let tree: NodeTree
    let appearance: FeatureAppearance
    @State private var focusedGroup: NodeID?
    @State private var showRisky = false

    public init(model: ScanCleanViewModel, tree: NodeTree, appearance: FeatureAppearance) {
        self.model = model
        self.tree = tree
        self.appearance = appearance
    }

    private var visibleRoots: [Node] {
        tree.roots.filter { showRisky || $0.safety != .risky }
    }

    public var body: some View {
        VStack(spacing: 14) {
            header
            if !model.warnings.isEmpty { warningsBanner }
            if tree.isEmpty {
                Spacer()
                VStack(spacing: 10) {
                    Image(systemName: "checkmark.seal.fill").font(.system(size: 56)).foregroundStyle(.white)
                    Text("Không tìm thấy gì cần dọn").font(Theme.Font.title).foregroundStyle(.white)
                    Button("Quét lại", action: model.startScan).buttonStyle(GlassButtonStyle())
                }
                Spacer()
            } else {
                HStack(spacing: 12) {
                    groupList.frame(width: 270)
                    detail
                }
                footer
            }
        }
        .onAppear {
            showRisky = model.services.settings.showRiskyItems
            if focusedGroup == nil { focusedGroup = visibleRoots.first?.id }
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text(appearance.title).font(Theme.Font.title).foregroundStyle(.white)
                Text("Tìm thấy \(tree.totalSize.formatted) trong \(tree.allLeaves.count.formatted()) mục")
                    .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            if tree.roots.contains(where: { $0.safety == .risky }) {
                Toggle("Hiện mục nâng cao (rủi ro)", isOn: $showRisky).toggleStyle(.switch).foregroundStyle(.white).font(Theme.Font.caption)
            }
            Button("Quét lại", action: model.startScan).buttonStyle(GlassButtonStyle())
        }
    }

    private var warningsBanner: some View {
        let needsFDA = model.warnings.contains { $0.kind == .needsFullDiskAccess }
        let text = needsFDA ? "Một số nhóm cần Full Disk Access để quét đầy đủ." : "\(model.warnings.count) cảnh báo khi quét: " + model.warnings.prefix(2).map(\.message).joined(separator: "; ")
        return NoticeBanner(text: text, actionTitle: needsFDA ? "Mở cài đặt" : nil) {
            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")!)
        }
    }

    private var groupList: some View {
        ScrollView {
            VStack(spacing: 4) {
                ForEach(visibleRoots) { group in
                    GroupRow(node: group, state: model.selection.state(of: group.id, in: tree), selectedBytes: selectedBytes(under: group),
                             isFocused: focusedGroup == group.id,
                             onToggle: { model.toggle(group.id) },
                             onFocus: { focusedGroup = group.id })
                }
            }
            .padding(6)
        }
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private func selectedBytes(under node: Node) -> ByteCount {
        tree.leaves(under: node.id).filter { model.selection.isSelected($0.id) }.sum(\.size)
    }

    @ViewBuilder private var detail: some View {
        let focused = focusedGroup.flatMap { tree.node($0) } ?? visibleRoots.first
        VStack(alignment: .leading, spacing: 8) {
            if let focused {
                HStack {
                    Text(focused.title).font(Theme.Font.headline).foregroundStyle(.white)
                    Spacer()
                    SafetyBadge(focused.safety)
                }
                if !focused.reason.resolved.isEmpty {
                    Text(focused.reason.resolved).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                }
                NodeOutlineView(roots: focused.isContainer ? focused.children : [focused], tree: tree, selection: $model.selection,
                                onIgnore: { model.ignore($0) })
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private var footer: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text("Đã chọn \(model.selection.selectedBytes.formatted)").font(Theme.Font.headline).foregroundStyle(.white)
                Text("\(model.selection.selectedCount.formatted()) mục · dung lượng là ước tính (APFS clone có thể làm lệch)")
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            Button(appearance.cleanTitle) { model.prepareClean() }
                .buttonStyle(PrimaryButtonStyle(accent: appearance.accent))
                .disabled(model.selection.selectedCount == 0)
                .keyboardShortcut(.defaultAction)
        }
    }
}

struct GroupRow: View {
    let node: Node
    let state: CheckState
    let selectedBytes: ByteCount
    let isFocused: Bool
    let onToggle: () -> Void
    let onFocus: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            TriStateCheckbox(state: state, action: onToggle)
            Image(systemName: node.icon ?? "folder").frame(width: 20).foregroundStyle(.white)
            VStack(alignment: .leading, spacing: 1) {
                Text(node.title).font(.system(size: 13, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                if !node.badges.isEmpty {
                    Text(node.badges.joined(separator: " · ")).font(Theme.Font.caption).foregroundStyle(Theme.review).lineLimit(1)
                }
            }
            Spacer()
            Text(node.size.formatted).font(.system(size: 12, weight: .semibold)).monospacedDigit().foregroundStyle(Theme.secondaryText)
        }
        .padding(.horizontal, 8).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 8).fill(isFocused ? Color.white.opacity(0.16) : .clear))
        .contentShape(Rectangle())
        .onTapGesture(perform: onFocus)
    }
}

public struct TriStateCheckbox: View {
    let state: CheckState
    let action: () -> Void

    public init(state: CheckState, action: @escaping () -> Void) {
        self.state = state
        self.action = action
    }

    public var body: some View {
        Button(action: action) {
            Image(systemName: state == .on ? "checkmark.circle.fill" : state == .mixed ? "minus.circle.fill" : "circle")
                .font(.system(size: 16))
                .foregroundStyle(state == .off ? Color.white.opacity(0.6) : Color.white)
        }
        .buttonStyle(.plain)
        .accessibilityValue(state == .on ? "đã chọn" : state == .mixed ? "chọn một phần" : "chưa chọn")
    }
}

/// Hộp xác nhận: liệt kê mục cần root, mục cần xem lại, mục bị chặn (mục 7.1, 15.2).
public struct ConfirmView: View {
    let plan: CleanPlan
    let appearance: FeatureAppearance
    let onCancel: () -> Void
    let onConfirm: () -> Void

    public init(plan: CleanPlan, appearance: FeatureAppearance, onCancel: @escaping () -> Void, onConfirm: @escaping () -> Void) {
        self.plan = plan
        self.appearance = appearance
        self.onCancel = onCancel
        self.onConfirm = onConfirm
    }

    public var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "exclamationmark.shield").font(.system(size: 46)).foregroundStyle(.white)
            Text("Xác nhận dọn dẹp").font(Theme.Font.title).foregroundStyle(.white)
            Text("Sẽ xử lý \(plan.items.count) mục, khoảng \(plan.totalSize.formatted).")
                .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if !plan.rootItems.isEmpty {
                        section("Cần quyền quản trị (\(plan.rootItems.count))", symbol: "lock.fill", items: plan.rootItems.map { ($0.title, $0.url?.path ?? "", $0.expectedSize) })
                    }
                    if !plan.reviewItems.isEmpty {
                        section("Cần xem lại (\(plan.reviewItems.count))", symbol: "eye", items: plan.reviewItems.map { ($0.title, $0.url?.path ?? "", $0.expectedSize) })
                    }
                    let permanent = plan.permanentItems.filter { $0.safety != .safe }
                    if !permanent.isEmpty {
                        Text("\(permanent.count) mục sẽ bị xoá vĩnh viễn (không qua Thùng rác).").font(Theme.Font.caption).foregroundStyle(Theme.review)
                    }
                    if !plan.blocked.isEmpty {
                        section("Bị chặn vì lý do an toàn (\(plan.blocked.count))", symbol: "hand.raised.fill",
                                items: plan.blocked.map { ($0.title, $0.violation.description, ByteCount.zero) })
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxWidth: 640)
            HStack(spacing: 12) {
                Button("Huỷ", action: onCancel).buttonStyle(GlassButtonStyle()).keyboardShortcut(.cancelAction)
                Button(appearance.cleanTitle, action: onConfirm)
                    .buttonStyle(PrimaryButtonStyle(accent: appearance.accent))
                    .disabled(plan.items.isEmpty)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func section(_ title: String, symbol: String, items: [(String, String, ByteCount)]) -> some View {
        Card(padding: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Label(title, systemImage: symbol).font(Theme.Font.headline).foregroundStyle(.white)
                ForEach(Array(items.prefix(50).enumerated()), id: \.offset) { _, item in
                    HStack {
                        VStack(alignment: .leading, spacing: 0) {
                            Text(item.0).font(Theme.Font.body).foregroundStyle(.white).lineLimit(1)
                            Text(item.1.abbreviatingHome).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).lineLimit(1).truncationMode(.middle)
                        }
                        Spacer()
                        if item.2 > .zero { Text(item.2.formatted).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText) }
                    }
                }
                if items.count > 50 { Text("… và \(items.count - 50) mục khác").font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText) }
            }
        }
    }
}

public struct CleaningView: View {
    let appearance: FeatureAppearance
    let progress: Double
    let currentItem: String
    let freed: ByteCount

    public init(appearance: FeatureAppearance, progress: Double, currentItem: String, freed: ByteCount) {
        self.appearance = appearance
        self.progress = progress
        self.currentItem = currentItem
        self.freed = freed
    }

    public var body: some View {
        VStack(spacing: 22) {
            Spacer()
            ProgressRing(progress: progress, lineWidth: 12, label: "đang dọn").frame(width: 170, height: 170)
            Text("Đã giải phóng \(freed.formatted)").font(Theme.Font.headline).foregroundStyle(.white)
            Text(currentItem.abbreviatingHome).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).lineLimit(1).truncationMode(.middle).frame(maxWidth: 520)
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

public struct DoneView: View {
    let report: CleanReport
    let appearance: FeatureAppearance
    let onRescan: () -> Void
    let onClose: () -> Void

    public init(report: CleanReport, appearance: FeatureAppearance, onRescan: @escaping () -> Void, onClose: @escaping () -> Void) {
        self.report = report
        self.appearance = appearance
        self.onRescan = onRescan
        self.onClose = onClose
    }

    public var body: some View {
        VStack(spacing: 18) {
            Spacer()
            Image(systemName: report.failed.isEmpty ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.system(size: 64)).foregroundStyle(.white)
            Text(report.dryRun ? "Chạy thử xong (không xoá gì)" : "Hoàn tất").font(Theme.Font.title).foregroundStyle(.white)
            HStack(spacing: 32) {
                StatTile(value: report.estimatedFreed.formatted, label: "Ước tính đã dọn", symbol: "sparkles")
                if let measured = report.measuredFreed {
                    StatTile(value: measured.formatted, label: "Đo thực tế trên ổ", symbol: "internaldrive")
                }
                StatTile(value: "\(report.succeeded.count)", label: "Mục thành công", symbol: "checkmark")
                if !report.failed.isEmpty { StatTile(value: "\(report.failed.count)", label: "Mục lỗi", symbol: "xmark") }
            }
            if let measured = report.measuredFreed, measured < report.estimatedFreed {
                Text("Con số thực tế có thể thấp hơn do snapshot APFS giữ lại block hoặc file clone.")
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            if !report.inUse.isEmpty {
                NoticeBanner(symbol: "lock.open", text: "\(report.inUse.count) mục đang được sử dụng nên đã bỏ qua. Hãy tắt app liên quan rồi thử lại.")
                    .frame(maxWidth: 560)
            }
            if !report.failed.isEmpty {
                Card(padding: 10) {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 4) {
                            ForEach(report.failed.prefix(30)) { e in
                                HStack {
                                    Text(e.item.title).foregroundStyle(.white).lineLimit(1)
                                    Spacer()
                                    if case let .failed(_, message) = e.result.outcome {
                                        Text(message).foregroundStyle(Theme.risky).lineLimit(1)
                                    }
                                }
                                .font(Theme.Font.caption)
                            }
                        }
                    }
                    .frame(maxHeight: 140)
                }
                .frame(maxWidth: 560)
            }
            Spacer()
            HStack(spacing: 12) {
                Button("Xong", action: onClose).buttonStyle(GlassButtonStyle())
                Button("Quét lại", action: onRescan).buttonStyle(PrimaryButtonStyle(accent: appearance.accent))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

public struct ErrorView: View {
    let message: String
    let onRetry: () -> Void

    public init(message: String, onRetry: @escaping () -> Void) {
        self.message = message
        self.onRetry = onRetry
    }

    public var body: some View {
        VStack(spacing: 14) {
            Image(systemName: "xmark.octagon.fill").font(.system(size: 54)).foregroundStyle(.white)
            Text("Đã có lỗi").font(Theme.Font.title).foregroundStyle(.white)
            Text(message).font(Theme.Font.body).foregroundStyle(Theme.secondaryText).multilineTextAlignment(.center).frame(maxWidth: 480)
            Button("Thử lại", action: onRetry).buttonStyle(GlassButtonStyle())
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

struct PermanentDeleteSheet: View {
    let prompt: ScanCleanViewModel.PermanentDeletePrompt

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Không thể chuyển vào Thùng rác").font(.headline)
            Text("\"\(prompt.url.lastPathComponent)\" nằm trên ổ không có Thùng rác (ổ ngoài hoặc ổ mạng). Bạn có muốn xoá vĩnh viễn không? Thao tác này không khôi phục được.")
                .font(.body)
            HStack {
                Spacer()
                Button("Bỏ qua") { prompt.answer(false) }.keyboardShortcut(.cancelAction)
                Button("Xoá vĩnh viễn") { prompt.answer(true) }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}
