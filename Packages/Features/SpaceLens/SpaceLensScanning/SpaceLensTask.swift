import FileSystemKit
import Foundation
import NodeTree
import ScanEngine
import SweepCore

/// Task quét dung lượng một thư mục cho `FeatureScanProvider`: trả các mục cấp 1 sắp theo dung lượng.
/// Màn Space Lens dùng `DiskScanner` trực tiếp để giữ cây `DiskTree` đầy đủ.
public struct SpaceLensTask: ScanTask {
    public let id: ScanTaskID = "spaceLens"
    public let title = "Bản đồ dung lượng"
    public let estimatedWeight: Double = 4
    public let root: URL

    public init(root: URL) { self.root = root }

    public func run(context: ScanContext) async throws -> ScanOutput {
        let progress = DiskScanProgress()
        let tree = try await DiskScanner(fileSystem: context.fileSystem).scan(root: root, priority: .utility, progress: progress)
        context.progress.addFilesVisited(progress.snapshot.items)
        let nodes = SpaceLensNodes.nodes(for: Array(tree.children(of: tree.root)), in: tree)
            .filter { !context.environment.ignore.ignores(path: $0.url?.path ?? "") }
            .sorted { $0.size > $1.size }
        context.progress.report(1)
        return ScanOutput(nodes: nodes)
    }
}

/// Dựng `Node` xoá được từ node của cây Space Lens: file người dùng nên chuyển vào Thùng rác,
/// mức `review`, không tự chọn (mục 11.4, 15.2).
public enum SpaceLensNodes {
    public static func node(for index: Int, in tree: DiskTree) -> Node {
        let url = URL(fileURLWithPath: tree.path(of: index))
        let isDir = tree.isDirectory(index)
        let kind: NodeKind = isDir ? .directory(url, recursive: true) : .file(url)
        return Node(kind: kind, title: tree.name(of: index), size: ByteCount(tree.size(of: index)),
                    itemCount: isDir ? max(1, tree.descendantCount(of: index)) : 1,
                    safety: .review, reason: "Chọn từ bản đồ dung lượng", category: "spaceLens",
                    removal: .moveToTrash, allowedRoots: [tree.rootPath])
    }

    public static func nodes(for indices: [Int], in tree: DiskTree) -> [Node] {
        indices.filter { tree.size(of: $0) > 0 || !tree.isDirectory($0) }.map { node(for: $0, in: tree) }
    }
}
