import Foundation
import GRDB
import os
import SweepCore
import SweepLogging

/// Database SQLite dùng chung giữa app chính và app menu bar (mục 12).
/// Mở ở chế độ WAL (`DatabasePool`) để menu bar đọc được trong lúc app chính ghi.
public final class Storage: Sendable {
    public let pool: DatabasePool
    public let url: URL

    public init(url: URL, readOnly: Bool = false) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        var config = Configuration()
        config.readonly = readOnly
        config.foreignKeysEnabled = true
        config.busyMode = .timeout(5)
        config.label = "MashClean"
        pool = try DatabasePool(path: url.path, configuration: config)
        self.url = url
        if !readOnly { try Self.migrator.migrate(pool) }
    }

    /// Database trong bộ nhớ tạm, dùng cho test.
    public static func temporary() throws -> Storage {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("mashclean-db-\(UUID().uuidString)")
        return try Storage(url: dir.appendingPathComponent("test.sqlite"))
    }

    // MARK: Migration

    /// Mỗi migration có tên, không bao giờ sửa migration đã phát hành (mục 12.2).
    public static var migrator: DatabaseMigrator {
        var m = DatabaseMigrator()
        m.registerMigration("v1") { db in
            try db.execute(sql: """
            CREATE TABLE scan_session (
                id            TEXT PRIMARY KEY,
                kind          TEXT NOT NULL,
                started_at    REAL NOT NULL,
                finished_at   REAL,
                status        TEXT NOT NULL,
                found_bytes   INTEGER NOT NULL DEFAULT 0,
                rules_version INTEGER NOT NULL
            );

            CREATE TABLE clean_operation (
                id              TEXT PRIMARY KEY,
                session_id      TEXT REFERENCES scan_session(id),
                started_at      REAL NOT NULL,
                finished_at     REAL,
                planned_bytes   INTEGER NOT NULL,
                freed_bytes     INTEGER,
                items_ok        INTEGER NOT NULL DEFAULT 0,
                items_failed    INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE clean_item_log (
                operation_id  TEXT NOT NULL REFERENCES clean_operation(id) ON DELETE CASCADE,
                path          TEXT NOT NULL,
                rule_id       TEXT,
                strategy      TEXT NOT NULL,
                trashed_path  TEXT,
                size          INTEGER NOT NULL,
                result        TEXT NOT NULL,
                PRIMARY KEY (operation_id, path)
            );

            CREATE TABLE ignore_entry (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                kind       TEXT NOT NULL,
                value      TEXT NOT NULL,
                created_at REAL NOT NULL,
                UNIQUE (kind, value)
            );

            CREATE TABLE maintenance_run (
                task        TEXT NOT NULL,
                ran_at      REAL NOT NULL,
                result      TEXT NOT NULL,
                PRIMARY KEY (task, ran_at)
            );

            CREATE TABLE app_usage_cache (
                bundle_id     TEXT PRIMARY KEY,
                path          TEXT NOT NULL,
                team_id       TEXT,
                version       TEXT,
                size          INTEGER,
                last_used_at  REAL,
                updated_at    REAL NOT NULL
            );
            """)
        }
        m.registerMigration("v2-app-cache-detail") { db in
            try db.alter(table: "app_usage_cache") { t in
                t.add(column: "bundle_modified_at", .double)
                t.add(column: "name", .text)
                t.add(column: "is_app_store", .boolean)
            }
            try db.create(index: "clean_operation_started", on: "clean_operation", columns: ["started_at"])
            try db.create(index: "scan_session_started", on: "scan_session", columns: ["started_at"])
        }
        return m
    }

    // MARK: Vị trí

    /// `~/Library/Group Containers/group.com.mashclean/mashclean.sqlite` (mục 12.1).
    /// Nếu không truy cập được App Group container thì dùng `~/Library/Application Support/MashClean/`.
    public static var defaultURL: URL {
        // Bản ký ad-hoc không có entitlement App Group: macOS chặn ghi vào Group Containers, nên phải thử tạo được thư mục.
        if let container = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: MashCleanIdentifiers.appGroup),
           (try? FileManager.default.createDirectory(at: container, withIntermediateDirectories: true)) != nil,
           FileManager.default.isWritableFile(atPath: container.path) {
            return container.appendingPathComponent("mashclean.sqlite")
        }
        return URL.userHome.appendingPathComponent("Library/Application Support/MashClean/mashclean.sqlite")
    }

    // MARK: Dọn dữ liệu cũ

    /// `clean_item_log` giữ 90 ngày, `scan_session` giữ 1 năm; dọn khi app khởi động (mục 12.2).
    public func pruneOldData(now: Date = Date()) throws {
        let ninetyDays = now.addingTimeInterval(-90 * 86_400).timeIntervalSince1970
        let oneYear = now.addingTimeInterval(-365 * 86_400).timeIntervalSince1970
        try pool.write { db in
            try db.execute(sql: """
                DELETE FROM clean_item_log WHERE operation_id IN (SELECT id FROM clean_operation WHERE started_at < ?)
                """, arguments: [ninetyDays])
            try db.execute(sql: "UPDATE clean_operation SET session_id = NULL WHERE session_id IN (SELECT id FROM scan_session WHERE started_at < ?)", arguments: [oneYear])
            try db.execute(sql: "DELETE FROM scan_session WHERE started_at < ?", arguments: [oneYear])
            try db.execute(sql: "DELETE FROM clean_operation WHERE started_at < ?", arguments: [oneYear])
            try db.execute(sql: "DELETE FROM maintenance_run WHERE ran_at < ?", arguments: [oneYear])
        }
    }
}

