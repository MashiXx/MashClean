import DuplicatesScanning
import Foundation
import NodeTree
import ScanEngine
import SweepCore

/// Feature Duplicates (mục 11.8). Không chạy trong Smart Scan; mặc định không chọn gì (mục 15.2).
public struct DuplicatesFeature: FeatureScanProvider {
    public let featureID: FeatureID = .duplicates
    public let includeInSmartScan = false
    public let home: URL

    public init(home: URL = .userHome) { self.home = home }

    public var defaultRoots: [URL] { DuplicatesTask.defaultRoots(home: home) }

    public func scanTasks() -> [any ScanTask] { [makeTask(roots: defaultRoots)] }

    /// Task với thư mục do người dùng chọn và hộp tiến độ theo bước.
    public func makeTask(roots: [URL], progress: DuplicateProgressBox? = nil) -> DuplicatesTask {
        DuplicatesTask(roots: roots.isEmpty ? defaultRoots : roots, home: home, progressBox: progress)
    }

    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let wasted = nodes.reduce(ByteCount.zero) { $0 + DuplicatesTask.wasted($1) }
        return FeatureSummary(featureID: featureID, card: .cleanup, title: "File trùng lặp",
                              subtitle: nodes.isEmpty ? "Không có file trùng lặp" : "\(nodes.count) nhóm trùng lặp",
                              bytes: wasted, itemCount: nodes.count)
    }
}

/// Chọn tự động và thống kê cho nhóm trùng lặp.
public enum DuplicateSelection {
    public static func isKeep(_ node: Node) -> Bool { node.badges.contains(DuplicateBadges.keep) }
    public static func isClone(_ node: Node) -> Bool { node.badges.contains(DuplicateBadges.clone) }

    /// "Chọn tự động": mọi bản trừ bản nên giữ; clone bỏ qua vì xoá không giải phóng gì.
    public static func autoSelect(in tree: NodeTree) -> SelectionState {
        var s = SelectionState()
        for group in tree.roots {
            let leaves = group.removableLeaves
            guard leaves.contains(where: isKeep) else { continue }
            for leaf in leaves where !isKeep(leaf) && !isClone(leaf) { s.set(leaf.id, true, in: tree) }
        }
        return s
    }

    /// Không cho chọn hết mọi bản của một nhóm (luôn còn ít nhất một bản).
    public static func groupsWithEverythingSelected(_ selection: SelectionState, in tree: NodeTree) -> [Node] {
        tree.roots.filter { selection.state(of: $0.id, in: tree) == .on }
    }

    public static func wasted(_ group: Node) -> ByteCount { DuplicatesTask.wasted(group) }
}
