import Foundation
import GRDB

/// Bảng `scan_session` (mục 12.2).
public struct ScanSessionRecord: Codable, Sendable, FetchableRecord, PersistableRecord, Identifiable, Equatable {
    public static let databaseTableName = "scan_session"
    public static func databaseDateEncodingStrategy(for column: String) -> DatabaseDateEncodingStrategy { .timeIntervalSince1970 }
    public static func databaseDateDecodingStrategy(for column: String) -> DatabaseDateDecodingStrategy { .timeIntervalSince1970 }

    public var id: String
    public var kind: String           // smartScan | systemJunk | uninstaller ...
    public var startedAt: Date
    public var finishedAt: Date?
    public var status: String         // running | succeeded | failed | cancelled
    public var foundBytes: Int64
    public var rulesVersion: Int64

    public init(id: String = UUID().uuidString, kind: String, startedAt: Date = Date(), finishedAt: Date? = nil, status: String = "running", foundBytes: Int64 = 0, rulesVersion: Int64) {
        self.id = id
        self.kind = kind
        self.startedAt = startedAt
        self.finishedAt = finishedAt
        self.status = status
        self.foundBytes = foundBytes
        self.rulesVersion = rulesVersion
    }

    enum CodingKeys: String, CodingKey {
        case id, kind, status
        case startedAt = "started_at", finishedAt = "finished_at", foundBytes = "found_bytes", rulesVersion = "rules_version"
    }
}

/// Bảng `clean_operation`.
public struct CleanOperation: Codable, Sendable, FetchableRecord, PersistableRecord, Identifiable, Equatable {
    public static let databaseTableName = "clean_operation"
    public static func databaseDateEncodingStrategy(for column: String) -> DatabaseDateEncodingStrategy { .timeIntervalSince1970 }
    public static func databaseDateDecodingStrategy(for column: String) -> DatabaseDateDecodingStrategy { .timeIntervalSince1970 }

    public var id: String
    public var sessionId: String?
    public var startedAt: Date
    public var finishedAt: Date?
    public var plannedBytes: Int64
    public var freedBytes: Int64?      // đo thực tế từ dung lượng ổ
    public var itemsOk: Int
    public var itemsFailed: Int

    public init(id: String = UUID().uuidString, sessionId: String?, startedAt: Date = Date(), finishedAt: Date? = nil,
                plannedBytes: Int64, freedBytes: Int64? = nil, itemsOk: Int = 0, itemsFailed: Int = 0) {
        self.id = id
        self.sessionId = sessionId
        self.startedAt = startedAt
        self.finishedAt = finishedAt
        self.plannedBytes = plannedBytes
        self.freedBytes = freedBytes
        self.itemsOk = itemsOk
        self.itemsFailed = itemsFailed
    }

    enum CodingKeys: String, CodingKey {
        case id
        case sessionId = "session_id", startedAt = "started_at", finishedAt = "finished_at", plannedBytes = "planned_bytes"
        case freedBytes = "freed_bytes", itemsOk = "items_ok", itemsFailed = "items_failed"
    }
}

/// Bảng `clean_item_log`.
public struct CleanItemLog: Codable, Sendable, FetchableRecord, PersistableRecord, Hashable {
    public static let databaseTableName = "clean_item_log"
    public static func databaseDateEncodingStrategy(for column: String) -> DatabaseDateEncodingStrategy { .timeIntervalSince1970 }
    public static func databaseDateDecodingStrategy(for column: String) -> DatabaseDateDecodingStrategy { .timeIntervalSince1970 }

    public var operationId: String
    public var path: String
    public var ruleId: String?
    public var strategy: String        // trash | delete | deleteContents | custom
    public var trashedPath: String?    // đường dẫn trong Thùng rác, để khôi phục
    public var size: Int64
    public var result: String          // ok | skipped | failed:<code>

    public init(operationId: String, path: String, ruleId: String?, strategy: String, trashedPath: String?, size: Int64, result: String) {
        self.operationId = operationId
        self.path = path
        self.ruleId = ruleId
        self.strategy = strategy
        self.trashedPath = trashedPath
        self.size = size
        self.result = result
    }

