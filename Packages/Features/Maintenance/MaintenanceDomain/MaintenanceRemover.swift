import CleanEngine
import MaintenanceScanning
import Foundation
import NodeTree
import SweepCore

/// Remover cho node ảo `.maintenance(task)`: Smart Scan "Chạy" thực hiện cả tác vụ bảo trì trong cùng một Clean Plan (mục 11.1).
public struct MaintenanceRemover: Remover {
    public let id: RemoverID = .maintenance
    let runner: MaintenanceRunner

    public init(runner: MaintenanceRunner) { self.runner = runner }

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard case let .maintenance(task)? = item.virtual else {
                results.append(.skipped(item.title, .unsupported))
                continue
            }
            let label = "maintenance:\(task.rawValue)"
            if context.dryRun {
                results.append(.skipped(label, .dryRun))
                continue
            }
            let r = await runner.run(task)
            context.progress(label, 0)
            results.append(r.success ? .ok(label, freed: 0) : .failed(label, errno: EIO, r.message))
        }
        return results
    }
}
