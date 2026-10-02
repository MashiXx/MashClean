import Foundation
import LargeOldFilesScanning
import NodeTree
import ScanEngine
import SweepCore
import SweepStorage

/// Feature Large & Old Files (mục 11.7). Không chạy trong Smart Scan; mặc định không chọn gì (mục 15.2).
public struct LargeOldFilesFeature: FeatureScanProvider {
    public let featureID: FeatureID = .largeOldFiles
    public let includeInSmartScan = false
    public let settings: AppSettings
    public let home: URL

    public init(settings: AppSettings = .shared, home: URL = .userHome) {
        self.settings = settings
        self.home = home
    }

    /// Ngưỡng đọc từ cài đặt mỗi lần quét (`largeFileThresholdMB`, `oldFileDays`).
    public var criteria: LargeOldFilesCriteria {
        LargeOldFilesCriteria(largeThresholdMB: settings.largeFileThresholdMB > 0 ? settings.largeFileThresholdMB : 100,
                              oldDays: settings.oldFileDays > 0 ? settings.oldFileDays : 365, home: home)
    }

    public func scanTasks() -> [any ScanTask] { [LargeOldFilesTask(criteria: criteria)] }

    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let leaves = nodes.flatMap(\.removableLeaves)
        return FeatureSummary(featureID: featureID, card: .cleanup, title: String(localized: "File lớn và cũ"),
                              subtitle: leaves.isEmpty ? String(localized: "Không có file lớn hoặc cũ") : String(localized: "\(leaves.count) file cần xem lại"),
                              bytes: leaves.sum(\.size), itemCount: leaves.count)
    }
}

/// Một dòng trong bảng Large & Old.
public struct LargeOldItem: Identifiable, Sendable, Hashable {
    public let id: NodeID
    public let url: URL
    public let name: String
    public let folder: String
    public let size: ByteCount
    public let lastUsed: Date?
    public let kind: FileKind
    public let isLarge: Bool
    public let isOld: Bool
    public let isHidden: Bool

    /// Khoá sắp xếp không optional cho cột "Lần dùng cuối".
    public var lastUsedSortKey: Date { lastUsed ?? .distantPast }
    public var kindTitle: String { kind.title }

    public init(node: Node) {
        id = node.id
        url = node.url ?? URL(fileURLWithPath: "/")
        name = node.title
        folder = url.deletingLastPathComponent().path
        size = node.size
        lastUsed = node.lastAccess
        kind = FileKind.classify(url)
        isLarge = node.badges.contains(LargeOldBadges.large)
        isOld = node.badges.contains(LargeOldBadges.old)
        isHidden = node.badges.contains(LargeOldBadges.hidden)
    }

    public static func items(from tree: NodeTree) -> [LargeOldItem] {
        tree.allLeaves.filter { $0.url != nil }.map(LargeOldItem.init(node:))
    }
}

/// Bộ lọc: loại file, khoảng dung lượng, khoảng thời gian, thư mục (mục 11.7).
public struct LargeOldFilter: Sendable, Equatable {
    public enum SizeRange: String, Sendable, CaseIterable, Identifiable {
        case any, under100MB, from100MBTo1GB, from1GBTo5GB, over5GB
        public var id: String { rawValue }
        public var title: String {
            switch self {
            case .any: String(localized: "Mọi dung lượng")
            case .under100MB: String(localized: "Dưới 100 MB")
            case .from100MBTo1GB: "100 MB – 1 GB"
            case .from1GBTo5GB: "1 – 5 GB"
            case .over5GB: String(localized: "Trên 5 GB")
            }
        }

        public func contains(_ size: ByteCount) -> Bool {
            let b = size.bytes
            switch self {
            case .any: return true
            case .under100MB: return b < 100_000_000
            case .from100MBTo1GB: return b >= 100_000_000 && b < 1_000_000_000
            case .from1GBTo5GB: return b >= 1_000_000_000 && b < 5_000_000_000
            case .over5GB: return b >= 5_000_000_000
            }
        }
    }

    public enum AgeRange: String, Sendable, CaseIterable, Identifiable {
        case any, month, threeMonths, sixMonths, year, twoYears
        public var id: String { rawValue }
        public var title: String {
            switch self {
            case .any: String(localized: "Mọi thời điểm")
            case .month: String(localized: "Không dùng > 1 tháng")
            case .threeMonths: String(localized: "Không dùng > 3 tháng")
            case .sixMonths: String(localized: "Không dùng > 6 tháng")
            case .year: String(localized: "Không dùng > 1 năm")
            case .twoYears: String(localized: "Không dùng > 2 năm")
            }
        }

        var days: Int? {
            switch self {
            case .any: nil
            case .month: 30
            case .threeMonths: 90
            case .sixMonths: 180
            case .year: 365
            case .twoYears: 730
            }
        }

        public func contains(_ lastUsed: Date?, now: Date) -> Bool {
            guard let days else { return true }
            guard let lastUsed else { return true }
            return lastUsed < now.addingTimeInterval(-Double(days) * 86_400)
        }
    }

    /// Rỗng = mọi loại.
    public var kinds: Set<FileKind> = []
    public var size: SizeRange = .any
    public var age: AgeRange = .any
    /// Chỉ hiện file nằm trong thư mục này (đường dẫn tuyệt đối). `nil` = mọi thư mục.
    public var folder: String?
    public var search: String = ""

    public init() {}

    public func matches(_ item: LargeOldItem, now: Date = Date()) -> Bool {
        if !kinds.isEmpty && !kinds.contains(item.kind) { return false }
        if !size.contains(item.size) { return false }
        if !age.contains(item.lastUsed, now: now) { return false }
        if let folder, !(item.folder == folder || item.folder.hasPrefix(folder + "/")) { return false }
        if !search.isEmpty && !item.name.localizedCaseInsensitiveContains(search) { return false }
        return true
    }

    public func apply(_ items: [LargeOldItem], now: Date = Date()) -> [LargeOldItem] {
        items.filter { matches($0, now: now) }
    }

    /// Thư mục cấp 1 trong home có chứa kết quả (Downloads, Movies...), sắp theo tổng dung lượng.
    public static func folderOptions(_ items: [LargeOldItem], home: String) -> [String] {
        var totals: [String: Int64] = [:]
        let prefix = home.hasSuffix("/") ? home : home + "/"
        for item in items where item.folder.hasPrefix(prefix) || item.folder == home {
            let rest = item.folder.dropFirst(prefix.count)
            let first = rest.split(separator: "/").first.map(String.init)
            let key = first.map { prefix + $0 } ?? home
            totals[key, default: 0] += item.size.bytes
        }
        return totals.sorted { $0.value > $1.value }.map(\.key)
    }
}
