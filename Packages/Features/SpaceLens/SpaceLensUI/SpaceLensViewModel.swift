import AppKit
import CleanEngine
import FileSystemKit
import Foundation
import SharedUI
import SpaceLensDomain
import SpaceLensScanning
import SweepCore
import SweepLogging

/// Trạng thái màn Space Lens: chọn ổ/thư mục → quét → bản đồ + danh sách, cập nhật tăng dần bằng FSEvents (mục 23.6).
@MainActor
final class SpaceLensViewModel: ObservableObject {
    enum Phase {
        case idle
        case scanning(root: String, progress: DiskScanProgress.Snapshot)
        case results
        case error(String)
    }

    @Published private(set) var phase: Phase = .idle
    /// Tăng mỗi khi cây thay đổi để view vẽ lại.
    @Published private(set) var revision = 0
    @Published private(set) var navigator = SpaceLensNavigator()
    @Published private(set) var segments: [SunburstSegment] = []
    @Published var selection = Set<String>()
    @Published var hovered: SunburstSegment?
    @Published var pendingPlan: CleanPlan?
    @Published private(set) var isCleaning = false
    @Published var notice: String?
    @Published private(set) var scanDuration: TimeInterval = 0

    /// Không `@Published` để sửa tại chỗ, tránh chép cả mảng (mục 11.4).
    private(set) var tree = DiskTree(rootPath: "/")

    let services: ScanServices
    let feature: SpaceLensFeature
    private let scanner: DiskScanner
    private let cleaner: SpaceLensCleaner
    private var scanTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?
    private var pendingRefresh: [String] = []
    private let watcher = DirectoryWatcher()

    init(services: ScanServices, feature: SpaceLensFeature) {
        self.services = services
        self.feature = feature
        scanner = DiskScanner(fileSystem: services.fileSystem)
        cleaner = SpaceLensCleaner(cleanEngine: services.cleanEngine)
    }

    /// Gọi khi rời màn hình: dừng FSEvents.
    func stopWatching() { watcher.stop() }

    func resumeWatching() { watchCurrent() }

    // MARK: Quét

    func scan(_ url: URL) {
        scanTask?.cancel()
        watcher.stop()
        selection = []
        hovered = nil
        notice = nil
        let progress = DiskScanProgress()
        let scanner = scanner
        let path = url.path
        phase = .scanning(root: path, progress: progress.snapshot)
        let start = Date()
        scanTask = Task { [weak self] in
            let worker = Task.detached(priority: .userInitiated) { try await scanner.scan(root: url, progress: progress) }
            let ticker = Task { [weak self] in
                while !Task.isCancelled {
                    try? await Task.sleep(nanoseconds: 150_000_000)
                    guard let self, case .scanning = self.phase else { return }
                    self.phase = .scanning(root: path, progress: progress.snapshot)
                }
            }
            defer { ticker.cancel() }
            do {
                let tree = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                guard let self else { return }
                self.tree = tree
                self.scanDuration = Date().timeIntervalSince(start)
                self.navigator = SpaceLensNavigator()
                self.phase = .results
                self.treeChanged()
                Log.info(.scan, "spaceLens", "Quét \(tree.count) mục trong \(String(format: "%.1f", self.scanDuration)) giây")
            } catch is CancellationError {
                self?.phase = .idle
            } catch {
                self?.phase = .error("Không quét được \(path.abbreviatingHome): \(error.localizedDescription)")
            }
        }
    }

    func cancel() {
        scanTask?.cancel()
        refreshTask?.cancel()
        watcher.stop()
        phase = .idle
    }

    func chooseAnother() { cancel() }

    // MARK: Điều hướng

    var currentIndex: Int { navigator.currentIndex(in: tree) }
    var currentPath: String { tree.path(of: currentIndex) }

    /// Con của thư mục đang xem, sắp giảm dần theo dung lượng.
    var currentChildren: [Int] {
        tree.children(of: currentIndex).sorted { tree.size(of: $0) > tree.size(of: $1) }
    }

    func drill(into index: Int) {
        guard tree.isDirectory(index), !tree.children(of: index).isEmpty else { return }
        navigator.drill(into: index, in: tree)
        hovered = nil
        navigationChanged()
    }

    func goUp() {
        navigator.goUp()
        hovered = nil
        navigationChanged()
    }

    func go(toLevel level: Int) {
        navigator.go(toLevel: level)
        hovered = nil
        navigationChanged()
    }

