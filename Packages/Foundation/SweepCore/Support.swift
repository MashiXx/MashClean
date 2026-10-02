import Foundation
import os

/// Giá trị được bảo vệ bằng khoá, dùng cho state chia sẻ giữa các luồng mà không cần actor.
public final class Locked<Value>: @unchecked Sendable {
    private let lock = OSAllocatedUnfairLock()
    private var value: Value

    public init(_ value: Value) { self.value = value }

    public func withLock<R>(_ body: (inout Value) throws -> R) rethrows -> R {
        lock.lock()
        defer { lock.unlock() }
        return try body(&value)
    }

    public var current: Value { withLock { $0 } }
}

/// Chuỗi đa ngôn ngữ đến từ rule JSON (`{"en": "...", "vi": "..."}`).
public struct LocalizedText: Sendable, Hashable, Codable, ExpressibleByStringLiteral, ExpressibleByDictionaryLiteral, CustomStringConvertible {
    public var values: [String: String]

    public init(_ values: [String: String]) { self.values = values }
    public init(_ text: String) { values = ["vi": text, "en": text] }
    public init(stringLiteral value: String) { self.init(value) }
    public init(dictionaryLiteral elements: (String, String)...) { values = Dictionary(elements, uniquingKeysWith: { a, _ in a }) }

    public init(from decoder: any Decoder) throws {
        let c = try decoder.singleValueContainer()
        if let s = try? c.decode(String.self) {
            values = ["en": s]
        } else {
            values = try c.decode([String: String].self)
        }
    }

    public func encode(to encoder: any Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(values)
    }

    /// Ngôn ngữ ưu tiên của người dùng → tiếng Anh → tiếng Việt → bất kỳ.
    public var resolved: String {
        for lang in Locale.preferredLanguages {
            let code = String(lang.prefix(2))
            if let v = values[lang] ?? values[code] { return v }
        }
        return values["en"] ?? values["vi"] ?? values.values.first ?? ""
    }

    public var description: String { resolved }
}

/// Phiên bản macOS dạng `13.0`, so sánh được (dùng cho `conditions.minOS`).
public struct OSVersion: Sendable, Comparable, Hashable, CustomStringConvertible {
    public var major: Int
    public var minor: Int
    public var patch: Int

    public init(major: Int, minor: Int = 0, patch: Int = 0) {
        self.major = major
        self.minor = minor
        self.patch = patch
    }

    public init?(_ string: String) {
        let parts = string.split(separator: ".").map { Int($0) }
        guard let first = parts.first, let major = first else { return nil }
        self.major = major
        minor = parts.count > 1 ? (parts[1] ?? 0) : 0
        patch = parts.count > 2 ? (parts[2] ?? 0) : 0
    }

    public static var current: OSVersion {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        return OSVersion(major: v.majorVersion, minor: v.minorVersion, patch: v.patchVersion)
    }

    public static func < (l: OSVersion, r: OSVersion) -> Bool {
        (l.major, l.minor, l.patch) < (r.major, r.minor, r.patch)
    }

    /// Mã hoá thành số nguyên (dùng cho header rule bundle: phiên bản app tối thiểu).
    public var packed: UInt32 { UInt32(major) * 10_000 + UInt32(minor) * 100 + UInt32(patch) }

    public init(packed: UInt32) {
        major = Int(packed / 10_000)
        minor = Int(packed / 100 % 100)
        patch = Int(packed % 100)
    }

    public var description: String { "\(major).\(minor).\(patch)" }
}

/// Phiên bản app từ Info.plist.
public enum AppVersion {
    public static var current: OSVersion {
        let s = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0"
        return OSVersion(s) ?? OSVersion(major: 1)
    }
}

extension URL {
    /// Đường dẫn chuẩn hoá dùng làm khoá gộp trùng (mục 6.3).
    public var canonicalPath: String {
        PathPolicy.canonicalize(standardizedFileURL.path) ?? standardizedFileURL.path
    }

    public static var userHome: URL { URL(fileURLWithPath: NSHomeDirectory()) }
}

extension String {
    /// Mở rộng `~` đầu chuỗi.
    public var expandingTilde: String { (self as NSString).expandingTildeInPath }
    public var abbreviatingHome: String { (self as NSString).abbreviatingWithTildeInPath }
}

/// Chế độ chạy thử (mục 15.4): chạy toàn bộ luồng nhưng không xoá gì.
public enum DryRun {
    public static var isEnabledByEnvironment: Bool {
        let env = ProcessInfo.processInfo.environment
        return env["MASHCLEAN_DRY_RUN"] == "1" || ProcessInfo.processInfo.arguments.contains("--dry-run")
    }
}
