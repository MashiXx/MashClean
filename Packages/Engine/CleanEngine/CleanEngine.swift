import CleanEngineCore
import FileSystemKit
import Foundation
import NodeTree
import os
import SweepCore
import SweepIPC
import SweepLogging
import SweepStorage

/// Lõi dọn dẹp (mục 7). Luồng: makePlan → (xác nhận) → execute theo batch 200 mục → ghi `clean_item_log` → báo cáo.
public final class CleanEngine: Sendable {
    public let fileSystem: FileSystemService
    public let helper: HelperClient?
    public let storage: Storage?
    public let userPolicy: PathPolicy
    public let rootPolicy: PathPolicy
    public static let batchSize = 200
    private let removers: Locked<[RemoverID: any Remover]>

    public init(fileSystem: FileSystemService, helper: HelperClient?, storage: Storage?, policy: PathPolicy = .user()) {
        self.fileSystem = fileSystem
        self.helper = helper
        self.storage = storage
        userPolicy = policy
        rootPolicy = .root
        let builtIn: [any Remover] = [
            FileRemover(), PrivilegedFileRemover(), SimulatorRemover(), SimulatorRuntimeRemover(),
            LaunchItemRemover(), LocalSnapshotRemover(), AppRemover(), DockerRemover(),
        ]
        removers = Locked(Dictionary(uniqueKeysWithValues: builtIn.map { ($0.id, $0) }))
    }

    /// Feature đăng ký remover riêng (ví dụ Maintenance đăng ký `maintenance`).
    public func register(_ remover: any Remover) {
        removers.withLock { $0[remover.id] = remover }
    }

    public func remover(for id: RemoverID) -> (any Remover)? { removers.withLock { $0[id] } }

    // MARK: Lập kế hoạch

    public func makePlan(tree: NodeTree, selection: SelectionState, sessionID: String? = nil) -> CleanPlan {
        makePlan(nodes: selection.selectedLeaves(in: tree), sessionID: sessionID)
    }

    /// Kiểm tra từng đường dẫn với PathPolicy; mục không thuộc người dùng thì chuyển sang helper nếu chính sách root cho phép.
    public func makePlan(nodes: [Node], sessionID: String? = nil) -> CleanPlan {
        var items: [CleanPlan.Item] = []
        var blocked: [CleanPlan.Blocked] = []
        for node in nodes where !node.isContainer {
            let virtual: VirtualItem? = if case let .virtual(v) = node.kind { v } else { nil }
            var requiresRoot = node.requiresRoot
            if case .custom = node.removal {
                // Remover chuyên biệt tự kiểm tra; với mục có file thật (vd plist) vẫn chặn vùng cấm.
                if let url = node.url, let v = userPolicy.isForbidden(url.path) {
                    blocked.append(.init(url: url, violation: v, title: node.title))
                    continue
                }
                if case let .launchItem(_, _, domain)? = virtual, domain != .userAgent { requiresRoot = true }
            } else if let url = node.url {
                let intent: PathPolicy.Intent = node.removal == .deleteContents ? .removeContents : .removeItem
                let roots = node.allowedRoots.isEmpty ? nil : node.allowedRoots
                do {
                    try userPolicy.check(url, intent: intent, allowedRoots: roots, checkOwnership: !requiresRoot)
                } catch let violation {
                    if violation.isOwnershipOnly || requiresRoot {
                        do {
                            try rootPolicy.check(url, intent: intent, allowedRoots: roots)
                            requiresRoot = true
                        } catch let rootViolation {
                            blocked.append(.init(url: url, violation: rootViolation, title: node.title))
                            continue
                        }
                    } else {
                        blocked.append(.init(url: url, violation: violation, title: node.title))
                        continue
                    }
                }
                if requiresRoot {
                    do { try rootPolicy.check(url, intent: intent, allowedRoots: roots) } catch {
                        blocked.append(.init(url: url, violation: error, title: node.title))
                        continue
                    }
                }
            }
            if requiresRoot, helper == nil, let url = node.url ?? URL(string: "item:\(node.id)") {
                blocked.append(.init(url: url, violation: .init(.needsAdminHelper, path: url.path), title: node.title))
                continue
            }
            items.append(CleanPlan.Item(
                nodeID: node.id, url: node.url, title: node.title, strategy: node.removal, requiresRoot: requiresRoot,
                expectedSize: node.size, ruleID: node.ruleID, category: node.category, safety: node.safety,
                allowedRoots: node.allowedRoots, virtual: virtual
            ))
        }
        return CleanPlan(items: items, blocked: blocked, sessionID: sessionID)
    }

    // MARK: Thực thi

