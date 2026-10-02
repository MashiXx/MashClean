import AppCatalog
import CleanEngine
import DesignSystem
import NodeTree
import SharedUI
import SweepCore
import SwiftUI
import UninstallerDomain
import UninstallerScanning

/// Màn Uninstaller (mục 11.3): tab "Ứng dụng" (gỡ app + file sót) và tab "File sót của app đã gỡ".
public struct UninstallerView: View {
    enum Tab: String, CaseIterable, Identifiable {
        case apps, orphans
        var id: String { rawValue }
        var title: String { self == .apps ? "Ứng dụng" : "File sót của app đã gỡ" }
    }

    @StateObject private var model: UninstallerViewModel
    @StateObject private var orphans: ScanCleanViewModel
    @State private var tab: Tab = .apps

    public static let appearance = FeatureAppearance(
        accent: .applications, symbol: "xmark.app",
        title: "Gỡ cài đặt",
        subtitle: "Gỡ app cùng toàn bộ file sót: dữ liệu, cache, LaunchAgent, gói cài đặt. Mỗi mục kèm mức tin cậy.",
        scanTitle: "Quét", cleanTitle: "Gỡ cài đặt"
    )

    static let resetAppearance = FeatureAppearance(accent: .applications, symbol: "arrow.counterclockwise", title: "Đặt lại app",
                                                   subtitle: "", cleanTitle: "Đặt lại")

    public static let orphanAppearance = FeatureAppearance(
        accent: .applications, symbol: "puzzlepiece.extension",
        title: "File sót của app đã gỡ",
        subtitle: "Tìm dữ liệu còn lại của app đã bị kéo thẳng vào Thùng rác. Mọi mục cần bạn xem lại trước khi xoá.",
        scanTitle: "Quét", cleanTitle: "Chuyển vào Thùng rác"
    )

    /// - Parameter initialAppPath: mở thẳng một app (FinderSync gửi `mashclean://uninstall?path=/Applications/Foo.app`).
    public init(services: ScanServices, feature: UninstallerFeature, initialAppPath: String? = nil) {
        _model = StateObject(wrappedValue: UninstallerViewModel(services: services, feature: feature, initialAppPath: initialAppPath))
        _orphans = StateObject(wrappedValue: ScanCleanViewModel(services: services, kind: "orphanedLeftovers", tasks: { feature.scanTasks() }))
    }

