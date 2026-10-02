import AppKit
import DesignSystem
import NodeTree
import SweepCore
import SwiftUI

/// Cây kết quả lớn dùng `NSOutlineView` bọc trong `NSViewRepresentable` (mục 16.3, 22.3):
/// `List` của SwiftUI chậm khi có trên 10.000 dòng; outline view chỉ tạo view cho dòng đang hiển thị.
public struct NodeOutlineView: NSViewRepresentable {
    let roots: [Node]
    let tree: NodeTree
    @Binding var selection: SelectionState
    var onIgnore: ((Node) -> Void)?

    public init(roots: [Node], tree: NodeTree, selection: Binding<SelectionState>, onIgnore: ((Node) -> Void)? = nil) {
        self.roots = roots
        self.tree = tree
        _selection = selection
        self.onIgnore = onIgnore
    }

    public func makeNSView(context: Context) -> NSScrollView {
        let outline = NSOutlineView()
        outline.headerView = nil
        outline.rowHeight = 26
        outline.usesAlternatingRowBackgroundColors = false
        outline.backgroundColor = .clear
        outline.style = .plain
        outline.selectionHighlightStyle = .regular
        outline.indentationPerLevel = 16
        outline.autoresizesOutlineColumn = false

        let name = NSTableColumn(identifier: .init("name"))
        name.resizingMask = .autoresizingMask
        let size = NSTableColumn(identifier: .init("size"))
        size.width = 90
        size.minWidth = 80
        size.maxWidth = 120
        outline.addTableColumn(name)
        outline.addTableColumn(size)
        outline.outlineTableColumn = name
        outline.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle

        outline.dataSource = context.coordinator
        outline.delegate = context.coordinator
        outline.target = context.coordinator
        outline.doubleAction = #selector(Coordinator.doubleClicked(_:))
        outline.menu = context.coordinator.makeMenu()
        context.coordinator.outline = outline

        let scroll = NSScrollView()
        scroll.documentView = outline
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false
        scroll.autohidesScrollers = true
        return scroll
    }

    public func updateNSView(_ view: NSScrollView, context: Context) {
        let coordinator = context.coordinator
        let rootsChanged = coordinator.rootIDs != roots.map(\.id)
        coordinator.tree = tree
        coordinator.roots = roots
        coordinator.selection = $selection
        coordinator.onIgnore = onIgnore
        guard let outline = view.documentView as? NSOutlineView else { return }
        if rootsChanged {
            coordinator.rootIDs = roots.map(\.id)
            coordinator.boxes.removeAll()
            outline.reloadData()
            if roots.count == 1 { outline.expandItem(coordinator.box(roots[0].id)) }
        } else {
            // Chỉ cập nhật checkbox của dòng đang hiển thị.
            let visible = outline.rows(in: outline.visibleRect)
            for row in visible.lowerBound..<visible.upperBound {
                if let cell = outline.view(atColumn: 0, row: row, makeIfNecessary: false) as? NodeCellView,
                   let box = outline.item(atRow: row) as? NodeBox {
                    cell.check.state = coordinator.checkState(box.id)
                }
            }
        }
    }

    public func makeCoordinator() -> Coordinator { Coordinator(tree: tree, roots: roots, selection: $selection) }

    final class NodeBox: NSObject {
        let id: NodeID
        init(_ id: NodeID) { self.id = id }
    }

    @MainActor
    public final class Coordinator: NSObject, NSOutlineViewDataSource, NSOutlineViewDelegate, NSMenuDelegate {
        var tree: NodeTree
        var roots: [Node]
        var rootIDs: [NodeID] = []
        var selection: Binding<SelectionState>
        var onIgnore: ((Node) -> Void)?
        weak var outline: NSOutlineView?
        var boxes: [NodeID: NodeBox] = [:]

        init(tree: NodeTree, roots: [Node], selection: Binding<SelectionState>) {
            self.tree = tree
            self.roots = roots
            self.selection = selection
            rootIDs = roots.map(\.id)
        }

