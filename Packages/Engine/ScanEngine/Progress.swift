import FileSystemKit
import Foundation
import SweepCore

/// Ảnh chụp tiến độ đẩy lên UI.
public struct ScanProgress: Sendable, Equatable {
    /// Tiến độ tổng = Σ(weightᵢ × progressᵢ) / Σ weightᵢ (mục 5.4).
    public var fraction: Double
    /// Tên task đang chạy.
    public var currentTask: String
    public var runningTasks: [String]
    /// Số file đã duyệt.
    public var filesVisited: Int
    /// Dung lượng tìm được tới hiện tại.
    public var bytesFound: ByteCount
    public var completedTasks: Int
    public var totalTasks: Int

    public static let zero = ScanProgress(fraction: 0, currentTask: "", runningTasks: [], filesVisited: 0, bytesFound: .zero, completedTasks: 0, totalTasks: 0)
}

/// Reporter tiến độ của một task: báo trong khoảng `0...1`, cộng số file, cộng dung lượng tìm được.
public final class ProgressReporter: Sendable {
    private let aggregator: ProgressAggregator?
    private let taskID: ScanTaskID
    public let fileCounter: VisitCounter

    init(aggregator: ProgressAggregator?, taskID: ScanTaskID, fileCounter: VisitCounter) {
        self.aggregator = aggregator
        self.taskID = taskID
        self.fileCounter = fileCounter
    }

    /// Reporter rỗng, dùng khi chạy task ngoài engine (test).
    public static func detached() -> ProgressReporter {
        ProgressReporter(aggregator: nil, taskID: "detached", fileCounter: VisitCounter())
    }

    public func report(_ fraction: Double) {
        aggregator?.update(taskID, fraction: min(max(fraction, 0), 1))
    }

    public func addBytesFound(_ bytes: ByteCount) {
        aggregator?.addBytes(bytes)
    }

    public func addFilesVisited(_ n: Int) { fileCounter.add(n) }
}

/// Gom cập nhật của mọi task và đẩy lên UI tối đa 10 lần/giây (throttle, mục 5.4).
final class ProgressAggregator: Sendable {
    private struct State {
        var fractions: [ScanTaskID: Double] = [:]
        var running: [ScanTaskID] = []
        var completed = 0
        var bytes: ByteCount = .zero
        var lastEmit: Date = .distantPast
    }

    private let weights: [ScanTaskID: Double]
    private let titles: [ScanTaskID: String]
    private let totalWeight: Double
    private let state = Locked(State())
    private let emit: @Sendable (ScanProgress) -> Void
    private let minInterval: TimeInterval
    let fileCounter = VisitCounter()

    init(graph: ScanGraph, minInterval: TimeInterval = 0.1, emit: @escaping @Sendable (ScanProgress) -> Void) {
        weights = Dictionary(uniqueKeysWithValues: graph.tasks.map { ($0.id, max($0.estimatedWeight, 0.0001)) })
        titles = Dictionary(uniqueKeysWithValues: graph.tasks.map { ($0.id, $0.title) })
        totalWeight = weights.values.reduce(0, +)
        self.emit = emit
        self.minInterval = minInterval
    }

    func reporter(for id: ScanTaskID) -> ProgressReporter {
        ProgressReporter(aggregator: self, taskID: id, fileCounter: fileCounter)
    }

    func update(_ id: ScanTaskID, fraction: Double) {
        let snapshot = state.withLock { s -> ScanProgress? in
            s.fractions[id] = fraction
            return throttled(&s)
        }
        if let snapshot { emit(snapshot) }
    }

    func addBytes(_ bytes: ByteCount) {
        let snapshot = state.withLock { s -> ScanProgress? in
            s.bytes = s.bytes + bytes
            return throttled(&s)
        }
        if let snapshot { emit(snapshot) }
    }

    func started(_ id: ScanTaskID) {
        let snapshot = state.withLock { s -> ScanProgress? in
            s.running.append(id)
            return throttled(&s)
        }
        if let snapshot { emit(snapshot) }
    }

    func finished(_ id: ScanTaskID) {
        let snapshot = state.withLock { s -> ScanProgress? in
            s.fractions[id] = 1
            s.running.removeAll { $0 == id }
            s.completed += 1
            return throttled(&s)
        }
        if let snapshot { emit(snapshot) }
    }

    /// Phát bản cuối cùng, không throttle.
    func flush() {
        let snapshot = state.withLock { s in make(s) }
        emit(snapshot)
    }

    /// Tiến độ tổng có trọng số.
    static func weightedFraction(_ fractions: [ScanTaskID: Double], weights: [ScanTaskID: Double]) -> Double {
        let total = weights.values.reduce(0, +)
        guard total > 0 else { return 0 }
        let done = weights.reduce(0.0) { $0 + $1.value * (fractions[$1.key] ?? 0) }
        return min(done / total, 1)
    }

    private func throttled(_ s: inout State) -> ScanProgress? {
        let now = Date()
        guard now.timeIntervalSince(s.lastEmit) >= minInterval else { return nil }
        s.lastEmit = now
        return make(s)
    }

    private func make(_ s: State) -> ScanProgress {
        let current = s.running.last.flatMap { titles[$0] } ?? ""
        return ScanProgress(
            fraction: Self.weightedFraction(s.fractions, weights: weights),
            currentTask: current,
            runningTasks: s.running.compactMap { titles[$0] },
            filesVisited: fileCounter.count,
            bytesFound: s.bytes,
            completedTasks: s.completed,
            totalTasks: weights.count
        )
    }
}