    enum CodingKeys: String, CodingKey {
        case path, strategy, size, result
        case operationId = "operation_id", ruleId = "rule_id", trashedPath = "trashed_path"
    }

    public var isRestorable: Bool { trashedPath != nil && result == "ok" }
}

/// Bảng `ignore_entry`: người dùng chọn "không bao giờ đề xuất mục này".
public struct IgnoreEntry: Codable, Sendable, FetchableRecord, MutablePersistableRecord, Identifiable, Hashable {
    public static let databaseTableName = "ignore_entry"
    public static func databaseDateEncodingStrategy(for column: String) -> DatabaseDateEncodingStrategy { .timeIntervalSince1970 }
    public static func databaseDateDecodingStrategy(for column: String) -> DatabaseDateDecodingStrategy { .timeIntervalSince1970 }

    public enum Kind: String, Codable, Sendable, CaseIterable {
        case path, rule, bundleID
    }

    public var id: Int64?
    public var kind: Kind
    public var value: String
    public var createdAt: Date

    public init(id: Int64? = nil, kind: Kind, value: String, createdAt: Date = Date()) {
        self.id = id
        self.kind = kind
        self.value = value
        self.createdAt = createdAt
    }

    enum CodingKeys: String, CodingKey {
        case id, kind, value
        case createdAt = "created_at"
    }

    public mutating func didInsert(_ inserted: InsertionSuccess) { id = inserted.rowID }
}

/// Bảng `maintenance_run`.
public struct MaintenanceRun: Codable, Sendable, FetchableRecord, PersistableRecord, Hashable {
    public static let databaseTableName = "maintenance_run"
    public static func databaseDateEncodingStrategy(for column: String) -> DatabaseDateEncodingStrategy { .timeIntervalSince1970 }
    public static func databaseDateDecodingStrategy(for column: String) -> DatabaseDateDecodingStrategy { .timeIntervalSince1970 }

    public var task: String
    public var ranAt: Date
    public var result: String

    public init(task: String, ranAt: Date = Date(), result: String) {
        self.task = task
        self.ranAt = ranAt
        self.result = result
    }

    enum CodingKeys: String, CodingKey {
        case task, result
        case ranAt = "ran_at"
    }
}

/// Bảng `app_usage_cache`: cache thông tin app để quét lần sau nhanh hơn.
public struct AppUsageCache: Codable, Sendable, FetchableRecord, PersistableRecord, Hashable {
    public static let databaseTableName = "app_usage_cache"
    public static func databaseDateEncodingStrategy(for column: String) -> DatabaseDateEncodingStrategy { .timeIntervalSince1970 }
    public static func databaseDateDecodingStrategy(for column: String) -> DatabaseDateDecodingStrategy { .timeIntervalSince1970 }

    public var bundleId: String
    public var path: String
    public var teamId: String?
    public var version: String?
    public var size: Int64?
    public var lastUsedAt: Date?
    public var updatedAt: Date
    /// `modificationDate` của bundle lúc ghi cache; chỉ đọc lại app khi giá trị này đổi (mục 16.3).
    public var bundleModifiedAt: Date?
    public var name: String?
    public var isAppStore: Bool?

    public init(bundleId: String, path: String, teamId: String?, version: String?, size: Int64?, lastUsedAt: Date?, updatedAt: Date = Date(),
                bundleModifiedAt: Date?, name: String?, isAppStore: Bool?) {
        self.bundleId = bundleId
        self.path = path
        self.teamId = teamId
        self.version = version
        self.size = size
        self.lastUsedAt = lastUsedAt
        self.updatedAt = updatedAt
        self.bundleModifiedAt = bundleModifiedAt
        self.name = name
        self.isAppStore = isAppStore
    }

    enum CodingKeys: String, CodingKey {
        case path, version, size, name
        case bundleId = "bundle_id", teamId = "team_id", lastUsedAt = "last_used_at", updatedAt = "updated_at"
        case bundleModifiedAt = "bundle_modified_at", isAppStore = "is_app_store"
    }
}

extension CleanItemLog: Identifiable {
    public var id: String { operationId + "\u{0}" + path }
}