        func box(_ id: NodeID) -> NodeBox {
            if let b = boxes[id] { return b }
            let b = NodeBox(id)
            boxes[id] = b
            return b
        }

        private func node(_ item: Any?) -> Node? {
            guard let b = item as? NodeBox else { return nil }
            return tree.node(b.id)
        }

        func checkState(_ id: NodeID) -> NSControl.StateValue {
            switch selection.wrappedValue.state(of: id, in: tree) {
            case .on: .on
            case .off: .off
            case .mixed: .mixed
            }
        }

        // MARK: Data source

        public func outlineView(_ outlineView: NSOutlineView, numberOfChildrenOfItem item: Any?) -> Int {
            if item == nil { return roots.count }
            return node(item)?.children.count ?? 0
        }

        public func outlineView(_ outlineView: NSOutlineView, child index: Int, ofItem item: Any?) -> Any {
            if item == nil { return box(roots[index].id) }
            return box(node(item)!.children[index].id)
        }

        public func outlineView(_ outlineView: NSOutlineView, isItemExpandable item: Any) -> Bool {
            !(node(item)?.children.isEmpty ?? true)
        }

        // MARK: Delegate

        public func outlineView(_ outlineView: NSOutlineView, viewFor tableColumn: NSTableColumn?, item: Any) -> NSView? {
            guard let n = node(item), let column = tableColumn else { return nil }
            if column.identifier.rawValue == "size" {
                let id = NSUserInterfaceItemIdentifier("sizeCell")
                let field = (outlineView.makeView(withIdentifier: id, owner: nil) as? NSTextField) ?? {
                    let f = NSTextField(labelWithString: "")
                    f.identifier = id
                    f.alignment = .right
                    f.font = .monospacedDigitSystemFont(ofSize: 12, weight: .medium)
                    f.textColor = .white
                    return f
                }()
                field.stringValue = n.size.formatted
                return field
            }
            let id = NSUserInterfaceItemIdentifier("nodeCell")
            let cell = (outlineView.makeView(withIdentifier: id, owner: nil) as? NodeCellView) ?? NodeCellView(identifier: id)
            cell.configure(node: n, state: checkState(n.id)) { [weak self] in self?.toggle(n.id) }
            return cell
        }

        func toggle(_ id: NodeID) {
            var s = selection.wrappedValue
            s.toggle(id, in: tree)
            selection.wrappedValue = s
            refreshVisibleChecks()
        }

        func refreshVisibleChecks() {
            guard let outline else { return }
            let visible = outline.rows(in: outline.visibleRect)
            for row in visible.lowerBound..<visible.upperBound {
                if let cell = outline.view(atColumn: 0, row: row, makeIfNecessary: false) as? NodeCellView,
                   let b = outline.item(atRow: row) as? NodeBox {
                    cell.check.state = checkState(b.id)
                }
            }
        }

        @objc func doubleClicked(_ sender: NSOutlineView) {
            guard let n = node(sender.item(atRow: sender.clickedRow)), let url = n.url else { return }
            NSWorkspace.shared.activateFileViewerSelecting([url])
        }

        // MARK: Menu chuột phải

        func makeMenu() -> NSMenu {
            let menu = NSMenu()
            menu.delegate = self
            return menu
        }

