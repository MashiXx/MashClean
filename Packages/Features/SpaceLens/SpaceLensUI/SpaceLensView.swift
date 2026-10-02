import AppKit
import CleanEngine
import DesignSystem
import FileSystemKit
import SharedUI
import SpaceLensDomain
import SpaceLensScanning
import SweepCore
import SwiftUI

/// Màn Space Lens (mục 11.4): chọn ổ/thư mục, bản đồ sunburst, danh sách theo dung lượng, chuyển vào Thùng rác.
public struct SpaceLensView: View {
    @StateObject private var model: SpaceLensViewModel
    private let initialPath: URL?

    public static let accent: Theme.Accent = .spaceLens

    /// - Parameter initialPath: thư mục cần quét ngay (ví dụ từ FinderSync qua `cleanboost://spacelens?path=...`).
    public init(services: ScanServices, feature: SpaceLensFeature, initialPath: URL? = nil) {
        _model = StateObject(wrappedValue: SpaceLensViewModel(services: services, feature: feature))
        self.initialPath = initialPath
    }

    public var body: some View {
        ZStack {
            Theme.background(for: Self.accent).ignoresSafeArea()
            content.padding(24)
        }
        .onAppear {
            if let initialPath, case .idle = model.phase { model.scan(initialPath) } else { model.resumeWatching() }
        }
        .onDisappear { model.stopWatching() }
        .onChange(of: initialPath) { newValue in
            if let newValue { model.scan(newValue) }
        }
    }

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .idle:
            SpaceLensPicker(onPick: model.scan, home: model.feature.defaultRoot)
        case let .scanning(root, progress):
            SpaceLensScanningView(root: root, progress: progress, onCancel: model.cancel)
        case .results:
            SpaceLensResultsView(model: model)
        case let .error(message):
            ErrorView(message: message, onRetry: model.chooseAnother)
        }
    }
}

// MARK: - Chọn ổ hoặc thư mục

struct SpaceLensPicker: View {
    let onPick: (URL) -> Void
    let home: URL
    @State private var volumes: [VolumeInfo] = []

    var body: some View {
        VStack(spacing: 26) {
            Spacer()
            FeatureHeader(symbol: "circle.circle", title: "Space Lens",
                          subtitle: String(localized: "Xem trực quan thư mục nào đang chiếm nhiều dung lượng nhất, đi sâu dần và dọn ngay trên bản đồ."))
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 220), spacing: 14)], spacing: 14) {
                ForEach(volumes) { v in
                    Button { onPick(URL(fileURLWithPath: v.mountPoint)) } label: { VolumeCard(volume: v) }
                        .buttonStyle(.plain)
                }
            }
            .frame(maxWidth: 720)
            HStack(spacing: 12) {
                Button { onPick(home) } label: { Label(String(localized: "Thư mục người dùng"), systemImage: "house") }
                    .buttonStyle(GlassButtonStyle())
                Button { chooseFolder() } label: { Label(String(localized: "Chọn thư mục…"), systemImage: "folder") }
                    .buttonStyle(GlassButtonStyle())
            }
            Spacer()
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear { volumes = VolumeInfo.mounted() }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = String(localized: "Quét")
        panel.message = String(localized: "Chọn thư mục để xem bản đồ dung lượng")
        if panel.runModal() == .OK, let url = panel.url { onPick(url) }
    }
}

struct VolumeCard: View {
    let volume: VolumeInfo

    var body: some View {
        Card(padding: 14) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Image(systemName: volume.isInternal ? "internaldrive" : "externaldrive").font(.system(size: 22)).foregroundStyle(.white)
                    Text(volume.name).font(Theme.Font.headline).foregroundStyle(.white).lineLimit(1)
                    Spacer()
                }
                ProgressView(value: volume.usedFraction).tint(.white)
                Text(String(localized: "Còn trống \(ByteCount(volume.availableForImportantUsage).formatted) / \(ByteCount(volume.totalCapacity).formatted)"))
                    .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
            }
        }
        .contentShape(Rectangle())
    }
}

// MARK: - Đang quét

struct SpaceLensScanningView: View {
    let root: String
    let progress: DiskScanProgress.Snapshot
    let onCancel: () -> Void

