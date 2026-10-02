import Foundation

/// Dung lượng tính bằng byte. Dùng `Int64` để khớp với các API của Foundation và SQLite.
public struct ByteCount: Hashable, Comparable, Sendable, Codable, AdditiveArithmetic, ExpressibleByIntegerLiteral, CustomStringConvertible {
    public var bytes: Int64

    public init(_ bytes: Int64) { self.bytes = bytes }
    public init(_ bytes: UInt64) { self.bytes = Int64(clamping: bytes) }
    public init(_ bytes: Int) { self.bytes = Int64(bytes) }
    public init(integerLiteral value: Int64) { bytes = value }

    public init(from decoder: any Decoder) throws {
        bytes = try decoder.singleValueContainer().decode(Int64.self)
    }

    public func encode(to encoder: any Encoder) throws {
        var c = encoder.singleValueContainer()
        try c.encode(bytes)
    }

    public static let zero = ByteCount(Int64(0))
    public static func kilobytes(_ v: Int64) -> ByteCount { ByteCount(v * 1_000) }
    public static func megabytes(_ v: Int64) -> ByteCount { ByteCount(v * 1_000_000) }
    public static func gigabytes(_ v: Int64) -> ByteCount { ByteCount(v * 1_000_000_000) }

    public static func + (l: ByteCount, r: ByteCount) -> ByteCount { ByteCount(l.bytes &+ r.bytes) }
    public static func - (l: ByteCount, r: ByteCount) -> ByteCount { ByteCount(l.bytes &- r.bytes) }
    public static func < (l: ByteCount, r: ByteCount) -> Bool { l.bytes < r.bytes }

    /// "1,2 GB" theo đơn vị thập phân giống Finder.
    public var formatted: String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    public var description: String { formatted }
}

extension Sequence {
    public func sum(_ keyPath: (Element) -> ByteCount) -> ByteCount {
        reduce(into: ByteCount.zero) { $0 = $0 + keyPath($1) }
    }
}
