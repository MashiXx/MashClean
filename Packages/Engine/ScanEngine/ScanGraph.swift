import Foundation

public enum ScanGraphError: Error, Sendable, Equatable, CustomStringConvertible {
    case cycle([ScanTaskID])
    case missingDependency(task: ScanTaskID, dependency: ScanTaskID)

    public var description: String {
        switch self {
        case let .cycle(ids): String(localized: "Graph quét có chu trình: \(ids.map(\.rawValue).joined(separator: ", "))")
        case let .missingDependency(t, d): String(localized: "Task \(t) phụ thuộc task không tồn tại \(d)")
        }
    }
}

/// DAG các task (mục 5.1). Kiểm tra không có chu trình bằng thuật toán Kahn ngay khi tạo (mục 5.3 bước 1).
public struct ScanGraph: Sendable {
    public let tasks: [any ScanTask]
    /// Thứ tự topo (ổn định theo thứ tự thêm vào).
    public let topologicalOrder: [ScanTaskID]
    public let dependents: [ScanTaskID: [ScanTaskID]]

    /// - Parameter tasks: task trùng id chỉ giữ bản đầu tiên (nhiều feature cùng cần `installedApps`).
    public init(tasks: [any ScanTask]) throws {
        var seen = Set<ScanTaskID>()
        var unique: [any ScanTask] = []
        for t in tasks where seen.insert(t.id).inserted { unique.append(t) }
        self.tasks = unique

        let ids = Set(unique.map(\.id))
        var indegree: [ScanTaskID: Int] = [:]
        var dependents: [ScanTaskID: [ScanTaskID]] = [:]
        for t in unique {
            indegree[t.id, default: 0] += 0
            for d in Set(t.dependencies) {
                guard ids.contains(d) else { throw ScanGraphError.missingDependency(task: t.id, dependency: d) }
                indegree[t.id, default: 0] += 1
                dependents[d, default: []].append(t.id)
            }
        }
        self.dependents = dependents

        // Kahn
        var queue = unique.map(\.id).filter { indegree[$0] == 0 }
        var order: [ScanTaskID] = []
        var head = 0
        while head < queue.count {
            let id = queue[head]
            head += 1
            order.append(id)
            for dep in dependents[id] ?? [] {
                indegree[dep]! -= 1
                if indegree[dep] == 0 { queue.append(dep) }
            }
        }
        guard order.count == unique.count else {
            throw ScanGraphError.cycle(unique.map(\.id).filter { (indegree[$0] ?? 0) > 0 })
        }
        topologicalOrder = order
    }

    public func task(_ id: ScanTaskID) -> (any ScanTask)? { tasks.first { $0.id == id } }

    /// Mọi task phụ thuộc trực tiếp hoặc gián tiếp vào `id`.
    public func transitiveDependents(of id: ScanTaskID) -> Set<ScanTaskID> {
        var result = Set<ScanTaskID>()
        var stack = dependents[id] ?? []
        while let next = stack.popLast() {
            if result.insert(next).inserted { stack += dependents[next] ?? [] }
        }
        return result
    }
}