    var body: some View {
        VStack(spacing: 22) {
            Spacer()
            ProgressView().controlSize(.large).tint(.white)
            Text(String(localized: "Đang quét \(root.abbreviatingHome)")).font(Theme.Font.headline).foregroundStyle(.white)
            HStack(spacing: 18) {
                Label(String(localized: "\(progress.items.formatted()) mục"), systemImage: "doc.on.doc")
                Label(ByteCount(progress.bytes).formatted, systemImage: "externaldrive")
            }
            .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).monospacedDigit()
            Spacer()
            Button(String(localized: "Dừng"), action: onCancel).buttonStyle(GlassButtonStyle()).keyboardShortcut(.cancelAction)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Kết quả

struct SpaceLensResultsView: View {
    @ObservedObject var model: SpaceLensViewModel
    @State private var confirmTrash = false

    var body: some View {
        VStack(spacing: 12) {
            header
            if let notice = model.notice {
                NoticeBanner(symbol: "info.circle", text: notice, actionTitle: String(localized: "Đóng")) { model.notice = nil }
            }
            HStack(spacing: 14) {
                SunburstChart(model: model)
                    .frame(minWidth: 360, maxWidth: .infinity, maxHeight: .infinity)
                SpaceLensList(model: model)
                    .frame(width: 340)
            }
            footer
        }
        .onChange(of: model.pendingPlan != nil) { confirmTrash = $0 }
        .alert(String(localized: "Chuyển vào Thùng rác?"), isPresented: $confirmTrash, presenting: model.pendingPlan) { plan in
            Button(String(localized: "Huỷ"), role: .cancel) { model.pendingPlan = nil }
            if !plan.items.isEmpty {
                Button(String(localized: "Chuyển vào Thùng rác"), role: .destructive) { model.confirmTrash() }
            }
        } message: { plan in
            Text(confirmMessage(plan))
        }
    }

    private func confirmMessage(_ plan: CleanPlan) -> String {
        var text = String(localized: "\(plan.items.count) mục, khoảng \(plan.totalSize.formatted). Có thể khôi phục từ Thùng rác hoặc màn Lịch sử.")
        if !plan.blocked.isEmpty {
            text += String(localized: "\n\n\(plan.blocked.count) mục bị chặn vì lý do an toàn:\n") + plan.blocked.prefix(5).map { "• \($0.title)" }.joined(separator: "\n")
        }
        return text
    }

    private var header: some View {
        HStack(spacing: 10) {
            Button(action: model.goUp) { Image(systemName: "chevron.left") }
                .buttonStyle(GlassButtonStyle())
                .disabled(!model.navigator.canGoUp)
                .keyboardShortcut(.leftArrow, modifiers: [.command])
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 4) {
                    let crumbs = model.navigator.breadcrumb(in: model.tree)
                    ForEach(crumbs) { crumb in
                        Button(crumb.title) { model.go(toLevel: crumb.level) }
                            .buttonStyle(.plain)
                            .font(.system(size: 13, weight: crumb.level == crumbs.count - 1 ? .bold : .regular))
                            .foregroundStyle(.white)
                        if crumb.level < crumbs.count - 1 {
                            Image(systemName: "chevron.right").font(.system(size: 10)).foregroundStyle(Theme.tertiaryText)
                        }
                    }
                }
            }
            Spacer()
            Text(String(localized: "Quét trong \(String(format: "%.1f", model.scanDuration)) giây")).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            Button(String(localized: "Quét lại")) { model.scan(URL(fileURLWithPath: model.tree.rootPath)) }.buttonStyle(GlassButtonStyle())
            Button(String(localized: "Chọn khác"), action: model.chooseAnother).buttonStyle(GlassButtonStyle())
        }
    }

    private var footer: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(String(localized: "Đã chọn \(ByteCount(model.selectedBytes).formatted)")).font(Theme.Font.headline).foregroundStyle(.white)
                Text(String(localized: "\(model.selection.count) mục · dung lượng là ước tính (APFS clone có thể làm lệch)"))
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            if model.isCleaning { ProgressView().controlSize(.small) }
            Button(String(localized: "Hiện trong Finder")) { model.revealInFinder(Array(model.selection)) }
                .buttonStyle(GlassButtonStyle())
                .disabled(model.selection.isEmpty)
            Button(String(localized: "Chuyển vào Thùng rác"), action: model.prepareTrash)
                .buttonStyle(PrimaryButtonStyle(accent: SpaceLensView.accent))
                .disabled(model.selection.isEmpty || model.isCleaning)
        }
    }
}

