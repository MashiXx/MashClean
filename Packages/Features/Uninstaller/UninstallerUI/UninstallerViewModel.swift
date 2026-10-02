import AppCatalog
import AppKit
import CleanEngine
import Foundation
import NodeTree
import ScanEngine
import SharedUI
import SweepCore
import SweepLogging
import SweepPermissions
import UninstallerDomain
import UninstallerScanning

/// View model màn Uninstaller (mục 11.3, 23.5): danh sách app, tìm file sót, gỡ qua CleanEngine.
@MainActor
final class UninstallerViewModel: ObservableObject {
    enum SortKey: String, CaseIterable, Identifiable {
        case name, size, lastUsed
        var id: String { rawValue }
        var title: String {
            switch self {
            case .name: String(localized: "Tên")
            case .size: String(localized: "Dung lượng")
            case .lastUsed: String(localized: "Lần dùng cuối")
            }
        }
    }

    enum Filter: Hashable {
        case all, unused, appStore, otherSources, checkedOnly
        case vendor(String)

        var title: String {
            switch self {
            case .all: String(localized: "Tất cả")
            case .unused: String(localized: "Không dùng > 6 tháng")
            case .appStore: "App Store"
            case .otherSources: String(localized: "Nguồn khác")
            case .checkedOnly: String(localized: "Đã chọn")
            case let .vendor(v): UninstallerViewModel.vendorTitle(v)
            }
        }
    }

    enum Phase {
        case browsing
        case confirming(Confirmation)
        case working(title: String, progress: Double, item: String, freed: ByteCount)
        case done(message: String, report: CleanReport)
    }

    struct Confirmation {
        let plan: CleanPlan
        let apps: [InstalledApp]
        let resetOnly: Bool
    }

    struct ForceQuitPrompt: Identifiable {
        let id = UUID()
        let app: InstalledApp
        let resolve: (Bool) -> Void
    }

    struct PermanentDeletePrompt: Identifiable {
        let id = UUID()
        let url: URL
        let resolve: (Bool) -> Void
    }

    @Published private(set) var apps: [InstalledApp] = []
    @Published private(set) var isLoadingApps = false
    @Published var search = ""
    @Published var sort: SortKey = .size
    @Published var filter: Filter = .all
    @Published private(set) var checked: Set<String> = []
    @Published var focusedID: String?
    @Published private(set) var reports: [String: LeftoverReport] = [:]
    @Published private(set) var loading: Set<String> = []
    @Published private(set) var tree: NodeTree = .empty
    @Published var selection = SelectionState()
    @Published private(set) var phase: Phase = .browsing
    @Published var forceQuitPrompt: ForceQuitPrompt?
    @Published var permanentDeletePrompt: PermanentDeletePrompt?

    let services: ScanServices
    let feature: UninstallerFeature
    private let planner: UninstallPlanner
    private var initialAppPath: String?
    private var icons: [String: NSImage] = [:]
    private var workTask: Task<Void, Never>?

    init(services: ScanServices, feature: UninstallerFeature, initialAppPath: String?) {
        self.services = services
        self.feature = feature
        self.initialAppPath = initialAppPath
        planner = UninstallPlanner(fileSystem: services.fileSystem, storage: services.storage)
    }

    // MARK: Danh sách app

    func loadIfNeeded() {
        guard apps.isEmpty, !isLoadingApps else { return }
        reload()
    }

    func reload() {
        isLoadingApps = true
        let scanner = services.appScanner
        Task {
            let found = await scanner.scan()
            apps = found.filter { !$0.isSystemApp }
            isLoadingApps = false
            if let path = initialAppPath {
                initialAppPath = nil
                open(appPath: path)
            } else if focusedID == nil {
                focusedID = visibleApps.first?.id
                if let id = focusedID { loadLeftovers(for: id) }
            }
        }
    }

