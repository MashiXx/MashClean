import AppKit
import DesignSystem
import DuplicatesDomain
import DuplicatesScanning
import NodeTree
import QuickLookThumbnailing
import ScanEngine
import SharedUI
import SweepCore
import SwiftUI

/// View model Duplicates: thư mục quét do người dùng chỉnh, mặc định không chọn gì (mục 11.8, 15.2).
@MainActor
final class DuplicatesViewModel: ScanCleanViewModel {
    @Published private(set) var roots: [URL]
    @Published var focusedGroup: NodeID?
    @Published var confirmAllSelected = false
    let progressBox = DuplicateProgressBox()
    private let rootsBox: Locked<[URL]>
    let feature: DuplicatesFeature

    init(services: ScanServices, feature: DuplicatesFeature) {
        let box = Locked(feature.defaultRoots)
        let progress = progressBox
        rootsBox = box
        roots = feature.defaultRoots
        self.feature = feature
        super.init(services: services, kind: "duplicates", tasks: { [feature.makeTask(roots: box.current, progress: progress)] })
    }

    override func defaultSelection(for tree: NodeTree) -> SelectionState { SelectionState() }

    override func scanDidFinish(_ result: ScanResult) {
        focusedGroup = sortedGroups.first?.id
    }

    var sortedGroups: [Node] {
        (currentTree?.roots ?? []).sorted { DuplicateSelection.wasted($0) > DuplicateSelection.wasted($1) }
    }

    func addRoot() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Thêm"
        guard panel.runModal() == .OK else { return }
        for url in panel.urls where !roots.contains(url) { roots.append(url) }
        rootsBox.withLock { $0 = roots }
    }

    func removeRoot(_ url: URL) {
        roots.removeAll { $0 == url }
        rootsBox.withLock { $0 = roots }
    }

    func resetRoots() {
        roots = feature.defaultRoots
        rootsBox.withLock { $0 = roots }
    }

    func autoSelect() {
        guard let tree = currentTree else { return }
        selection = DuplicateSelection.autoSelect(in: tree)
    }

    /// Cảnh báo nếu có nhóm bị chọn hết mọi bản.
    func requestClean() {
        guard let tree = currentTree else { return }
        if !DuplicateSelection.groupsWithEverythingSelected(selection, in: tree).isEmpty {
            confirmAllSelected = true
        } else {
            prepareClean()
        }
    }
}

/// Màn Duplicates (mục 11.8).
public struct DuplicatesView: View {
    @StateObject private var model: DuplicatesViewModel

    public static let appearance = FeatureAppearance(
        accent: .files, symbol: "doc.on.doc",
        title: "File trùng lặp",
        subtitle: "Tìm file có nội dung giống hệt nhau (so dung lượng, băm nhanh rồi SHA-256). Bỏ qua hard link và clone APFS vì xoá chúng không giải phóng dung lượng.",
        scanTitle: "Tìm", cleanTitle: "Chuyển vào Thùng rác"
    )

    public init(services: ScanServices, feature: DuplicatesFeature) {
        _model = StateObject(wrappedValue: DuplicatesViewModel(services: services, feature: feature))
    }

