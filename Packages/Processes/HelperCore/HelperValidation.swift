import Darwin
import Foundation
import SweepCore

/// Kiểm tra tham số phía helper (mục 3.2, 9.4). Không tin bất kỳ kiểm tra nào đã làm ở app.
public enum HelperValidation {
    /// Tối đa 10.000 đường dẫn mỗi lần gọi `removeItems` (mục 9.4.4).
    public static let maxPathsPerCall = 10_000

    /// Thư mục chứa plist launchd mà helper được phép bootout + xoá.
    public static let launchItemDirectories = ["/Library/LaunchDaemons", "/Library/LaunchAgents"]

    /// Label launchd: chỉ ký tự an toàn, không phải của Apple, không phải chính helper.
    public static func isValidLabel(_ label: String) -> Bool {
        guard (1...255).contains(label.utf8.count) else { return false }
        guard label.range(of: #"^[A-Za-z0-9][A-Za-z0-9._-]*$"#, options: .regularExpression) != nil else { return false }
        let lower = label.lowercased()
        if lower.hasPrefix("com.apple.") { return false }
        if lower == MashCleanIdentifiers.helperLabel { return false }
        return true
    }

    /// Package id dạng reverse-DNS (`com.vendor.pkg.app`), không phải gói của Apple.
    public static func isValidPackageID(_ id: String) -> Bool {
        guard (3...255).contains(id.utf8.count) else { return false }
        guard id.range(of: #"^[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9][A-Za-z0-9_-]*)+$"#, options: .regularExpression) != nil else { return false }
        return !id.lowercased().hasPrefix("com.apple.")
    }

    /// Ngày snapshot Time Machine: `YYYY-MM-DD-HHMMSS`.
    public static func isValidSnapshotDate(_ date: String) -> Bool {
        guard date.range(of: #"^[0-9]{4}-[0-9]{2}-[0-9]{2}-[0-9]{6}$"#, options: .regularExpression) != nil else { return false }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd-HHmmss"
        return f.date(from: date) != nil
    }

    /// Đường dẫn là mount point thật (khớp `f_mntonname` của `statfs`).
    public static func isMountPoint(_ path: String) -> Bool {
        guard path.hasPrefix("/"), !path.contains("/../"), !path.hasSuffix("/..") else { return false }
        var fs = statfs()
        guard statfs(path, &fs) == 0 else { return false }
        let mountedOn = withUnsafeBytes(of: &fs.f_mntonname) { raw in
            String(decoding: raw.prefix(while: { $0 != 0 }), as: UTF8.self)
        }
        let normalized = path.count > 1 && path.hasSuffix("/") ? String(path.dropLast()) : path
        return mountedOn == normalized
    }

    /// Kiểm tra plist launchd: nằm trực tiếp trong `/Library/LaunchDaemons` hoặc `/Library/LaunchAgents`,
    /// là file thường (không phải symlink), đuôi `.plist`, và `Label` bên trong khớp `label`.
    /// Trả về đường dẫn chuẩn hoá và thư mục chứa.
    public static func validateLaunchPlist(label: String, plistPath: String) -> Result<(path: String, directory: String), ValidationError> {
        guard isValidLabel(label) else { return .failure(.invalidLabel) }
        guard plistPath.hasPrefix("/"), let canonical = PathPolicy.canonicalize(plistPath) else { return .failure(.invalidPath) }
        let directory = (canonical as NSString).deletingLastPathComponent
        let name = (canonical as NSString).lastPathComponent
        guard launchItemDirectories.contains(directory), name.hasSuffix(".plist"), !name.hasPrefix(".") else {
            return .failure(.invalidPath)
        }

        // Mở bằng O_NOFOLLOW để không bị đánh tráo bằng symlink giữa lúc kiểm tra và đọc.
        let fd = open(canonical, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { return .failure(errno == ENOENT ? .notFound : .invalidPath) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        var st = stat()
        guard fstat(fd, &st) == 0, st.st_mode & S_IFMT == S_IFREG, st.st_size < 1_048_576 else { return .failure(.invalidPath) }
        guard let data = try? handle.readToEnd(),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let plistLabel = plist["Label"] as? String
        else { return .failure(.unreadablePlist) }
        guard plistLabel == label else { return .failure(.labelMismatch) }
        return .success((canonical, directory))
    }

    public enum ValidationError: Error, Sendable, CustomStringConvertible {
        case invalidLabel
        case invalidPath
        case notFound
        case unreadablePlist
        case labelMismatch

        public var description: String {
            switch self {
            case .invalidLabel: "Label không hợp lệ"
            case .invalidPath: "Đường dẫn plist không nằm trong /Library/LaunchDaemons hoặc /Library/LaunchAgents"
            case .notFound: "Không tìm thấy plist"
            case .unreadablePlist: "Không đọc được plist"
            case .labelMismatch: "Label trong plist không khớp"
            }
        }
    }
}
