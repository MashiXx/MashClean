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
        var title: String { self == .apps ? String(localized: "Ứng dụng") : String(localized: "File sót của app đã gỡ") }
    }

    @StateObject private var model: UninstallerViewModel
    @StateObject private var orphans: ScanCleanViewModel
    @State private var tab: Tab = .apps

    public static let appearance = FeatureAppearance(
        accent: .applications, symbol: "xmark.app",
        title: String(localized: "Gỡ cài đặt"),
        subtitle: String(localized: "Gỡ app cùng toàn bộ file sót: dữ liệu, cache, LaunchAgent, gói cài đặt. Mỗi mục kèm mức tin cậy."),
        scanTitle: String(localized: "Quét"), cleanTitle: String(localized: "action.uninstall", defaultValue: "Gỡ cài đặt")
    )

    static let resetAppearance = FeatureAppearance(accent: .applications, symbol: "arrow.counterclockwise", title: String(localized: "Đặt lại app"),
                                                   subtitle: "", cleanTitle: String(localized: "Đặt lại"))

    public static let orphanAppearance = FeatureAppearance(
        accent: .applications, symbol: "puzzlepiece.extension",
        title: String(localized: "File sót của app đã gỡ"),
        subtitle: String(localized: "Tìm dữ liệu còn lại của app đã bị kéo thẳng vào Thùng rác. Mọi mục cần bạn xem lại trước khi xoá."),
        scanTitle: String(localized: "Quét"), cleanTitle: String(localized: "Chuyển vào Thùng rác")
    )

    /// - Parameter initialAppPath: mở thẳng một app (FinderSync gửi `cleanboost://uninstall?path=/Applications/Foo.app`).
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
                Alert(title: Text(String(localized: "\(prompt.app.name) chưa thoát")),
                      message: Text(String(localized: "App không thoát sau 10 giây. Buộc thoát có thể làm mất dữ liệu chưa lưu.")),
                      primaryButton: .destructive(Text(String(localized: "Buộc thoát"))) { prompt.resolve(true) },
                      secondaryButton: .cancel(Text(String(localized: "Bỏ qua app này"))) { prompt.resolve(false) })
            }
            .sheet(item: $model.permanentDeletePrompt) { prompt in
                VStack(alignment: .leading, spacing: 12) {
                    Text(String(localized: "Không thể chuyển vào Thùng rác")).font(.headline)
                    Text(String(localized: "\"\(prompt.url.lastPathComponent)\" không chuyển vào Thùng rác được (ổ ngoài hoặc file của hệ thống). Xoá vĩnh viễn? Thao tác này không khôi phục được."))
                    HStack {
                        Spacer()
                        Button(String(localized: "Bỏ qua")) { prompt.resolve(false) }.keyboardShortcut(.cancelAction)
                        Button(String(localized: "Xoá vĩnh viễn")) { prompt.resolve(true) }.keyboardShortcut(.defaultAction)
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
                Text(c.resetOnly ? String(localized: "Đặt lại \(c.apps.first?.name ?? "")") : String(localized: "Gỡ \(c.apps.map(\.name).joined(separator: ", "))"))
                    .font(Theme.Font.title).foregroundStyle(.white).lineLimit(2).multilineTextAlignment(.center)
                if c.resetOnly {
                    Text(String(localized: "Chỉ xoá dữ liệu đã chọn, giữ lại ứng dụng. App sẽ như mới cài.")).font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
                } else if c.apps.contains(where: { AppTerminator.isRunning($0) }) {
                    Text(String(localized: "App đang chạy sẽ được yêu cầu thoát trước khi gỡ.")).font(Theme.Font.body).foregroundStyle(Theme.review)
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
                    TextField(String(localized: "Tìm ứng dụng"), text: $model.search).textFieldStyle(.plain).foregroundStyle(.white)
                }
                .padding(.horizontal, 10).padding(.vertical, 6)
                .background(RoundedRectangle(cornerRadius: 8).fill(Color.black.opacity(0.25)))
                Menu {
                    Picker(String(localized: "Sắp xếp"), selection: $model.sort) {
                        ForEach(UninstallerViewModel.SortKey.allCases) { Text($0.title).tag($0) }
                    }
                    Picker(String(localized: "Lọc"), selection: $model.filter) {
                        ForEach([UninstallerViewModel.Filter.all, .unused, .appStore, .otherSources, .checkedOnly], id: \.self) { Text($0.title).tag($0) }
                    }
                    if !model.vendors.isEmpty {
                        Picker(String(localized: "Nhà phát triển"), selection: $model.filter) {
                            ForEach(model.vendors, id: \.self) { Text(UninstallerViewModel.vendorTitle($0)).tag(UninstallerViewModel.Filter.vendor($0)) }
                        }
                    }
                } label: {
                    Image(systemName: "line.3.horizontal.decrease.circle").foregroundStyle(.white)
                }
                .menuStyle(.borderlessButton)
                .frame(width: 36)
                .help(String(localized: "Sắp xếp và lọc"))
            }
            HStack {
                let checkable = model.checkableVisibleApps
                SelectAllToggle(selected: checkable.filter { model.checked.contains($0.id) }.count, total: checkable.count,
                                action: model.setCheckedVisible)
                    .disabled(checkable.isEmpty)
                Text(String(localized: "\(model.filter.title) · sắp theo \(model.sort.title.lowercased())")).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                    .lineLimit(1)
                Spacer()
                if !model.checked.isEmpty {
                    Button(String(localized: "Bỏ chọn (\(model.checked.count))"), action: model.clearChecked).buttonStyle(.plain).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                }
                Button { model.reload() } label: { Image(systemName: "arrow.clockwise") }
                    .buttonStyle(.plain).foregroundStyle(Theme.secondaryText).help(String(localized: "Quét lại danh sách app"))
            }
            if model.isLoadingApps && model.apps.isEmpty {
                Spacer()
                ProgressView().controlSize(.large)
                Text(String(localized: "Đang tìm ứng dụng đã cài…")).font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
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
                Image(systemName: "lock.fill").frame(width: 16).foregroundStyle(Theme.tertiaryText).help(String(localized: "Không gỡ được: \(blocked)"))
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
            Button(String(localized: "Hiện trong Finder")) { NSWorkspace.shared.activateFileViewerSelecting([app.url]) }
        }
    }

    private var lastUsedText: String {
        guard let last = app.lastUsed else { return String(localized: "Chưa rõ lần dùng cuối") }
        return String(localized: "Dùng ") + Self.relative.localizedString(for: last, relativeTo: Date())
    }
}

