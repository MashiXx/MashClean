import FileSystemKit
import Foundation
import NodeTree
import os
import RuleEngine
import SweepCore
import SweepLogging

/// Trạng thái task (mục 5.3).
public enum TaskState: Sendable, Equatable {
    case pending
    case ready
    case running
    case succeeded
    case failed(String)
    case cancelled
    case skipped

    public var isTerminal: Bool {
        switch self {
        case .succeeded, .failed, .cancelled, .skipped: true
        default: false
        }
    }
}

public enum ScanEvent: Sendable {
    case progress(ScanProgress)
    case taskStateChanged(ScanTaskID, TaskState)
    case finished(ScanResult)
}

/// Kết quả một phiên quét (`ScanSession`, mục 5.1).
public struct ScanResult: Sendable {
    public let tree: NodeTree
    public let outputs: [ScanTaskID: ScanOutput]
    public let states: [ScanTaskID: TaskState]
    public let warnings: [ScanWarning]
    public let duration: TimeInterval
    public let filesVisited: Int

    /// Node của một nhóm task (để Smart Scan tách theo feature).
    public func nodes(of taskIDs: Set<ScanTaskID>) -> [Node] {
        let wanted = Set(outputs.filter { taskIDs.contains($0.key) }.flatMap { $0.value.nodes }.map(\.id))
        return tree.roots.filter { wanted.contains($0.id) }
    }
}

/// Scheduler giữ hàng đợi `Ready` sắp theo `(priority desc, estimatedWeight desc)` (mục 5.3 bước 2).
actor ScanScheduler {
    private let graph: ScanGraph
    private(set) var states: [ScanTaskID: TaskState]
    private(set) var outputs: [ScanTaskID: ScanOutput] = [:]
    private var ready: [any ScanTask] = []
    private var remainingDeps: [ScanTaskID: Set<ScanTaskID>]

    init(graph: ScanGraph) {
        self.graph = graph
        states = Dictionary(uniqueKeysWithValues: graph.tasks.map { ($0.id, TaskState.pending) })
        remainingDeps = Dictionary(uniqueKeysWithValues: graph.tasks.map { ($0.id, Set($0.dependencies)) })
        var initial: [any ScanTask] = []
        for t in graph.tasks where t.dependencies.isEmpty {
            states[t.id] = .ready
            initial.append(t)
        }
        ready = Self.sorted(initial)
    }

    private static func sorted(_ tasks: [any ScanTask]) -> [any ScanTask] {
        tasks.sorted { a, b in
            if a.priority != b.priority { return a.priority > b.priority }
            return a.estimatedWeight > b.estimatedWeight
        }
    }

    private func sortReady() { ready = Self.sorted(ready) }

    /// Lấy task kế tiếp sẵn sàng chạy, kèm kết quả các task phụ thuộc.
    func next() -> (task: any ScanTask, upstream: [ScanTaskID: ScanOutput])? {
        guard !ready.isEmpty else { return nil }
        let task = ready.removeFirst()
        states[task.id] = .running
        var upstream: [ScanTaskID: ScanOutput] = [:]
        for d in transitiveDependencies(of: task.id) { upstream[d] = outputs[d] }
        return (task, upstream)
    }

    private func transitiveDependencies(of id: ScanTaskID) -> Set<ScanTaskID> {
        var result = Set<ScanTaskID>()
        var stack = graph.task(id)?.dependencies ?? []
        while let d = stack.popLast() {
            if result.insert(d).inserted { stack += graph.task(d)?.dependencies ?? [] }
        }
        return result
    }

    /// Ghi nhận kết thúc; trả về các task bị chuyển sang `Skipped` (vì dependency Failed).
    func complete(_ id: ScanTaskID, state: TaskState, output: ScanOutput?) -> [ScanTaskID] {
        states[id] = state
        if let output { outputs[id] = output }
        var skipped: [ScanTaskID] = []
        if state == .succeeded {
            for dep in graph.dependents[id] ?? [] {
                remainingDeps[dep]?.remove(id)
                if remainingDeps[dep]?.isEmpty == true, states[dep] == .pending, let t = graph.task(dep) {
                    states[dep] = .ready
                    ready.append(t)
                }
            }
            sortReady()
        } else {
            // Task lỗi: các task phụ thuộc thành Skipped, các nhánh khác vẫn chạy tiếp (mục 5.3 bước 4).
            for dep in graph.transitiveDependents(of: id) where states[dep] == .pending {
                states[dep] = .skipped
                skipped.append(dep)
            }
        }
        return skipped
    }

    func cancelRemaining() -> [ScanTaskID] {
        var cancelled: [ScanTaskID] = []
        for (id, s) in states where !s.isTerminal {
            states[id] = .cancelled
            cancelled.append(id)
        }
        ready.removeAll()
        return cancelled
    }

    var isFinished: Bool { states.values.allSatisfy(\.isTerminal) }
}

/// Lõi quét (mục 5).
public final class ScanEngine: Sendable {
    /// Số task I/O chạy đồng thời; `nil` = tự chọn theo loại ổ (4 trên SSD, 1 trên HDD).
    public let maxConcurrentIO: Int?
    /// Timeout mềm mỗi task (mặc định 120 giây).
    public let taskTimeout: TimeInterval
    public let fileSystem: FileSystemService

    public init(fileSystem: FileSystemService = FileSystemService(), maxConcurrentIO: Int? = nil, taskTimeout: TimeInterval = 120) {
        self.fileSystem = fileSystem
        self.maxConcurrentIO = maxConcurrentIO
        self.taskTimeout = taskTimeout
    }