    /// Mở thẳng một app (FinderSync "Gỡ bằng Clean Boost": `cleanboost://uninstall?path=...`).
    func open(appPath: String) {
        let target = URL(fileURLWithPath: appPath).standardizedFileURL.path
        var app = apps.first { $0.url.standardizedFileURL.path == target }
        if app == nil, let read = services.appScanner.readApp(at: URL(fileURLWithPath: target)) {
            apps.append(read)
            app = read
        }
        guard let app else {
            Log.warning(.ui, "uninstaller", "Không đọc được app tại \(appPath)")
            return
        }
        search = ""
        filter = .all
        focusedID = app.id
        if UninstallPolicy.canUninstall(app) { checked.insert(app.id) }
        loadLeftovers(for: app.id)
    }

    var visibleApps: [InstalledApp] {
        let q = search.trimmingCharacters(in: .whitespaces).lowercased()
        let sixMonths = Date().addingTimeInterval(-180 * 86_400)
        let filtered = apps.filter { app in
            if !q.isEmpty, !app.name.lowercased().contains(q), !app.bundleID.lowercased().contains(q) { return false }
            switch filter {
            case .all: return true
            case .unused:
                if let last = app.lastUsed { return last < sixMonths }
                return (app.bundleModified ?? .distantFuture) < sixMonths
            case .appStore: return app.isAppStore
            case .otherSources: return !app.isAppStore && !app.isApple
            case .checkedOnly: return checked.contains(app.id)
            case let .vendor(v): return Self.vendor(of: app) == v
            }
        }
        switch sort {
        case .name: return filtered.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        case .size: return filtered.sorted { $0.size > $1.size }
        case .lastUsed: return filtered.sorted { ($0.lastUsed ?? .distantPast) < ($1.lastUsed ?? .distantPast) }
        }
    }

    /// Nhà phát triển: tiền tố bundle ID (`com.google`), nhóm có từ 2 app trở lên.
    var vendors: [String] {
        let counts = Dictionary(grouping: apps.compactMap(Self.vendor(of:)), by: { $0 }).mapValues(\.count)
        return counts.filter { $0.value >= 2 }.keys.sorted { Self.vendorTitle($0) < Self.vendorTitle($1) }
    }

    nonisolated static func vendor(of app: InstalledApp) -> String? { ReverseDNS.vendorPrefix(app.bundleID) }

    nonisolated static func vendorTitle(_ vendor: String) -> String {
        let name = vendor.split(separator: ".").last.map(String.init) ?? vendor
        return name.prefix(1).uppercased() + name.dropFirst()
    }

    func icon(for app: InstalledApp) -> NSImage {
        if let cached = icons[app.id] { return cached }
        let image = NSWorkspace.shared.icon(forFile: app.url.path)
        icons[app.id] = image
        return image
    }

    var focusedApp: InstalledApp? { focusedID.flatMap { id in apps.first { $0.id == id } } }
    var checkedApps: [InstalledApp] { apps.filter { checked.contains($0.id) } }

    // MARK: Chọn app

    func focus(_ app: InstalledApp) {
        focusedID = app.id
        loadLeftovers(for: app.id)
    }

    func toggleChecked(_ app: InstalledApp) {
        guard UninstallPolicy.canUninstall(app) else { return }
        if checked.contains(app.id) {
            checked.remove(app.id)
        } else {
            checked.insert(app.id)
            loadLeftovers(for: app.id)
        }
    }

    func clearChecked() { checked.removeAll() }

    /// App đang hiện mà gỡ được (đích của "Chọn tất cả").
    var checkableVisibleApps: [InstalledApp] { visibleApps.filter(UninstallPolicy.canUninstall) }

    func setCheckedVisible(_ on: Bool) {
        for app in checkableVisibleApps {
            if on {
                guard checked.insert(app.id).inserted else { continue }
                loadLeftovers(for: app.id)
            } else {
                checked.remove(app.id)
            }
        }
    }

    // MARK: File sót