    public func execute(
        _ plan: CleanPlan,
        dryRun: Bool = AppSettings.shared.dryRun,
        confirmPermanentDelete: @escaping @Sendable (URL) async -> Bool = { _ in false }
    ) -> AsyncStream<CleanEvent> {
        AsyncStream { continuation in
            let worker = Task(priority: .userInitiated) {
                let report = await run(plan, dryRun: dryRun, confirmPermanentDelete: confirmPermanentDelete) { continuation.yield($0) }
                continuation.yield(.finished(report))
                continuation.finish()
            }
            continuation.onTermination = { _ in worker.cancel() }
        }
    }

    public func run(
        _ plan: CleanPlan,
        dryRun: Bool,
        confirmPermanentDelete: @escaping @Sendable (URL) async -> Bool = { _ in false },
        onEvent: @escaping @Sendable (CleanEvent) -> Void = { _ in }
    ) async -> CleanReport {
        let start = Date()
        var operation = CleanOperation(sessionId: plan.sessionID, plannedBytes: plan.totalSize.bytes)
        do { try storage?.insert(operation) } catch { Log.error(.clean, "clean", "Không ghi được clean_operation: \(error)") }

        // Đo dung lượng trống trước khi dọn (mục 7.5).
        let volumes = Set(plan.items.compactMap { $0.url.flatMap { fileSystem.volume(for: $0)?.mountPoint } })
        let before = Dictionary(uniqueKeysWithValues: volumes.compactMap { mp in fileSystem.volume(for: URL(fileURLWithPath: mp)).map { (mp, $0.availableForImportantUsage) } })

        let freed = Locked<Int64>(0)
        let total = max(plan.totalSize.bytes, 1)
        let totalCount = max(plan.items.count, 1)
        let done = Locked<Int>(0)
        let context = RemoveContext(
            fileSystem: fileSystem, helper: helper, policy: userPolicy, dryRun: dryRun,
            progress: { path, delta in
                let f = freed.withLock { $0 += delta; return $0 }
                let byBytes = Double(f) / Double(total)
                let byCount = Double(done.current) / Double(totalCount)
                onEvent(.progress(fraction: min(1, max(byBytes, byCount)), currentItem: path, freedSoFar: ByteCount(f)))
            },
            confirmPermanentDelete: confirmPermanentDelete
        )

        var entries: [CleanReport.Entry] = []
        // Nhóm theo remover, giữ thứ tự xuất hiện.
        var order: [RemoverID] = []
        var groups: [RemoverID: [CleanPlan.Item]] = [:]
        for item in plan.items {
            if groups[item.removerID] == nil { order.append(item.removerID) }
            groups[item.removerID, default: []].append(item)
        }

        for removerID in order {
            guard let items = groups[removerID] else { continue }
            guard let remover = remover(for: removerID) else {
                entries += items.map { .init(item: $0, result: .skipped($0.url?.path ?? $0.title, .unsupported)) }
                continue
            }
            for start in stride(from: 0, to: items.count, by: Self.batchSize) {
                if Task.isCancelled { break }
                let batch = Array(items[start..<min(start + Self.batchSize, items.count)])
                var results = await remover.remove(batch, context: context)
                if removerID == .file { results = await retryPermissionFailures(batch: batch, results: results, context: context) }
                let batchEntries = Self.pair(batch, results)
                entries += batchEntries
                done.withLock { $0 += batch.count }
                operation.itemsOk = entries.filter { $0.result.isSuccess }.count
                operation.itemsFailed = entries.filter { if case .failed = $0.result.outcome { true } else { false } }.count
                logBatch(batchEntries, operationID: operation.id)
                let f = max(freed.current, entries.reduce(0) { $0 + $1.result.freedBytes })
                onEvent(.progress(fraction: Double(done.current) / Double(totalCount), currentItem: batch.last?.title ?? "", freedSoFar: ByteCount(f)))
            }
        }

        // Đo lại sau khi dọn: con số thực tế bên cạnh con số ước tính.
        var measured: Int64 = 0
        for (mp, value) in before {
            if let after = fileSystem.volume(for: URL(fileURLWithPath: mp))?.availableForImportantUsage { measured += max(0, after - value) }
        }
        let estimated = entries.filter { $0.result.isSuccess }.reduce(Int64(0)) { $0 + $1.result.freedBytes }
        operation.finishedAt = Date()
        operation.freedBytes = dryRun ? 0 : (before.isEmpty ? estimated : max(measured, 0))
        do { try storage?.update(operation) } catch { Log.error(.clean, "clean", "Không cập nhật được clean_operation: \(error)") }

        for e in entries where e.result.isSuccess { Analytics.shared.recordClean(category: e.item.category ?? "other", bytes: e.result.freedBytes) }
        for e in entries { if case .failed = e.result.outcome, let r = e.item.ruleID { Analytics.shared.recordRuleFailure(r.rawValue) } }
        Log.info(.clean, "clean", "Dọn \(entries.count) mục\(dryRun ? " (dry run)" : ""): ok \(operation.itemsOk), lỗi \(operation.itemsFailed), ước tính \(estimated) byte, đo được \(measured) byte")
        if !dryRun {
            DistributedNotificationCenter.default().postNotificationName(MashCleanIdentifiers.didCleanNotification, object: nil, userInfo: nil, deliverImmediately: true)
        }
        return CleanReport(operationID: operation.id, entries: entries, estimatedFreed: ByteCount(estimated),
                           measuredFreed: before.isEmpty || dryRun ? nil : ByteCount(measured), duration: Date().timeIntervalSince(start), dryRun: dryRun)
    }

