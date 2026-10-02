import CleanEngine
import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import ScanEngine
import SweepCore
import SweepStorage
import SystemJunkScanning
import Testing

/// Tích hợp Scan → Plan → Clean trên thư mục tạm có nội dung thật (mục 18).
@Suite(.serialized) struct CleanEngineIntegrationTests {
    final class Fixture: @unchecked Sendable {
        let dir: TemporaryDirectory
        let home: String
        let storage: Storage
        let engine: CleanEngine
        let policy: PathPolicy

        init() throws {
            dir = try TemporaryDirectory("clean")
            try dir.dir("home")
            try dir.dir("root")
            home = dir.path("home")
            storage = try Storage.temporary()
            policy = .user(home: URL(fileURLWithPath: home))
            engine = CleanEngine(fileSystem: FileSystemService(home: URL(fileURLWithPath: home)), helper: nil, storage: storage, policy: policy)
        }

        deinit { try? FileManager.default.removeItem(at: storage.url.deletingLastPathComponent()) }

        var rules: [Rule] {
            [
                makeRule("t.caches", category: RuleCategory.userCaches, paths: ["~/Library/Caches/*"], removal: .deleteContents,
                         exclude: ["~/Library/Caches/com.apple.*"]),
                makeRule("t.logs", category: RuleCategory.userLogs, paths: ["~/Library/Logs/*"], removal: .delete),
                makeRule("t.downloads", category: RuleCategory.oldDownloads, paths: ["~/Downloads/*.dmg"], safety: .review,
                         removal: .moveToTrash, minAgeDays: 30),
            ]
        }

        func scan() async throws -> ScanResult {
            let snapshot = RuleSnapshot(version: 1, rules: rules, knowledge: Knowledge(),
                                        resolver: PathResolver(home: home, rootPrefix: dir.path("root")), source: .development)
            let tasks: [any ScanTask] = [
                RuleCategoryTask(id: "userCaches", category: RuleCategory.userCaches, title: "Cache"),
                RuleCategoryTask(id: "userLogs", category: RuleCategory.userLogs, title: "Log"),
                RuleCategoryTask(id: "oldDownloads", category: RuleCategory.oldDownloads, title: "Tải về"),
            ]
            return try await ScanEngine(fileSystem: FileSystemService(home: URL(fileURLWithPath: home)), maxConcurrentIO: 2)
                .runToCompletion(try ScanGraph(tasks: tasks), rules: snapshot, environment: ScanEnvironment(policy: policy))
        }

        /// Xoá mục đã chuyển vào Thùng rác thật trong lúc test (chỉ mục do test tạo).
        func purgeTrashed(_ report: CleanReport) {
            for e in report.entries {
                guard let t = e.result.trashedPath, t.contains("mashclean-test-") || t.hasSuffix(".dmg") else { continue }
                try? FileManager.default.removeItem(atPath: t)
            }
        }
    }

    @Test func scanPlanCleanDefaultSelection() async throws {
        let f = try Fixture()
        try f.dir.file("home/Library/Caches/com.vendor.app/blob", size: 64 * 1024)
        try f.dir.file("home/Library/Caches/com.vendor.app/sub/blob2", size: 16 * 1024)
        try f.dir.file("home/Library/Caches/com.apple.Safari/keep", size: 4096)
        try f.dir.file("home/Library/Logs/Vendor/x.log", size: 8192)
        try f.dir.file("home/Downloads/mashclean-test-old.dmg", size: 4096, ageDays: 60)

        let result = try await f.scan()
        #expect(result.tree.allLeaves.count == 3)

        // Mặc định: chỉ mục safe được chọn (bản tải về là review).
        let selection = SelectionState.defaults(for: result.tree)
        let plan = f.engine.makePlan(tree: result.tree, selection: selection)
        #expect(plan.items.count == 2)
        #expect(plan.blocked.isEmpty)
        #expect(!plan.requiresConfirmation)

        let report = await f.engine.run(plan, dryRun: false)
        #expect(report.failed.isEmpty, "\(report.failed.map(\.result))")
        #expect(report.succeeded.count == 2)
        #expect(report.estimatedFreed.bytes >= 80 * 1024)

        // deleteContents giữ thư mục, xoá nội dung; delete xoá cả thư mục; mục loại trừ và review không bị đụng.
        #expect(f.dir.exists("home/Library/Caches/com.vendor.app"))
        #expect(try FileManager.default.contentsOfDirectory(atPath: f.dir.path("home/Library/Caches/com.vendor.app")).isEmpty)
        #expect(!f.dir.exists("home/Library/Logs/Vendor"))
        #expect(f.dir.exists("home/Library/Caches/com.apple.Safari/keep"))
        #expect(f.dir.exists("home/Downloads/mashclean-test-old.dmg"))

        // Ghi clean_operation và clean_item_log.
        let ops = try f.storage.operations()
        #expect(ops.count == 1)
        #expect(ops.first?.itemsOk == 2)
        let logs = try f.storage.items(of: report.operationID)
        #expect(Set(logs.map(\.strategy)) == ["deleteContents", "delete"])
        #expect(logs.allSatisfy { $0.result == "ok" })
    }

