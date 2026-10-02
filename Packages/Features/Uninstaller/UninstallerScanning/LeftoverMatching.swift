import Foundation
import NodeTree
import RuleEngine
import SweepCore

extension RemoverID {
    /// `pkgutil --forget <pkgid>` qua helper (mục 11.3 bước 6).
    public static let pkgForget: RemoverID = "pkgForget"
}

/// Nhóm kết quả riêng của Uninstaller.
public enum UninstallerCategory {
    /// File sót của một app đang gỡ (rule `app.<bundleID>.leftovers` cũng thuộc nhóm này, mục 8.2).
    public static let appLeftovers = RuleCategory.appLeftovers
    /// File sót của app đã bị xoá tay (mục 11.3).
    public static let orphanedLeftovers = "orphanedLeftovers"
}

/// Mức tin cậy khi khớp file sót với app (bảng mục 11.3).
public enum LeftoverConfidence: Int, Sendable, Comparable, CaseIterable, Hashable {
    case certain = 0
    case high = 1
    case medium = 2
    case low = 3

    public static func < (l: LeftoverConfidence, r: LeftoverConfidence) -> Bool { l.rawValue < r.rawValue }

    public var title: String {
        switch self {
        case .certain: "Chắc chắn"
        case .high: "Cao"
        case .medium: "Trung bình"
        case .low: "Thấp"
        }
    }

    public var explanation: String {
        switch self {
        case .certain: "Khớp chính xác bundle ID hoặc Team ID của app"
        case .high: "Khớp tên app trong thư mục chuẩn, hoặc LaunchAgent/Daemon chạy file của app"
        case .medium: "Do gói cài đặt (pkg) của app tạo ra"
        case .low: "Tên gần giống app, hãy xem lại trước khi xoá"
        }
    }

    public var symbol: String {
        switch self {
        case .certain: "checkmark.seal.fill"
        case .high: "checkmark.circle"
        case .medium: "shippingbox"
        case .low: "questionmark.circle"
        }
    }

    /// Chắc chắn/Cao → safe (chọn sẵn); Trung bình/Thấp → review (mục 11.3, 23.5).
    public var safety: SafetyLevel { self <= .high ? .safe : .review }
}

/// Nhận diện tên dạng reverse-DNS (`com.vendor.app`), dùng cho task `orphanedLeftovers`.
public enum ReverseDNS {
    /// Ít nhất 3 thành phần, thành phần đầu là chữ (tld), không có khoảng trắng hay ký tự lạ.
    public static func isReverseDNS(_ name: String) -> Bool {
        let parts = name.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count >= 3, name.count <= 255 else { return false }
        guard let tld = parts.first, (2...10).contains(tld.count), tld.allSatisfy({ $0.isASCII && $0.isLetter }) else { return false }
        for p in parts {
            guard !p.isEmpty else { return false }
            guard p.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") }) else { return false }
        }
        // Thành phần thứ hai (tên hãng) phải có chữ cái: loại `com.1.2`
        return parts[1].contains { $0.isLetter }
    }

    /// Tên mục → bundle ID: bỏ đuôi `.plist`, `.savedState`, `.binarycookies`.
    public static func bundleID(fromEntryName name: String) -> String? {
        let stripped = LeftoverMatcher.strippedName(name)
        return isReverseDNS(stripped) ? stripped : nil
    }

    /// `com.vendor.app` → `com.vendor`.
    public static func vendorPrefix(_ bundleID: String) -> String? {
        let parts = bundleID.lowercased().split(separator: ".")
        guard parts.count >= 3 else { return nil }
        return parts.prefix(2).joined(separator: ".")
    }
}

/// Các phép khớp thuần (tách ra để test).
public enum LeftoverMatcher {
    static let knownSuffixes = [".plist", ".savedState", ".binarycookies"]

    public static func strippedName(_ name: String) -> String {
        for s in knownSuffixes where name.hasSuffix(s) && name.count > s.count { return String(name.dropLast(s.count)) }
        return name
    }

    /// Tên mục khớp bundle ID: bằng nhau (không phân biệt hoa thường) hoặc bắt đầu bằng `<bundleID>.`.
    public static func matchesBundleID(_ name: String, bundleID: String) -> Bool {
        let n = strippedName(name).lowercased()
        let id = bundleID.lowercased()
        guard !id.isEmpty else { return false }
        return n == id || n.hasPrefix(id + ".")
    }