    public var body: some View {
        ZStack(alignment: .top) {
            Theme.background(for: .applications).ignoresSafeArea()
            switch tab {
            case .apps:
                AppsPane(model: model)
                    .padding(.horizontal, 24).padding(.bottom, 20).padding(.top, 58)
            case .orphans:
                FeatureFlowView(model: orphans, appearance: Self.orphanAppearance)
                    .padding(.top, 34)
            }
            Picker("", selection: $tab) {
                ForEach(Tab.allCases) { Text($0.title).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .frame(width: 380)
            .padding(.top, 16)
        }
        .onAppear { model.loadIfNeeded() }
    }
}

// MARK: - Tab Ứng dụng

struct AppsPane: View {
    @ObservedObject var model: UninstallerViewModel

    var body: some View {
        content
            .alert(item: $model.forceQuitPrompt) { prompt in
                Alert(title: Text("\(prompt.app.name) chưa thoát"),
                      message: Text("App không thoát sau 10 giây. Buộc thoát có thể làm mất dữ liệu chưa lưu."),
                      primaryButton: .destructive(Text("Buộc thoát")) { prompt.resolve(true) },
                      secondaryButton: .cancel(Text("Bỏ qua app này")) { prompt.resolve(false) })
            }
            .sheet(item: $model.permanentDeletePrompt) { prompt in
                VStack(alignment: .leading, spacing: 12) {
                    Text("Không thể chuyển vào Thùng rác").font(.headline)
                    Text("\"\(prompt.url.lastPathComponent)\" không chuyển vào Thùng rác được (ổ ngoài hoặc file của hệ thống). Xoá vĩnh viễn? Thao tác này không khôi phục được.")
                    HStack {
                        Spacer()
                        Button("Bỏ qua") { prompt.resolve(false) }.keyboardShortcut(.cancelAction)
                        Button("Xoá vĩnh viễn") { prompt.resolve(true) }.keyboardShortcut(.defaultAction)
                    }
                }
                .padding(20).frame(width: 420)
            }
    }

    @ViewBuilder private var content: some View {
        switch model.phase {
        case .browsing:
            browser
        case let .confirming(c):
            VStack(spacing: 12) {
                Text(c.resetOnly ? "Đặt lại \(c.apps.first?.name ?? "")" : "Gỡ \(c.apps.map(\.name).joined(separator: ", "))")
                    .font(Theme.Font.title).foregroundStyle(.white).lineLimit(2).multilineTextAlignment(.center)
                if c.resetOnly {
                    Text("Chỉ xoá dữ liệu đã chọn, giữ lại ứng dụng. App sẽ như mới cài.").font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
                } else if c.apps.contains(where: { AppTerminator.isRunning($0) }) {
                    Text("App đang chạy sẽ được yêu cầu thoát trước khi gỡ.").font(Theme.Font.body).foregroundStyle(Theme.review)
                }
                ConfirmView(plan: c.plan, appearance: c.resetOnly ? UninstallerView.resetAppearance : UninstallerView.appearance,
                            onCancel: model.cancelConfirmation, onConfirm: { model.confirm(c) })
            }
        case let .working(title, progress, item, freed):
            VStack(spacing: 8) {
                Text(title).font(Theme.Font.headline).foregroundStyle(.white)
                CleaningView(appearance: UninstallerView.appearance, progress: progress, currentItem: item, freed: freed)
            }
        case let .done(message, report):
            DoneCard(message: message, report: report, onClose: model.dismissDone)
        }
    }

    private var browser: some View {
        HStack(spacing: 12) {
            AppListColumn(model: model).frame(width: 380)
            AppDetailColumn(model: model)
        }
    }
}

struct AppListColumn: View {
    @ObservedObject var model: UninstallerViewModel

    var body: some View {
        VStack(spacing: 8) {
            HStack(spacing: 8) {
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(Theme.secondaryText)
                    TextField("Tìm ứng dụng", text: $model.search).textFieldStyle(.plain).foregroundStyle(.white)
                }
                .padding(.horizontal, 10).padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.black.opacity(0.25)))
                Menu {
                    Picker("Sắp xếp", selection: $model.sort) {
                        ForEach(UninstallerViewModel.SortKey.allCases) { Text($0.title).tag($0) }
                    }
                    Picker("Lọc", selection: $model.filter) {
                        ForEach([UninstallerViewModel.Filter.all, .unused, .appStore, .otherSources, .checkedOnly], id: \.self) { Text($0.title).tag($0) }
                    }
                    if !model.vendors.isEmpty {
                        Picker("Nhà phát triển", selection: $model.filter) {
                            ForEach(model.vendors, id: \.self) { Text(UninstallerViewModel.vendorTitle($0)).tag(UninstallerViewModel.Filter.vendor($0)) }
                        }
                    }
                } label: {
                    Image(systemName: "line.3.horizontal.decrease.circle").foregroundStyle(.white)
                }
                .menuStyle(.borderlessButton)
                .frame(width: 36)
                .help("Sắp xếp và lọc")
            }
            HStack {
                Text("\(model.filter.title) · sắp theo \(model.sort.title.lowercased())").font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                Spacer()
                if !model.checked.isEmpty {
                    Button("Bỏ chọn (\(model.checked.count))", action: model.clearChecked).buttonStyle(.plain).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                }
                Button { model.reload() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.plain).foregroundStyle(Theme.secondaryText).help("Quét lại danh sách app")
            }
            if model.isLoadingApps && model.apps.isEmpty {
                Spacer()
                ProgressView().controlSize(.large)
                Text("Đang tìm ứng dụng đã cài…").font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
                Spacer()
            } else {
                ScrollView {
                    LazyVStack(spacing: 2) {
                        ForEach(model.visibleApps) { app in
                            AppRow(app: app, icon: model.icon(for: app), isChecked: model.checked.contains(app.id),
                                   isFocused: model.focusedID == app.id,
                                   onToggle: { model.toggleChecked(app) }, onFocus: { model.focus(app) })
                        }
                    }
                    .padding(6)
                }
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }
}

struct AppRow: View {
    let app: InstalledApp
    let icon: NSImage
    let isChecked: Bool
    let isFocused: Bool
    let onToggle: () -> Void
    let onFocus: () -> Void

    private static let relative: RelativeDateTimeFormatter = {
        let f = RelativeDateTimeFormatter()
        f.locale = Locale(identifier: "vi")
        f.unitsStyle = .short
        return f
    }()

    var body: some View {
        let blocked = UninstallPolicy.reasonCannotUninstall(app)
        HStack(spacing: 8) {
            if let blocked {
                Image(systemName: "lock.fill").frame(width: 16).foregroundStyle(Theme.tertiaryText).help("Không gỡ được: \(blocked)")
            } else {
                TriStateCheckbox(state: isChecked ? .on : .off, action: onToggle)
            }
            Image(nsImage: icon).resizable().frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Text(app.name).font(.system(size: 13, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                    if app.isAppStore { Pill("App Store", color: Theme.safe) }
                    if app.isApple { Pill("Apple") }
                }
                Text([app.version.map { "v\($0)" }, lastUsedText].compactMap { $0 }.joined(separator: " · "))
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).lineLimit(1)
            }
            Spacer()
            Text(app.size.formatted).font(.system(size: 12, weight: .semibold)).monospacedDigit().foregroundStyle(Theme.secondaryText)
        }
        .padding(.horizontal, 8).padding(.vertical, 6)
        .background(RoundedRectangle(cornerRadius: 8).fill(isFocused ? Color.white.opacity(0.16) : .clear))
        .contentShape(Rectangle())
        .onTapGesture(perform: onFocus)
        .contextMenu {
            Button("Hiện trong Finder") { NSWorkspace.shared.activateFileViewerSelecting([app.url]) }
        }
    }

