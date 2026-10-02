import Foundation
import SweepCore

/// Một rule dọn dẹp (mục 8.2, 8.3).
public struct Rule: Sendable, Codable, Hashable, Identifiable {
    public struct Match: Sendable, Codable, Hashable {
        /// Glob: hỗ trợ `~`, `*`, `**`, biến `${bundleID}`, `${teamID}`, `${appName}`, `${userHome}`.
        public var paths: [String]
        /// Áp rule cho từng app trong `installedApps`.
        public var forEachInstalledApp: Bool?
        /// Chỉ lấy file không được truy cập/sửa trong N ngày.
        public var minAgeDays: Int?
        /// Bỏ qua mục nhỏ hơn ngưỡng.
        public var minSizeBytes: Int64?
        /// Chỉ áp cho app có bundle ID khớp (glob), dùng cho rule `app.<bundleID>.leftovers`.
        public var bundleIDs: [String]?
        /// Nguồn dữ liệu đặc biệt cài trong code (ví dụ `simctl.unavailableDevices`), khi đường dẫn không đủ diễn tả.
        public var provider: String?

        public init(paths: [String] = [], forEachInstalledApp: Bool? = nil, minAgeDays: Int? = nil, minSizeBytes: Int64? = nil,
                    bundleIDs: [String]? = nil, provider: String? = nil) {
            self.paths = paths
            self.forEachInstalledApp = forEachInstalledApp
            self.minAgeDays = minAgeDays
            self.minSizeBytes = minSizeBytes
            self.bundleIDs = bundleIDs
            self.provider = provider
        }
    }

    public struct Conditions: Sendable, Codable, Hashable {
        /// Không đề xuất xoá khi app đang chạy.
        public var appNotRunning: [String]?
        public var minOS: String?

        public init(appNotRunning: [String]? = nil, minOS: String? = nil) {
            self.appNotRunning = appNotRunning
            self.minOS = minOS
        }
    }

    public var id: RuleID
    public var version: Int?
    public var category: String
    public var title: LocalizedText?
    public var reason: LocalizedText?
    public var safety: SafetyLevel
    public var removal: RemovalStrategy
    public var requiresRoot: Bool?
    public var match: Match
    public var conditions: Conditions?
    /// Loại trừ, luôn thắng `match`.
    public var exclude: [String]?
    /// Ghi chú cho người review rule (không hiển thị).
    public var notes: String?

    public init(id: RuleID, version: Int? = 1, category: String, title: LocalizedText? = nil, reason: LocalizedText? = nil,
                safety: SafetyLevel, removal: RemovalStrategy, requiresRoot: Bool? = false, match: Match,
                conditions: Conditions? = nil, exclude: [String]? = nil, notes: String? = nil) {
        self.id = id
        self.version = version
        self.category = category
        self.title = title
        self.reason = reason
        self.safety = safety
        self.removal = removal
        self.requiresRoot = requiresRoot
        self.match = match
        self.conditions = conditions
        self.exclude = exclude
        self.notes = notes
    }

    enum CodingKeys: String, CodingKey {
        case id, version, category, title, reason, safety, removal, requiresRoot, match, conditions, exclude, notes
    }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(RuleID.self, forKey: .id)
        version = try c.decodeIfPresent(Int.self, forKey: .version)
        category = try c.decode(String.self, forKey: .category)
        title = try c.decodeIfPresent(LocalizedText.self, forKey: .title)
        reason = try c.decodeIfPresent(LocalizedText.self, forKey: .reason)
        let safetyRaw = try c.decode(String.self, forKey: .safety)
        guard let s = SafetyLevel(ruleValue: safetyRaw) else {
            throw DecodingError.dataCorruptedError(forKey: .safety, in: c, debugDescription: String(localized: "safety phải là safe/review/risky, nhận \(safetyRaw)"))
        }
        safety = s
        removal = try c.decode(RemovalStrategy.self, forKey: .removal)
        requiresRoot = try c.decodeIfPresent(Bool.self, forKey: .requiresRoot)
        match = try c.decode(Match.self, forKey: .match)
        conditions = try c.decodeIfPresent(Conditions.self, forKey: .conditions)
        exclude = try c.decodeIfPresent([String].self, forKey: .exclude)
        notes = try c.decodeIfPresent(String.self, forKey: .notes)
    }

    public func encode(to encoder: any Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(id, forKey: .id)
        try c.encodeIfPresent(version, forKey: .version)
        try c.encode(category, forKey: .category)
        try c.encodeIfPresent(title, forKey: .title)
        try c.encodeIfPresent(reason, forKey: .reason)
        try c.encode(safety.ruleValue, forKey: .safety)
        try c.encode(removal, forKey: .removal)
        try c.encodeIfPresent(requiresRoot, forKey: .requiresRoot)
        try c.encode(match, forKey: .match)
        try c.encodeIfPresent(conditions, forKey: .conditions)
        try c.encodeIfPresent(exclude, forKey: .exclude)
        try c.encodeIfPresent(notes, forKey: .notes)
    }

    public var displayTitle: String { title?.resolved ?? id.rawValue }
    public var usesAppVariables: Bool {
        (match.forEachInstalledApp ?? false) || match.paths.contains { $0.contains("${bundleID}") || $0.contains("${teamID}") || $0.contains("${appName}") }
    }
}

