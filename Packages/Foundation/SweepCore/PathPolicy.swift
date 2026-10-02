import Darwin
import Foundation

/// Module duy nhất quyết định đường dẫn nào được phép xoá (mục 15.1).
/// Cả app lẫn helper dùng chung; helper chạy lại kiểm tra ở phía root, không tin kết quả từ app.
public struct PathPolicy: Sendable {
    public enum Mode: Sendable, Hashable {
        /// Thao tác với quyền người dùng: file phải thuộc user hiện tại.
        case user(uid: uid_t)
        /// Helper root: chỉ thao tác trong danh sách vùng hệ thống được phép.
        case root
    }

    /// Mục đích kiểm tra.
    public enum Intent: Sendable {
        /// Xoá chính đường dẫn này.
        case removeItem
        /// Giữ thư mục, xoá nội dung bên trong (`deleteContents`). Cho phép nhắm vào các thư mục "vỏ"
        /// như `~/Library/Caches`, nhưng vẫn cấm vùng cấm.
        case removeContents
    }

    public struct Violation: Error, Sendable, Hashable, CustomStringConvertible {
        public enum Kind: Sendable, Hashable {
            case notAbsolute
            case forbiddenZone(String)       // khớp danh sách luôn cấm
            case protectedDirectory(String)  // chính thư mục quan trọng (home, Documents...)
            case outsideAllowedRoots         // sau khi chuẩn hoá đã ra khỏi vùng rule cho phép
            case readOnlyVolume
            case notOwnedByUser(owner: uid_t)
            case notInRootAllowlist
            case untrustedParent(String)
            case needsAdminHelper            // bản Mac App Store không có helper root
        }

        public let kind: Kind
        public let path: String

        public init(_ kind: Kind, path: String) {
            self.kind = kind
            self.path = path
        }

        public var description: String {
            switch kind {
            case .notAbsolute: String(localized: "Đường dẫn không tuyệt đối: \(path)")
            case let .forbiddenZone(rule): String(localized: "Nằm trong vùng cấm (\(rule)): \(path)")
            case let .protectedDirectory(rule): String(localized: "Thư mục được bảo vệ (\(rule)): \(path)")
            case .outsideAllowedRoots: String(localized: "Đường dẫn ra ngoài vùng rule cho phép sau khi giải symlink: \(path)")
            case .readOnlyVolume: String(localized: "Nằm trên ổ chỉ đọc hoặc ổ hệ thống: \(path)")
            case let .notOwnedByUser(owner): String(localized: "Không thuộc người dùng hiện tại (uid \(owner)): \(path)")
            case .notInRootAllowlist: String(localized: "Không nằm trong vùng helper được phép: \(path)")
            case .needsAdminHelper: String(localized: "Cần quyền quản trị, bản Mac App Store không xoá được: \(path)")
            case let .untrustedParent(why): String(localized: "Thư mục cha không an toàn (\(why)): \(path)")
            }
        }

        /// Vi phạm chỉ do quyền sở hữu: có thể thử lại qua helper (mục 7.4).
        public var isOwnershipOnly: Bool {
            if case .notOwnedByUser = kind { return true }
            return false
        }
    }

    public let mode: Mode
    /// Thư mục home đã chuẩn hoá. Ở chế độ root dùng mẫu `/Users/*` để bao mọi tài khoản.
    public let homePattern: String
    private let forbiddenSubtrees: [Glob]
    private let protectedExact: [Glob]
    private let protectedShells: [Glob]
    private let rootAllowlist: [Glob]
    /// Tiền tố cố định của vùng cấm, để kiểm tra nhanh khi duyệt (không cần khớp glob cho từng mục).
    private let forbiddenLiteralPrefixes: [String]
    private let forbiddenPatternGlobs: [Glob]

    // MARK: Khởi tạo

    public static func user(home: URL = .userHome, uid: uid_t = getuid()) -> PathPolicy {
        PathPolicy(mode: .user(uid: uid), home: Self.canonicalize(home.path) ?? home.path)
    }

    /// Chính sách phía root của helper.
    public static let root = PathPolicy(mode: .root, home: "/Users/*")

