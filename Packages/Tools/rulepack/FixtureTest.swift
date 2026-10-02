import Darwin
import FileSystemKit
import Foundation
import RuleEngine
import SweepCore

/// Fixture cho `rulepack test` (mục 18, mức "Rule"): mô tả cây thư mục giả lập và danh sách đường dẫn kỳ vọng.
///
/// ```json
/// {
///   "name": "npm cache",
///   "rules": ["devtools.npm.cache"],
///   "apps": [{ "bundleID": "com.example.app", "appName": "Example", "teamID": "ABCDE12345" }],
///   "running": ["com.example.other"],
///   "files": [
///     { "path": "~/.npm/_cacache/index-v5/aa", "size": 4096, "ageDays": 3 },
///     { "path": "/Library/Caches/com.vendor/x", "size": 100 },
///     { "path": "~/Library/Caches/empty", "dir": true },
///     { "path": "~/Library/Caches/link", "symlink": "/etc" }
///   ],
///   "expect": ["~/.npm/_cacache"]
/// }
/// ```
/// `~` là home giả, đường dẫn tuyệt đối được đặt dưới gốc giả (`PathResolver.rootPrefix`).
/// Một file fixture có thể là một case, một mảng case, hoặc `{ "cases": [...] }`.
struct FixtureCase: Decodable {
    struct File: Decodable {
        var path: String
        var size: Int?
        var ageDays: Double?
        var dir: Bool?
        var symlink: String?
    }

    struct App: Decodable {
        var bundleID: String
        var appName: String?
        var teamID: String?
    }

    var name: String
    var rules: [String]
    var apps: [App]?
    var running: [String]?
    var files: [File]
    var expect: [String]

    static func load(_ url: URL) throws -> [FixtureCase] {
        let data = try Data(contentsOf: url)
        let decoder = JSONDecoder()
        struct Wrapper: Decodable { var cases: [FixtureCase] }
        if let w = try? decoder.decode(Wrapper.self, from: data) { return w.cases }
        if let many = try? decoder.decode([FixtureCase].self, from: data) { return many }
        do {
            return [try decoder.decode(FixtureCase.self, from: data)]
        } catch {
            throw CLIError("\(url.lastPathComponent): fixture sai định dạng: \(RuleSource.describe(error))")
        }
    }
}

struct FixtureResult {
    let file: String
    let name: String
    let missing: [String]
    let unexpected: [String]
    let errors: [String]

    var passed: Bool { missing.isEmpty && unexpected.isEmpty && errors.isEmpty }
}

/// Chạy rule trên cây thư mục tạm, dùng đúng `RuleEvaluator` của app.
struct FixtureRunner {
    let payload: RulePayload

    func run(_ c: FixtureCase, file: String) throws -> FixtureResult {
        let fm = FileManager.default
        let tmpRaw = fm.temporaryDirectory.appendingPathComponent("rulepack-\(UUID().uuidString)")
        try fm.createDirectory(at: tmpRaw, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: tmpRaw) }
        // Chuẩn hoá (/var → /private/var) để so khớp ổn định.
        let tmp = PathPolicy.realPath(tmpRaw.path) ?? tmpRaw.path
        let home = tmp + "/home"
        let root = tmp + "/root"
        try fm.createDirectory(atPath: home, withIntermediateDirectories: true)
        try fm.createDirectory(atPath: root, withIntermediateDirectories: true)

        func map(_ p: String) -> String {
            if p == "~" { return home }
            if p.hasPrefix("~/") { return home + p.dropFirst(1) }
            return root + p
        }
        func unmap(_ p: String) -> String {
            if p == home || p.hasPrefix(home + "/") { return "~" + p.dropFirst(home.count) }
            if p.hasPrefix(root + "/") { return String(p.dropFirst(root.count)) }
            return p
        }

