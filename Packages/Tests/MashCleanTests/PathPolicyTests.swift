import Darwin
import Foundation
import Testing
@testable import SweepCore

/// PathPolicy: mọi vùng cấm mục 15.1 và các kiểm tra bổ sung (mục 18, 22.3).
@Suite struct PathPolicyTests {
    let policy = PathPolicy.user(home: URL(fileURLWithPath: "/Users/test"))

    @Test(arguments: [
        "/", "/System", "/System/Library", "/System/Library/CoreServices/Finder.app",
        "/usr", "/usr/bin/ls", "/bin/sh", "/sbin/launchd", "/dev/disk0", "/cores/core.1",
        "/private/var/db/x", "/private/var/vm/swapfile0", "/private/etc/hosts", "/Library/Apple/System",
        "/Library/Keychains/System.keychain", "/Library/Application Support/com.apple.TCC/TCC.db",
        "/Users/test", "/Users/test/Documents", "/Users/test/Desktop", "/Users/test/Pictures",
        "/Users/test/Movies", "/Users/test/Music", "/Users/test/Downloads", "/Users/test/Library",
        "/Users/test/Library/Keychains/login.keychain-db",
        "/Users/test/Library/Mobile Documents/com~apple~CloudDocs/report.pages",
        "/Users/test/Library/Messages/chat.db", "/Users/test/Library/Photos/Libraries/x",
        "/Users/test/Pictures/Photos Library.photoslibrary/database/Photos.sqlite",
        "/Volumes/External/Old.photoslibrary",
        "/Users/test/Library/Application Support/com.apple.TCC/TCC.db",
        "/Users/test/Library/CloudStorage/Dropbox/file",
        "/Users/test/Library/Accounts/Accounts4.sqlite",
        "/Applications", "/Library", "/Users", "/Volumes", "/private", "/private/var", "/private/tmp",
        "/Applications/Utilities", "/Users/Shared", "/opt/homebrew", "/usr/local",
    ])
    func forbidden(_ path: String) {
        #expect(throws: PathPolicy.Violation.self) { try policy.check(URL(fileURLWithPath: path), checkOwnership: false) }
        #expect(policy.isForbidden(path) != nil)
    }

    /// Bí danh qua symlink hệ thống: `/var` → `/private/var`, `/etc` → `/private/etc`, `/tmp` → `/private/tmp`.
    @Test(arguments: ["/var/db/x", "/var/vm/sleepimage", "/etc/hosts", "/var/db"])
    func symlinkedSystemAliasesAreCanonicalized(_ path: String) {
        #expect(throws: PathPolicy.Violation.self) { try policy.check(URL(fileURLWithPath: path), checkOwnership: false) }
    }

    /// Chính symlink `/var`, `/tmp`, `/etc` (thành phần cuối không được giải) cũng được bảo vệ.
    @Test(arguments: ["/var", "/tmp", "/etc", "/var/log/..", "/tmp/x/.."])
    func topLevelSystemSymlinksThemselves(_ path: String) {
        do {
            #expect(throws: PathPolicy.Violation.self) { try policy.check(URL(fileURLWithPath: path), checkOwnership: false) }
        }
    }

    @Test func varAndPrivateVarCanonicalizeTheSame() {
        #expect(PathPolicy.canonicalize("/var/log/system.log") == "/private/var/log/system.log")
        #expect(PathPolicy.canonicalize("/tmp/x") == "/private/tmp/x")
        #expect(PathPolicy.canonicalize("/a/./b/../c") == "/a/c")
        #expect(PathPolicy.canonicalize("/../..") == "/")
        #expect(PathPolicy.canonicalize("relative/path") == nil)
    }

    @Test(arguments: [
        "/Users/test/Library/Caches/com.example.app",
        "/Users/test/Library/Caches/com.example.app/data.db",
        "/Users/test/Library/Logs/Example/x.log",
        "/Users/test/Library/Developer/Xcode/DerivedData/App-abc",
        "/Users/test/Downloads/Installer.dmg",
        "/Users/test/.npm/_cacache",
    ])
    func allowed(_ path: String) throws {
        try policy.check(URL(fileURLWithPath: path), checkOwnership: false)
        #expect(policy.isForbidden(path) == nil)
    }

    @Test func relativePathIsRejected() {
        #expect(throws: PathPolicy.Violation.self) { try policy.check(URL(string: "relative/x")!) }
    }