    func loadLeftovers(for appID: String, force: Bool = false) {
        guard let app = apps.first(where: { $0.id == appID }), UninstallPolicy.canUninstall(app) else { return }
        guard force || (reports[appID] == nil && !loading.contains(appID)) else { return }
        loading.insert(appID)
        let installed = apps
        let rules = services.rules
        let environment = services.makeEnvironment()
        let planner = planner
        Task {
            let report = await planner.findLeftovers(for: app, installedApps: installed, rules: rules, environment: environment)
            loading.remove(appID)
            reports[appID] = report
            rebuildTree()
        }
    }

    /// Dựng lại cây gộp mọi app đã tìm file sót; giữ lựa chọn cũ, app mới thì chọn sẵn Chắc chắn/Cao.
    private func rebuildTree() {
        let ordered = apps.compactMap { reports[$0.id] }
        let previous = Set(tree.allLeaves.map(\.id))
        let previouslySelected = Set(selection.selectedIDs)
        let newTree = UninstallPlanner.tree(for: ordered)
        var newSelection = SelectionState()
        for leaf in newTree.allLeaves {
            let on = previous.contains(leaf.id) ? previouslySelected.contains(leaf.id) : leaf.safety == .safe
            if on { newSelection.set(leaf.id, true, in: newTree) }
        }
        tree = newTree
        selection = newSelection
    }

    func appRoot(for app: InstalledApp) -> Node? {
        tree.roots.first { if case let .application(_, url) = $0.kind { url.standardizedFileURL == app.url.standardizedFileURL } else { false } }
    }

    func selectedLeaves(of app: InstalledApp, includeApp: Bool) -> [Node] {
        guard let root = appRoot(for: app) else { return [] }
        return tree.leaves(under: root.id).filter { selection.isSelected($0.id) && (includeApp || $0.removal != .custom(.app)) }
    }

    func selectedBytes(of app: InstalledApp) -> ByteCount { selectedLeaves(of: app, includeApp: true).sum(\.size) }

    var pendingApps: [InstalledApp] { checkedApps.filter { reports[$0.id] != nil } }
    var totalSelectedBytes: ByteCount { pendingApps.reduce(ByteCount.zero) { $0 + selectedBytes(of: $1) } }

    // MARK: Gỡ

    func requestUninstall() {
        let targets = pendingApps
        guard !targets.isEmpty else { return }
        let nodes = targets.flatMap { selectedLeaves(of: $0, includeApp: true) }
        let plan = services.cleanEngine.makePlan(nodes: nodes)
        phase = .confirming(Confirmation(plan: plan, apps: targets, resetOnly: false))
    }

    /// "Đặt lại app": xoá dữ liệu đã chọn (file sót), giữ lại app.
    func requestReset(_ app: InstalledApp) {
        let nodes = selectedLeaves(of: app, includeApp: false)
        guard !nodes.isEmpty else { return }
        phase = .confirming(Confirmation(plan: services.cleanEngine.makePlan(nodes: nodes), apps: [app], resetOnly: true))
    }

    func cancelConfirmation() { phase = .browsing }

    func confirm(_ confirmation: Confirmation) {
        workTask = Task { await run(confirmation) }
    }

