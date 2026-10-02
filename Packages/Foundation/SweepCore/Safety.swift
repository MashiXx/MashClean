import Foundation

/// Mức an toàn của một mục đề xuất xoá (mục 6.1).
public enum SafetyLevel: Int, Sendable, Comparable, Codable, CaseIterable {
    case safe = 0     // tự chọn sẵn
    case review = 1   // hiển thị nhưng không tự chọn
    case risky = 2    // ẩn trong "Nâng cao", cần xác nhận

    public static func < (l: SafetyLevel, r: SafetyLevel) -> Bool { l.rawValue < r.rawValue }

    public init?(ruleValue: String) {
        switch ruleValue {
        case "safe": self = .safe
        case "review": self = .review
        case "risky": self = .risky
        default: return nil
        }
    }

    public var ruleValue: String {
        switch self {
        case .safe: "safe"
        case .review: "review"
        case .risky: "risky"
        }
    }

    public var localizedTitle: String {
        switch self {
        case .safe: "An toàn"
        case .review: "Cần xem lại"
        case .risky: "Rủi ro"
        }
    }

    public func raised(to other: SafetyLevel) -> SafetyLevel { max(self, other) }
}

/// Chiến lược xoá (mục 7.3).
public enum RemovalStrategy: Sendable, Hashable, Codable, CustomStringConvertible {
    case moveToTrash        // mặc định cho file người dùng
    case delete             // cache, log: xoá thẳng (tái tạo được)
    case deleteContents     // giữ thư mục, xoá nội dung (vd Caches/<bundle>)
    case custom(RemoverID)  // remover chuyên biệt

    /// Đọc từ trường `removal` của rule: `moveToTrash` / `delete` / `deleteContents` / `custom:<id>`.
    public init?(ruleValue: String) {
        switch ruleValue {
        case "moveToTrash": self = .moveToTrash
        case "delete": self = .delete
        case "deleteContents": self = .deleteContents
        default:
            guard ruleValue.hasPrefix("custom:") else { return nil }
            let id = String(ruleValue.dropFirst("custom:".count))
            guard !id.isEmpty else { return nil }
            self = .custom(RemoverID(id))
        }
    }

    public var ruleValue: String {
        switch self {
        case .moveToTrash: "moveToTrash"
        case .delete: "delete"
        case .deleteContents: "deleteContents"
        case let .custom(id): "custom:\(id.rawValue)"
        }
    }

    /// Giá trị cột `clean_item_log.strategy` (mục 12.2).
    public var logValue: String {
        switch self {
        case .moveToTrash: "trash"
        case .delete: "delete"
        case .deleteContents: "deleteContents"
        case let .custom(id): "custom:\(id.rawValue)"
        }
    }

    public var isRecoverable: Bool { self == .moveToTrash }

    public var description: String { ruleValue }

    public init(from decoder: any Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        guard let value = RemovalStrategy(ruleValue: raw) else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "Unknown removal strategy \(raw)"))
        }
        self = value
    }

    public func encode(to encoder: any Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(ruleValue)
    }
}

/// Chế độ xoá gửi sang helper (mục 9.2), dạng `Int` để đi qua XPC.
public enum RemoveMode: Int, Sendable, Codable {
    case delete = 0
    case deleteContents = 1
}

/// Kết quả xoá một đường dẫn, trả về từ remover hoặc helper (JSON có phiên bản, mục 9.2).
public struct RemoveResult: Sendable, Codable, Hashable {
    public enum Outcome: Sendable, Codable, Hashable {
        case ok
        case skipped(reason: SkipReason)
        case failed(code: Int32, message: String)
    }

    public enum SkipReason: String, Sendable, Codable, Hashable {
        case inUse            // file bị khoá hoặc app đang mở
        case notFound         // không còn tồn tại: coi như thành công, giải phóng 0
        case blockedByPolicy  // PathPolicy chặn
        case protectedBySIP   // SIP chặn
        case userDeclined     // người dùng không đồng ý xoá vĩnh viễn
        case dryRun           // chế độ thử
        case appRunning
        case unsupported
    }

    public var path: String
    public var outcome: Outcome
    public var freedBytes: Int64
    /// Đường dẫn trong Thùng rác (nếu chuyển vào Thùng rác), để khôi phục (mục 15.3).
    public var trashedPath: String?

    public init(path: String, outcome: Outcome, freedBytes: Int64 = 0, trashedPath: String? = nil) {
        self.path = path
        self.outcome = outcome
        self.freedBytes = freedBytes
        self.trashedPath = trashedPath
    }

    public var isSuccess: Bool {
        switch outcome {
        case .ok: true
        case .skipped(reason: .notFound): true
        default: false
        }
    }

    /// Giá trị cột `clean_item_log.result`: `ok | skipped | failed:<code>`.
    public var logValue: String {
        switch outcome {
        case .ok: "ok"
        case let .skipped(reason): "skipped:\(reason.rawValue)"
        case let .failed(code, _): "failed:\(code)"
        }
    }

    public static func ok(_ path: String, freed: Int64, trashedPath: String? = nil) -> RemoveResult {
        RemoveResult(path: path, outcome: .ok, freedBytes: freed, trashedPath: trashedPath)
    }

    public static func skipped(_ path: String, _ reason: SkipReason) -> RemoveResult {
        RemoveResult(path: path, outcome: .skipped(reason: reason))
    }

    public static func failed(_ path: String, errno code: Int32, _ message: String? = nil) -> RemoveResult {
        RemoveResult(path: path, outcome: .failed(code: code, message: message ?? String(cString: strerror(code))))
    }
}

/// Phong bì JSON có phiên bản cho dữ liệu trả về qua XPC.
public struct VersionedPayload<T: Codable & Sendable>: Codable, Sendable {
    public var version: Int
    public var value: T

    public init(_ value: T, version: Int = MashCleanIdentifiers.helperProtocolVersion) {
        self.version = version
        self.value = value
    }

    public static func encode(_ value: T) -> Data {
        (try? JSONEncoder().encode(VersionedPayload(value))) ?? Data()
    }

    public static func decode(_ data: Data) throws -> T {
        try JSONDecoder().decode(VersionedPayload<T>.self, from: data).value
    }
}
