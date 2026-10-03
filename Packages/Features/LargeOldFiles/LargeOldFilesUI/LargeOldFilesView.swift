import AppKit
import DesignSystem
import LargeOldFilesDomain
import LargeOldFilesScanning
import NodeTree
import ScanEngine
import SharedUI
import SweepCore
import SwiftUI

/// View model Large & Old: dùng máy trạng thái chung, nhưng mặc định không chọn gì (mục 11.7, 15.2).
@MainActor
final class LargeOldFilesViewModel: ScanCleanViewModel {
    @Published var filter = LargeOldFilter()
    @Published private(set) var items: [LargeOldItem] = []

    init(services: ScanServices, feature: LargeOldFilesFeature) {
        super.init(services: services, kind: "largeOldFiles", tasks: { feature.scanTasks() })
    }

    override func defaultSelection(for tree: NodeTree) -> SelectionState { SelectionState() }

    override func scanDidFinish(_ result: ScanResult) {
        items = currentTree.map(LargeOldItem.items(from:)) ?? []
    }

    var visibleItems: [LargeOldItem] { filter.apply(items) }

    func isSelected(_ item: LargeOldItem) -> Bool { selection.isSelected(item.id) }

    func setSelected(_ item: LargeOldItem, _ on: Bool) { set(item.id, on) }

    func selectAllVisible(_ on: Bool) {
        for item in visibleItems { set(item.id, on) }
    }
}

/// Màn Large & Old Files (mục 11.7): thanh lọc, bảng, chọn tay, chuyển vào Thùng rác qua Clean Engine.
public struct LargeOldFilesView: View {
    @StateObject private var model: LargeOldFilesViewModel
    private let autoStart: Bool

    public static let appearance = FeatureAppearance(
        accent: .files, symbol: "doc.badge.clock",
        title: String(localized: "File lớn và cũ"),
        subtitle: String(localized: "Tìm file lớn hoặc lâu không mở trong thư mục người dùng. Không có gì được chọn sẵn: bạn tự quyết định file nào chuyển vào Thùng rác."),
        cleanTitle: String(localized: "Chuyển vào Thùng rác")
    )

    public init(services: ScanServices, feature: LargeOldFilesFeature, autoStart: Bool = false) {
        _model = StateObject(wrappedValue: LargeOldFilesViewModel(services: services, feature: feature))
        self.autoStart = autoStart
    }

    public var body: some View {
        let appearance = Self.appearance
        ZStack {
            Theme.background(for: appearance.accent).ignoresSafeArea()
            Group {
                switch model.phase {
                case .idle:
                    IdleView(appearance: appearance, accessory: KindChips(), onScan: model.startScan)
                case let .scanning(progress):
                    ScanningView(appearance: appearance, progress: progress, onCancel: model.cancel)
                case .results:
                    LargeOldResultsView(model: model)
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
        .onAppear {
            if autoStart, case .idle = model.phase { model.startScan() }
        }
        .sheet(item: $model.permanentDeletePrompt) { prompt in
            PermanentDeleteConfirm(prompt: prompt)
        }
    }
}

struct KindChips: View {
    var body: some View {
        HStack(spacing: 8) {
            ForEach(FileKind.allCases) { Pill($0.title) }
        }
    }
}

struct PermanentDeleteConfirm: View {
    let prompt: ScanCleanViewModel.PermanentDeletePrompt

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(String(localized: "Không thể chuyển vào Thùng rác")).font(.headline)
            Text(String(localized: "\"\(prompt.url.lastPathComponent)\" nằm trên ổ không có Thùng rác. Xoá vĩnh viễn? Thao tác này không khôi phục được."))
            HStack {
                Spacer()
                Button(String(localized: "Bỏ qua")) { prompt.answer(false) }.keyboardShortcut(.cancelAction)
                Button(String(localized: "Xoá vĩnh viễn")) { prompt.answer(true) }
            }
        }
        .padding(20)
        .frame(width: 420)
    }
}

struct LargeOldResultsView: View {
    @ObservedObject var model: LargeOldFilesViewModel
    @State private var sortOrder = [KeyPathComparator(\LargeOldItem.size, order: .reverse)]

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .none
        return f
    }()

    var body: some View {
        let rows = model.visibleItems.sorted(using: sortOrder)
        VStack(spacing: 12) {
            header(rows)
            if !model.warnings.isEmpty {
                NoticeBanner(text: model.warnings.prefix(2).map(\.message).joined(separator: "; "))
            }
            filterBar
            if model.items.isEmpty {
                Spacer()
                VStack(spacing: 10) {
                    Image(systemName: "checkmark.seal.fill").font(.system(size: 56)).foregroundStyle(.white)
                    Text(String(localized: "Không tìm thấy file lớn hoặc cũ")).font(Theme.Font.title).foregroundStyle(.white)
                }
                Spacer()
            } else {
                table(rows)
            }
            footer
        }
    }