// MARK: - Scan session

extension Storage {
    public func beginSession(kind: String, rulesVersion: UInt64) throws -> ScanSessionRecord {
        let record = ScanSessionRecord(kind: kind, rulesVersion: Int64(rulesVersion))
        try pool.write { try record.insert($0) }
        return record
    }

    public func finishSession(_ id: String, status: String, foundBytes: Int64) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE scan_session SET finished_at = ?, status = ?, found_bytes = ? WHERE id = ?",
                           arguments: [Date().timeIntervalSince1970, status, foundBytes, id])
        }
    }

    public func recentSessions(limit: Int = 50) throws -> [ScanSessionRecord] {
        try pool.read { try ScanSessionRecord.order(Column("started_at").desc).limit(limit).fetchAll($0) }
    }

    public func lastSession(kind: String) throws -> ScanSessionRecord? {
        try pool.read { try ScanSessionRecord.filter(Column("kind") == kind).order(Column("started_at").desc).fetchOne($0) }
    }
}

// MARK: - Clean log

extension Storage {
    public func insert(_ operation: CleanOperation) throws {
        try pool.write { try operation.insert($0) }
    }

    public func update(_ operation: CleanOperation) throws {
        try pool.write { try operation.update($0) }
    }

    public func log(_ items: [CleanItemLog]) throws {
        guard !items.isEmpty else { return }
        try pool.write { db in
            for item in items { try item.upsert(db) }
        }
    }

    public func operations(limit: Int = 100) throws -> [CleanOperation] {
        try pool.read { try CleanOperation.order(Column("started_at").desc).limit(limit).fetchAll($0) }
    }

    public func items(of operationID: String) throws -> [CleanItemLog] {
        try pool.read { try CleanItemLog.filter(Column("operation_id") == operationID).order(Column("size").desc).fetchAll($0) }
    }

    public func markRestored(operationID: String, path: String) throws {
        try pool.write { db in
            try db.execute(sql: "UPDATE clean_item_log SET result = 'restored', trashed_path = NULL WHERE operation_id = ? AND path = ?",
                           arguments: [operationID, path])
        }
    }