    public var body: some View {
        let appearance = Self.appearance
        ZStack {
            Theme.background(for: appearance.accent).ignoresSafeArea()
            Group {
                switch model.phase {
                case .idle:
                    IdleView(appearance: appearance, accessory: RootsEditor(model: model), onScan: model.startScan)
                case let .scanning(progress):
                    DuplicatesScanningView(model: model, progress: progress)
                case .results:
                    DuplicatesResultsView(model: model)
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
            .padding(24)
        }
        .alert("Chọn hết mọi bản trong nhóm?", isPresented: $model.confirmAllSelected) {
            Button("Huỷ", role: .cancel) {}
            Button("Vẫn tiếp tục", role: .destructive) { model.prepareClean() }
        } message: {
            Text("Có nhóm mà mọi bản đều được chọn: sau khi dọn sẽ không còn bản nào ngoài Thùng rác.")
        }
        .sheet(item: $model.permanentDeletePrompt) { prompt in
            VStack(alignment: .leading, spacing: 12) {
                Text("Không thể chuyển vào Thùng rác").font(.headline)
                Text("\"\(prompt.url.lastPathComponent)\" nằm trên ổ không có Thùng rác. Xoá vĩnh viễn? Thao tác này không khôi phục được.")
                HStack {
                    Spacer()
                    Button("Bỏ qua") { prompt.answer(false) }.keyboardShortcut(.cancelAction)
                    Button("Xoá vĩnh viễn") { prompt.answer(true) }
                }
            }
            .padding(20)
            .frame(width: 420)
        }
    }
}

/// Danh sách thư mục quét (thêm/bớt).
struct RootsEditor: View {
    @ObservedObject var model: DuplicatesViewModel

    var body: some View {
        Card(padding: 12) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Thư mục quét").font(Theme.Font.headline).foregroundStyle(.white)
                ForEach(model.roots, id: \.self) { url in
                    HStack {
                        Image(systemName: "folder").foregroundStyle(.white)
                        Text(url.path.abbreviatingHome).font(Theme.Font.body).foregroundStyle(.white).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button { model.removeRoot(url) } label: { Image(systemName: "minus.circle") }
                            .buttonStyle(.plain).foregroundStyle(Theme.secondaryText)
                    }
                }
                Text("Bỏ qua ~/Library, thư mục ẩn, nội dung package và file nhỏ hơn 1 MB.")
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                HStack {
                    Button("Thêm thư mục…", action: model.addRoot).buttonStyle(GlassButtonStyle())
                    Button("Mặc định", action: model.resetRoots).buttonStyle(GlassButtonStyle())
                }
            }
        }
        .frame(maxWidth: 480)
    }
}

/// Tiến độ theo bước (liệt kê → nhóm dung lượng → băm nhanh → SHA-256 → clone).
struct DuplicatesScanningView: View {
    @ObservedObject var model: DuplicatesViewModel
    let progress: ScanProgress

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.2)) { _ in
            let step = model.progressBox.current
            VStack(spacing: 22) {
                Spacer()
                ProgressRing(progress: step.fraction, lineWidth: 12, label: "đang tìm").frame(width: 170, height: 170)
                Text("Bước \(min(step.stage.step, 5))/5: \(step.stage.title)").font(Theme.Font.headline).foregroundStyle(.white)
                HStack(spacing: 18) {
                    Label("\(step.filesSeen.formatted()) mục đã duyệt", systemImage: "doc.on.doc")
                    if step.total > 0 { Label("\(step.processed.formatted())/\(step.total.formatted()) file", systemImage: "number") }
                }
                .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).monospacedDigit()
                Spacer()
                Button("Dừng", action: model.cancel).buttonStyle(GlassButtonStyle()).keyboardShortcut(.cancelAction)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }
}

struct DuplicatesResultsView: View {
    @ObservedObject var model: DuplicatesViewModel

    var body: some View {
        let groups = model.sortedGroups
        VStack(spacing: 12) {
            header(groups)
            if groups.isEmpty {
                Spacer()
                VStack(spacing: 10) {
                    Image(systemName: "checkmark.seal.fill").font(.system(size: 56)).foregroundStyle(.white)
                    Text("Không tìm thấy file trùng lặp").font(Theme.Font.title).foregroundStyle(.white)
                }
                Spacer()
            } else if let tree = model.currentTree {
                HStack(spacing: 12) {
                    groupList(groups, tree: tree).frame(width: 300)
                    if let focused = model.focusedGroup.flatMap({ tree.node($0) }) ?? groups.first {
                        GroupDetail(model: model, group: focused, tree: tree)
                    }
                }
            }
            footer
        }
    }