    private func header(_ rows: [LargeOldItem]) -> some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text(LargeOldFilesView.appearance.title).font(Theme.Font.title).foregroundStyle(.white)
                Text(String(localized: "\(model.items.count.formatted()) file, \(model.items.reduce(ByteCount.zero) { $0 + $1.size }.formatted) · đang hiện \(rows.count.formatted())"))
                    .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            Button(String(localized: "Quét lại"), action: model.startScan).buttonStyle(GlassButtonStyle())
        }
    }

    private var filterBar: some View {
        HStack(spacing: 10) {
            Menu {
                Button(String(localized: "Mọi loại")) { model.filter.kinds = [] }
                Divider()
                ForEach(FileKind.allCases) { kind in
                    Button {
                        if model.filter.kinds.contains(kind) { model.filter.kinds.remove(kind) } else { model.filter.kinds.insert(kind) }
                    } label: {
                        Label(kind.title, systemImage: model.filter.kinds.contains(kind) ? "checkmark" : kind.symbol)
                    }
                }
            } label: {
                Text(model.filter.kinds.isEmpty ? String(localized: "Mọi loại") : model.filter.kinds.map(\.title).sorted().joined(separator: ", "))
            }
            .frame(maxWidth: 200)
            Picker("", selection: $model.filter.size) {
                ForEach(LargeOldFilter.SizeRange.allCases) { Text($0.title).tag($0) }
            }
            .labelsHidden().frame(width: 150)
            Picker("", selection: $model.filter.age) {
                ForEach(LargeOldFilter.AgeRange.allCases) { Text($0.title).tag($0) }
            }
            .labelsHidden().frame(width: 180)
            Picker("", selection: $model.filter.folder) {
                Text(String(localized: "Mọi thư mục")).tag(String?.none)
                ForEach(LargeOldFilter.folderOptions(model.items, home: model.services.fileSystem.home.path), id: \.self) {
                    Text($0.abbreviatingHome).tag(String?.some($0))
                }
            }
            .labelsHidden().frame(width: 170)
            TextField(String(localized: "Tìm theo tên"), text: $model.filter.search)
                .textFieldStyle(.roundedBorder)
                .frame(maxWidth: 200)
            Spacer()
        }
    }

    private func table(_ rows: [LargeOldItem]) -> some View {
        Table(rows, sortOrder: $sortOrder) {
            TableColumn("") { (item: LargeOldItem) in
                Toggle("", isOn: Binding(get: { model.isSelected(item) }, set: { model.setSelected(item, $0) }))
                    .labelsHidden().toggleStyle(.checkbox)
            }
            .width(24)
            TableColumn(String(localized: "Tên"), value: \LargeOldItem.name) { (item: LargeOldItem) in
                HStack(spacing: 6) {
                    FileIcon(url: item.url, fallback: item.kind.symbol, size: 16)
                    Text(item.name).lineLimit(1).truncationMode(.middle)
                    if item.isHidden { Pill(LargeOldBadges.hidden) }
                }
                .contextMenu {
                    Button(String(localized: "Hiện trong Finder")) { NSWorkspace.shared.activateFileViewerSelecting([item.url]) }
                    Button(String(localized: "Mở")) { NSWorkspace.shared.open(item.url) }
                }
            }
            .width(min: 180, ideal: 260)
            TableColumn(String(localized: "Thư mục"), value: \LargeOldItem.folder) { (item: LargeOldItem) in
                Text(item.folder.abbreviatingHome).lineLimit(1).truncationMode(.middle).foregroundStyle(.secondary)
            }
            .width(min: 120, ideal: 200)
            TableColumn(String(localized: "Dung lượng"), value: \LargeOldItem.size) { (item: LargeOldItem) in
                Text(item.size.formatted).monospacedDigit()
            }
            .width(90)
            TableColumn(String(localized: "Lần dùng cuối"), value: \LargeOldItem.lastUsedSortKey) { (item: LargeOldItem) in
                Text(item.lastUsed.map { Self.dateFormatter.string(from: $0) } ?? "—").foregroundStyle(item.isOld ? Color.orange : .primary)
            }
            .width(110)
            TableColumn(String(localized: "Loại"), value: \LargeOldItem.kindTitle) { (item: LargeOldItem) in
                Text(item.kind.title)
            }
            .width(110)
        }
        .scrollContentBackground(.hidden)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.black.opacity(0.25)))
        .environment(\.colorScheme, .dark)
    }

    private var footer: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(String(localized: "Đã chọn \(model.selection.selectedBytes.formatted)")).font(Theme.Font.headline).foregroundStyle(.white)
                Text(String(localized: "\(model.selection.selectedCount.formatted()) file · có thể khôi phục từ Thùng rác"))
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            Button(String(localized: "Chọn tất cả đang hiện")) { model.selectAllVisible(true) }.buttonStyle(GlassButtonStyle())
            Button(String(localized: "Bỏ chọn")) { model.selection = SelectionState() }.buttonStyle(GlassButtonStyle())
                .disabled(model.selection.selectedCount == 0)
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            Button(LargeOldFilesView.appearance.cleanTitle) { model.prepareClean() }
                .buttonStyle(PrimaryButtonStyle(accent: LargeOldFilesView.appearance.accent))
                .disabled(model.selection.selectedCount == 0)
        }
    }
}
