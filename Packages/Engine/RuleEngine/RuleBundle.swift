import CryptoKit
import Foundation
import SweepCore

public enum RuleError: Error, Sendable, CustomStringConvertible, Equatable {
    case badFormat
    case badMagic
    case unsupportedFormatVersion(UInt16)
    case badSignature
    case payloadLengthMismatch
    case decompressionFailed
    case invalidJSON(String)
    case appTooOld(required: String)
    case downgrade(current: UInt64, offered: UInt64)
    case checksumMismatch

    public var description: String {
        switch self {
        case .badFormat: "Rule bundle sai định dạng"
        case .badMagic: "Rule bundle sai magic"
        case let .unsupportedFormatVersion(v): "Phiên bản định dạng rule không hỗ trợ: \(v)"
        case .badSignature: "Chữ ký rule bundle không hợp lệ"
        case .payloadLengthMismatch: "Độ dài payload không khớp"
        case .decompressionFailed: "Không giải nén được payload"
        case let .invalidJSON(m): "JSON rule không hợp lệ: \(m)"
        case let .appTooOld(v): "Bộ rule cần app phiên bản \(v) trở lên"
        case let .downgrade(c, o): "Từ chối hạ cấp rule: đang dùng \(c), nhận \(o)"
        case .checksumMismatch: "SHA-256 không khớp manifest"
        }
    }
}

/// Header 22 byte của `rules.bundle` (mục 8.5). Số nguyên little-endian.
///
/// | Offset | Size | Trường |
/// |---|---|---|
/// | 0 | 4 | Magic `MSRB` |
/// | 4 | 2 | Phiên bản định dạng (`1`) |
/// | 6 | 8 | Phiên bản rule (`yyyymmddNN`) |
/// | 14 | 4 | Phiên bản app tối thiểu |
/// | 18 | 4 | Độ dài payload |
public struct RuleBundleHeader: Sendable, Equatable {
    public static let magic = Data("MSRB".utf8)
    public static let size = 22
    public static let currentFormatVersion: UInt16 = 1
    public static let signatureSize = 64

    public var formatVersion: UInt16
    public var rulesVersion: UInt64
    public var minAppVersion: UInt32
    public var payloadLength: UInt32

    public init(formatVersion: UInt16 = currentFormatVersion, rulesVersion: UInt64, minAppVersion: UInt32, payloadLength: UInt32) {
        self.formatVersion = formatVersion
        self.rulesVersion = rulesVersion
        self.minAppVersion = minAppVersion
        self.payloadLength = payloadLength
    }

    public init(parsing data: Data) throws {
        let d = Data(data)  // về chỉ số 0
        guard d.count >= Self.size else { throw RuleError.badFormat }
        guard d.prefix(4) == Self.magic else { throw RuleError.badMagic }
        formatVersion = d.readLE(UInt16.self, at: 4)
        rulesVersion = d.readLE(UInt64.self, at: 6)
        minAppVersion = d.readLE(UInt32.self, at: 14)
        payloadLength = d.readLE(UInt32.self, at: 18)
        guard formatVersion == Self.currentFormatVersion else { throw RuleError.unsupportedFormatVersion(formatVersion) }
    }

    public var serialized: Data {
        var d = Self.magic
        d.appendLE(formatVersion)
        d.appendLE(rulesVersion)
        d.appendLE(minAppVersion)
        d.appendLE(payloadLength)
        return d
    }
}

/// Bộ rule đã kiểm tra chữ ký và giải nén.
public struct RuleBundle: Sendable {
    public let header: RuleBundleHeader
    public let payload: RulePayload

    public init(header: RuleBundleHeader, json: Data) throws {
        self.header = header
        do {
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            payload = try decoder.decode(RulePayload.self, from: json)
        } catch {
            throw RuleError.invalidJSON(String(describing: error))
        }
    }

    public var version: UInt64 { header.rulesVersion }
}

/// Kiểm tra chữ ký và giải nén (mục 8.5).
public struct RuleBundleVerifier: Sendable {
    public let publicKey: Curve25519.Signing.PublicKey

    public init(publicKey: Curve25519.Signing.PublicKey) { self.publicKey = publicKey }

    public init(publicKeyBase64: String) throws {
        guard let raw = Data(base64Encoded: publicKeyBase64) else { throw RuleError.badFormat }
        publicKey = try Curve25519.Signing.PublicKey(rawRepresentation: raw)
    }

    public func verify(_ data: Data) throws -> RuleBundle {
        let data = Data(data)
        guard data.count > RuleBundleHeader.size + RuleBundleHeader.signatureSize else { throw RuleError.badFormat }
        guard data.prefix(4) == RuleBundleHeader.magic else { throw RuleError.badMagic }
        let signed = data.prefix(data.count - RuleBundleHeader.signatureSize)
        let signature = data.suffix(RuleBundleHeader.signatureSize)
        guard publicKey.isValidSignature(signature, for: signed) else { throw RuleError.badSignature }
        let header = try RuleBundleHeader(parsing: signed.prefix(RuleBundleHeader.size))
        let payload = signed.dropFirst(RuleBundleHeader.size)
        guard payload.count == Int(header.payloadLength) else { throw RuleError.payloadLengthMismatch }
        guard let json = try? (Data(payload) as NSData).decompressed(using: .lzfse) as Data else { throw RuleError.decompressionFailed }
        return try RuleBundle(header: header, json: json)
    }
}

/// Đóng gói và ký (dùng bởi `rulepack build` / `rulepack sign`). Private key chỉ nằm trong secret của CI.
public enum RuleBundleBuilder {
    public static func encodePayload(_ payload: RulePayload) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(payload)
    }

    /// Tạo phần chưa ký: header + payload LZFSE.
    public static func pack(payload: RulePayload, minAppVersion: OSVersion) throws -> Data {
        let json = try encodePayload(payload)
        let compressed = try (json as NSData).compressed(using: .lzfse) as Data
        let header = RuleBundleHeader(rulesVersion: payload.version, minAppVersion: minAppVersion.packed, payloadLength: UInt32(compressed.count))
        return header.serialized + compressed
    }

    /// Ký các byte `[0, 22+n)` bằng Ed25519 và nối chữ ký vào cuối.
    public static func sign(_ unsigned: Data, privateKey: Curve25519.Signing.PrivateKey) throws -> Data {
        let signature = try privateKey.signature(for: unsigned)
        return unsigned + signature
    }
}

extension Data {
    func readLE<T: FixedWidthInteger>(_: T.Type, at offset: Int) -> T {
        var value: T = 0
        _ = Swift.withUnsafeMutableBytes(of: &value) { dest in
            copyBytes(to: dest, from: offset..<(offset + MemoryLayout<T>.size))
        }
        return T(littleEndian: value)
    }

    mutating func appendLE<T: FixedWidthInteger>(_ value: T) {
        var le = value.littleEndian
        Swift.withUnsafeBytes(of: &le) { append(contentsOf: $0) }
    }
}