    private func navigationChanged() {
        segments = SunburstLayout.layout(tree: tree, center: currentIndex)
        watchCurrent()
    }

    private func treeChanged() {
        navigator.normalize(in: tree)
        segments = SunburstLayout.layout(tree: tree, center: currentIndex)
        selection = selection.filter { tree.index(forPath: $0) != nil }
        revision += 1
        watchCurrent()
    }

    // MARK: FSEvents

    /// Chỉ theo dõi thư mục đang xem (mục 11.4).
    private func watchCurrent() {
        guard case .results = phase else { return }
        let root = tree.rootPath
        watcher.start(paths: [currentPath]) { [weak self] paths in
            let dirs = SpaceLensRefreshPlanner.directories(forChangedPaths: paths, rootPath: root)
            guard !dirs.isEmpty else { return }
            Task { @MainActor [weak self] in self?.enqueueRefresh(dirs) }
        }
    }

    func enqueueRefresh(_ paths: [String]) {
        for p in paths where !pendingRefresh.contains(p) { pendingRefresh.append(p) }
        guard refreshTask == nil else { return }
        refreshTask = Task { [weak self] in
            await self?.drainRefreshQueue()
            self?.refreshTask = nil
        }
    }

    /// Đọc lại từng thư mục được báo (một cấp), áp vào cây rồi cập nhật tổng lên tới gốc.
    private func drainRefreshQueue() async {
        while !pendingRefresh.isEmpty {
            let requested = pendingRefresh.removeFirst()
            guard var index = tree.nearestDirectory(forPath: requested) else { continue }
            while index != tree.root, !services.fileSystem.exists(URL(fileURLWithPath: tree.path(of: index))) {
                index = tree.parent(of: index) ?? tree.root
            }
            let dirPath = tree.path(of: index)
            let known = Set(tree.children(of: index).filter { tree.isDirectory($0) }.map { tree.name(of: $0) })
            let scanner = scanner
            let listing = try? await Task.detached(priority: .userInitiated) {
                try scanner.refreshListing(path: dirPath, knownDirectories: known)
            }.value
            guard !Task.isCancelled else { return }
            guard let listing, let target = tree.index(forPath: dirPath) else { continue }
            tree.apply(listing, at: target)
            tree.compactIfNeeded()
            treeChanged()
        }
    }

    // MARK: Chọn

    func isSelected(_ index: Int) -> Bool { selection.contains(tree.path(of: index)) }

    func toggle(_ index: Int) {
        guard index != tree.root else { return }
        let p = tree.path(of: index)
        if selection.contains(p) { selection.remove(p) } else { selection.insert(p) }
    }

    var selectedBytes: UInt64 {
        let indices = selection.compactMap { tree.index(forPath: $0) }
        let set = Set(indices)
        return indices.filter { i in !tree.lineage(of: i).dropLast().contains { set.contains($0) } }
            .reduce(0) { $0 &+ tree.size(of: $1) }
    }

    func revealInFinder(_ paths: [String]) {
        let urls = paths.map { URL(fileURLWithPath: $0) }
        guard !urls.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    // MARK: Xoá

    /// Lập kế hoạch qua Clean Engine (PathPolicy chặn vùng cấm), hiện hộp xác nhận.
    func prepareTrash() {
        let plan = cleaner.plan(paths: Array(selection), in: tree)
        pendingPlan = plan
    }

    func confirmTrash() {
        guard let plan = pendingPlan else { return }
        pendingPlan = nil
        guard !plan.items.isEmpty else { return }
        isCleaning = true
        let cleaner = cleaner
        let dryRun = services.settings.dryRun
        Task { [weak self] in
            let report = await cleaner.run(plan, dryRun: dryRun)
            guard let self else { return }
            self.isCleaning = false
            let moved = report.succeeded.count
            var text = report.dryRun ? "Chạy thử: \(moved) mục sẽ được chuyển vào Thùng rác." : "Đã chuyển \(moved) mục (\(report.estimatedFreed.formatted)) vào Thùng rác."
            if !report.failed.isEmpty { text += " \(report.failed.count) mục lỗi." }
            if !report.skipped.isEmpty { text += " \(report.skipped.count) mục bị bỏ qua." }
            self.notice = text
            self.selection = []
            if !report.dryRun { self.enqueueRefresh(SpaceLensCleaner.affectedDirectories(of: report)) }
        }
    }
}