// MARK: - Sunburst

/// Biểu đồ sunburst vẽ bằng `Canvas`, hit-test để hover (tooltip) và click đi sâu.
struct SunburstChart: View {
    @ObservedObject var model: SpaceLensViewModel
    @State private var hoverPoint: CGPoint?

    var body: some View {
        GeometryReader { geo in
            let side = min(geo.size.width, geo.size.height)
            let center = CGPoint(x: geo.size.width / 2, y: geo.size.height / 2)
            let outer = side / 2 - 8
            let inner = outer * 0.24
            let ringWidth = (outer - inner) / 4
            ZStack {
                Canvas { ctx, _ in
                    draw(in: ctx, center: center, inner: inner, ringWidth: ringWidth)
                }
                centerLabel.frame(width: inner * 1.8)
                    .position(center)
                    .allowsHitTesting(false)
                if let seg = model.hovered, let p = hoverPoint {
                    tooltip(for: seg)
                        .position(x: min(max(p.x, 110), geo.size.width - 110), y: max(p.y - 34, 20))
                        .allowsHitTesting(false)
                }
            }
            .contentShape(Rectangle())
            .onContinuousHover(coordinateSpace: .local) { phase in
                switch phase {
                case let .active(p):
                    hoverPoint = p
                    model.hovered = hit(p, center: center, inner: inner, ringWidth: ringWidth)
                case .ended:
                    hoverPoint = nil
                    model.hovered = nil
                }
            }
            .gesture(SpatialTapGesture().onEnded { value in
                let (_, r) = SunburstLayout.polar(dx: value.location.x - center.x, dy: value.location.y - center.y)
                if r < inner {
                    model.goUp()
                } else if let seg = hit(value.location, center: center, inner: inner, ringWidth: ringWidth) {
                    switch seg.target {
                    case let .node(i): model.drill(into: i)
                    case .others: break
                    }
                }
            })
        }
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private func hit(_ p: CGPoint, center: CGPoint, inner: CGFloat, ringWidth: CGFloat) -> SunburstSegment? {
        let (angle, radius) = SunburstLayout.polar(dx: p.x - center.x, dy: p.y - center.y)
        return SunburstLayout.hitTest(model.segments, angle: angle, radius: radius, innerRadius: inner, ringWidth: ringWidth)
    }

    private func draw(in ctx: GraphicsContext, center: CGPoint, inner: CGFloat, ringWidth: CGFloat) {
        for seg in model.segments {
            let r0 = inner + CGFloat(seg.depth - 1) * ringWidth + 1
            let r1 = r0 + ringWidth - 2
            let a0 = Angle(radians: seg.startAngle - .pi / 2)
            let a1 = Angle(radians: seg.endAngle - .pi / 2)
            var path = Path()
            path.addArc(center: center, radius: r1, startAngle: a0, endAngle: a1, clockwise: false)
            path.addArc(center: center, radius: r0, startAngle: a1, endAngle: a0, clockwise: true)
            path.closeSubpath()
            let isHovered = model.hovered?.id == seg.id
            ctx.fill(path, with: .color(color(for: seg, highlighted: isHovered)))
            if seg.span > 0.02 {
                ctx.stroke(path, with: .color(.black.opacity(0.25)), lineWidth: 0.5)
            }
        }
    }

    private func color(for seg: SunburstSegment, highlighted: Bool) -> Color {
        if seg.branch < 0 { return Color.white.opacity(highlighted ? 0.45 : 0.22) }
        let hue = (0.08 + Double(seg.branch) * 0.137).truncatingRemainder(dividingBy: 1)
        let depthFade = Double(seg.depth - 1) * 0.12
        let isFile = seg.nodeIndex.map { !model.tree.isDirectory($0) } ?? false
        return Color(hue: hue, saturation: max(0.25, 0.75 - depthFade) * (isFile ? 0.6 : 1),
                     brightness: highlighted ? 1 : 0.92 - depthFade / 2)
    }

    private var centerLabel: some View {
        let index = model.currentIndex
        return VStack(spacing: 2) {
            Text(model.tree.name(of: index) == model.tree.rootPath ? (model.tree.rootPath as NSString).lastPathComponent : model.tree.name(of: index))
                .font(.system(size: 13, weight: .semibold)).lineLimit(1)
            Text(ByteCount(model.tree.size(of: index)).formatted).font(.system(size: 18, weight: .bold, design: .rounded)).monospacedDigit()
            if model.navigator.canGoUp {
                Image(systemName: "arrow.up.circle").font(.system(size: 12)).opacity(0.7)
            }
        }
        .foregroundStyle(.white)
    }

    private func tooltip(for seg: SunburstSegment) -> some View {
        let title: String
        switch seg.target {
        case let .node(i): title = model.tree.name(of: i)
        case let .others(_, count): title = String(localized: "Các mục nhỏ khác (\(count))")
        }
        return VStack(spacing: 1) {
            Text(title).font(.system(size: 12, weight: .semibold)).lineLimit(1)
            Text(ByteCount(seg.size).formatted).font(.system(size: 11)).monospacedDigit()
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 10).padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.black.opacity(0.75)))
        .frame(maxWidth: 220)
    }
}

