import Foundation
import SweepCore

/// Trạng thái chọn của node cha tính từ con.
public enum CheckState: Sendable, Hashable {
    case on, off, mixed
}

/// Trạng thái chọn lưu tách khỏi cây (mục 6.2), để cây bất biến và chia sẻ được giữa các luồng.
/// Dung lượng "sẽ giải phóng" được tính tăng dần: chỉ cộng trừ phần thay đổi, không duyệt lại cả cây.
public struct SelectionState: Sendable, Equatable {
    /// Lá xoá được → đang chọn.
    public private(set) var values: [NodeID: Bool] = [:]
    public private(set) var selectedBytes: ByteCount = .zero
    public private(set) var selectedCount: Int = 0
    /// Số lá đang chọn dưới mỗi node chứa (cập nhật tăng dần dọc đường tới gốc).
    private var selectedLeafCount: [NodeID: Int] = [:]

    public init() {}

    /// Lựa chọn mặc định: node `safe` được chọn sẵn, còn lại không (mục 6.2).
    public static func defaults(for tree: NodeTree, where predicate: ((Node) -> Bool)? = nil) -> SelectionState {
        var s = SelectionState()
        for leaf in tree.allLeaves where leaf.safety == .safe && (predicate?(leaf) ?? true) {
            s.setLeaf(leaf, true, in: tree)
        }
        return s
    }

    public func isSelected(_ id: NodeID) -> Bool { values[id] ?? false }

    public var selectedIDs: [NodeID] { values.compactMap { $0.value ? $0.key : nil } }

    public func state(of id: NodeID, in tree: NodeTree) -> CheckState {
        let total = tree.leafIDs[id]?.count ?? 0
        guard total > 0 else { return .off }
        let selected: Int
        if let node = tree.node(id), node.isContainer {
            selected = selectedLeafCount[id] ?? 0
        } else {
            selected = isSelected(id) ? 1 : 0
        }
        if selected == 0 { return .off }
        return selected == total ? .on : .mixed
    }

    /// Chọn hoặc bỏ chọn một node; với node cha áp cho toàn bộ node con.
    public mutating func set(_ id: NodeID, _ on: Bool, in tree: NodeTree) {
        for leaf in tree.leaves(under: id) { setLeaf(leaf, on, in: tree) }
    }

    /// Đảo trạng thái: `off`/`mixed` → chọn hết, `on` → bỏ hết.
    public mutating func toggle(_ id: NodeID, in tree: NodeTree) {
        set(id, state(of: id, in: tree) != .on, in: tree)
    }

    public mutating func selectAll(in tree: NodeTree) {
        for leaf in tree.allLeaves { setLeaf(leaf, true, in: tree) }
    }

    public mutating func clear() {
        self = SelectionState()
    }

    private mutating func setLeaf(_ leaf: Node, _ on: Bool, in tree: NodeTree) {
        let was = values[leaf.id] ?? false
        guard was != on else { return }
        values[leaf.id] = on
        let delta = on ? 1 : -1
        selectedCount += delta
        selectedBytes = on ? selectedBytes + leaf.size : selectedBytes - leaf.size
        for a in tree.ancestors(of: leaf.id) { selectedLeafCount[a, default: 0] += delta }
    }

    /// Các lá đang chọn (theo thứ tự trong cây).
    public func selectedLeaves(in tree: NodeTree) -> [Node] {
        tree.allLeaves.filter { isSelected($0.id) }
    }
}
