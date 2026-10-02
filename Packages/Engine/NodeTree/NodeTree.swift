import Foundation
import SweepCore

/// Cây kết quả bất biến, chia sẻ được giữa các luồng (mục 6).
/// Có chỉ mục phẳng để tra cứu cha/con, số lá và dung lượng lá theo node mà không duyệt lại cả cây.
public struct NodeTree: Sendable {
    public let roots: [Node]
    /// Chỉ mục: id → node (bản sao có children).
    public let index: [NodeID: Node]
    public let parent: [NodeID: NodeID]
    /// id node → danh sách id lá xoá được bên dưới (cache, dùng cho chọn/bỏ chọn nhanh).
    public let leafIDs: [NodeID: [NodeID]]
    public let totalSize: ByteCount

    public init(roots: [Node]) {
        var normalized = roots
        for i in normalized.indices { normalized[i].recomputeAggregates() }
        self.roots = normalized
        var index: [NodeID: Node] = [:]
        var parent: [NodeID: NodeID] = [:]
        var leaves: [NodeID: [NodeID]] = [:]
        @discardableResult
        func visit(_ node: Node, parentID: NodeID?) -> [NodeID] {
            index[node.id] = node
            if let parentID { parent[node.id] = parentID }
            var mine: [NodeID] = []
            if node.isContainer {
                for c in node.children { mine += visit(c, parentID: node.id) }
            } else {
                mine = [node.id]
                for c in node.children { visit(c, parentID: node.id) } // con của mục xoá được chỉ để hiển thị
            }
            leaves[node.id] = mine
            return mine
        }
        for r in normalized { visit(r, parentID: nil) }
        self.index = index
        self.parent = parent
        leafIDs = leaves
        totalSize = normalized.sum(\.size)
    }

    public static let empty = NodeTree(roots: [])

    public var isEmpty: Bool { roots.isEmpty }

    public func node(_ id: NodeID) -> Node? { index[id] }

    public func ancestors(of id: NodeID) -> [NodeID] {
        var result: [NodeID] = []
        var current = parent[id]
        while let p = current {
            result.append(p)
            current = parent[p]
        }
        return result
    }

    /// Mọi lá xoá được trong cây.
    public var allLeaves: [Node] { roots.flatMap(\.removableLeaves) }

    public func leaves(under id: NodeID) -> [Node] {
        (leafIDs[id] ?? []).compactMap { index[$0] }
    }

    /// Tổng dung lượng các lá thoả điều kiện.
    public func size(where predicate: (Node) -> Bool) -> ByteCount {
        allLeaves.filter(predicate).sum(\.size)
    }

    // MARK: Gộp nhiều kết quả

    /// Gộp node từ nhiều task, khử trùng lặp và hạ mức an toàn của mục cache lớn bất thường.
    public static func merge(_ roots: [Node], categoryPriority: [String] = NodeTree.defaultCategoryPriority) -> NodeTree {
        var deduped = deduplicate(roots, categoryPriority: categoryPriority)
        for i in deduped.indices { applyLargeItemGuard(&deduped[i]) }
        return NodeTree(roots: deduped.filter { !$0.isContainer || !$0.children.isEmpty })
    }

    /// Thứ tự ưu tiên khi hai rule cùng trỏ vào một đường dẫn (mục 6.3): file sót của app > cache > log > còn lại.
    public static let defaultCategoryPriority = [
        "appLeftovers", "orphanedLeftovers", "userCaches", "systemCaches", "devToolCaches", "xcodeJunk",
        "logs", "userLogs", "systemLogs", "crashReports",
    ]

    /// Một đường dẫn chỉ thuộc **một** node (mục 6.3):
    /// 1. Chỉ mục theo đường dẫn chuẩn hoá.
    /// 2. Trùng đường dẫn: giữ node có category ưu tiên cao hơn.
    /// 3. Node là thư mục cha của node khác: giữ node cha, bỏ node con (không xoá hai lần, không đếm hai lần).
    public static func deduplicate(_ roots: [Node], categoryPriority: [String] = defaultCategoryPriority) -> [Node] {
        func rank(_ category: String?) -> Int {
            guard let category, let i = categoryPriority.firstIndex(of: category) else { return categoryPriority.count }
            return i
        }

        // Thu thập mọi lá có đường dẫn.
        struct Candidate { let id: NodeID; let path: String; let rank: Int; let order: Int }
        var candidates: [Candidate] = []
        var order = 0
        func collect(_ n: Node, inheritedCategory: String?) {
            let cat = n.category ?? inheritedCategory
            if !n.isContainer {
                if let url = n.url {
                    candidates.append(Candidate(id: n.id, path: url.canonicalPath, rank: rank(cat), order: order))
                    order += 1
                }
                return
            }
            for c in n.children { collect(c, inheritedCategory: cat) }
        }
        for r in roots { collect(r, inheritedCategory: nil) }

        // Bước 2: trùng đường dẫn chính xác.
        var winnerByPath: [String: Candidate] = [:]
        for c in candidates {
            if let existing = winnerByPath[c.path] {
                if (c.rank, c.order) < (existing.rank, existing.order) { winnerByPath[c.path] = c }
            } else {
                winnerByPath[c.path] = c
            }
        }
        var removed = Set(candidates.filter { winnerByPath[$0.path]?.id != $0.id }.map(\.id))

        // Bước 3: node là con cháu của một node khác → bỏ node con. Sắp theo đường dẫn để kiểm tra tiền tố trong O(n log n).
        let survivors = winnerByPath.values.sorted { $0.path < $1.path }
        var stack: [String] = []
        for c in survivors {
            while let top = stack.last, !(c.path.hasPrefix(top + "/") || top == "/") { stack.removeLast() }
            if stack.last != nil {
                removed.insert(c.id)
            } else {
                stack.append(c.path)
            }
        }

        guard !removed.isEmpty else { return roots }
        func prune(_ n: Node) -> Node? {
            if removed.contains(n.id) { return nil }
            guard n.isContainer else { return n }
            var copy = n
            copy.children = n.children.compactMap(prune)
            if copy.children.isEmpty { return nil }
            copy.recomputeAggregates()
            return copy
        }
        return roots.compactMap(prune)
    }

    /// Mục lớn bất thường (thư mục > 50 GB mà rule loại cache) thì hạ xuống `review` (mục 15.1).
    static func applyLargeItemGuard(_ node: inout Node) {
        let cacheCategories: Set<String> = ["userCaches", "systemCaches", "devToolCaches"]
        if !node.isContainer, let cat = node.category, cacheCategories.contains(cat), node.size > .gigabytes(50), node.safety == .safe {
            node.safety = .review
            node.badges.append("Lớn bất thường")
        }
        for i in node.children.indices { applyLargeItemGuard(&node.children[i]) }
    }
}
