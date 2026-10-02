import CleanEngine
import Foundation
import NodeTree
import os
import ScanEngine
import SweepCore
import SweepLogging
import SweepStorage

/// Máy trạng thái chung của một màn tính năng (mục 22.3):
/// Idle → Scanning → Results → Confirming → Cleaning → Done, kèm Error và huỷ.
@MainActor
open class ScanCleanViewModel: ObservableObject {
    public enum Phase {
        case idle
        case scanning(ScanProgress)
        case results(NodeTree)
        case confirming(CleanPlan, NodeTree)
        case cleaning(progress: Double, currentItem: String, freed: ByteCount)
        case done(CleanReport)
        case error(String)
    }

    @Published public private(set) var phase: Phase = .idle
    @Published public var selection = SelectionState()
    @Published public private(set) var warnings: [ScanWarning] = []
    @Published public private(set) var lastResult: ScanResult?
    /// Mục cần hỏi xoá vĩnh viễn (Thùng rác không dùng được).
    @Published public var permanentDeletePrompt: PermanentDeletePrompt?

    public let services: ScanServices
    public let kind: String
    private let makeTasks: @Sendable () -> [any ScanTask]
    private var scanTask: Task<Void, Never>?
    private var cleanTask: Task<Void, Never>?
    private var sessionID: String?
    private var lastTree: NodeTree?

    public struct PermanentDeletePrompt: Identifiable {
        public let id = UUID()
        public let url: URL
        let resolve: (Bool) -> Void
        public func answer(_ yes: Bool) { resolve(yes) }
    }

    public init(services: ScanServices, kind: String, tasks: @escaping @Sendable () -> [any ScanTask]) {
        self.services = services
        self.kind = kind
        makeTasks = tasks
    }

    // MARK: Quét

    public var isBusy: Bool {
        switch phase {
        case .scanning, .cleaning: true
        default: false
        }
    }

    public func startScan() {
        scanTask?.cancel()
        warnings = []
        phase = .scanning(.zero)
        let services = services
        let environment = services.makeEnvironment()
        let rules = services.rules
        let kind = kind
        let tasks = makeTasks()
        scanTask = Task { [weak self] in
            let session = try? services.storage?.beginSession(kind: kind, rulesVersion: rules.version)
            self?.sessionID = session?.id
            do {
                let graph = try ScanGraph(tasks: tasks)
                for try await event in services.scanEngine.run(graph, rules: rules, environment: environment) {
                    guard let self else { return }
                    switch event {
                    case let .progress(p):
                        self.phase = .scanning(p)
                    case .taskStateChanged:
                        break
                    case let .finished(result):
                        self.lastResult = result
                        self.warnings = result.warnings
                        let tree = self.postProcess(result.tree)
                        self.lastTree = tree
                        // Chọn sẵn mục safe; mục risky chỉ hiện khi bật "Nâng cao".
                        self.selection = self.defaultSelection(for: tree)
                        self.phase = .results(tree)
                        if let id = session?.id { try? services.storage?.finishSession(id, status: "succeeded", foundBytes: tree.totalSize.bytes) }
                        Analytics.shared.recordScan(kind: kind, duration: result.duration)
                        self.scanDidFinish(result)
                    }
                }
            } catch is CancellationError {
                if let id = session?.id { try? services.storage?.finishSession(id, status: "cancelled", foundBytes: 0) }
                self?.phase = .idle
            } catch {
                if let id = session?.id { try? services.storage?.finishSession(id, status: "failed", foundBytes: 0) }
                Log.error(.scan, "scan", "Quét \(kind) lỗi: \(error)")
                self?.phase = .error(String(describing: error))
            }
        }
    }

    public func cancel() {
        scanTask?.cancel()
        cleanTask?.cancel()
        if case .scanning = phase { phase = .idle }
    }

    /// Lớp con có thể biến đổi cây trước khi hiển thị (ẩn mục risky...).
    open func postProcess(_ tree: NodeTree) -> NodeTree {
        guard !services.settings.showRiskyItems else { return tree }
        return tree
    }

    open func defaultSelection(for tree: NodeTree) -> SelectionState {
        SelectionState.defaults(for: tree)
    }

    open func scanDidFinish(_ result: ScanResult) {}

    // MARK: Chọn

    public func toggle(_ id: NodeID) {
        guard let tree = currentTree else { return }
        selection.toggle(id, in: tree)
    }

    public func set(_ id: NodeID, _ on: Bool) {
        guard let tree = currentTree else { return }
        selection.set(id, on, in: tree)
    }

    public var currentTree: NodeTree? {
        switch phase {
        case let .results(tree): tree
        case let .confirming(_, tree): tree
        default: lastTree
        }
    }

    // MARK: Dọn

    public func prepareClean() {
        guard let tree = currentTree else { return }
        let plan = services.cleanEngine.makePlan(tree: tree, selection: selection, sessionID: sessionID)
        if plan.requiresConfirmation || !plan.blocked.isEmpty {
            phase = .confirming(plan, tree)
        } else {
            execute(plan)
        }
    }

    public func cancelConfirmation() {
        if case let .confirming(_, tree) = phase { phase = .results(tree) }
    }

    public func execute(_ plan: CleanPlan) {
        phase = .cleaning(progress: 0, currentItem: "", freed: .zero)
        let engine = services.cleanEngine
        let dryRun = services.settings.dryRun
        let confirm: @Sendable (URL) async -> Bool = { [weak self] url in
            await self?.askPermanentDelete(url) ?? false
        }
        cleanTask = Task { [weak self] in
            for await event in engine.execute(plan, dryRun: dryRun, confirmPermanentDelete: confirm) {
                guard let self else { return }
                switch event {
                case let .progress(fraction, item, freed):
                    self.phase = .cleaning(progress: fraction, currentItem: item, freed: freed)
                case let .finished(report):
                    self.phase = .done(report)
                    self.cleanDidFinish(report)
                }
            }
        }
    }

    open func cleanDidFinish(_ report: CleanReport) {}

    private func askPermanentDelete(_ url: URL) async -> Bool {
        await withCheckedContinuation { cont in
            permanentDeletePrompt = PermanentDeletePrompt(url: url) { [weak self] answer in
                self?.permanentDeletePrompt = nil
                cont.resume(returning: answer)
            }
        }
    }

    public func reset() {
        scanTask?.cancel()
        cleanTask?.cancel()
        selection = SelectionState()
        lastTree = nil
        phase = .idle
    }

    /// Bỏ qua mục: thêm vào ignore list rồi gỡ khỏi lựa chọn.
    public func ignore(_ node: Node) {
        services.ignore(node)
        set(node.id, false)
    }

    /// Hiển thị trực tiếp một cây có sẵn (ví dụ Uninstaller tự dựng cây file sót).
    public func show(tree: NodeTree, selection: SelectionState? = nil) {
        lastTree = tree
        self.selection = selection ?? SelectionState.defaults(for: tree)
        phase = .results(tree)
    }
}
