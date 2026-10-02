import Foundation
import os
import SweepCore
import SweepLogging
import FileSystemKit

/// Thông tin app tối thiểu để mở rộng biến của rule (`${bundleID}`, `${teamID}`, `${appName}`).
public struct RuleAppInfo: Sendable, Hashable {
    public var bundleID: String
    public var teamID: String?
    public var appName: String
    public var url: URL?

    public init(bundleID: String, teamID: String?, appName: String, url: URL?) {
        self.bundleID = bundleID
        self.teamID = teamID
        self.appName = appName
        self.url = url
    }
}

/// Một đường dẫn rule khớp, kèm số đo.
public struct RuleMatch: Sendable {
    public let url: URL
    public let measurement: PathMeasurement
    public let rule: Rule
    public let app: RuleAppInfo?
    /// Vùng rule cho phép (phần cố định của glob), dùng cho kiểm tra PathPolicy lúc xoá.
    public let allowedRoot: String
}

public struct RuleEvaluationContext: Sendable {
    public var fileSystem: FileSystemService
    public var policy: PathPolicy
    public var apps: [RuleAppInfo]
    public var runningBundleIDs: Set<String>
    public var ignore: IgnoreList
    public var now: Date
    public var osVersion: OSVersion
    public var tracker: HardLinkTracker
    public var counter: VisitCounter
    /// Huỷ được cả các luồng đo song song (không có Swift Task nên không thấy `Task.isCancelled`).
    public var cancellation: CancellationFlag

    public init(fileSystem: FileSystemService, policy: PathPolicy = .user(), apps: [RuleAppInfo] = [], runningBundleIDs: Set<String> = [],
                ignore: IgnoreList = .empty, now: Date = Date(), osVersion: OSVersion = .current,
                tracker: HardLinkTracker = HardLinkTracker(), counter: VisitCounter = VisitCounter(),
                cancellation: CancellationFlag = CancellationFlag()) {
        self.fileSystem = fileSystem
        self.policy = policy
        self.apps = apps
        self.runningBundleIDs = runningBundleIDs
        self.ignore = ignore
        self.now = now
        self.osVersion = osVersion
        self.tracker = tracker
        self.counter = counter
        self.cancellation = cancellation
    }
}

/// Đánh giá rule khi chạy (mục 8.6).
public enum RuleEvaluator {
    /// Trả về các mục khớp của một rule. Rule có `provider` được xử lý bởi scan task chuyên biệt, ở đây trả rỗng.
    public static func evaluate(_ compiled: CompiledRule, snapshot: RuleSnapshot, context: RuleEvaluationContext) throws -> [RuleMatch] {
        let rule = compiled.rule
        guard rule.match.provider == nil else { return [] }
        guard !snapshot.disabled.contains(rule.id), !context.ignore.ignores(rule: rule.id.rawValue) else { return [] }
        if let minOS = compiled.minOS, context.osVersion < minOS { return [] }

        let resolver = snapshot.resolver
        var candidates: [(path: String, app: RuleAppInfo?, root: String, excludes: GlobSet)] = []

        if let globs = compiled.staticGlobs, compiled.bundleIDFilter == nil {
            if isBlockedByRunningApp(rule, app: nil, running: context.runningBundleIDs) { return [] }
            for glob in globs {
                for path in GlobExpander.expand(glob, policy: context.policy) {
                    candidates.append((path, nil, glob.literalPrefix, compiled.staticExcludes))
                }
            }
        } else {
            for app in context.apps where compiled.applies(toBundleID: app.bundleID) {
                if context.ignore.ignores(bundleID: app.bundleID) { continue }
                if isBlockedByRunningApp(rule, app: app, running: context.runningBundleIDs) { continue }
                let excludes = GlobSet(globs: (rule.exclude ?? []).compactMap { substitute($0, app: app) }.map(resolver.glob))
                for template in rule.match.paths {
                    guard let pattern = substitute(template, app: app) else { continue }
                    let glob = resolver.glob(pattern)
                    for path in GlobExpander.expand(glob, policy: context.policy) {
                        candidates.append((path, app, glob.literalPrefix, excludes))
                    }
                }
            }
        }

        // Lọc trước (trùng, exclude, ignore, vùng cấm), rồi mới đo vì đo là phần tốn I/O.
        var seen = Set<String>()
        var pending: [(url: URL, app: RuleAppInfo?, root: String, excludes: GlobSet)] = []
        for c in candidates {
            guard seen.insert(c.path).inserted else { continue }
            if c.excludes.matchesSelfOrAncestor(c.path) { continue }
            if context.ignore.ignores(path: c.path) { continue }
            // Mọi đường dẫn sau khi mở rộng đều đi qua PathPolicy; trỏ vào vùng cấm thì loại và ghi log (mục 8.6 bước 4).
            if let violation = context.policy.isForbidden(c.path) {
                Log.warning(.rules, "rules", "Rule \(rule.id) trỏ vào vùng cấm, bỏ qua: \(violation.kind)")
                continue
            }
            pending.append((URL(fileURLWithPath: c.path), c.app, c.root, c.excludes))
        }
        try Task.checkCancellation()
        try context.cancellation.check()

        // Đo song song, giới hạn đồng thời theo loại ổ (mục 16.3). Mục không đọc được (TCC, quyền) thì bỏ qua,
        // không làm hỏng cả nhóm.
        let results = Locked<[Int: RuleMatch]>([:])
        let cancelled = Locked(false)
        let limit = pending.count > 1 ? context.fileSystem.recommendedConcurrency(for: context.fileSystem.home) : 1
        let next = Locked(0)
        let flag = context.cancellation
        DispatchQueue.concurrentPerform(iterations: min(limit, max(pending.count, 1))) { _ in
            while !flag.isCancelled && !cancelled.current {
                let i = next.withLock { v -> Int in defer { v += 1 }; return v }
                guard i < pending.count else { return }
                let item = pending[i]
                let skipExcluded: @Sendable (String) -> Bool = { [excludes = item.excludes, policy = context.policy] p in
                    excludes.matches(p) || policy.shouldSkipDescending(into: p)
                }
                let m: PathMeasurement
                do {
                    m = try context.fileSystem.measureConcurrently(item.url, tracker: context.tracker, counter: context.counter, skip: skipExcluded,
                                                                  cancellation: flag)
                } catch is CancellationError {
                    cancelled.withLock { $0 = true }
                    return
                } catch {
                    Log.debugPath(.rules, "Bỏ qua mục không đọc được", item.url.path)
                    continue
                }
                guard m.exists else { continue }
                if m.isDirectory && m.itemCount == 0 && rule.removal != .delete { continue }   // không còn gì để dọn
                if let minSize = rule.match.minSizeBytes, Int64(m.allocatedSize) < minSize { continue }
                if let days = rule.match.minAgeDays, days > 0 {
                    let cutoff = context.now.addingTimeInterval(-Double(days) * 86_400)
                    if m.lastUse > cutoff { continue }
                }
                let match = RuleMatch(url: item.url, measurement: m, rule: rule, app: item.app, allowedRoot: item.root)
                results.withLock { $0[i] = match }
            }
        }
        if cancelled.current { throw CancellationError() }
        try flag.check()
        try Task.checkCancellation()
        let collected = results.current
        return collected.keys.sorted().compactMap { collected[$0] }
    }

