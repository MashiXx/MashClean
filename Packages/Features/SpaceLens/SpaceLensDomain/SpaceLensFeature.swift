import CleanEngine
import FileSystemKit
import Foundation
import NodeTree
import ScanEngine
import SpaceLensScanning
import SweepCore

/// Feature Space Lens (mục 11.4). Không chạy trong Smart Scan; màn riêng dùng `DiskScanner` trực tiếp.
public struct SpaceLensFeature: FeatureScanProvider {
    public let featureID: FeatureID = .spaceLens
    public let includeInSmartScan = false
    public let defaultRoot: URL

    public init(defaultRoot: URL = .userHome) { self.defaultRoot = defaultRoot }

    public func scanTasks() -> [any ScanTask] { [SpaceLensTask(root: defaultRoot)] }

    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let total = nodes.sum(\.size)
        let top = nodes.sorted { $0.size > $1.size }.prefix(3).map { "\($0.title) (\($0.size.formatted))" }
        return FeatureSummary(featureID: featureID, card: .cleanup, title: String(localized: "Bản đồ dung lượng"),
                              subtitle: top.isEmpty ? String(localized: "Không có dữ liệu") : top.joined(separator: ", "),
                              bytes: total, itemCount: nodes.count)
    }
}

/// Điều hướng trong cây: lưu đường đi bằng tên (ổn định khi cây được quét lại và chỉ số thay đổi).
public struct SpaceLensNavigator: Sendable, Equatable {
    public private(set) var components: [String] = []

    public init(components: [String] = []) { self.components = components }

    /// Node đang xem; nếu thư mục không còn thì lùi về tổ tiên gần nhất còn tồn tại.
    public func currentIndex(in tree: DiskTree) -> Int {
        var cur = tree.root
        for name in components {
            guard let c = tree.child(named: name, of: cur), tree.isDirectory(c) else { break }
            cur = c
        }
        return cur
    }

    public struct Crumb: Sendable, Hashable, Identifiable {
        public var id: Int { level }
        public let level: Int
        public let title: String
    }

    public func breadcrumb(in tree: DiskTree) -> [Crumb] {
        let rootTitle = (tree.rootPath as NSString).lastPathComponent.isEmpty ? tree.rootPath : (tree.rootPath as NSString).lastPathComponent
        return [Crumb(level: 0, title: rootTitle)] + components.enumerated().map { Crumb(level: $0.offset + 1, title: $0.element) }
    }

    public var canGoUp: Bool { !components.isEmpty }

    /// Đi sâu vào một thư mục con bất kỳ trong cây.
    public mutating func drill(into index: Int, in tree: DiskTree) {
        guard tree.isDirectory(index), tree.children(of: index).count > 0 else { return }
        components = tree.components(of: index)
    }

    public mutating func goUp() {
        if !components.isEmpty { components.removeLast() }
    }

    public mutating func go(toLevel level: Int) {
        components = Array(components.prefix(max(0, level)))
    }

    /// Cắt lại đường đi theo thư mục thực sự còn tồn tại.
    public mutating func normalize(in tree: DiskTree) {
        components = tree.components(of: currentIndex(in: tree))
    }
}

/// Xoá mục chọn trên bản đồ qua Clean Engine (mặc định chuyển vào Thùng rác, mục 11.4, 15.2).
public struct SpaceLensCleaner: Sendable {
    public let cleanEngine: CleanEngine

    public init(cleanEngine: CleanEngine) { self.cleanEngine = cleanEngine }

    public func nodes(forPaths paths: [String], in tree: DiskTree) -> [Node] {
        let indices = paths.compactMap { tree.index(forPath: $0) }.filter { $0 != tree.root }
        return SpaceLensNodes.nodes(for: Self.removingNested(indices, in: tree), in: tree)
    }

    /// Kiểm tra PathPolicy và lập kế hoạch; mục bị chặn nằm trong `plan.blocked`.
    public func plan(paths: [String], in tree: DiskTree) -> CleanPlan {
        cleanEngine.makePlan(nodes: nodes(forPaths: paths, in: tree))
    }

    public func run(_ plan: CleanPlan, dryRun: Bool, confirmPermanentDelete: @escaping @Sendable (URL) async -> Bool = { _ in false },
                    onEvent: @escaping @Sendable (CleanEvent) -> Void = { _ in }) async -> CleanReport {
        await cleanEngine.run(plan, dryRun: dryRun, confirmPermanentDelete: confirmPermanentDelete, onEvent: onEvent)
    }

    /// Thư mục cha của các mục đã xoá: cần quét lại để cập nhật tổng (mục 23.6).
    public static func affectedDirectories(of report: CleanReport) -> [String] {
        let parents = report.succeeded.compactMap { $0.item.url?.deletingLastPathComponent().path }
        return Array(Set(parents)).sorted()
    }

    /// Bỏ mục nằm bên trong một mục khác cũng đang chọn.
    static func removingNested(_ indices: [Int], in tree: DiskTree) -> [Int] {
        let set = Set(indices)
        return indices.filter { i in !tree.lineage(of: i).dropLast().contains { set.contains($0) } }
    }
}

/// Gom đường dẫn FSEvents báo về thành danh sách thư mục cần đọc lại (mục 23.6).
public enum SpaceLensRefreshPlanner {
    /// Bỏ dấu `/` cuối, bỏ trùng, bỏ đường dẫn ngoài cây.
    public static func directories(forChangedPaths paths: [String], rootPath: String) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        let prefix = rootPath == "/" ? "/" : rootPath + "/"
        for raw in paths {
            var p = raw
            while p.count > 1 && p.hasSuffix("/") { p.removeLast() }
            guard p == rootPath || p.hasPrefix(prefix) else { continue }
            if seen.insert(p).inserted { result.append(p) }
        }
        return result
    }
}
