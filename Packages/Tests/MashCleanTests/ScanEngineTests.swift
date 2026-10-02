import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import SweepCore
import Testing
@testable import ScanEngine

/// Task giả cho test scheduler (mục 5).
struct StubTask: ScanTask {
    enum Behavior: Sendable {
        case succeed(size: Int64)
        case fail
        case sleep(seconds: Double)
        case readUpstream(ArtifactKey)
    }

    let id: ScanTaskID
    var dependencies: [ScanTaskID] = []
    var estimatedWeight: Double = 1
    var behavior: Behavior = .succeed(size: 10)

    struct Failure: Error {}

    func run(context: ScanContext) async throws -> ScanOutput {
        switch behavior {
        case let .succeed(size):
            context.progress.report(0.5)
            let node = Node(kind: .virtual(.packageReceipt(id: id.rawValue)), size: ByteCount(size), safety: .safe, category: "test")
            return ScanOutput(nodes: [Node.group(LocalizedText("G-\(id)"), icon: "folder", children: [node])], artifacts: [ArtifactKey(rawValue: id.rawValue): size])
        case .fail:
            throw Failure()
        case let .sleep(seconds):
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            return .empty
        case let .readUpstream(key):
            let v = context.upstreamArtifact(key, as: Int64.self) ?? -1
            return ScanOutput(artifacts: ["seen": v])
        }
    }
}

@Suite struct ProgressTests {
    @Test func weightedFraction() {
        let w: [ScanTaskID: Double] = ["a": 1, "b": 3]
        #expect(ProgressAggregator.weightedFraction(["a": 1, "b": 0], weights: w) == 0.25)
        #expect(ProgressAggregator.weightedFraction(["a": 1, "b": 1], weights: w) == 1)
        #expect(ProgressAggregator.weightedFraction(["b": 0.5], weights: w) == 0.375)
        #expect(ProgressAggregator.weightedFraction([:], weights: w) == 0)
        #expect(ProgressAggregator.weightedFraction(["a": 1], weights: [:]) == 0)
        #expect(ProgressAggregator.weightedFraction(["a": 5, "b": 5], weights: w) == 1)   // không vượt 1
    }

    @Test func engineEmitsFinalProgressOfOne() async throws {
        let graph = try ScanGraph(tasks: [StubTask(id: "a", estimatedWeight: 1), StubTask(id: "b", estimatedWeight: 4)])
        let last = Locked<ScanProgress?>(nil)
        let result = try await ScanEngine(maxConcurrentIO: 2).runToCompletion(graph, rules: .empty) { event in
            if case let .progress(p) = event { last.withLock { $0 = p } }
        }
        let p = try #require(last.current)
        #expect(p.fraction == 1)
        #expect(p.completedTasks == 2)
        #expect(p.totalTasks == 2)
        #expect(result.states.values.allSatisfy { $0 == .succeeded })
    }
}

@Suite struct ScanGraphTests {
    @Test func cycleIsRejected() {
        #expect(throws: ScanGraphError.self) {
            try ScanGraph(tasks: [StubTask(id: "a", dependencies: ["b"]), StubTask(id: "b", dependencies: ["c"]), StubTask(id: "c", dependencies: ["a"])])
        }
        do {
            _ = try ScanGraph(tasks: [StubTask(id: "a", dependencies: ["a"])])
            Issue.record("Tự phụ thuộc phải là chu trình")
        } catch let e as ScanGraphError {
            #expect(e == .cycle(["a"]))
        } catch {
            Issue.record("Lỗi sai loại: \(error)")
        }
    }

    @Test func missingDependencyIsRejected() {
        #expect(throws: ScanGraphError.missingDependency(task: "a", dependency: "ghost")) {
            try ScanGraph(tasks: [StubTask(id: "a", dependencies: ["ghost"])])
        }
    }

    @Test func topologicalOrderAndDuplicates() throws {
        let g = try ScanGraph(tasks: [
            StubTask(id: "c", dependencies: ["b"]), StubTask(id: "b", dependencies: ["a"]), StubTask(id: "a"),
            StubTask(id: "a", behavior: .fail),
        ])
        #expect(g.tasks.count == 3)
        #expect(g.topologicalOrder == ["a", "b", "c"])
        #expect(g.transitiveDependents(of: "a") == ["b", "c"])
        if case .fail = (g.task("a") as? StubTask)?.behavior { Issue.record("Task trùng id phải giữ bản đầu tiên") }
    }

    @Test func failedDependencySkipsDependentsButOthersRun() async throws {
        let graph = try ScanGraph(tasks: [
            StubTask(id: "apps", behavior: .fail),
            StubTask(id: "caches", dependencies: ["apps"]),
            StubTask(id: "deep", dependencies: ["caches"]),
            StubTask(id: "logs"),
        ])
        let result = try await ScanEngine(maxConcurrentIO: 2).runToCompletion(graph, rules: .empty)
        #expect(result.states["apps"] == .failed(String(describing: StubTask.Failure())))
        #expect(result.states["caches"] == .skipped)
        #expect(result.states["deep"] == .skipped)
        #expect(result.states["logs"] == .succeeded)
        #expect(result.warnings.filter { $0.kind == .skipped }.count == 2)
        #expect(result.warnings.contains { $0.kind == .failed && $0.taskID == "apps" })
        #expect(result.tree.allLeaves.count == 1)
    }

    @Test func upstreamArtifactsReachDependents() async throws {
        let graph = try ScanGraph(tasks: [
            StubTask(id: "producer", behavior: .succeed(size: 42)),
            StubTask(id: "middle", dependencies: ["producer"]),
            StubTask(id: "consumer", dependencies: ["middle"], behavior: .readUpstream("producer")),
        ])
        let result = try await ScanEngine(maxConcurrentIO: 4).runToCompletion(graph, rules: .empty)
        #expect(result.outputs["consumer"]?.artifact("seen", as: Int64.self) == 42)
        // Node từ nhiều task được gộp vào cây.
        #expect(result.tree.totalSize == 52)
    }

    @Test func timeoutMarksTaskFailed() async throws {
        let graph = try ScanGraph(tasks: [
            StubTask(id: "slow", behavior: .sleep(seconds: 30)),
            StubTask(id: "after", dependencies: ["slow"]),
            StubTask(id: "fast"),
        ])
        let start = Date()
        let result = try await ScanEngine(maxConcurrentIO: 2, taskTimeout: 0.3).runToCompletion(graph, rules: .empty)
        #expect(Date().timeIntervalSince(start) < 10)
        #expect(result.states["slow"] == .failed("timeout"))
        #expect(result.states["after"] == .skipped)
        #expect(result.states["fast"] == .succeeded)
        #expect(result.warnings.contains { $0.kind == .timeout && $0.taskID == "slow" })
    }

    @Test func cancellationStopsTheScan() async throws {
        let graph = try ScanGraph(tasks: [StubTask(id: "slow", behavior: .sleep(seconds: 30))])
        let engine = ScanEngine(maxConcurrentIO: 1, taskTimeout: 60)
        let task = Task { try await engine.runToCompletion(graph, rules: .empty) }
        try await Task.sleep(nanoseconds: 200_000_000)
        task.cancel()
        let start = Date()
        await #expect(throws: CancellationError.self) { _ = try await task.value }
        #expect(Date().timeIntervalSince(start) < 10)
    }
}