    public init(mode: Mode, home: String) {
        self.mode = mode
        homePattern = home
        let h = home
        let roots = ["/var/root", h]

        /// Luôn cấm: không rule nào vượt qua được.
        var subtrees = [
            "/System", "/usr", "/bin", "/sbin", "/dev", "/cores",
            "/private/var/db", "/private/var/vm", "/private/etc", "/private/var/protected",
            "/Library/Apple", "/Library/Keychains", "/Library/Application Support/com.apple.TCC",
            "/Library/Security", "/Library/SystemExtensions", "/Library/Preferences/SystemConfiguration",
            "**/*.photoslibrary",
        ]
        for r in roots {
            subtrees += [
                "\(r)/Library/Keychains",
                "\(r)/Library/Mobile Documents",
                "\(r)/Library/Messages",
                "\(r)/Library/Photos",
                "\(r)/Library/Application Support/com.apple.TCC",
                "\(r)/Library/Accounts",
                "\(r)/Library/CloudStorage",
            ]
        }
        forbiddenSubtrees = subtrees.map { Glob($0, home: h) }
        forbiddenLiteralPrefixes = forbiddenSubtrees.filter(\.isLiteral).map(\.pattern)
        forbiddenPatternGlobs = forbiddenSubtrees.filter { !$0.isLiteral && !$0.pattern.hasPrefix("**") }

        /// Chính thư mục (không phải nội dung) bị cấm xoá.
        var exact = ["/", "/var", "/tmp", "/etc", "/Applications", "/Library", "/Users", "/Volumes", "/private", "/private/var", "/private/tmp",
                     "/Applications/Utilities", "/Users/Shared", "/opt", "/opt/homebrew", "/usr/local", "/Volumes/*"]
        for r in roots {
            exact += [r, "\(r)/Documents", "\(r)/Desktop", "\(r)/Pictures", "\(r)/Movies", "\(r)/Music",
                      "\(r)/Downloads", "\(r)/Library", "\(r)/Applications", "\(r)/Public", "\(r)/Sites"]
        }
        protectedExact = exact.map { Glob($0, home: h) }

        /// Thư mục "vỏ": không được xoá chính nó, nhưng được xoá nội dung (`deleteContents`).
        var shells = ["/Library/Caches", "/Library/Logs", "/private/var/log", "/Library/LaunchAgents", "/Library/LaunchDaemons",
                      "/Library/Application Support", "/Library/Preferences", "/Volumes/*/.Trashes", "/Volumes/*/.Trashes/*"]
        for r in roots {
            shells += ["\(r)/Library/Caches", "\(r)/Library/Logs", "\(r)/Library/Application Support", "\(r)/Library/Preferences",
                       "\(r)/Library/Containers", "\(r)/Library/Group Containers", "\(r)/Library/LaunchAgents",
                       "\(r)/Library/Logs/DiagnosticReports", "\(r)/.Trash", "\(r)/Library/Developer",
                       "\(r)/Library/Developer/Xcode", "\(r)/Library/Developer/Xcode/DerivedData",
                       "\(r)/Library/Developer/Xcode/Archives", "\(r)/Library/Developer/CoreSimulator",
                       "\(r)/Library/Saved Application State", "\(r)/Library/HTTPStorages"]
        }
        protectedShells = shells.map { Glob($0, home: h) }

        /// Vùng helper root được phép thao tác (luôn là con, không phải chính thư mục).
        rootAllowlist = [
            "/Library/Caches/**", "/Library/Logs/**", "/private/var/log/**",
            "/Library/LaunchAgents/**", "/Library/LaunchDaemons/**",
            "/Library/Application Support/**", "/Library/Preferences/**",
            "/Library/PrivilegedHelperTools/**", "/Library/Internet Plug-Ins/**", "/Library/Audio/Plug-Ins/**",
            "/Library/Input Methods/**", "/Library/PreferencePanes/**", "/Library/Receipts/**",
            "/Applications/**",
            "/Users/*/Library/**", "/Users/*/.Trash/**",
            "/Volumes/*/.Trashes/**",
        ].map { Glob($0, home: h) }
    }

    // MARK: Kiểm tra

    /// Kiểm tra một đường dẫn, trả về đường dẫn đã chuẩn hoá (bỏ `..`, giải symlink của thư mục cha).
    /// - Parameters:
    ///   - allowedRoots: vùng rule cho phép; sau khi chuẩn hoá đường dẫn phải vẫn nằm trong đó.
    ///   - checkOwnership: với chế độ user, file phải thuộc user hiện tại.
    @discardableResult
    public func check(
        _ url: URL,
        intent: Intent = .removeItem,
        allowedRoots: [String]? = nil,
        checkOwnership: Bool = true
    ) throws(Violation) -> URL {
        let raw = url.path
        guard raw.hasPrefix("/") else { throw Violation(.notAbsolute, path: raw) }
        guard let path = Self.canonicalize(raw) else { throw Violation(.notAbsolute, path: raw) }

        if let rule = forbiddenSubtrees.first(where: { $0.matchesSelfOrAncestor(path) }) {
            throw Violation(.forbiddenZone(rule.pattern), path: path)
        }
        if let rule = protectedExact.first(where: { $0.matches(path) }) {
            throw Violation(.protectedDirectory(rule.pattern), path: path)
        }
        if intent == .removeItem, let rule = protectedShells.first(where: { $0.matches(path) }) {
            throw Violation(.protectedDirectory(rule.pattern), path: path)
        }
        // /usr/local được phép theo rule riêng, nhưng phần còn lại của /usr đã nằm trong vùng cấm ở trên.

        if let allowedRoots, !allowedRoots.isEmpty {
            let roots = allowedRoots.compactMap { Self.canonicalize($0) }
            let inside = roots.contains { root in path == root || path.hasPrefix(root.hasSuffix("/") ? root : root + "/") }
            if !inside { throw Violation(.outsideAllowedRoots, path: path) }
        }

        if Self.isOnReadOnlyVolume(path) { throw Violation(.readOnlyVolume, path: path) }

        switch mode {
        case let .user(uid):
            if checkOwnership, let owner = Self.owner(of: path), owner != uid {
                throw Violation(.notOwnedByUser(owner: owner), path: path)
            }
        case .root:
            guard rootAllowlist.contains(where: { $0.matches(path) }) else {
                throw Violation(.notInRootAllowlist, path: path)
            }
        }
        return URL(fileURLWithPath: path)
    }

