import Foundation

/// Danh sách "không bao giờ đề xuất mục này" (bảng `ignore_entry`, mục 12.2).
public struct IgnoreList: Sendable, Hashable {
    public var paths: Set<String>
    public var rules: Set<String>
    public var bundleIDs: Set<String>

    public init(paths: Set<String> = [], rules: Set<String> = [], bundleIDs: Set<String> = []) {
        self.paths = paths
        self.rules = rules
        self.bundleIDs = bundleIDs
    }

    public static let empty = IgnoreList()

    public func ignores(path: String) -> Bool {
        guard !paths.isEmpty else { return false }
        if paths.contains(path) { return true }
        return paths.contains { path.hasPrefix($0.hasSuffix("/") ? $0 : $0 + "/") }
    }

    public func ignores(rule: String?) -> Bool { rule.map(rules.contains) ?? false }
    public func ignores(bundleID: String?) -> Bool { bundleID.map(bundleIDs.contains) ?? false }
}
