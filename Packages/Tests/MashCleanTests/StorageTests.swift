import Foundation
import GRDB
import SweepCore
import SweepStorage
import Testing

/// Database: migration, giữ dữ liệu có thời hạn, ignore list, thống kê (mục 12.2).
@Suite struct StorageTests {
    final class TempStorage: @unchecked Sendable {
        let storage: Storage
        init() throws { storage = try Storage.temporary() }
        deinit { try? FileManager.default.removeItem(at: storage.url.deletingLastPathComponent()) }
    }

    @Test func migrationsAreApplied() throws {
        let t = try TempStorage()
        let applied = try t.storage.pool.read { try Storage.migrator.appliedIdentifiers($0) }
        #expect(applied == ["v1", "v2-app-cache-detail"])
        let columns = try t.storage.pool.read { try $0.columns(in: "app_usage_cache").map(\.name) }
        #expect(columns.contains("bundle_modified_at") && columns.contains("name") && columns.contains("is_app_store"))
        for table in ["scan_session", "clean_operation", "clean_item_log", "ignore_entry", "maintenance_run", "app_usage_cache"] {
            #expect(try t.storage.pool.read { try $0.tableExists(table) }, "\(table)")
        }
    }

    @Test func upgradeFromV1KeepsData() throws {
        let dir = try TemporaryDirectory("db")
        let url = dir.url("old.sqlite")
        do {
            let pool = try DatabasePool(path: url.path)
            try Storage.migrator.migrate(pool, upTo: "v1")
            try pool.write { db in
                try db.execute(sql: "INSERT INTO app_usage_cache (bundle_id, path, updated_at) VALUES ('com.x', '/Applications/X.app', 1)")
            }
            try pool.close()
        }
        let storage = try Storage(url: url)
        let apps = try storage.cachedApps()
        #expect(apps["/Applications/X.app"]?.bundleId == "com.x")
        #expect(try storage.pool.read { try Storage.migrator.hasCompletedMigrations($0) })
    }

    @Test func retentionPrunesOldData() throws {
        let t = try TempStorage()
        let s = t.storage
        let now = Date()
        let day: TimeInterval = 86_400
        let oldSession = ScanSessionRecord(kind: "smartScan", startedAt: now - 400 * day, rulesVersion: 1)
        let newSession = ScanSessionRecord(kind: "smartScan", startedAt: now - 10 * day, rulesVersion: 1)
        try s.pool.write { db in
            try oldSession.insert(db)
            try newSession.insert(db)
        }
        let ancient = CleanOperation(sessionId: oldSession.id, startedAt: now - 400 * day, plannedBytes: 1)
        let old = CleanOperation(sessionId: nil, startedAt: now - 100 * day, plannedBytes: 1)
        let recent = CleanOperation(sessionId: newSession.id, startedAt: now - 5 * day, plannedBytes: 1)
        for op in [ancient, old, recent] { try s.insert(op) }
        for op in [ancient, old, recent] {
            try s.log([CleanItemLog(operationId: op.id, path: "/x/\(op.id)", ruleId: "r", strategy: "delete", trashedPath: nil, size: 1, result: "ok")])
        }
        try s.recordMaintenance(task: "flushDNS", result: "ok", at: now - 400 * day)
        try s.recordMaintenance(task: "flushDNS", result: "ok", at: now - 1 * day)

        try s.pruneOldData(now: now)

        do {
            let ops = try s.operations().map(\.id)
            #expect(Set(ops) == [old.id, recent.id])
            #expect(try s.items(of: old.id).isEmpty)          // log chi tiết giữ 90 ngày
            #expect(try s.recentSessions().map(\.id) == [newSession.id])
            let runs = try s.pool.read { try Int.fetchOne($0, sql: "SELECT COUNT(*) FROM maintenance_run") }
            #expect(runs == 1)
        }
        #expect(try s.items(of: recent.id).count == 1)
        #expect(try s.lastMaintenanceRuns()["flushDNS"] != nil)
    }

    @Test func ignoreList() throws {
        let t = try TempStorage()
        let s = t.storage
        try s.addIgnore(kind: .path, value: "/Users/x/Library/Caches/keep")
        try s.addIgnore(kind: .path, value: "/Users/x/Library/Caches/keep")   // trùng: bỏ qua
        try s.addIgnore(kind: .rule, value: "user.caches.all")
        try s.addIgnore(kind: .bundleID, value: "com.example.app")
        let entries = try s.ignoreEntries()
        #expect(entries.count == 3)
        let list = try s.ignoreList()
        #expect(list.ignores(path: "/Users/x/Library/Caches/keep"))
        #expect(list.ignores(path: "/Users/x/Library/Caches/keep/inner/file"))
        #expect(!list.ignores(path: "/Users/x/Library/Caches/keeper"))
        #expect(list.ignores(rule: "user.caches.all"))
        #expect(list.ignores(bundleID: "com.example.app"))
        #expect(!list.ignores(rule: nil))
        let ruleEntry = try #require(entries.first { $0.kind == .rule })
        try s.removeIgnore(id: try #require(ruleEntry.id))
        #expect(try !s.ignoreList().ignores(rule: "user.caches.all"))
    }

    @Test func freedThisMonthAndRestoreMark() throws {
        let t = try TempStorage()
        let s = t.storage
        var current = CleanOperation(sessionId: nil, startedAt: Date(), plannedBytes: 2000)
        current.freedBytes = 1500
        var lastYear = CleanOperation(sessionId: nil, startedAt: Date() - 200 * 86_400, plannedBytes: 1)
        lastYear.freedBytes = 99_999
        let unfinished = CleanOperation(sessionId: nil, startedAt: Date(), plannedBytes: 10)
        for op in [current, lastYear, unfinished] { try s.insert(op) }
        let freed = try s.freedThisMonth()
        do {
            #expect(freed == 1500)
        }
        #expect(try s.totalFreed() == 101_499)

        try s.log([CleanItemLog(operationId: current.id, path: "/x", ruleId: nil, strategy: "trash", trashedPath: "/Users/x/.Trash/x", size: 10, result: "ok")])
        #expect(try s.items(of: current.id).first?.isRestorable == true)
        try s.markRestored(operationID: current.id, path: "/x")
        let item = try #require(try s.items(of: current.id).first)
        #expect(item.result == "restored")
        #expect(item.trashedPath == nil)
        #expect(!item.isRestorable)
    }

    /// Cột thời gian phải lưu dạng số giây (REAL) để các truy vấn theo mốc thời gian đúng (mục 12.2).
    @Test func datesAreStoredAsUnixTime() throws {
        let t = try TempStorage()
        try t.storage.insert(CleanOperation(sessionId: nil, plannedBytes: 1))
        try t.storage.recordMaintenance(task: "x", result: "ok")
        let types = try t.storage.pool.read { db in
            [try String.fetchOne(db, sql: "SELECT typeof(started_at) FROM clean_operation"),
             try String.fetchOne(db, sql: "SELECT typeof(ran_at) FROM maintenance_run")]
        }
        do {
            #expect(types == ["real", "real"])
        }
    }

    @Test func sessions() throws {
        let t = try TempStorage()
        let s = t.storage
        let session = try s.beginSession(kind: "systemJunk", rulesVersion: 2026100201)
        try s.finishSession(session.id, status: "succeeded", foundBytes: 42)
        let last = try #require(try s.lastSession(kind: "systemJunk"))
        #expect(last.status == "succeeded")
        #expect(last.foundBytes == 42)
        #expect(last.rulesVersion == 2026100201)
        #expect(try s.lastSession(kind: "other") == nil)
    }
}