        public func menuNeedsUpdate(_ menu: NSMenu) {
            menu.removeAllItems()
            guard let outline, outline.clickedRow >= 0, let n = node(outline.item(atRow: outline.clickedRow)) else { return }
            if let url = n.url {
                let reveal = NSMenuItem(title: String(localized: "Hiện trong Finder"), action: #selector(reveal(_:)), keyEquivalent: "")
                reveal.target = self
                reveal.representedObject = url
                menu.addItem(reveal)
                let copy = NSMenuItem(title: String(localized: "Sao chép đường dẫn"), action: #selector(copyPath(_:)), keyEquivalent: "")
                copy.target = self
                copy.representedObject = url
                menu.addItem(copy)
            }
            if onIgnore != nil, n.url != nil {
                menu.addItem(.separator())
                let ignore = NSMenuItem(title: String(localized: "Không bao giờ đề xuất mục này"), action: #selector(ignore(_:)), keyEquivalent: "")
                ignore.target = self
                ignore.representedObject = box(n.id)
                menu.addItem(ignore)
            }
        }

        @objc func reveal(_ sender: NSMenuItem) {
            guard let url = sender.representedObject as? URL else { return }
            NSWorkspace.shared.activateFileViewerSelecting([url])
        }

        @objc func copyPath(_ sender: NSMenuItem) {
            guard let url = sender.representedObject as? URL else { return }
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(url.path, forType: .string)
        }

        @objc func ignore(_ sender: NSMenuItem) {
            guard let b = sender.representedObject as? NodeBox, let n = tree.node(b.id) else { return }
            onIgnore?(n)
            refreshVisibleChecks()
        }
    }
}

/// Dòng: checkbox, icon, tên, lý do, nhãn an toàn.
final class NodeCellView: NSTableCellView {
    let check = NSButton(checkboxWithTitle: "", target: nil, action: nil)
    let icon = NSImageView()
    let title = NSTextField(labelWithString: "")
    let detail = NSTextField(labelWithString: "")
    let badge = NSTextField(labelWithString: "")
    private var onToggle: (() -> Void)?

    init(identifier: NSUserInterfaceItemIdentifier) {
        super.init(frame: .zero)
        self.identifier = identifier
        check.allowsMixedState = true
        check.target = self
        check.action = #selector(toggled)
        title.textColor = .white
        title.font = .systemFont(ofSize: 13)
        title.lineBreakMode = .byTruncatingMiddle
        detail.textColor = NSColor.white.withAlphaComponent(0.55)
        detail.font = .systemFont(ofSize: 11)
        detail.lineBreakMode = .byTruncatingTail
        badge.font = .systemFont(ofSize: 10, weight: .semibold)
        icon.imageScaling = .scaleProportionallyUpOrDown

        let stack = NSStackView(views: [check, icon, title, detail, badge])
        stack.orientation = .horizontal
        stack.spacing = 6
        stack.alignment = .centerY
        stack.translatesAutoresizingMaskIntoConstraints = false
        title.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        detail.setContentCompressionResistancePriority(.init(100), for: .horizontal)
        addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 2),
            stack.trailingAnchor.constraint(lessThanOrEqualTo: trailingAnchor, constant: -4),
            stack.centerYAnchor.constraint(equalTo: centerYAnchor),
            icon.widthAnchor.constraint(equalToConstant: 18),
            icon.heightAnchor.constraint(equalToConstant: 18),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    func configure(node: Node, state: NSControl.StateValue, onToggle: @escaping () -> Void) {
        self.onToggle = onToggle
        check.state = state
        title.stringValue = node.title
        let reason = node.reason.resolved
        detail.stringValue = node.badges.isEmpty ? reason : node.badges.joined(separator: " · ")
        toolTip = [node.url?.path, reason.isEmpty ? nil : reason, node.ruleID.map { "rule: \($0.rawValue)" }].compactMap { $0 }.joined(separator: "\n")
        if let url = node.url, FileManager.default.fileExists(atPath: url.path) {
            icon.image = NSWorkspace.shared.icon(forFile: url.path)
        } else {
            icon.image = NSImage(systemSymbolName: node.icon ?? (node.isContainer ? "folder" : "doc"), accessibilityDescription: nil)
            icon.contentTintColor = .white
        }
        if node.safety == .safe || node.isContainer {
            badge.isHidden = true
        } else {
            badge.isHidden = false
            badge.stringValue = node.safety.localizedTitle
            badge.textColor = node.safety == .review ? NSColor.systemYellow : NSColor.systemRed
        }
    }

    @objc private func toggled() { onToggle?() }
}