/// Danh mục rule (mục 8.4). Tên hiển thị và icon dùng cho nhóm trên UI.
public enum RuleCategory {
    public static let userCaches = "userCaches"
    public static let systemCaches = "systemCaches"
    public static let userLogs = "userLogs"
    public static let systemLogs = "systemLogs"
    public static let crashReports = "crashReports"
    public static let xcodeJunk = "xcodeJunk"
    public static let devToolCaches = "devToolCaches"
    public static let languageFiles = "languageFiles"
    public static let iosBackups = "iosBackups"
    public static let iosSoftwareUpdates = "iosSoftwareUpdates"
    public static let trash = "trash"
    public static let oldDownloads = "oldDownloads"
    public static let mailAttachments = "mailAttachments"
    public static let appLeftovers = "appLeftovers"

    public static let all = [userCaches, systemCaches, userLogs, systemLogs, crashReports, xcodeJunk, devToolCaches,
                             languageFiles, iosBackups, iosSoftwareUpdates, trash, oldDownloads, mailAttachments, appLeftovers]

    public static func title(_ category: String) -> LocalizedText {
        switch category {
        case userCaches: ["vi": "Cache người dùng", "en": "User Cache Files"]
        case systemCaches: ["vi": "Cache hệ thống", "en": "System Cache Files"]
        case userLogs: ["vi": "Log người dùng", "en": "User Log Files"]
        case systemLogs: ["vi": "Log hệ thống", "en": "System Log Files"]
        case crashReports: ["vi": "Báo cáo crash", "en": "Crash Reports"]
        case xcodeJunk: ["vi": "Rác Xcode", "en": "Xcode Junk"]
        case devToolCaches: ["vi": "Cache công cụ lập trình", "en": "Developer Tool Caches"]
        case languageFiles: ["vi": "Gói ngôn ngữ", "en": "Language Files"]
        case iosBackups: ["vi": "Bản sao lưu iOS", "en": "iOS Device Backups"]
        case iosSoftwareUpdates: ["vi": "Bản cài iOS cũ", "en": "Old iOS Software Updates"]
        case trash: ["vi": "Thùng rác", "en": "Trash Bins"]
        case oldDownloads: ["vi": "Bản tải về cũ", "en": "Old Downloads"]
        case mailAttachments: ["vi": "Tệp đính kèm Mail", "en": "Mail Attachments"]
        case appLeftovers: ["vi": "File sót của app", "en": "App Leftovers"]
        default: LocalizedText(category)
        }
    }

    public static func icon(_ category: String) -> String {
        switch category {
        case userCaches: "archivebox"
        case systemCaches: "gearshape.2"
        case userLogs, systemLogs: "doc.text"
        case crashReports: "exclamationmark.triangle"
        case xcodeJunk: "hammer"
        case devToolCaches: "chevron.left.forwardslash.chevron.right"
        case languageFiles: "globe"
        case iosBackups: "iphone"
        case iosSoftwareUpdates: "arrow.down.app"
        case trash: "trash"
        case oldDownloads: "arrow.down.circle"
        case mailAttachments: "paperclip"
        case appLeftovers: "puzzlepiece.extension"
        default: "folder"
        }
    }
}

/// Tri thức đi kèm bộ rule (thay cho `info.cmmkb` của CleanMyMac, mục 8, ANALYSIS mục 5).
public struct Knowledge: Sendable, Codable, Hashable {
    /// Tiền tố bundle ID của Apple: không bao giờ coi là file sót.
    public var appleBundlePrefixes: [String]
    /// Tên thư mục/bundle ID không bao giờ coi là file sót của app đã gỡ (mục 11.3).
    public var orphanWhitelist: [String]
    /// Tên thư mục trong `~/Library/Caches` không bao giờ đề xuất xoá.
    public var protectedCacheNames: [String]
    /// Ngôn ngữ luôn giữ khi dọn gói ngôn ngữ.
    public var keepLanguages: [String]

    public init(appleBundlePrefixes: [String] = ["com.apple."], orphanWhitelist: [String] = [], protectedCacheNames: [String] = [], keepLanguages: [String] = ["Base", "en"]) {
        self.appleBundlePrefixes = appleBundlePrefixes
        self.orphanWhitelist = orphanWhitelist
        self.protectedCacheNames = protectedCacheNames
        self.keepLanguages = keepLanguages
    }

    public func isApple(_ bundleID: String) -> Bool {
        appleBundlePrefixes.contains { bundleID.hasPrefix($0) }
    }

    public func isWhitelistedOrphan(_ name: String) -> Bool {
        orphanWhitelist.contains { Glob.componentMatchesPublic($0, name) }
    }
}

extension Glob {
    public static func componentMatchesPublic(_ pattern: String, _ name: String) -> Bool {
        fnmatch(pattern, name, 0) == 0
    }
}

/// Payload JSON bên trong `rules.bundle`.
public struct RulePayload: Sendable, Codable {
    public var version: UInt64
    public var generatedAt: Date?
    public var rules: [Rule]
    public var knowledge: Knowledge

    public init(version: UInt64, generatedAt: Date? = Date(), rules: [Rule], knowledge: Knowledge) {
        self.version = version
        self.generatedAt = generatedAt
        self.rules = rules
        self.knowledge = knowledge
    }
}