    /// Thay biến theo app. Giá trị biến được kiểm tra để không thể chèn `/` hay `..` (chống path traversal từ bundle ID giả).
    public static func substitute(_ template: String, app: RuleAppInfo) -> String? {
        var result = template
        let vars: [(String, String?)] = [("${bundleID}", app.bundleID), ("${teamID}", app.teamID), ("${appName}", app.appName)]
        for (name, value) in vars where result.contains(name) {
            guard let value, isSafeComponent(value) else { return nil }
            result = result.replacingOccurrences(of: name, with: value)
        }
        return result
    }

    public static func isSafeComponent(_ value: String) -> Bool {
        !value.isEmpty && value != "." && value != ".." && !value.contains("/") && !value.contains("\0") && !Glob.hasMagic(value)
    }

    static func isBlockedByRunningApp(_ rule: Rule, app: RuleAppInfo?, running: Set<String>) -> Bool {
        guard let ids = rule.conditions?.appNotRunning, !ids.isEmpty else { return false }
        for id in ids {
            let resolved = app.flatMap { substitute(id, app: $0) } ?? id
            if running.contains(resolved) { return true }
        }
        return false
    }
}

/// Mở rộng glob thành đường dẫn có thật trên ổ.
public enum GlobExpander {
    /// Giới hạn độ sâu cho `**` để không duyệt cả ổ.
    public static let maxGlobstarDepth = 8

    public static func expand(_ glob: Glob, policy: PathPolicy? = nil) -> [String] {
        var current: [String] = ["/"]
        let segments = glob.segments
        for (i, seg) in segments.enumerated() {
            var next: [String] = []
            for base in current {
                switch seg {
                case let .literal(name):
                    let p = join(base, name)
                    // phần tử cuối có thể là symlink (vẫn liệt kê để xoá chính symlink), phần giữa phải là thư mục
                    if i == segments.count - 1 ? exists(p) : isDirectory(p) { next.append(p) }
                case let .wildcard(pattern):
                    for child in listChildren(base) where fnmatch(pattern, child.name, 0) == 0 {
                        let p = join(base, child.name)
                        if i < segments.count - 1 && !child.isDirectory { continue }
                        if let policy, policy.shouldSkipDescending(into: p) { continue }
                        next.append(p)
                    }
                case .globstar:
                    next.append(base)
                    var frontier = [(base, 0)]
                    while let (dir, depth) = frontier.popLast() {
                        guard depth < maxGlobstarDepth else { continue }
                        for child in listChildren(dir) where child.isDirectory && !child.isSymlink {
                            let p = join(dir, child.name)
                            if let policy, policy.shouldSkipDescending(into: p) { continue }
                            next.append(p)
                            frontier.append((p, depth + 1))
                        }
                    }
                }
            }
            current = next
            if current.isEmpty { break }
        }
        return segments.isEmpty ? [] : Array(Set(current)).sorted()
    }

    private static func join(_ base: String, _ name: String) -> String {
        base == "/" ? "/" + name : base + "/" + name
    }

    private static func exists(_ path: String) -> Bool {
        var st = stat()
        return lstat(path, &st) == 0
    }

    private static func isDirectory(_ path: String) -> Bool {
        var st = stat()
        // Thư mục giữa đường dẫn được phép là symlink tới thư mục (ví dụ /var → /private/var); PathPolicy sẽ chuẩn hoá sau.
        return stat(path, &st) == 0 && st.st_mode & S_IFMT == S_IFDIR
    }

    private static func listChildren(_ dir: String) -> [BulkEntry] {
        var result: [BulkEntry] = []
        try? BulkReader.readDirectory(dir) { result.append($0) }
        return result
    }
}
