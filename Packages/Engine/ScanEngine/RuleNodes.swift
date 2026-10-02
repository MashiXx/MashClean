import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import SweepCore

extension RuleMatch {
    /// Dựng node từ một mục khớp rule.
    public func makeNode(title: String? = nil, safety: SafetyLevel? = nil, badges: [String] = []) -> Node {
        let kind: NodeKind = measurement.isDirectory ? .directory(url, recursive: true) : .file(url)
        return Node(
            kind: kind,
            title: title ?? FileManager.default.displayName(atPath: url.path),
            size: ByteCount(measurement.allocatedSize),
            itemCount: measurement.itemCount,
            safety: safety ?? rule.safety,
            reason: rule.reason ?? "",
            ruleID: rule.id,
            category: rule.category,
            removal: rule.removal,
            requiresRoot: rule.requiresRoot ?? false,
            allowedRoots: [allowedRoot],
            lastAccess: measurement.lastUse,
            badges: badges
        )
    }
}

extension Rule {
    /// Node cho mục do provider tạo ra (simulator, snapshot...), lấy metadata từ rule.
    public func makeVirtualNode(_ item: VirtualItem, title: String? = nil, size: ByteCount, itemCount: Int = 1, badges: [String] = [], safety: SafetyLevel? = nil) -> Node {
        Node(kind: .virtual(item), title: title, size: size, itemCount: itemCount, safety: safety ?? self.safety, reason: reason ?? "",
             ruleID: id, category: category, removal: removal, requiresRoot: requiresRoot ?? false, badges: badges)
    }

    public func makeFileNode(_ url: URL, measurement: PathMeasurement, title: String? = nil, safety: SafetyLevel? = nil, allowedRoot: String? = nil, badges: [String] = []) -> Node {
        Node(kind: measurement.isDirectory ? .directory(url, recursive: true) : .file(url), title: title ?? url.lastPathComponent,
             size: ByteCount(measurement.allocatedSize), itemCount: measurement.itemCount, safety: safety ?? self.safety, reason: reason ?? "",
             ruleID: id, category: category, removal: removal, requiresRoot: requiresRoot ?? false,
             allowedRoots: allowedRoot.map { [$0] } ?? [], lastAccess: measurement.lastUse, badges: badges)
    }
}

/// Phát hiện thư mục bị TCC chặn (cần Full Disk Access).
public enum AccessProbe {
    public static func isBlockedByTCC(_ path: String) -> Bool {
        let fd = open(path, O_RDONLY | O_DIRECTORY)
        if fd >= 0 {
            close(fd)
            return false
        }
        return errno == EPERM
    }
}
