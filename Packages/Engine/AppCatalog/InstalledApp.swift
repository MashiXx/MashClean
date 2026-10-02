import AppKit
import FileSystemKit
import Foundation
import Security
import SweepCore

/// Một app đã cài (mục 11.3).
public struct InstalledApp: Sendable, Hashable, Identifiable {
    public var id: String { url.path }
    public let bundleID: String
    public let name: String
    public let version: String?
    public let url: URL
    public let teamID: String?
    public let size: ByteCount
    public let lastUsed: Date?
    /// Cài từ App Store (có `Contents/_MASReceipt`).
    public let isAppStore: Bool
    public let isApple: Bool
    public let executableName: String?
    public let bundleModified: Date?
    /// Trình gỡ riêng đi kèm (ví dụ `Uninstall <App>.app`), gợi ý dùng trước (mục 11.3 bước 2).
    public let bundledUninstaller: URL?

    public init(bundleID: String, name: String, version: String?, url: URL, teamID: String?, size: ByteCount, lastUsed: Date?,
                isAppStore: Bool, isApple: Bool, executableName: String?, bundleModified: Date?, bundledUninstaller: URL?) {
        self.bundleID = bundleID
        self.name = name
        self.version = version
        self.url = url
        self.teamID = teamID
        self.size = size
        self.lastUsed = lastUsed
        self.isAppStore = isAppStore
        self.isApple = isApple
        self.executableName = executableName
        self.bundleModified = bundleModified
        self.bundledUninstaller = bundledUninstaller
    }

    /// App nằm trong vùng hệ thống (không gỡ được).
    public var isSystemApp: Bool { url.path.hasPrefix("/System/") }

    public var icon: NSImage { NSWorkspace.shared.icon(forFile: url.path) }
}

/// Đọc Team ID của app qua Security.framework (mục 22.3).
public enum CodeSignature {
    public static func teamIdentifier(of appURL: URL) -> String? {
        signingInfo(of: appURL)?[kSecCodeInfoTeamIdentifier as String] as? String
    }

    public static func signingInfo(of url: URL) -> [String: Any]? {
        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &staticCode) == errSecSuccess, let code = staticCode else { return nil }
        var info: CFDictionary?
        let flags = SecCSFlags(rawValue: kSecCSSigningInformation)
        guard SecCodeCopySigningInformation(code, flags, &info) == errSecSuccess, let dict = info as? [String: Any] else { return nil }
        return dict
    }

    /// Kiểm tra chữ ký còn hợp lệ (phát hiện app bị sửa, ví dụ sau khi xoá `.lproj`).
    public static func isValid(_ url: URL) -> Bool {
        var staticCode: SecStaticCode?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &staticCode) == errSecSuccess, let code = staticCode else { return false }
        return SecStaticCodeCheckValidity(code, SecCSFlags(rawValue: 0), nil) == errSecSuccess
    }
}