    /// Tổng dung lượng đã dọn từ đầu tháng (menu bar hiển thị "Đã dọn tháng này").
    public func freedThisMonth() throws -> Int64 {
        try pool.read { try Self.freedThisMonthQuery($0) }
    }

    static func freedThisMonthQuery(_ db: Database) throws -> Int64 {
        try Int64.fetchOne(db, sql: """
            SELECT COALESCE(SUM(COALESCE(freed_bytes, 0)), 0) FROM clean_operation
            WHERE started_at >= CAST(strftime('%s', 'now', 'start of month') AS REAL)
            """) ?? 0
    }

    /// UI tự cập nhật khi chính tiến trình này ghi. Tiến trình khác cần nghe thêm `DistributedNotificationCenter` (mục 22.3).
    public func observeFreedThisMonth() -> AsyncValueObservation<Int64> {
        ValueObservation.tracking { try Self.freedThisMonthQuery($0) }.values(in: pool)
    }

    public func totalFreed() throws -> Int64 {
        try pool.read { try Int64.fetchOne($0, sql: "SELECT COALESCE(SUM(COALESCE(freed_bytes, 0)), 0) FROM clean_operation") ?? 0 }
    }
}

// MARK: - Ignore list

extension Storage {
    public func addIgnore(kind: IgnoreEntry.Kind, value: String) throws {
        try pool.write { db in
            var entry = IgnoreEntry(kind: kind, value: value)
            try entry.insert(db, onConflict: .ignore)
        }
    }

    public func removeIgnore(id: Int64) throws {
        _ = try pool.write { try IgnoreEntry.deleteOne($0, key: id) }
    }

    public func ignoreEntries() throws -> [IgnoreEntry] {
        try pool.read { try IgnoreEntry.order(Column("created_at").desc).fetchAll($0) }
    }

    public func ignoreList() throws -> IgnoreList {
        let entries = try ignoreEntries()
        var list = IgnoreList()
        for e in entries {
            switch e.kind {
            case .path: list.paths.insert(e.value)
            case .rule: list.rules.insert(e.value)
            case .bundleID: list.bundleIDs.insert(e.value)
            }
        }
        return list
    }
}

// MARK: - Maintenance

extension Storage {
    public func recordMaintenance(task: String, result: String, at date: Date = Date()) throws {
        try pool.write { try MaintenanceRun(task: task, ranAt: date, result: result).insert($0) }
    }

    /// Lần chạy gần nhất của từng tác vụ.
    public func lastMaintenanceRuns() throws -> [String: MaintenanceRun] {
        try pool.read { db in
            let rows = try MaintenanceRun.fetchAll(db, sql: """
                SELECT m.* FROM maintenance_run m
                JOIN (SELECT task, MAX(ran_at) AS ran_at FROM maintenance_run GROUP BY task) last
                ON m.task = last.task AND m.ran_at = last.ran_at
                """)
            return Dictionary(rows.map { ($0.task, $0) }, uniquingKeysWith: { a, _ in a })
        }
    }
}

// MARK: - App cache

extension Storage {
    public func cachedApps() throws -> [String: AppUsageCache] {
        try pool.read { db in
            Dictionary(try AppUsageCache.fetchAll(db).map { ($0.path, $0) }, uniquingKeysWith: { a, _ in a })
        }
    }

    public func upsertApps(_ apps: [AppUsageCache]) throws {
        try pool.write { db in
            for a in apps { try a.upsert(db) }
        }
    }

    public func removeApp(bundleID: String) throws {
        _ = try pool.write { try AppUsageCache.deleteOne($0, key: ["bundle_id": bundleID]) }
    }

    public func removeApps(notIn paths: Set<String>) throws {
        try pool.write { db in
            let all = try AppUsageCache.fetchAll(db)
            for a in all where !paths.contains(a.path) { try a.delete(db) }
        }
    }
}