    private func run(_ confirmation: Confirmation) async {
        var plan = confirmation.plan
        var apps = confirmation.apps
        // Bước 1 (mục 11.3): tắt app đang chạy, chờ 10 giây, chưa tắt thì hỏi buộc tắt.
        // Bản Mac App Store: cần quyền ghi /Applications trước khi chuyển app vào Thùng rác.
        if !confirmation.resetOnly, apps.contains(where: { $0.url.path.hasPrefix("/Applications/") }),
           !FolderAccess.request(.applications) {
            phase = .browsing
            return
        }
        var skipped: [InstalledApp] = []
        for app in apps where AppTerminator.isRunning(app) {
            if AppEdition.isSandboxed {
                // Sandbox không cho tắt app khác: nhờ người dùng tự thoát rồi chờ.
                phase = .working(title: String(localized: "Hãy thoát \(app.name) để tiếp tục gỡ…"), progress: 0, item: app.name, freed: .zero)
                if await AppTerminator.waitUntilQuit(app, timeout: 60) { continue }
                skipped.append(app)
                continue
            }
            phase = .working(title: String(localized: "Đang tắt \(app.name)…"), progress: 0, item: app.name, freed: .zero)
            if await AppTerminator.terminate(app, timeout: 10) { continue }
            let force = await askForceQuit(app)
            if force, await AppTerminator.forceTerminate(app) { continue }
            skipped.append(app)
        }
        if !skipped.isEmpty {
            let skippedIDs = Set(skipped.flatMap { app in appRoot(for: app).map { tree.leaves(under: $0.id).map(\.id) } ?? [] })
            let remaining = plan.items.filter { !skippedIDs.contains($0.nodeID) }
            plan = CleanPlan(items: remaining, blocked: plan.blocked, sessionID: plan.sessionID)
            apps.removeAll { a in skipped.contains { $0.id == a.id } }
        }
        guard !plan.isEmpty else {
            phase = .browsing
            return
        }

        let title = confirmation.resetOnly ? String(localized: "Đang đặt lại \(apps.first?.name ?? "")…") : String(localized: "Đang gỡ cài đặt…")
        phase = .working(title: title, progress: 0, item: "", freed: .zero)
        let confirm: @Sendable (URL) async -> Bool = { [weak self] url in await self?.askPermanentDelete(url) ?? false }
        for await event in services.cleanEngine.execute(plan, dryRun: services.settings.dryRun, confirmPermanentDelete: confirm) {
            switch event {
            case let .progress(fraction, item, freed):
                phase = .working(title: title, progress: fraction, item: item, freed: freed)
            case let .finished(report):
                finish(report: report, apps: apps, resetOnly: confirmation.resetOnly)
            }
        }
    }

    private func finish(report: CleanReport, apps targets: [InstalledApp], resetOnly: Bool) {
        let removed = resetOnly || report.dryRun ? [] : UninstallPlanner.uninstalledApps(targets, report: report)
        planner.didUninstall(removed)
        let removedIDs = Set(removed.map(\.id))
        apps.removeAll { removedIDs.contains($0.id) }
        checked.subtract(removedIDs)
        for id in removedIDs { reports[id] = nil }
        if focusedID.map(removedIDs.contains) ?? false { focusedID = visibleApps.first?.id }
        // App còn lại (đặt lại, gỡ lỗi): tìm lại file sót.
        for app in targets where !removedIDs.contains(app.id) {
            reports[app.id] = nil
            loadLeftovers(for: app.id)
        }
        rebuildTree()
        if !removedIDs.isEmpty { Log.info(.clean, "uninstaller", "Đã gỡ \(removed.map(\.bundleID).joined(separator: ", "))") }
        phase = .done(message: UninstallPlanner.summary(uninstalled: removed, report: report, resetOnly: resetOnly), report: report)
    }

    func dismissDone() {
        phase = .browsing
        if let id = focusedID { loadLeftovers(for: id) }
    }

    private func askForceQuit(_ app: InstalledApp) async -> Bool {
        await withCheckedContinuation { cont in
            forceQuitPrompt = ForceQuitPrompt(app: app) { [weak self] answer in
                self?.forceQuitPrompt = nil
                cont.resume(returning: answer)
            }
        }
    }

    private func askPermanentDelete(_ url: URL) async -> Bool {
        await withCheckedContinuation { cont in
            permanentDeletePrompt = PermanentDeletePrompt(url: url) { [weak self] answer in
                self?.permanentDeletePrompt = nil
                cont.resume(returning: answer)
            }
        }
    }

    func openBundledUninstaller(_ app: InstalledApp) {
        guard let url = app.bundledUninstaller else { return }
        NSWorkspace.shared.open(url)
    }
}
