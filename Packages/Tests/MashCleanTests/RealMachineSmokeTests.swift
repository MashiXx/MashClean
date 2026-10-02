import AppCatalog
import FileSystemKit
import Foundation
import RuleEngine
import ScanEngine
import SystemJunkScanning
import SweepCore
import Testing

/// Smoke test trên máy thật, không xoá gì: `MASHCLEAN_SMOKE=1 swift test --filter smokeRealSystemJunkScan`
/// (đo thời gian quét rác so với mục tiêu G2 < 30 giây). `MASHCLEAN_SMOKE=2` đo từng rule nhóm devToolCaches.
private let bundledRulesURL = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    .appendingPathComponent("App/Resources/Rules/rules.bundle")

@Test(.enabled(if: ProcessInfo.processInfo.environment["MASHCLEAN_SMOKE"] == "1"))
func smokeRealSystemJunkScan() async throws {
    let store = try RuleStore(bundled: bundledRulesURL, cache: FileManager.default.temporaryDirectory.appendingPathComponent("smoke-rules"))
    let fs = FileSystemService()
    let scanner = AppScanner(fileSystem: fs, storage: nil, useSpotlight: false, knowledge: { store.snapshot.knowledge })
    let graph = try ScanGraph(tasks: SystemJunkTasks.tasks(appScanner: scanner, smartScanOnly: true))
    let start = Date()
    let times = Locked<[String: Date]>([:])
    let result = try await ScanEngine(fileSystem: fs).runToCompletion(graph, rules: store.snapshot) { event in
        if case let .taskStateChanged(id, state) = event {
            if state == .running { times.withLock { $0[id.rawValue] = Date() } }
            else if let t = times.current[id.rawValue] { print("SMOKE task \(id) \(state) \(String(format: "%.1f", Date().timeIntervalSince(t)))s") }
        }
    }
    print("SMOKE duration=\(String(format: "%.1f", Date().timeIntervalSince(start)))s files=\(result.filesVisited) total=\(result.tree.totalSize.formatted) leaves=\(result.tree.allLeaves.count)")
    for root in result.tree.roots { print("SMOKE  \(root.title): \(root.size.formatted) (\(root.removableLeaves.count) mục, safety \(root.safety))") }
    for w in result.warnings.prefix(8) { print("SMOKE  warn \(w.taskID): \(w.message)") }
}

@Test(.enabled(if: ProcessInfo.processInfo.environment["MASHCLEAN_SMOKE"] == "2"))
func smokePerRule() async throws {
    let store = try RuleStore(bundled: bundledRulesURL,
                              cache: FileManager.default.temporaryDirectory.appendingPathComponent("smoke-rules"))
    let ctx = RuleEvaluationContext(fileSystem: FileSystemService())
    for r in store.snapshot.rules(in: "devToolCaches") {
        let t = Date()
        let m = try RuleEvaluator.evaluate(r, snapshot: store.snapshot, context: ctx)
        print("SMOKE rule \(r.id) \(String(format: "%.1f", Date().timeIntervalSince(t)))s n=\(m.count) provider=\(r.rule.match.provider ?? "-")")
    }
}