    @Test func moveToTrashRecordsTrashedPathAndRestores() async throws {
        let f = try Fixture()
        let name = "mashclean-test-\(UUID().uuidString).dmg"
        try f.dir.file("home/Downloads/\(name)", size: 8192, ageDays: 90)
        let result = try await f.scan()
        let dmg = try #require(result.tree.allLeaves.first { $0.url?.lastPathComponent == name })
        #expect(dmg.safety == .review)
        #expect(dmg.removal == .moveToTrash)

        var selection = SelectionState()
        selection.set(dmg.id, true, in: result.tree)
        let plan = f.engine.makePlan(tree: result.tree, selection: selection)
        #expect(plan.requiresConfirmation)
        let report = await f.engine.run(plan, dryRun: false)
        defer { f.purgeTrashed(report) }
        let entry = try #require(report.entries.first)
        #expect(entry.result.isSuccess)
        let trashed = try #require(entry.result.trashedPath)
        #expect(FileManager.default.fileExists(atPath: trashed))
        #expect(!f.dir.exists("home/Downloads/\(name)"))

        let log = try #require(try f.storage.items(of: report.operationID).first)
        #expect(log.isRestorable)
        #expect(log.strategy == "trash")
        try f.engine.restore(log)
        #expect(f.dir.exists("home/Downloads/\(name)"))
        #expect(!FileManager.default.fileExists(atPath: trashed))
        #expect(try f.storage.items(of: report.operationID).first?.result == "restored")
        #expect(throws: CleanEngine.RestoreError.self) { try f.engine.restore(log) }
    }

    @Test func missingFileIsSuccessWithZeroBytes() async throws {
        let f = try Fixture()
        let node = Node(kind: .file(f.dir.url("home/Library/Caches/gone.db")), size: 5000, safety: .safe, category: "userCaches",
                        removal: .delete, allowedRoots: [f.dir.path("home/Library/Caches")])
        let plan = f.engine.makePlan(nodes: [node])
        #expect(plan.items.count == 1)
        let report = await f.engine.run(plan, dryRun: false)
        let r = try #require(report.entries.first?.result)
        #expect(r.isSuccess)
        #expect(r.outcome == .skipped(reason: .notFound))
        #expect(r.freedBytes == 0)
        #expect(report.skipped.isEmpty)
    }

    @Test func dryRunDeletesNothing() async throws {
        let f = try Fixture()
        try f.dir.file("home/Library/Caches/com.vendor.app/blob", size: 8192)
        try f.dir.file("home/Library/Logs/Vendor/x.log", size: 8192)
        let result = try await f.scan()
        let plan = f.engine.makePlan(tree: result.tree, selection: .defaults(for: result.tree))
        #expect(plan.items.count == 2)
        let report = await f.engine.run(plan, dryRun: true)
        #expect(report.dryRun)
        #expect(report.entries.allSatisfy { $0.result.outcome == .skipped(reason: .dryRun) })
        #expect(report.measuredFreed == nil)
        #expect(f.dir.exists("home/Library/Caches/com.vendor.app/blob"))
        #expect(f.dir.exists("home/Library/Logs/Vendor/x.log"))
        #expect(try f.storage.operations().first?.freedBytes == 0)
    }

    @Test func blockedPathsNeverReachRemovers() async throws {
        let f = try Fixture()
        try f.dir.file("outside/secret.txt")
        try f.dir.symlink("home/Library/Caches/link", to: f.dir.path("outside"))
        try f.dir.file("home/Documents/report.txt")
        let nodes = [
            Node(kind: .directory(URL(fileURLWithPath: "/System/Library/Fonts"), recursive: true), safety: .safe, removal: .delete),
            Node(kind: .directory(f.dir.url("home/Documents"), recursive: true), safety: .safe, removal: .delete),
            Node(kind: .directory(f.dir.url("home/Library/Caches"), recursive: true), safety: .safe, removal: .delete),
            Node(kind: .file(f.dir.url("home/Library/Caches/link/secret.txt")), safety: .safe, removal: .delete,
                 allowedRoots: [f.dir.path("home/Library/Caches")]),
            Node(kind: .file(URL(fileURLWithPath: f.home + "/Library/Keychains/login.keychain-db")), safety: .safe, removal: .custom(.app)),
        ]
        let plan = f.engine.makePlan(nodes: nodes)
        #expect(plan.items.isEmpty)
        #expect(plan.blocked.count == nodes.count)
        _ = await f.engine.run(plan, dryRun: false)
        #expect(f.dir.exists("outside/secret.txt"))
        #expect(f.dir.exists("home/Documents/report.txt"))
    }

    @Test func deleteContentsOnLooseFileRemovesIt() async throws {
        let f = try Fixture()
        try f.dir.file("home/Library/Caches/loose.db", size: 4096)
        let result = try await f.scan()
        let loose = try #require(result.tree.allLeaves.first { $0.url?.lastPathComponent == "loose.db" })
        let plan = f.engine.makePlan(nodes: [loose])
        let report = await f.engine.run(plan, dryRun: false)
        // Rule `~/Library/Caches/*` + deleteContents khớp cả file lẻ: xoá chính file.
        #expect(report.entries.first?.result.isSuccess == true)
        #expect(!f.dir.exists("home/Library/Caches/loose.db"))
    }
}