    private var lastUsedText: String {
        guard let last = app.lastUsed else { return "Chưa rõ lần dùng cuối" }
        return "Dùng " + Self.relative.localizedString(for: last, relativeTo: Date())
    }
}

struct AppDetailColumn: View {
    @ObservedObject var model: UninstallerViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let app = model.focusedApp {
                header(app)
                if let reason = UninstallPolicy.reasonCannotUninstall(app) {
                    NoticeBanner(symbol: "lock.fill", text: "Không gỡ được: \(reason).")
                    Spacer()
                } else {
                    if app.bundledUninstaller != nil {
                        NoticeBanner(symbol: "lightbulb", text: "App có trình gỡ riêng. Nên chạy trình đó trước để gỡ sạch driver, dịch vụ nền.",
                                     actionTitle: "Mở trình gỡ") { model.openBundledUninstaller(app) }
                    }
                    leftovers(app)
                }
            } else {
                Spacer()
                Text("Chọn một ứng dụng để xem file sót").font(Theme.Font.body).foregroundStyle(Theme.secondaryText).frame(maxWidth: .infinity)
                Spacer()
            }
            footer
        }
        .padding(12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private func header(_ app: InstalledApp) -> some View {
        HStack(spacing: 12) {
            Image(nsImage: model.icon(for: app)).resizable().frame(width: 48, height: 48)
            VStack(alignment: .leading, spacing: 2) {
                Text(app.name).font(Theme.Font.title).foregroundStyle(.white).lineLimit(1)
                Text([app.version.map { "Phiên bản \($0)" }, app.bundleID, app.teamID.map { "Team \($0)" }].compactMap { $0 }.joined(separator: " · "))
                    .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).lineLimit(1).textSelection(.enabled)
            }
            Spacer()
            Button { NSWorkspace.shared.activateFileViewerSelecting([app.url]) } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(GlassButtonStyle()).help("Hiện trong Finder")
        }
    }

    @ViewBuilder private func leftovers(_ app: InstalledApp) -> some View {
        if model.loading.contains(app.id) && model.appRoot(for: app) == nil {
            Spacer()
            HStack { Spacer(); ProgressView(); Text("Đang tìm file sót…").foregroundStyle(Theme.secondaryText); Spacer() }
            Spacer()
        } else if let root = model.appRoot(for: app) {
            HStack(spacing: 14) {
                StatTile(value: app.size.formatted, label: "Ứng dụng", symbol: "app")
                StatTile(value: (root.size - app.size).formatted, label: "File sót", symbol: "doc.on.doc")
                StatTile(value: model.selectedBytes(of: app).formatted, label: "Đã chọn", symbol: "checkmark.circle")
            }
            Text("Mục Chắc chắn và Cao được chọn sẵn. Mục Trung bình và Thấp cần bạn xem lại.")
                .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            NodeOutlineView(roots: root.children, tree: model.tree, selection: $model.selection)
        } else {
            Spacer()
        }
    }

    private var footer: some View {
        HStack {
            let pending = model.pendingApps
            VStack(alignment: .leading, spacing: 2) {
                Text(pending.isEmpty ? "Chưa chọn app nào" : "Gỡ \(pending.count) ứng dụng · \(model.totalSelectedBytes.formatted)")
                    .font(Theme.Font.headline).foregroundStyle(.white)
                Text("App chuyển vào Thùng rác, khôi phục được từ Lịch sử").font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            if let app = model.focusedApp, UninstallPolicy.canUninstall(app), !model.selectedLeaves(of: app, includeApp: false).isEmpty {
                Button("Đặt lại app") { model.requestReset(app) }
                    .buttonStyle(GlassButtonStyle())
                    .help("Chỉ xoá dữ liệu đã chọn của app, giữ lại ứng dụng")
            }
            Button("Gỡ cài đặt") { model.requestUninstall() }
                .buttonStyle(PrimaryButtonStyle(accent: .applications))
                .disabled(pending.isEmpty || model.checkedApps.contains { model.loading.contains($0.id) })
                .keyboardShortcut(.defaultAction)
        }
    }
}

struct DoneCard: View {
    let message: String
    let report: CleanReport
    let onClose: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Spacer()
            Image(systemName: report.failed.isEmpty ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .font(.system(size: 64)).foregroundStyle(.white)
            Text(message).font(Theme.Font.title).foregroundStyle(.white).multilineTextAlignment(.center)
            if !report.failed.isEmpty {
                NoticeBanner(text: "\(report.failed.count) mục không xoá được: " + report.failed.prefix(4).map(\.item.title).joined(separator: ", "))
                    .frame(maxWidth: 560)
            }
            if !report.inUse.isEmpty {
                NoticeBanner(symbol: "lock.open", text: "\(report.inUse.count) mục đang được sử dụng nên đã bỏ qua. Hãy tắt app rồi thử lại.").frame(maxWidth: 560)
            }
            Spacer()
            Button("Xong", action: onClose).buttonStyle(PrimaryButtonStyle(accent: .applications))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