    /// Chạy graph. Huỷ bằng cách huỷ Task đang lặp stream: `Task.cancel()` lan xuống các task con.
    public func run(_ graph: ScanGraph, rules: RuleSnapshot, environment: ScanEnvironment = ScanEnvironment()) -> AsyncThrowingStream<ScanEvent, any Error> {
        AsyncThrowingStream { continuation in
            let worker = Task(priority: .utility) {
                do {
                    let result = try await execute(graph, rules: rules, environment: environment) { continuation.yield($0) }
                    continuation.yield(.finished(result))
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { termination in
                if case .cancelled = termination { worker.cancel() }
            }
        }
    }

    /// Chạy graph và trả về kết quả cuối (không cần stream).
    public func runToCompletion(_ graph: ScanGraph, rules: RuleSnapshot, environment: ScanEnvironment = ScanEnvironment(),
                                onEvent: @escaping @Sendable (ScanEvent) -> Void = { _ in }) async throws -> ScanResult {
        try await execute(graph, rules: rules, environment: environment, onEvent: onEvent)
    }

    private func execute(_ graph: ScanGraph, rules: RuleSnapshot, environment: ScanEnvironment, onEvent: @escaping @Sendable (ScanEvent) -> Void) async throws -> ScanResult {
        let start = Date()
        let scheduler = ScanScheduler(graph: graph)
        let aggregator = ProgressAggregator(graph: graph) { onEvent(.progress($0)) }
        let limit = max(1, maxConcurrentIO ?? fileSystem.recommendedConcurrency(for: fileSystem.home))
        let fs = fileSystem
        let timeout = taskTimeout
        let warnings = Locked<[ScanWarning]>([])

        Logger.scan.info("Bắt đầu quét \(graph.tasks.count) task, đồng thời \(limit)")

        try await withThrowingTaskGroup(of: (ScanTaskID, TaskState, ScanOutput?).self) { group in
            var running = 0

            func launchReady() async {
                while running < limit, let (task, upstream) = await scheduler.next() {
                    running += 1
                    onEvent(.taskStateChanged(task.id, .running))
                    aggregator.started(task.id)
                    let context = ScanContext(fileSystem: fs, rules: rules, progress: aggregator.reporter(for: task.id), upstream: upstream, environment: environment)
                    group.addTask(priority: .utility) {
                        let outcome = await Self.runWithTimeout(task, context: context, timeout: timeout)
                        return (task.id, outcome.0, outcome.1)
                    }
                }
            }

            await launchReady()
            while let (id, state, output) = try await group.next() {
                running -= 1
                aggregator.finished(id)
                onEvent(.taskStateChanged(id, state))
                if let output { warnings.withLock { $0 += output.warnings } }
                switch state {
                case let .failed(message):
                    let kind: ScanWarning.Kind = message == "timeout" ? .timeout : .failed
                    warnings.withLock { $0.append(ScanWarning(taskID: id, kind: kind, message: message)) }
                    Log.warning(.scan, "scan", "Task \(id) lỗi: \(message)")
                default: break
                }
                let skipped = await scheduler.complete(id, state: state, output: output)
                for s in skipped {
                    onEvent(.taskStateChanged(s, .skipped))
                    warnings.withLock { $0.append(ScanWarning(taskID: s, kind: .skipped, message: "Bỏ qua vì task phụ thuộc lỗi")) }
                }
                if Task.isCancelled {
                    group.cancelAll()
                    for c in await scheduler.cancelRemaining() { onEvent(.taskStateChanged(c, .cancelled)) }
                    throw CancellationError()
                }
                await launchReady()
            }
        }
        try Task.checkCancellation()
        aggregator.flush()

        let outputs = await scheduler.outputs
        let states = await scheduler.states
        // Gộp nodes theo thứ tự topo, deduplicate, tính tổng (mục 23.2).
        let nodes = graph.topologicalOrder.flatMap { outputs[$0]?.nodes ?? [] }
        let tree = NodeTree.merge(nodes)
        let duration = Date().timeIntervalSince(start)
        Logger.scan.info("Quét xong trong \(duration, format: .fixed(precision: 2)) giây, tìm được \(tree.totalSize.bytes) byte")
        return ScanResult(tree: tree, outputs: outputs, states: states, warnings: warnings.current, duration: duration, filesVisited: aggregator.fileCounter.count)
    }

    /// Mỗi task có timeout mềm; quá timeout thì huỷ và báo cảnh báo (mục 5.3 bước 6).
    static func runWithTimeout(_ task: any ScanTask, context: ScanContext, timeout: TimeInterval) async -> (TaskState, ScanOutput?) {
        await withTaskGroup(of: (TaskState, ScanOutput?)?.self) { group in
            group.addTask {
                do {
                    let output = try await task.run(context: context)
                    return (.succeeded, output)
                } catch is CancellationError {
                    return Task.isCancelled ? (.cancelled, nil) : (.failed("timeout"), nil)
                } catch {
                    return (.failed(String(describing: error)), nil)
                }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                return Task.isCancelled ? nil : (.failed("timeout"), nil)
            }
            var result: (TaskState, ScanOutput?) = (.cancelled, nil)
            while let next = await group.next() {
                guard let next else { continue }
                result = next
                group.cancelAll()
                break
            }
            return result
        }
    }
}
