import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import SweepCore

public struct ScanTaskID: Hashable, Sendable, RawRepresentable, ExpressibleByStringLiteral, CustomStringConvertible, Comparable {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
    public init(_ rawValue: String) { self.rawValue = rawValue }
    public init(stringLiteral value: String) { rawValue = value }
    public var description: String { rawValue }
    public static func < (l: ScanTaskID, r: ScanTaskID) -> Bool { l.rawValue < r.rawValue }
}

public enum ScanPriority: Int, Sendable, Comparable {
    case low = 0, normal = 1, high = 2
    public static func < (l: ScanPriority, r: ScanPriority) -> Bool { l.rawValue < r.rawValue }
}

/// Một đơn vị công việc quét (mục 5.1, 5.2).
public protocol ScanTask: Sendable {
    var id: ScanTaskID { get }
    var dependencies: [ScanTaskID] { get }
    var priority: ScanPriority { get }
    /// Trọng số dùng để chia thanh tiến độ tổng (ước lượng, không cần chính xác).
    var estimatedWeight: Double { get }
    /// Tên hiển thị khi task đang chạy.
    var title: String { get }

    func run(context: ScanContext) async throws -> ScanOutput
}

extension ScanTask {
    public var dependencies: [ScanTaskID] { [] }
    public var priority: ScanPriority { .normal }
    public var estimatedWeight: Double { 1 }
    public var title: String { id.rawValue }
}

/// Khoá dữ liệu trung gian giữa các task (ví dụ danh sách app đã cài).
public struct ArtifactKey: Hashable, Sendable, RawRepresentable, ExpressibleByStringLiteral {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
    public init(stringLiteral value: String) { rawValue = value }
}

public struct ScanWarning: Sendable, Hashable, Identifiable {
    public enum Kind: Sendable, Hashable {
        case needsFullDiskAccess
        case timeout
        case failed
        case skipped
        case info
    }

    public let id = UUID()
    public var taskID: ScanTaskID
    public var kind: Kind
    public var message: String

    public init(taskID: ScanTaskID, kind: Kind, message: String) {
        self.taskID = taskID
        self.kind = kind
        self.message = message
    }
}

/// Kết quả của một task (mục 5.2).
public struct ScanOutput: Sendable {
    public var nodes: [Node]
    public var artifacts: [ArtifactKey: any Sendable]
    public var warnings: [ScanWarning]

    public init(nodes: [Node] = [], artifacts: [ArtifactKey: any Sendable] = [:], warnings: [ScanWarning] = []) {
        self.nodes = nodes
        self.artifacts = artifacts
        self.warnings = warnings
    }

    public static let empty = ScanOutput()

    public func artifact<T>(_ key: ArtifactKey, as _: T.Type = T.self) -> T? { artifacts[key] as? T }
}

/// Môi trường dùng chung cho mọi task trong một phiên quét.
public struct ScanEnvironment: Sendable {
    public var policy: PathPolicy
    public var ignore: IgnoreList
    public var runningBundleIDs: Set<String>
    public var hasFullDiskAccess: Bool
    public var showRiskyItems: Bool
    public var now: Date
    /// Dùng chung trong phiên để không đếm hard link hai lần (mục 6.4).
    public var tracker: HardLinkTracker

    public init(policy: PathPolicy = .user(), ignore: IgnoreList = .empty, runningBundleIDs: Set<String> = [], hasFullDiskAccess: Bool = true,
                showRiskyItems: Bool = false, now: Date = Date(), tracker: HardLinkTracker = HardLinkTracker()) {
        self.policy = policy
        self.ignore = ignore
        self.runningBundleIDs = runningBundleIDs
        self.hasFullDiskAccess = hasFullDiskAccess
        self.showRiskyItems = showRiskyItems
        self.now = now
        self.tracker = tracker
    }
}

/// Truyền vào task: dịch vụ hệ thống tệp, rule, kết quả task phụ thuộc, reporter tiến độ (mục 5.1).
public struct ScanContext: Sendable {
    public let fileSystem: FileSystemService
    public let rules: RuleSnapshot
    public let progress: ProgressReporter
    /// Kết quả của các task phụ thuộc, tra theo id.
    public let upstream: [ScanTaskID: ScanOutput]
    public let environment: ScanEnvironment

    public init(fileSystem: FileSystemService, rules: RuleSnapshot, progress: ProgressReporter, upstream: [ScanTaskID: ScanOutput], environment: ScanEnvironment) {
        self.fileSystem = fileSystem
        self.rules = rules
        self.progress = progress
        self.upstream = upstream
        self.environment = environment
    }

    /// Tìm một artifact trong kết quả các task phụ thuộc.
    public func upstreamArtifact<T>(_ key: ArtifactKey, as _: T.Type = T.self) -> T? {
        for output in upstream.values {
            if let v = output.artifact(key, as: T.self) { return v }
        }
        return nil
    }

    /// Ngữ cảnh đánh giá rule, dùng chung bộ đếm file và tracker hard link của phiên.
    public func ruleContext(apps: [RuleAppInfo] = []) -> RuleEvaluationContext {
        RuleEvaluationContext(fileSystem: fileSystem, policy: environment.policy, apps: apps, runningBundleIDs: environment.runningBundleIDs,
                              ignore: environment.ignore, now: environment.now, tracker: environment.tracker, counter: progress.fileCounter)
    }
}