    /// Tên mục thuộc một app khác có bundle ID dài hơn (vd `com.foo.bar.pro` khi gỡ `com.foo.bar`).
    public static func belongsToOtherApp(_ name: String, bundleID: String, otherBundleIDs: [String]) -> Bool {
        let n = strippedName(name).lowercased()
        let mine = bundleID.lowercased()
        return otherBundleIDs.contains { other in
            let o = other.lowercased()
            guard o != mine, o.count > mine.count else { return false }
            return n == o || n.hasPrefix(o + ".")
        }
    }

    /// Group Container: `group.<bundleID>...` hoặc `<teamID>.<bundleID>...` là chắc chắn của app.
    public static func groupContainerMatchesBundle(_ name: String, bundleID: String, teamID: String?) -> Bool {
        if matchesBundleID(name, bundleID: "group." + bundleID) { return true }
        if let teamID, matchesBundleID(name, bundleID: teamID + "." + bundleID) { return true }
        return false
    }

    /// `<teamID>.*`.
    public static func groupContainerMatchesTeam(_ name: String, teamID: String?) -> Bool {
        guard let teamID, !teamID.isEmpty else { return false }
        return name.hasPrefix(teamID + ".")
    }

    /// Bỏ khoảng trắng, dấu gạch, dấu chấm, chữ thường: "Visual Studio Code" → "visualstudiocode".
    public static func normalized(_ s: String) -> String {
        String(strippedName(s).lowercased().filter { $0.isLetter || $0.isNumber })
    }

    /// Khớp chính xác tên app (không phân biệt hoa thường), dùng cho mức Cao.
    public static func matchesAppName(_ name: String, appNames: [String]) -> Bool {
        let n = name.lowercased()
        return appNames.contains { $0.count >= 3 && $0.lowercased() == n }
    }

    /// Tên gần giống (mức Thấp): chứa tên app đã chuẩn hoá (≥ 4 ký tự), hoặc chứa thành phần cuối của bundle ID.
    public static func isSimilarName(_ name: String, appName: String, bundleID: String) -> Bool {
        let n = normalized(name)
        guard !n.isEmpty else { return false }
        var keys = [normalized(appName)]
        if let last = bundleID.split(separator: ".").last { keys.append(normalized(String(last))) }
        return keys.contains { $0.count >= 4 && n.contains($0) }
    }

    /// Đường dẫn nằm trong bundle app.
    public static func isPath(_ path: String, inside appPath: String) -> Bool {
        let a = appPath.hasSuffix("/") ? String(appPath.dropLast()) : appPath
        return path == a || path.hasPrefix(a + "/")
    }

    /// `/Applications/Foo.app/Contents/MacOS/foo` → `/Applications/Foo.app` (bundle `.app` ngoài cùng).
    public static func appBundle(containing path: String) -> String? {
        if let r = path.range(of: ".app/") { return String(path[..<path.index(before: r.upperBound)]) }
        return path.hasSuffix(".app") ? path : nil
    }
}

/// Thông tin tối thiểu trong plist launchd, đủ để khớp với app (mục 11.3).
public struct LaunchPlistInfo: Sendable, Hashable {
    public var label: String
    /// `Program` hoặc `ProgramArguments[0]`.
    public var executable: String?
    public var associatedBundleIDs: [String]

    public init(label: String, executable: String?, associatedBundleIDs: [String] = []) {
        self.label = label
        self.executable = executable
        self.associatedBundleIDs = associatedBundleIDs
    }

    public static func parse(_ data: Data) -> LaunchPlistInfo? {
        guard let dict = (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String: Any],
              let label = dict["Label"] as? String, !label.isEmpty else { return nil }
        let program = (dict["Program"] as? String) ?? (dict["ProgramArguments"] as? [String])?.first
        let associated = dict["AssociatedBundleIdentifiers"]
        let ids: [String] = (associated as? [String]) ?? ((associated as? String).map { [$0] } ?? [])
        return LaunchPlistInfo(label: label, executable: program, associatedBundleIDs: ids)
    }

    /// Mức tin cậy plist thuộc app: label/tên file khớp bundle ID → Chắc chắn; chạy file trong app
    /// hoặc khai báo `AssociatedBundleIdentifiers` → Cao.
    public func confidence(fileName: String, bundleID: String, appPath: String) -> LeftoverConfidence? {
        if LeftoverMatcher.matchesBundleID(label, bundleID: bundleID) || LeftoverMatcher.matchesBundleID(fileName, bundleID: bundleID) {
            return .certain
        }
        if let executable, LeftoverMatcher.isPath(executable, inside: appPath) { return .high }
        if associatedBundleIDs.contains(where: { $0.caseInsensitiveCompare(bundleID) == .orderedSame }) { return .high }
        return nil
    }
}