    /// Chỉ kiểm tra vùng cấm, không kiểm tra quyền sở hữu hay ổ chỉ đọc. Dùng khi lọc đường dẫn mở rộng từ rule.
    public func isForbidden(_ path: String) -> Violation? {
        guard let canonical = Self.canonicalize(path) else { return Violation(.notAbsolute, path: path) }
        if let rule = forbiddenSubtrees.first(where: { $0.matchesSelfOrAncestor(canonical) }) {
            return Violation(.forbiddenZone(rule.pattern), path: canonical)
        }
        if let rule = protectedExact.first(where: { $0.matches(canonical) }) {
            return Violation(.protectedDirectory(rule.pattern), path: canonical)
        }
        return nil
    }

    /// Thư mục có nằm trong vùng cấm không, để bỏ qua sớm khi duyệt (mục 16.3).
    /// Được gọi cho từng thư mục khi đi xuống, nên chỉ cần xét chính thư mục đó (tổ tiên đã được xét trước).
    public func shouldSkipDescending(into path: String) -> Bool {
        if path.hasSuffix(".photoslibrary") { return true }
        for prefix in forbiddenLiteralPrefixes where path.hasPrefix(prefix) {
            if path.count == prefix.count || path[path.index(path.startIndex, offsetBy: prefix.count)] == "/" { return true }
        }
        return forbiddenPatternGlobs.contains { $0.matches(path) }
    }

    /// Kiểm tra thư mục cha đã mở bằng `O_NOFOLLOW` trước khi `unlinkat` (mục 9.4, 22.3).
    /// Thư mục cha phải thuộc root hoặc chính chủ của vùng home, và không được ghi bởi người khác khi không có sticky bit.
    public func checkParent(owner: uid_t, mode fileMode: mode_t, path: String) throws(Violation) {
        let isHomeArea = path.hasPrefix("/Users/") || path.hasPrefix("/Volumes/")
        switch self.mode {
        case let .user(uid):
            guard owner == uid || owner == 0 else {
                throw Violation(.untrustedParent("owner \(owner)"), path: path)
            }
        case .root:
            guard owner == 0 || (isHomeArea && owner >= 500) else {
                throw Violation(.untrustedParent("owner \(owner)"), path: path)
            }
        }
        let worldWritable = fileMode & S_IWOTH != 0
        let sticky = fileMode & S_ISVTX != 0
        if worldWritable && !sticky {
            throw Violation(.untrustedParent("world-writable"), path: path)
        }
    }

    // MARK: Tiện ích

    /// Chuẩn hoá: bỏ `.`/`..`, giải symlink của tổ tiên gần nhất đang tồn tại (dùng `realpath`,
    /// không dùng `resolvingSymlinksInPath` vì API đó tự bỏ tiền tố `/private`).
    /// Thành phần cuối cùng không được giải: nếu là symlink thì ta xoá chính symlink, không đụng đích.
    public static func canonicalize(_ path: String) -> String? {
        guard path.hasPrefix("/") else { return nil }
        var stack: [String] = []
        for comp in path.split(separator: "/", omittingEmptySubsequences: true) {
            switch comp {
            case ".": continue
            case "..": if !stack.isEmpty { stack.removeLast() }
            default: stack.append(String(comp))
            }
        }
        guard let last = stack.popLast() else { return "/" }
        var parentParts = stack
        var tail: [String] = [last]
        while true {
            let parent = "/" + parentParts.joined(separator: "/")
            if let real = realPath(parent) {
                let base = real == "/" ? "" : real
                return base + "/" + tail.joined(separator: "/")
            }
            guard let p = parentParts.popLast() else { return "/" + tail.joined(separator: "/") }
            tail.insert(p, at: 0)
        }
    }

    public static func realPath(_ path: String) -> String? {
        guard let resolved = realpath(path, nil) else { return nil }
        defer { free(resolved) }
        return String(cString: resolved)
    }

    public static func owner(of path: String) -> uid_t? {
        var st = stat()
        guard lstat(path, &st) == 0 else { return nil }
        return st.st_uid
    }

    public static func isOnReadOnlyVolume(_ path: String) -> Bool {
        var probe = path
        // statfs đi theo symlink: với chính symlink thì xét volume của thư mục cha (xoá symlink là hợp lệ).
        var st = stat()
        if lstat(path, &st) == 0, st.st_mode & S_IFMT == S_IFLNK { probe = (path as NSString).deletingLastPathComponent }
        var fs = statfs()
        while true {
            if statfs(probe, &fs) == 0 { return fs.f_flags & UInt32(MNT_RDONLY) != 0 }
            let parent = (probe as NSString).deletingLastPathComponent
            if parent == probe || parent.isEmpty { return false }
            probe = parent
        }
    }
}
