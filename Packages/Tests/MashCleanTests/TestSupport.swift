import Darwin
import Foundation
import RuleEngine
import SweepCore

/// Thư mục tạm cho test: đường dẫn đã chuẩn hoá (`/var` → `/private/var`), tự xoá khi giải phóng.
/// Mọi thao tác xoá trong test chỉ diễn ra bên trong thư mục này.
final class TemporaryDirectory: @unchecked Sendable {
    let url: URL
    var path: String { url.path }

    init(_ label: String = "test") throws {
        let raw = FileManager.default.temporaryDirectory.appendingPathComponent("mashclean-\(label)-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: raw, withIntermediateDirectories: true)
        url = URL(fileURLWithPath: PathPolicy.realPath(raw.path) ?? raw.path, isDirectory: true)
    }

    deinit {
        // Thư mục có thể đã bị đổi quyền trong test (chmod 0o777, 0o555): trả quyền trước khi xoá.
        if let e = FileManager.default.enumerator(atPath: path) {
            while let rel = e.nextObject() as? String {
                let p = path + "/" + rel
                var st = stat()
                if lstat(p, &st) == 0, st.st_mode & S_IFMT == S_IFDIR { chmod(p, 0o755) }
            }
        }
        try? FileManager.default.removeItem(at: url)
    }

    func url(_ rel: String) -> URL { url.appendingPathComponent(rel) }
    func path(_ rel: String) -> String { url(rel).path }

    /// Tạo file với `size` byte dữ liệu (không sparse) và thời gian sửa/truy cập lùi `ageDays` ngày.
    @discardableResult
    func file(_ rel: String, size: Int = 1024, ageDays: Double? = nil) throws -> URL {
        let u = url(rel)
        try FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
        var bytes = [UInt8](repeating: 0, count: size)
        for i in bytes.indices { bytes[i] = UInt8(truncatingIfNeeded: i &* 13 &+ 1) }
        try Data(bytes).write(to: u)
        if let ageDays { setAge(u.path, days: ageDays) }
        return u
    }

    @discardableResult
    func dir(_ rel: String) throws -> URL {
        let u = url(rel)
        try FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }

    @discardableResult
    func symlink(_ rel: String, to destination: String) throws -> URL {
        let u = url(rel)
        try FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(atPath: u.path, withDestinationPath: destination)
        return u
    }

    func exists(_ rel: String) -> Bool {
        var st = stat()
        return lstat(path(rel), &st) == 0
    }

    func setAge(_ path: String, days: Double) {
        let t = Date().addingTimeInterval(-days * 86_400).timeIntervalSince1970
        var times = [timeval(tv_sec: Int(t), tv_usec: 0), timeval(tv_sec: Int(t), tv_usec: 0)]
        _ = lutimes(path, &times)
    }
}

/// Đường dẫn gốc repo (để đọc `Rules/` và `App/Resources/Rules/rules.bundle`).
enum RepoPaths {
    static var root: URL {
        URL(fileURLWithPath: #filePath)            // Packages/Tests/MashCleanTests/TestSupport.swift
            .deletingLastPathComponent()           // MashCleanTests
            .deletingLastPathComponent()           // Tests
            .deletingLastPathComponent()           // Packages
            .deletingLastPathComponent()           // repo
    }

    static var rules: URL { root.appendingPathComponent("Rules", isDirectory: true) }
    static var bundledRules: URL { root.appendingPathComponent("App/Resources/Rules/rules.bundle") }
}

/// Tạo rule nhanh cho test.
func makeRule(
    _ id: String,
    category: String = RuleCategory.userCaches,
    paths: [String],
    safety: SafetyLevel = .safe,
    removal: RemovalStrategy = .delete,
    forEachApp: Bool? = nil,
    bundleIDs: [String]? = nil,
    minAgeDays: Int? = nil,
    minSizeBytes: Int64? = nil,
    appNotRunning: [String]? = nil,
    minOS: String? = nil,
    exclude: [String]? = nil,
    provider: String? = nil,
    requiresRoot: Bool = false
) -> Rule {
    Rule(
        id: RuleID(id), category: category, title: ["en": id, "vi": id], reason: ["en": "test", "vi": "thử"],
        safety: safety, removal: removal, requiresRoot: requiresRoot,
        match: .init(paths: paths, forEachInstalledApp: forEachApp, minAgeDays: minAgeDays, minSizeBytes: minSizeBytes,
                     bundleIDs: bundleIDs, provider: provider),
        conditions: (appNotRunning != nil || minOS != nil) ? .init(appNotRunning: appNotRunning, minOS: minOS) : nil,
        exclude: exclude
    )
}