    private func header(_ groups: [Node]) -> some View {
        let wasted = groups.reduce(ByteCount.zero) { $0 + DuplicateSelection.wasted($1) }
        return HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text(DuplicatesView.appearance.title).font(Theme.Font.title).foregroundStyle(.white)
                Text("\(groups.count.formatted()) nhóm · lãng phí khoảng \(wasted.formatted)").font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            Button("Quét lại", action: model.startScan).buttonStyle(GlassButtonStyle())
        }
    }

    private func groupList(_ groups: [Node], tree: NodeTree) -> some View {
        ScrollView {
            LazyVStack(spacing: 4) {
                ForEach(groups) { g in
                    HStack(spacing: 8) {
                        TriStateCheckbox(state: model.selection.state(of: g.id, in: tree)) { model.toggle(g.id) }
                        FileIcon(url: g.children.first?.url, fallback: "doc.on.doc", size: 20)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(g.children.first?.title ?? g.title).font(.system(size: 12, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                            Text("\(g.children.count) bản · \(g.reason.resolved)").font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                        }
                        Spacer()
                        Text(DuplicateSelection.wasted(g).formatted).font(.system(size: 11, weight: .semibold)).monospacedDigit()
                            .foregroundStyle(Theme.secondaryText)
                    }
                    .padding(.horizontal, 8).padding(.vertical, 6)
                    .background(RoundedRectangle(cornerRadius: 8).fill(model.focusedGroup == g.id ? Color.white.opacity(0.16) : .clear))
                    .contentShape(Rectangle())
                    .onTapGesture { model.focusedGroup = g.id }
                }
            }
            .padding(6)
        }
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private var footer: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text("Đã chọn \(model.selection.selectedBytes.formatted)").font(Theme.Font.headline).foregroundStyle(.white)
                Text("\(model.selection.selectedCount.formatted()) file · có thể khôi phục từ Thùng rác")
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            Button("Chọn tự động", action: model.autoSelect).buttonStyle(GlassButtonStyle())
                .help("Chọn mọi bản trừ bản nên giữ trong mỗi nhóm")
            Button("Bỏ chọn") { model.selection = SelectionState() }.buttonStyle(GlassButtonStyle())
                .disabled(model.selection.selectedCount == 0)
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            Button(DuplicatesView.appearance.cleanTitle, action: model.requestClean)
                .buttonStyle(PrimaryButtonStyle(accent: DuplicatesView.appearance.accent))
                .disabled(model.selection.selectedCount == 0)
        }
    }
}

/// Chi tiết một nhóm: ảnh xem trước + từng bản với checkbox, nhãn "Nên giữ" / clone.
struct GroupDetail: View {
    @ObservedObject var model: DuplicatesViewModel
    let group: Node
    let tree: NodeTree

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 14) {
                if let url = group.children.first?.url {
                    QuickLookThumbnail(url: url).frame(width: 140, height: 140)
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text(group.children.first?.title ?? group.title).font(Theme.Font.headline).foregroundStyle(.white).lineLimit(2)
                    Text("\(group.children.count) bản giống hệt nhau · \(group.reason.resolved)").font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                    Text("Có thể giải phóng \(DuplicateSelection.wasted(group).formatted) nếu chỉ giữ một bản")
                        .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                }
                Spacer()
            }
            ScrollView {
                VStack(spacing: 2) {
                    ForEach(group.children) { file in
                        row(file)
                    }
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private func row(_ file: Node) -> some View {
        HStack(spacing: 8) {
            TriStateCheckbox(state: model.selection.isSelected(file.id) ? .on : .off) { model.toggle(file.id) }
            FileIcon(url: file.url, size: 18)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    Text(file.title).font(.system(size: 12, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                    if DuplicateSelection.isKeep(file) { Pill(DuplicateBadges.keep, color: Theme.safe) }
                    if DuplicateSelection.isClone(file) { Pill("Clone APFS", color: Theme.review).help(DuplicateBadges.clone) }
                }
                Text((file.url?.deletingLastPathComponent().path ?? "").abbreviatingHome)
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            if let date = file.lastAccess {
                Text(Self.dateFormatter.string(from: date)).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
            }
        }
        .padding(.horizontal, 8).padding(.vertical, 5)
        .contentShape(Rectangle())
        .contextMenu {
            if let url = file.url {
                Button("Hiện trong Finder") { NSWorkspace.shared.activateFileViewerSelecting([url]) }
                Button("Mở") { NSWorkspace.shared.open(url) }
            }
        }
    }
}

/// Ảnh xem trước bằng `QLThumbnailGenerator`; chưa có thì dùng icon Finder.
struct QuickLookThumbnail: View {
    let url: URL
    @State private var image: NSImage?

    var body: some View {
        Group {
            if let image {
                Image(nsImage: image).resizable().scaledToFit()
            } else {
                FileIcon(url: url, size: 96)
            }
        }
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.black.opacity(0.2)))
        .task(id: url) {
            image = nil
            image = await Self.thumbnail(for: url, side: 280)
        }
    }

    static func thumbnail(for url: URL, side: CGFloat) async -> NSImage? {
        let request = QLThumbnailGenerator.Request(fileAt: url, size: CGSize(width: side, height: side), scale: 2, representationTypes: .thumbnail)
        return await withCheckedContinuation { (cont: CheckedContinuation<NSImage?, Never>) in
            QLThumbnailGenerator.shared.generateBestRepresentation(for: request) { rep, _ in
                cont.resume(returning: rep?.nsImage)
            }
        }
    }
}