    @Test func dotDotCannotEscapeToForbiddenZone() {
        #expect(throws: PathPolicy.Violation.self) {
            try policy.check(URL(fileURLWithPath: "/Users/test/Library/Caches/../Keychains/login.keychain-db"), checkOwnership: false)
        }
        #expect(throws: PathPolicy.Violation.self) {
            try policy.check(URL(fileURLWithPath: "/Users/test/Library/Caches/../../../../System/Library"), checkOwnership: false)
        }
    }

    /// Ví dụ mục 22.3.
    @Test func symlinkEscapeIsBlocked() throws {
        let dir = try TemporaryDirectory("policy")
        try dir.symlink("evil", to: "/System")
        #expect(throws: PathPolicy.Violation.self) { try policy.check(dir.url("evil/Library")) }
        let v = policy.isForbidden(dir.path("evil/Library"))
        #expect(v?.kind == .forbiddenZone("/System"))
    }

    @Test func symlinkAsLastComponentIsNotFollowed() throws {
        let dir = try TemporaryDirectory("policy")
        try dir.symlink("link", to: dir.path("target"))
        try dir.dir("target")
        // Chỉ xoá chính symlink: chuẩn hoá không giải thành phần cuối.
        let canonical = try policy.check(dir.url("link"), checkOwnership: false)
        #expect(canonical.path == dir.path("link"))
        #expect(PathPolicy.canonicalize(dir.path("link")) == dir.path("link"))
    }

    /// Symlink nằm trong vùng người dùng nhưng trỏ vào ổ hệ thống (chỉ đọc): xoá chính symlink là hợp lệ.
    @Test func symlinkToReadOnlyVolumeCanBeRemoved() throws {
        let dir = try TemporaryDirectory("policy")
        try dir.symlink("link", to: "/System")
        do {
            try policy.check(dir.url("link"), checkOwnership: false)
        }
    }

    @Test func intentRemoveContentsAllowsShellButNotProtected() {
        let caches = URL(fileURLWithPath: "/Users/test/Library/Caches")
        #expect(throws: PathPolicy.Violation.self) { try policy.check(caches, intent: .removeItem, checkOwnership: false) }
        #expect(throws: Never.self) { try policy.check(caches, intent: .removeContents, checkOwnership: false) }
        #expect(throws: Never.self) {
            try policy.check(URL(fileURLWithPath: "/Users/test/.Trash"), intent: .removeContents, checkOwnership: false)
        }
        // Chính home, Documents, Keychains vẫn cấm kể cả khi chỉ xoá nội dung.
        for p in ["/Users/test", "/Users/test/Documents", "/Users/test/Library", "/Users/test/Library/Keychains"] {
            #expect(throws: PathPolicy.Violation.self) { try policy.check(URL(fileURLWithPath: p), intent: .removeContents, checkOwnership: false) }
        }
    }

    @Test func allowedRootsRejectSymlinkedParentEscape() throws {
        let dir = try TemporaryDirectory("policy")
        try dir.file("outside/secret.txt")
        try dir.dir("rule-root")
        try dir.symlink("rule-root/link", to: dir.path("outside"))
        let roots = [dir.path("rule-root")]
        do throws(PathPolicy.Violation) {
            try policy.check(dir.url("rule-root/link/secret.txt"), allowedRoots: roots)
            Issue.record("Phải chặn đường dẫn ra ngoài allowedRoots")
        } catch {
            #expect(error.kind == .outsideAllowedRoots)
        }
        try dir.file("rule-root/ok.txt")
        let ok = try PathPolicy.user(home: dir.url("home")).check(dir.url("rule-root/ok.txt"), allowedRoots: roots)
        #expect(ok.path == dir.path("rule-root/ok.txt"))
    }

    @Test func ownershipIsChecked() throws {
        // /bin/ls thuộc root, nhưng đã nằm trong vùng cấm; dùng /Library/Caches (root) để thử quyền sở hữu.
        let other = PathPolicy.user(home: URL(fileURLWithPath: "/Users/test"), uid: 4242)
        let dir = try TemporaryDirectory("policy")
        try dir.file("mine.txt")
        do throws(PathPolicy.Violation) {
            try other.check(dir.url("mine.txt"))
            Issue.record("Phải báo file không thuộc người dùng")
        } catch {
            #expect(error.isOwnershipOnly)
        }
        #expect(throws: Never.self) { try PathPolicy.user(home: URL(fileURLWithPath: "/Users/test")).check(dir.url("mine.txt")) }
    }

    @Test(arguments: [
        "/Library/Caches/com.vendor.updater", "/Library/Logs/Vendor/agent.log", "/private/var/log/system.log.0.gz",
        "/Library/LaunchDaemons/com.vendor.helper.plist", "/Library/PrivilegedHelperTools/com.vendor.helper",
        "/Applications/Old.app", "/Users/someone/Library/Caches/x", "/Users/someone/.Trash/x",
        "/Volumes/External/.Trashes/501/x",
    ])
    func rootAllowlistAccepts(_ path: String) {
        #expect(throws: Never.self) { try PathPolicy.root.check(URL(fileURLWithPath: path)) }
    }

    @Test(arguments: [
        "/opt/homebrew/Cellar/x", "/Users/someone/Documents/x.txt", "/private/var/folders/x",
        "/Library/Fonts/x.ttf", "/Users/someone/Library/Keychains/login.keychain-db", "/System/Library/x",
    ])
    func rootAllowlistRejects(_ path: String) {
        #expect(throws: PathPolicy.Violation.self) { try PathPolicy.root.check(URL(fileURLWithPath: path)) }
    }

    @Test func rootPolicyProtectsShellsOfEveryUser() {
        #expect(throws: PathPolicy.Violation.self) { try PathPolicy.root.check(URL(fileURLWithPath: "/Library/Caches")) }
        #expect(throws: Never.self) { try PathPolicy.root.check(URL(fileURLWithPath: "/Library/Caches"), intent: .removeContents) }
        #expect(throws: PathPolicy.Violation.self) { try PathPolicy.root.check(URL(fileURLWithPath: "/Users/anyone/Library")) }
        #expect(throws: PathPolicy.Violation.self) { try PathPolicy.root.check(URL(fileURLWithPath: "/Users/anyone/Library/Messages/chat.db")) }
    }

    @Test func checkParentUserMode() {
        let me = getuid()
        #expect(throws: Never.self) { try policy.checkParent(owner: me, mode: 0o755, path: "/Users/test/Library/Caches") }
        #expect(throws: Never.self) { try policy.checkParent(owner: 0, mode: 0o755, path: "/Library/Caches") }
        // Thư mục /tmp kiểu sticky + world-writable: được phép.
        #expect(throws: Never.self) { try policy.checkParent(owner: 0, mode: 0o1777, path: "/private/tmp") }
        // World-writable không sticky: kẻ khác có thể tráo file → chặn.
        #expect(throws: PathPolicy.Violation.self) { try policy.checkParent(owner: me, mode: 0o777, path: "/Users/test/x") }
        // Chủ sở hữu là người dùng khác.
        #expect(throws: PathPolicy.Violation.self) { try policy.checkParent(owner: me &+ 1, mode: 0o755, path: "/Users/other") }
    }

    @Test func checkParentRootMode() {
        #expect(throws: Never.self) { try PathPolicy.root.checkParent(owner: 0, mode: 0o755, path: "/Library/Caches") }
        #expect(throws: Never.self) { try PathPolicy.root.checkParent(owner: 501, mode: 0o755, path: "/Users/someone/Library/Caches") }
        #expect(throws: PathPolicy.Violation.self) { try PathPolicy.root.checkParent(owner: 501, mode: 0o755, path: "/Library/Caches") }
        #expect(throws: PathPolicy.Violation.self) { try PathPolicy.root.checkParent(owner: 0, mode: 0o777, path: "/Library/Caches") }
    }

    @Test func shouldSkipDescendingIntoForbiddenZones() {
        #expect(policy.shouldSkipDescending(into: "/System"))
        #expect(policy.shouldSkipDescending(into: "/Users/test/Library/Mobile Documents"))
        #expect(policy.shouldSkipDescending(into: "/Users/test/Pictures/Photos Library.photoslibrary"))
        #expect(!policy.shouldSkipDescending(into: "/Users/test/Library/Caches"))
    }

    @Test func violationsAreDescribed() {
        let v = PathPolicy.Violation(.forbiddenZone("/System"), path: "/System/x")
        #expect(v.description.contains("/System/x"))
        #expect(!v.isOwnershipOnly)
    }
}
