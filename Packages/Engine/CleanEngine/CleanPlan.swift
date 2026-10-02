import Foundation
import NodeTree
import SweepCore

/// Kế hoạch dọn (mục 7.2). Lập kế hoạch tách khỏi thực thi để hiển thị chính xác những gì sắp xảy ra,
/// kiểm tra lại PathPolicy ngay trước khi xoá và ghi kế hoạch vào database.
public struct CleanPlan: Sendable {
    public struct Item: Sendable, Identifiable, Hashable {
        public var id: NodeID { nodeID }
        public let nodeID: NodeID
        public let url: URL?
        public let title: String
        public let strategy: RemovalStrategy
        public let requiresRoot: Bool
        public let expectedSize: ByteCount
        public let ruleID: RuleID?
        public let category: String?
        public let safety: SafetyLevel
        public let allowedRoots: [String]
        public let virtual: VirtualItem?

        public init(nodeID: NodeID, url: URL?, title: String, strategy: RemovalStrategy, requiresRoot: Bool, expectedSize: ByteCount,
                    ruleID: RuleID? = nil, category: String? = nil, safety: SafetyLevel = .safe, allowedRoots: [String] = [], virtual: VirtualItem? = nil) {
            self.nodeID = nodeID
            self.url = url
            self.title = title
            self.strategy = strategy
            self.requiresRoot = requiresRoot
            self.expectedSize = expectedSize
            self.ruleID = ruleID
            self.category = category
            self.safety = safety
            self.allowedRoots = allowedRoots
            self.virtual = virtual
        }

        /// Remover chịu trách nhiệm cho mục này.
        public var removerID: RemoverID {
            if case let .custom(id) = strategy { return id }
            return requiresRoot ? .privilegedFile : .file
        }
    }

    public struct Blocked: Sendable, Hashable, Identifiable {
        public var id: String { url.path }
        public let url: URL
        public let violation: PathPolicy.Violation
        public let title: String
    }

    public var id = UUID()
    public var items: [Item]
    public var blocked: [Blocked]
    public var totalSize: ByteCount
    public var requiresConfirmation: Bool
    public var sessionID: String?

    public init(items: [Item], blocked: [Blocked], sessionID: String? = nil) {
        self.items = items
        self.blocked = blocked
        self.sessionID = sessionID
        totalSize = items.sum(\.expectedSize)
        // Hộp xác nhận nếu có mục review/risky hoặc mục cần root (mục 7.1, 15.2).
        requiresConfirmation = items.contains { $0.safety != .safe || $0.requiresRoot }
    }

    public var rootItems: [Item] { items.filter(\.requiresRoot) }
    public var reviewItems: [Item] { items.filter { $0.safety != .safe } }
    public var permanentItems: [Item] { items.filter { !$0.strategy.isRecoverable } }
    public var isEmpty: Bool { items.isEmpty }
}

/// Báo cáo sau khi dọn.
public struct CleanReport: Sendable {
    public struct Entry: Sendable, Identifiable, Hashable {
        public var id: String { result.path + item.nodeID.description }
        public let item: CleanPlan.Item
        public let result: RemoveResult
    }

    public let operationID: String
    public let entries: [Entry]
    /// Tổng ước tính (cộng từ kết quả xoá).
    public let estimatedFreed: ByteCount
    /// Đo thực tế từ dung lượng trống của ổ trước/sau (mục 7.5). Có thể lệch do snapshot APFS hoặc file clone.
    public let measuredFreed: ByteCount?
    public let duration: TimeInterval
    public let dryRun: Bool

    public var succeeded: [Entry] { entries.filter { $0.result.isSuccess } }
    public var failed: [Entry] { entries.filter { if case .failed = $0.result.outcome { return true } else { return false } } }
    public var skipped: [Entry] {
        entries.filter {
            if case let .skipped(reason) = $0.result.outcome { return reason != .notFound }
            return false
        }
    }
    public var inUse: [Entry] { entries.filter { $0.result.outcome == .skipped(reason: .inUse) || $0.result.outcome == .skipped(reason: .appRunning) } }
}

public enum CleanEvent: Sendable {
    case progress(fraction: Double, currentItem: String, freedSoFar: ByteCount)
    case finished(CleanReport)
}
