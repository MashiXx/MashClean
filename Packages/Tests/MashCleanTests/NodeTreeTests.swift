import Foundation
import Testing
import NodeTree
import SweepCore

/// Gộp trùng Node Tree (mục 6.3) và trạng thái chọn (mục 6.2).
private let base = "/nonexistent-mashclean-tests"

private func leaf(_ path: String, size: Int64 = 100, category: String = "userCaches", safety: SafetyLevel = .safe, rule: String? = nil) -> Node {
    Node(kind: .directory(URL(fileURLWithPath: base + path), recursive: true), size: ByteCount(size), itemCount: 1, safety: safety,
         ruleID: rule.map { RuleID($0) }, category: category, removal: .delete)
}

private func group(_ title: String, _ children: [Node], category: String? = nil) -> Node {
    Node.group(LocalizedText(title), icon: "folder", category: category, children: children)
}

@Suite struct NodeTreeDedupeTests {
    @Test func samePathKeepsHigherPriorityCategory() {
        let cache = leaf("/Library/Caches/com.app", category: "userCaches", rule: "user.caches.all")
        let leftover = leaf("/Library/Caches/com.app", category: "appLeftovers", rule: "app.com.app.leftovers")
        let tree = NodeTree.merge([group("Cache", [cache]), group("Leftovers", [leftover])])
        #expect(tree.allLeaves.count == 1)
        #expect(tree.allLeaves.first?.category == "appLeftovers")
        // Nhóm rỗng sau gộp bị bỏ.
        #expect(tree.roots.count == 1)
    }

    @Test func samePathSameCategoryKeepsFirst() {
        let a = leaf("/x", rule: "first")
        let b = leaf("/x", rule: "second")
        let result = NodeTree.deduplicate([group("A", [a, b])])
        #expect(result.flatMap(\.removableLeaves).map(\.ruleID) == [RuleID("first")])
    }

    @Test func categoryInheritedFromGroup() {
        var a = leaf("/x", category: "userLogs")
        a.category = nil
        let b = leaf("/x", category: "devToolCaches")
        let result = NodeTree.deduplicate([group("Logs", [a], category: "userLogs"), group("Dev", [b], category: "devToolCaches")])
        #expect(result.flatMap(\.removableLeaves).map(\.category) == ["devToolCaches"])
    }

    @Test func parentWinsOverChild() {
        let parent = leaf("/Library/Caches/Google", size: 1000)
        let child = leaf("/Library/Caches/Google/Chrome/Default/Cache", size: 600, category: "appLeftovers")
        let other = leaf("/Library/Caches/GoogleUpdater", size: 50)
        let tree = NodeTree.merge([group("Cache", [parent, other]), group("Chrome", [child])])
        let paths = tree.allLeaves.compactMap(\.url?.path).sorted()
        #expect(paths == [base + "/Library/Caches/Google", base + "/Library/Caches/GoogleUpdater"])
        #expect(tree.totalSize == 1050)
    }

    @Test func childBeforeParentInInputStillDeduped() {
        let child = leaf("/a/b/c")
        let parent = leaf("/a/b")
        let tree = NodeTree.merge([group("G", [child, parent])])
        #expect(tree.allLeaves.compactMap(\.url?.path) == [base + "/a/b"])
    }

    @Test func noDuplicatesReturnsInputUnchanged() {
        let roots = [group("G", [leaf("/a"), leaf("/b")])]
        #expect(NodeTree.deduplicate(roots).first?.children.count == 2)
    }

    @Test func virtualNodesWithoutPathAreKept() {
        let docker = Node(kind: .virtual(.dockerPrune), size: 10, safety: .review, category: "devToolCaches", removal: .custom(.docker))
        let tree = NodeTree.merge([group("Dev", [docker, leaf("/a")])])
        #expect(tree.allLeaves.count == 2)
    }