struct AppDetailColumn: View {
    @ObservedObject var model: UninstallerViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let app = model.focusedApp {
                header(app)
                if let reason = UninstallPolicy.reasonCannotUninstall(app) {
                    NoticeBanner(symbol: "lock.fill", text: String(localized: "Không gỡ được: \(reason)."))
                    Spacer()
                } else {
                    if app.bundledUninstaller != nil {
                        NoticeBanner(symbol: "lightbulb", text: String(localized: "App có trình gỡ riêng. Nên chạy trình đó trước để gỡ sạch driver, dịch vụ nền."),
                                     actionTitle: String(localized: "Mở trình gỡ")) { model.openBundledUninstaller(app) }
                    }
                    leftovers(app)
                }
            } else {
                Spacer()
                Text(String(localized: "Chọn một ứng dụng để xem file sót")).font(Theme.Font.body).foregroundStyle(Theme.secondaryText).frame(maxWidth: .infinity)
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
                Text([app.version.map { String(localized: "Phiên bản \($0)") }, app.bundleID, app.teamID.map { "Team \($0)" }].compactMap { $0 }.joined(separator: " · "))
                    .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).lineLimit(1).textSelection(.enabled)
            }
            Spacer()
            Button { NSWorkspace.shared.activateFileViewerSelecting([app.url]) } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(GlassButtonStyle()).help(String(localized: "Hiện trong Finder"))
        }
    }

    @ViewBuilder private func leftovers(_ app: InstalledApp) -> some View {
        if model.loading.contains(app.id) && model.appRoot(for: app) == nil {
            Spacer()
            HStack { Spacer(); ProgressView(); Text(String(localized: "Đang tìm file sót…")).foregroundStyle(Theme.secondaryText); Spacer() }
            Spacer()
        } else if let root = model.appRoot(for: app) {
            HStack(spacing: 14) {
                StatTile(value: app.size.formatted, label: String(localized: "Ứng dụng"), symbol: "app")
                StatTile(value: (root.size - app.size).formatted, label: String(localized: "File sót"), symbol: "doc.on.doc")
                StatTile(value: model.selectedBytes(of: app).formatted, label: String(localized: "Đã chọn"), symbol: "checkmark.circle")
            }
            Text(String(localized: "Mục Chắc chắn và Cao được chọn sẵn. Mục Trung bình và Thấp cần bạn xem lại."))
                .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            SelectAllToggle(state: model.selection.state(of: root.id, in: model.tree)) { on in
                model.selection.set(root.id, on, in: model.tree)
            }
            NodeOutlineView(roots: root.children, tree: model.tree, selection: $model.selection)
        } else {
            Spacer()
        }
    }

    private var footer: some View {
        HStack {
            let pending = model.pendingApps
            VStack(alignment: .leading, spacing: 2) {
                Text(pending.isEmpty ? String(localized: "Chưa chọn app nào") : String(localized: "Gỡ \(pending.count) ứng dụng · \(model.totalSelectedBytes.formatted)"))
                    .font(Theme.Font.headline).foregroundStyle(.white)
                Text(String(localized: "App chuyển vào Thùng rác, khôi phục được từ Lịch sử")).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            if let app = model.focusedApp, UninstallPolicy.canUninstall(app), !model.selectedLeaves(of: app, includeApp: false).isEmpty {
                Button(String(localized: "Đặt lại app")) { model.requestReset(app) }
                    .buttonStyle(GlassButtonStyle())
                    .help(String(localized: "Chỉ xoá dữ liệu đã chọn của app, giữ lại ứng dụng"))
            }
            Button(String(localized: "action.uninstall", defaultValue: "Gỡ cài đặt")) { model.requestUninstall() }
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
                NoticeBanner(text: String(localized: "\(report.failed.count) mục không xoá được: ") + report.failed.prefix(4).map(\.item.title).joined(separator: ", "))
                    .frame(maxWidth: 560)
            }
            if !report.inUse.isEmpty {
                NoticeBanner(symbol: "lock.open", text: String(localized: "\(report.inUse.count) mục đang được sử dụng nên đã bỏ qua. Hãy tắt app rồi thử lại.")).frame(maxWidth: 560)
            }
            Spacer()
            Button("Xong", action: onClose).buttonStyle(PrimaryButtonStyle(accent: .applications))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