        var errors: [String] = []
        // Tạo cây, ghi nhớ tuổi để đặt thời gian sau cùng (tạo file làm đổi mtime thư mục cha).
        var ages: [String: Double] = [:]
        for f in c.files {
            guard f.path.hasPrefix("~/") || f.path.hasPrefix("/"), !f.path.split(separator: "/").contains("..") else {
                errors.append("Đường dẫn fixture không hợp lệ: \(f.path)")
                continue
            }
            let path = map(f.path)
            let parent = (path as NSString).deletingLastPathComponent
            try fm.createDirectory(atPath: parent, withIntermediateDirectories: true)
            if let target = f.symlink {
                try fm.createSymbolicLink(atPath: path, withDestinationPath: Self.symlinkTarget(target, map: map))
            } else if f.dir == true {
                try fm.createDirectory(atPath: path, withIntermediateDirectories: true)
            } else {
                let size = max(0, f.size ?? 1024)
                var bytes = [UInt8](repeating: 0, count: size)
                for i in bytes.indices { bytes[i] = UInt8(truncatingIfNeeded: i &* 31 &+ 7) }
                guard fm.createFile(atPath: path, contents: Data(bytes)) else {
                    errors.append("Không tạo được \(f.path)")
                    continue
                }
            }
            let age = f.ageDays ?? 0
            ages[path] = min(ages[path] ?? age, age)
            // Thư mục cha: mới nhất trong số con cháu.
            var p = parent
            while p.hasPrefix(tmp + "/") {
                ages[p] = min(ages[p] ?? age, age)
                p = (p as NSString).deletingLastPathComponent
            }
        }
        let now = Date()
        for (path, age) in ages.sorted(by: { $0.key.count > $1.key.count }) {
            let t = now.addingTimeInterval(-age * 86_400).timeIntervalSince1970
            var times = [timeval(tv_sec: Int(t), tv_usec: 0), timeval(tv_sec: Int(t), tv_usec: 0)]
            _ = lutimes(path, &times)
        }

        let resolver = PathResolver(home: home, rootPrefix: root)
        let snapshot = RuleSnapshot(version: payload.version, rules: payload.rules, knowledge: payload.knowledge, resolver: resolver, source: .development)
        let apps = (c.apps ?? []).map {
            RuleAppInfo(bundleID: $0.bundleID, teamID: $0.teamID, appName: $0.appName ?? $0.bundleID,
                        url: URL(fileURLWithPath: root + "/Applications/\($0.appName ?? $0.bundleID).app"))
        }
        let context = RuleEvaluationContext(
            fileSystem: FileSystemService(home: URL(fileURLWithPath: home)),
            policy: .user(home: URL(fileURLWithPath: home)),
            apps: apps,
            runningBundleIDs: Set(c.running ?? []),
            now: now
        )
        var actual = Set<String>()
        for id in c.rules {
            guard let compiled = snapshot.rule(RuleID(id)) else {
                errors.append("Không có rule \(id)")
                continue
            }
            if compiled.rule.match.provider != nil {
                errors.append("Rule \(id) dùng provider, không test bằng fixture được")
                continue
            }
            do {
                for m in try RuleEvaluator.evaluate(compiled, snapshot: snapshot, context: context) {
                    actual.insert(unmap(PathPolicy.canonicalize(m.url.path) ?? m.url.path))
                }
            } catch {
                errors.append("Rule \(id) lỗi: \(error)")
            }
        }
        let expected = Set(c.expect.map { unmap(map($0)) })
        return FixtureResult(file: file, name: c.name,
                             missing: expected.subtracting(actual).sorted(),
                             unexpected: actual.subtracting(expected).sorted(),
                             errors: errors)
    }

    /// Đích symlink bắt đầu bằng `~` hoặc `@root/...` được đổi sang cây giả; đường dẫn tuyệt đối khác giữ nguyên
    /// (để thử symlink trỏ ra ngoài, ví dụ `/System`).
    static func symlinkTarget(_ target: String, map: (String) -> String) -> String {
        if target.hasPrefix("~") { return map(target) }
        if target.hasPrefix("@root/") { return map(String(target.dropFirst(5))) }
        return target
    }
}