    @Test func largeCacheIsDowngradedToReview() {
        let huge = leaf("/huge", size: ByteCount.gigabytes(60).bytes, category: "userCaches")
        let hugeLog = leaf("/hugeLog", size: ByteCount.gigabytes(60).bytes, category: "userLogs")
        let tree = NodeTree.merge([group("G", [huge, hugeLog])])
        let byPath = Dictionary(uniqueKeysWithValues: tree.allLeaves.map { ($0.url!.lastPathComponent, $0) })
        #expect(byPath["huge"]?.safety == .review)
        #expect(byPath["huge"]?.badges.contains("Lớn bất thường") == true)
        #expect(byPath["hugeLog"]?.safety == .safe)
    }

    @Test func indexAndAggregates() {
        let a = leaf("/a", size: 10)
        let b = leaf("/b", size: 20)
        let inner = group("Inner", [b])
        let root = group("Root", [a, inner])
        let tree = NodeTree(roots: [root])
        #expect(tree.totalSize == 30)
        #expect(tree.leaves(under: root.id).count == 2)
        #expect(tree.ancestors(of: b.id) == [inner.id, root.id])
        #expect(tree.node(inner.id)?.size == 20)
        #expect(tree.size { $0.size > 15 } == 20)
    }
}

@Suite struct SelectionStateTests {
    let a = leaf("/a", size: 10, safety: .safe)
    let b = leaf("/b", size: 20, safety: .review)
    let c = leaf("/c", size: 40, safety: .risky)
    let d = leaf("/d", size: 80, safety: .safe)
    var inner: Node { group("Inner", [b, c]) }
    var tree: NodeTree { NodeTree(roots: [group("Root", [a, inner, d])]) }

    @Test func defaultsSelectOnlySafe() {
        let t = tree
        let s = SelectionState.defaults(for: t)
        #expect(s.isSelected(a.id) && s.isSelected(d.id))
        #expect(!s.isSelected(b.id) && !s.isSelected(c.id))
        #expect(s.selectedBytes == 90)
        #expect(s.selectedCount == 2)
        #expect(s.state(of: t.roots[0].id, in: t) == .mixed)
        let innerID = t.roots[0].children[1].id
        #expect(s.state(of: innerID, in: t) == .off)
    }

    @Test func defaultsWithPredicate() {
        let s = SelectionState.defaults(for: tree) { $0.size > 50 }
        #expect(s.selectedBytes == 80)
    }

    @Test func incrementalTotalsAndMixedState() {
        let t = tree
        let rootID = t.roots[0].id
        let innerID = t.roots[0].children[1].id
        var s = SelectionState()
        s.set(innerID, true, in: t)
        #expect(s.selectedBytes == 60)
        #expect(s.state(of: innerID, in: t) == .on)
        #expect(s.state(of: rootID, in: t) == .mixed)
        s.set(b.id, false, in: t)
        #expect(s.selectedBytes == 40)
        #expect(s.state(of: innerID, in: t) == .mixed)
        // Chọn lại cùng giá trị không cộng hai lần.
        s.set(c.id, true, in: t)
        #expect(s.selectedBytes == 40)
        #expect(s.selectedCount == 1)
    }

    @Test func toggle() {
        let t = tree
        let rootID = t.roots[0].id
        var s = SelectionState.defaults(for: t)
        s.toggle(rootID, in: t)   // mixed → chọn hết
        #expect(s.state(of: rootID, in: t) == .on)
        #expect(s.selectedBytes == 150)
        s.toggle(rootID, in: t)   // on → bỏ hết
        #expect(s.state(of: rootID, in: t) == .off)
        #expect(s.selectedBytes == .zero)
        #expect(s.selectedCount == 0)
    }

    @Test func selectAllAndClear() {
        let t = tree
        var s = SelectionState()
        s.selectAll(in: t)
        #expect(s.selectedLeaves(in: t).count == 4)
        #expect(s.state(of: a.id, in: t) == .on)
        s.clear()
        #expect(s.selectedIDs.isEmpty)
        #expect(s.selectedBytes == .zero)
    }
}
