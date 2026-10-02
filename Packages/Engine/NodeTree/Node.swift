import Foundation
import SweepCore

public struct NodeID: Hashable, Sendable, Codable, CustomStringConvertible {
    public let rawValue: UUID
    public init() { rawValue = UUID() }
    public init(rawValue: UUID) { self.rawValue = rawValue }
    public var description: String { rawValue.uuidString }
}

/// Mục ảo, không phải file thông thường (mục 6.1): simulator, snapshot, login item...
public enum VirtualItem: Sendable, Hashable {
    case simulatorDevice(udid: String, name: String, runtime: String, dataPath: String?)
    case simulatorRuntime(identifier: String, name: String, path: String?)
    case localSnapshot(volume: String, date: String)
    case launchItem(label: String, plistPath: String, domain: LaunchDomain)
    case maintenance(task: MaintenanceTaskName)
    case dockerPrune
    case packageReceipt(id: String)

    public enum LaunchDomain: String, Sendable, Hashable, Codable {
        case userAgent        // ~/Library/LaunchAgents
        case globalAgent      // /Library/LaunchAgents
        case globalDaemon     // /Library/LaunchDaemons
    }

    /// Đường dẫn file gắn với mục ảo (nếu có), để gộp trùng và hiển thị.
    public var associatedPath: String? {
        switch self {
        case let .simulatorDevice(_, _, _, path): path
        case let .simulatorRuntime(_, _, path): path
        case let .launchItem(_, plist, _): plist
        default: nil
        }
    }
}

public enum NodeKind: Sendable, Hashable {
    case group(title: LocalizedText, icon: String)        // "Cache người dùng"
    case application(bundleID: String, url: URL)           // nhóm theo app
    case file(URL)
    case directory(URL, recursive: Bool)
    case virtual(VirtualItem)                              // simulator, snapshot, login item...

    /// Node chứa (group/application) chỉ để gom nhóm; node còn lại là mục xoá được.
    public var isContainer: Bool {
        switch self {
        case .group, .application: true
        default: false
        }
    }

    public var url: URL? {
        switch self {
        case let .file(u): u
        case let .directory(u, _): u
        case let .application(_, u): u
        case let .virtual(v): v.associatedPath.map { URL(fileURLWithPath: $0) }
        case .group: nil
        }
    }
}

/// Một node trong cây kết quả (mục 6.1). Cây bất biến sau khi dựng; trạng thái chọn nằm ngoài (`SelectionState`).
public struct Node: Sendable, Identifiable {
    public let id: NodeID
    public let kind: NodeKind
    public var title: String
    public var size: ByteCount              // dung lượng thực chiếm (allocated)
    public var itemCount: Int
    public var safety: SafetyLevel
    public var reason: LocalizedText        // vì sao đề xuất xoá
    public var ruleID: RuleID?              // rule nào tạo ra node này
    public var category: String?            // nhóm rule (userCaches, logs, appLeftovers...), dùng khi gộp trùng
    public var removal: RemovalStrategy     // cách xoá (mục 7.3)
    public var requiresRoot: Bool
    /// Vùng rule cho phép: sau khi chuẩn hoá, đường dẫn phải vẫn nằm trong đây (mục 15.1).
    public var allowedRoots: [String]
    public var children: [Node]
    public var lastAccess: Date?
    /// Nhãn phụ hiển thị (ví dụ "đang chạy", "hỏng", "Cần Full Disk Access").
    public var badges: [String]
    public var icon: String?

    public init(
        id: NodeID = NodeID(),
        kind: NodeKind,
        title: String? = nil,
        size: ByteCount = .zero,
        itemCount: Int = 0,
        safety: SafetyLevel = .review,
        reason: LocalizedText = "",
        ruleID: RuleID? = nil,
        category: String? = nil,
        removal: RemovalStrategy = .moveToTrash,
        requiresRoot: Bool = false,
        allowedRoots: [String] = [],
        children: [Node] = [],
        lastAccess: Date? = nil,
        badges: [String] = [],
        icon: String? = nil
    ) {
        self.id = id
        self.kind = kind
        self.title = title ?? Node.defaultTitle(for: kind)
        self.size = size
        self.itemCount = itemCount
        self.safety = safety
        self.reason = reason
        self.ruleID = ruleID
        self.category = category
        self.removal = removal
        self.requiresRoot = requiresRoot
        self.allowedRoots = allowedRoots
        self.children = children
        self.lastAccess = lastAccess
        self.badges = badges
        self.icon = icon
    }

    public static func defaultTitle(for kind: NodeKind) -> String {
        switch kind {
        case let .group(title, _): title.resolved
        case let .application(bundleID, url): FileManager.default.displayName(atPath: url.path).nilIfEmpty ?? bundleID
        case let .file(u), let .directory(u, _): u.lastPathComponent
        case let .virtual(v):
            switch v {
            case let .simulatorDevice(_, name, runtime, _): "\(name) (\(runtime))"
            case let .simulatorRuntime(_, name, _): name
            case let .localSnapshot(_, date): "Snapshot \(date)"
            case let .launchItem(label, _, _): label
            case let .maintenance(task): task.title
            case .dockerPrune: "docker system prune"
            case let .packageReceipt(id): id
            }
        }
    }

    public static func group(_ title: LocalizedText, icon: String, category: String? = nil, children: [Node], safety: SafetyLevel? = nil) -> Node {
        var n = Node(kind: .group(title: title, icon: icon), category: category, children: children, icon: icon)
        n.safety = safety ?? children.map(\.safety).min() ?? .review
        n.recomputeAggregates()
        return n
    }

    public var isContainer: Bool { kind.isContainer }
    public var url: URL? { kind.url }

    /// Tổng dung lượng và số mục của node chứa = tổng của con (cộng từ lá lên gốc).
    public mutating func recomputeAggregates() {
        guard !children.isEmpty else { return }
        for i in children.indices { children[i].recomputeAggregates() }
        if isContainer {
            size = children.sum(\.size)
            itemCount = children.reduce(0) { $0 + max($1.itemCount, 1) }
            lastAccess = children.compactMap(\.lastAccess).max()
        }
    }

    /// Duyệt mọi node con cháu (kể cả chính nó), theo chiều sâu.
    public func forEach(_ body: (Node) -> Void) {
        body(self)
        for c in children { c.forEach(body) }
    }

    /// Các node xoá được (không phải container) bên dưới, kể cả chính nó.
    public var removableLeaves: [Node] {
        if !isContainer { return [self] }
        return children.flatMap(\.removableLeaves)
    }
}

extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