    /// `EPERM`/`EACCES` với file người dùng: thử lại qua helper nếu đường dẫn nằm trong danh sách root cho phép (mục 7.4).
    private func retryPermissionFailures(batch: [CleanPlan.Item], results: [RemoveResult], context: RemoveContext) async -> [RemoveResult] {
        guard let helper, !context.dryRun else { return results }
        var output = results
        var retry: [(Int, CleanPlan.Item)] = []
        for (i, r) in results.enumerated() {
            guard case let .failed(code, _) = r.outcome, code == EPERM || code == EACCES, i < batch.count,
                  let url = batch[i].url, batch[i].strategy != .moveToTrash else { continue }
            if (try? rootPolicy.check(url, intent: batch[i].strategy == .deleteContents ? .removeContents : .removeItem)) != nil {
                retry.append((i, batch[i]))
            }
        }
        guard !retry.isEmpty else { return results }
        for mode in [RemoveMode.delete, .deleteContents] {
            let subset = retry.filter { ($0.1.strategy == .deleteContents) == (mode == .deleteContents) }
            guard !subset.isEmpty else { continue }
            if let r = try? await helper.removeItems(subset.compactMap { $0.1.url?.path }, mode: mode) {
                for (j, entry) in subset.enumerated() where j < r.count { output[entry.0] = r[j] }
            }
        }
        return output
    }

    static func pair(_ items: [CleanPlan.Item], _ results: [RemoveResult]) -> [CleanReport.Entry] {
        var entries: [CleanReport.Entry] = []
        for (i, item) in items.enumerated() {
            let r = i < results.count ? results[i] : .failed(item.url?.path ?? item.title, errno: EIO, String(localized: "Remover không trả kết quả"))
            entries.append(.init(item: item, result: r))
        }
        return entries
    }

    private func logBatch(_ entries: [CleanReport.Entry], operationID: String) {
        let logs = entries.map { e in
            CleanItemLog(operationId: operationID, path: e.result.path.isEmpty ? (e.item.url?.path ?? e.item.title) : e.result.path,
                         ruleId: e.item.ruleID?.rawValue, strategy: e.item.strategy.logValue, trashedPath: e.result.trashedPath,
                         size: e.result.isSuccess ? e.result.freedBytes : e.item.expectedSize.bytes, result: e.result.logValue)
        }
        do { try storage?.log(logs) } catch { Log.error(.clean, "clean", "Không ghi được clean_item_log: \(error)") }
    }

    // MARK: Khôi phục

    public enum RestoreError: Error, CustomStringConvertible {
        case notRestorable
        case trashedItemMissing
        case destinationExists
        case underlying(String)

        public var description: String {
            switch self {
            case .notRestorable: String(localized: "Mục này đã bị xoá vĩnh viễn, không khôi phục được")
            case .trashedItemMissing: String(localized: "Không còn tìm thấy mục trong Thùng rác")
            case .destinationExists: String(localized: "Vị trí cũ đã có file cùng tên")
            case let .underlying(m): m
            }
        }
    }

    /// Chuyển mục từ Thùng rác về vị trí cũ (mục 15.3).
    public func restore(_ log: CleanItemLog) throws {
        guard log.isRestorable, let trashed = log.trashedPath else { throw RestoreError.notRestorable }
        let fm = FileManager.default
        guard fm.fileExists(atPath: trashed) else { throw RestoreError.trashedItemMissing }
        guard !fm.fileExists(atPath: log.path) else { throw RestoreError.destinationExists }
        do {
            try fm.createDirectory(at: URL(fileURLWithPath: log.path).deletingLastPathComponent(), withIntermediateDirectories: true)
            try fm.moveItem(atPath: trashed, toPath: log.path)
            try storage?.markRestored(operationID: log.operationId, path: log.path)
        } catch {
            throw RestoreError.underlying(error.localizedDescription)
        }
    }
}
