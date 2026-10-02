import Foundation

/// Định danh ổn định của một rule (mục 8.3). Database dùng để lưu ignore list.
public struct RuleID: Hashable, Sendable, Codable, RawRepresentable, ExpressibleByStringLiteral, CustomStringConvertible {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
    public init(_ rawValue: String) { self.rawValue = rawValue }
    public init(stringLiteral value: String) { rawValue = value }
    public init(from decoder: any Decoder) throws { rawValue = try decoder.singleValueContainer().decode(String.self) }
    public func encode(to encoder: any Encoder) throws { var c = encoder.singleValueContainer(); try c.encode(rawValue) }
    public var description: String { rawValue }
}

/// Định danh remover chuyên biệt (mục 7.3), ví dụ `simulator`, `launchItem`.
public struct RemoverID: Hashable, Sendable, Codable, RawRepresentable, ExpressibleByStringLiteral, CustomStringConvertible {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
    public init(_ rawValue: String) { self.rawValue = rawValue }
    public init(stringLiteral value: String) { rawValue = value }
    public init(from decoder: any Decoder) throws { rawValue = try decoder.singleValueContainer().decode(String.self) }
    public func encode(to encoder: any Encoder) throws { var c = encoder.singleValueContainer(); try c.encode(rawValue) }
    public var description: String { rawValue }

    public static let file: RemoverID = "file"
    public static let privilegedFile: RemoverID = "privilegedFile"
    public static let simulator: RemoverID = "simulator"
    public static let simulatorRuntime: RemoverID = "simulatorRuntime"
    public static let launchItem: RemoverID = "launchItem"
    public static let localSnapshot: RemoverID = "localSnapshot"
    public static let app: RemoverID = "app"
    public static let docker: RemoverID = "docker"
    public static let maintenance: RemoverID = "maintenance"
}

/// Định danh feature (System Junk, Uninstaller...).
public struct FeatureID: Hashable, Sendable, Codable, RawRepresentable, ExpressibleByStringLiteral, CustomStringConvertible {
    public let rawValue: String
    public init(rawValue: String) { self.rawValue = rawValue }
    public init(stringLiteral value: String) { rawValue = value }
    public var description: String { rawValue }

    public static let smartScan: FeatureID = "smartScan"
    public static let systemJunk: FeatureID = "systemJunk"
    public static let uninstaller: FeatureID = "uninstaller"
    public static let spaceLens: FeatureID = "spaceLens"
    public static let maintenance: FeatureID = "maintenance"
    public static let loginItems: FeatureID = "loginItems"
    public static let largeOldFiles: FeatureID = "largeOldFiles"
    public static let duplicates: FeatureID = "duplicates"
}

/// Hằng số định danh dùng chung giữa các tiến trình (mục 3, 12.1).
public enum MashCleanIdentifiers {
    public static let appBundleID = "com.cleanboost.mac"
    public static let menuBundleID = "com.cleanboost.mac.menu"
    public static let helperLabel = "com.cleanboost.mac.helper"
    public static let helperPlistName = "com.cleanboost.mac.helper.plist"
    public static let appGroup = "group.com.cleanboost.mac"
    public static let urlScheme = "cleanboost"
    public static let logSubsystem = "com.cleanboost"
    public static let helperLogSubsystem = "com.cleanboost.mac.helper"
    public static let didCleanNotification = Notification.Name("com.cleanboost.didClean")
    public static let settingsChangedNotification = Notification.Name("com.cleanboost.settingsChanged")
    /// Phiên bản giao thức XPC hiện tại; tăng khi đổi `HelperProtocol` (mục 9.5).
    public static let helperProtocolVersion = 3
}