// MARK: - Danh sách

struct SpaceLensList: View {
    @ObservedObject var model: SpaceLensViewModel
    private let limit = 400

    var body: some View {
        let children = model.currentChildren
        let total = max(model.tree.size(of: model.currentIndex), 1)
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                SelectAllToggle(selected: children.filter(model.isSelected).count, total: children.count,
                                action: model.setCurrentChildrenSelected)
                    .disabled(children.isEmpty)
                Spacer()
                Text(String(localized: "\(children.count.formatted()) mục")).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
            }
            .padding(.horizontal, 8)
            ScrollView {
                LazyVStack(spacing: 2) {
                    ForEach(children.prefix(limit), id: \.self) { i in
                        row(i, total: total)
                    }
                    if children.count > limit {
                        Text(String(localized: "… và \(children.count - limit) mục nhỏ hơn")).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).padding(6)
                    }
                }
                .padding(4)
            }
        }
        .padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private func row(_ i: Int, total: UInt64) -> some View {
        let tree = model.tree
        let flags = tree.flags(of: i)
        let isDir = flags.contains(.directory)
        let path = tree.path(of: i)
        let fraction = Double(tree.size(of: i)) / Double(total)
        let highlighted = model.hovered?.nodeIndex == i
        return HStack(spacing: 8) {
            TriStateCheckbox(state: model.isSelected(i) ? .on : .off) { model.toggle(i) }
            FileIcon(url: URL(fileURLWithPath: path), fallback: isDir ? "folder" : "doc", size: 18)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Text(tree.name(of: i)).font(.system(size: 12, weight: .medium)).foregroundStyle(.white).lineLimit(1).truncationMode(.middle)
                    if flags.contains(.unreadable) { Image(systemName: "lock.fill").font(.system(size: 9)).foregroundStyle(Theme.review).help(String(localized: "Không đọc được (cần Full Disk Access)")) }
                    if flags.contains(.otherVolume) { Image(systemName: "externaldrive").font(.system(size: 9)).foregroundStyle(Theme.tertiaryText).help(String(localized: "Volume khác, không tính")) }
                    if flags.contains(.symlink) { Image(systemName: "arrow.turn.up.right").font(.system(size: 9)).foregroundStyle(Theme.tertiaryText) }
                }
                GeometryReader { g in
                    Capsule().fill(Color.white.opacity(0.5)).frame(width: max(2, g.size.width * fraction), height: 3)
                }
                .frame(height: 3)
            }
            Text(ByteCount(tree.size(of: i)).formatted).font(.system(size: 11, weight: .semibold)).monospacedDigit().foregroundStyle(Theme.secondaryText)
            if isDir && !tree.children(of: i).isEmpty {
                Image(systemName: "chevron.right").font(.system(size: 10)).foregroundStyle(Theme.tertiaryText)
            }
        }
        .padding(.horizontal, 8).padding(.vertical, 5)
        .background(RoundedRectangle(cornerRadius: 6).fill(highlighted ? Color.white.opacity(0.16) : .clear))
        .contentShape(Rectangle())
        .onTapGesture { model.drill(into: i) }
        .contextMenu {
            Button(String(localized: "Hiện trong Finder")) { model.revealInFinder([path]) }
            Button(model.isSelected(i) ? String(localized: "Bỏ chọn") : String(localized: "Chọn")) { model.toggle(i) }
            if isDir { Button(String(localized: "Mở thư mục này")) { model.drill(into: i) } }
        }
    }
}
