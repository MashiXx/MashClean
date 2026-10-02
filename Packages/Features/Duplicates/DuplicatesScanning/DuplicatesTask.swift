import FileSystemKit
import Foundation
import NodeTree
import ScanEngine
import SweepCore

/// Nhãn trên node file trùng lặp.
public enum DuplicateBadges {
    public static let keep = "Nên giữ"
    public static let clone = "Clone APFS: xoá không giải phóng dung lượng"
    public static let category = "duplicates"
}

/// Tiến độ theo bước, UI đọc định kỳ (ScanProgress chỉ có tên task).
public final class DuplicateProgressBox: Sendable {
    private let value = Locked(DuplicateFinder.Progress.start)
    public init() {}
    public var current: DuplicateFinder.Progress { value.current }
    public func set(_ p: DuplicateFinder.Progress) { value.withLock { $0 = p } }
}

/// Task tìm trùng lặp (mục 11.8). Mỗi nhóm là node group; mỗi file là `.file`, chuyển vào Thùng rác,
/// mức `review`, mặc định không chọn (mục 15.2).
public struct DuplicatesTask: ScanTask {
    public let id: ScanTaskID = "duplicates"
    public let title = "Tìm file trùng lặp"
    public let estimatedWeight: Double = 5
    public let roots: [URL]
    public let home: URL
    public let progressBox: DuplicateProgressBox?

    public init(roots: [URL], home: URL = .userHome, progressBox: DuplicateProgressBox? = nil) {
        self.roots = roots
        self.home = home
        self.progressBox = progressBox
    }

    /// Thư mục quét mặc định: home (bỏ ~/Library, package, thư mục ẩn).
    public static func defaultRoots(home: URL = .userHome) -> [URL] { [home] }

    public func run(context: ScanContext) async throws -> ScanOutput {
        // Bỏ ~/Library và Thùng rác, trừ khi người dùng chủ động thêm chính thư mục đó (hoặc thư mục con của nó).
        let excluded = [home.appendingPathComponent("Library").path, home.appendingPathComponent(".Trash").path].filter { ex in
            !roots.contains { $0.path == ex || $0.path.hasPrefix(ex + "/") }
        }
        let options = DuplicateFinder.Options(
            roots: roots, excludedPrefixes: excluded,
            policy: context.environment.policy, ignore: context.environment.ignore,
            concurrency: context.fileSystem.recommendedConcurrency(for: roots.first ?? home)
        )
        let reporter = context.progress
        let box = progressBox
        let seenBox = Locked(0)
        let groups = try await DuplicateFinder(options: options).find { p in
            box?.set(p)
            reporter.report(p.fraction)
            let delta = seenBox.withLock { s -> Int in
                let d = p.filesSeen - s
                s = max(s, p.filesSeen)
                return max(d, 0)
            }
            if delta > 0 { reporter.addFilesVisited(delta) }
        }
        let nodes = groups.map { makeNode($0) }
        for n in nodes { context.progress.addBytesFound(Self.wasted(n)) }
        return ScanOutput(nodes: nodes)
    }

    func makeNode(_ group: DuplicateGroup) -> Node {
        let keepIndex = KeepAdvisor.suggestKeep(group.files.map { .init(path: $0.path, date: $0.created) }, home: home.path)
        var children: [Node] = []
        for (i, f) in group.files.enumerated() {
            let url = URL(fileURLWithPath: f.path)
            var badges: [String] = []
            if i == keepIndex { badges.append(DuplicateBadges.keep) }
            if f.isClone { badges.append(DuplicateBadges.clone) }
            let root = roots.indices.contains(f.candidate.rootIndex) ? roots[f.candidate.rootIndex].path : home.path
            children.append(Node(kind: .file(url), title: url.lastPathComponent,
                                 size: f.isClone ? .zero : ByteCount(f.candidate.allocatedSize), itemCount: 1,
                                 safety: .review, reason: LocalizedText("Trùng nội dung (SHA-256) với \(group.files.count - 1) file khác"),
                                 category: DuplicateBadges.category, removal: .moveToTrash, allowedRoots: [root],
                                 lastAccess: f.candidate.modified, badges: badges))
        }
        // Bản nên giữ lên đầu.
        children.sort { a, b in
            let ka = a.badges.contains(DuplicateBadges.keep), kb = b.badges.contains(DuplicateBadges.keep)
            return ka != kb ? ka : a.title < b.title
        }
        let name = children.first?.title ?? "?"
        var node = Node.group(LocalizedText("\(name) — \(group.files.count) bản"), icon: "doc.on.doc",
                              category: DuplicateBadges.category, children: children, safety: .review)
        node.reason = LocalizedText("Mỗi bản \(ByteCount(group.size).formatted)")
        return node
    }

    /// Dung lượng giải phóng được nếu xoá mọi bản trừ bản nên giữ.
    public static func wasted(_ group: Node) -> ByteCount {
        group.children.filter { !$0.badges.contains(DuplicateBadges.keep) }.sum(\.size)
    }
}
