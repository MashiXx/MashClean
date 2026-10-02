import Foundation
import RuleEngine
import SweepCore

/// Một vấn đề lint, gắn với file và rule.
struct LintIssue: CustomStringConvertible {
    enum Severity: String { case error, warning }

    let severity: Severity
    let file: String
    let ruleID: String?
    let message: String

    var description: String {
        let where_ = ruleID.map { "\(file) [\($0)]" } ?? file
        return "\(severity.rawValue): \(where_): \(message)"
    }
}

/// Rule nguồn kèm file chứa nó.
struct SourceRule {
    let rule: Rule
    let file: String
}

/// Đọc thư mục `Rules/` từng file một để báo lỗi kèm tên file (mục 8.5 "rulepack lint").
struct RuleSource {
    var rules: [SourceRule] = []
    var knowledge: Knowledge?
    var version: UInt64?
    var issues: [LintIssue] = []

    static let skippedFiles: Set<String> = ["knowledge.json", "manifest.json"]

    static let ruleKeys: Set<String> = ["id", "version", "category", "title", "reason", "safety", "removal", "requiresRoot", "match", "conditions", "exclude", "notes"]
    static let matchKeys: Set<String> = ["paths", "forEachInstalledApp", "minAgeDays", "minSizeBytes", "bundleIDs", "provider"]
    static let conditionKeys: Set<String> = ["appNotRunning", "minOS"]
    static let knowledgeKeys: Set<String> = ["appleBundlePrefixes", "orphanWhitelist", "protectedCacheNames", "keepLanguages"]

    init(directory: URL) throws {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: directory.path, isDirectory: &isDir), isDir.boolValue else {
            throw CLIError("Không tìm thấy thư mục rule: \(directory.path)")
        }
        let files = Self.jsonFiles(in: directory)
        let decoder = JSONDecoder()
        for url in files {
            let rel = Self.relative(url, to: directory)
            let data: Data
            do { data = try Data(contentsOf: url) } catch {
                issues.append(LintIssue(severity: .error, file: rel, ruleID: nil, message: "Không đọc được file: \(error.localizedDescription)"))
                continue
            }
            if url.lastPathComponent == "knowledge.json" {
                checkUnknownKeys(data, file: rel)
                do { knowledge = try decoder.decode(Knowledge.self, from: data) } catch {
                    issues.append(LintIssue(severity: .error, file: rel, ruleID: nil, message: "knowledge.json sai schema: \(Self.describe(error))"))
                }
                continue
            }
            if url.lastPathComponent == "manifest.json" {
                do { _ = try decoder.decode(RuleManifest.self, from: data) } catch {
                    issues.append(LintIssue(severity: .error, file: rel, ruleID: nil, message: "manifest.json sai schema mục 14.2: \(Self.describe(error))"))
                }
                continue
            }
            guard let json = try? JSONSerialization.jsonObject(with: data) else {
                issues.append(LintIssue(severity: .error, file: rel, ruleID: nil, message: "JSON không hợp lệ"))
                continue
            }
            let objects: [Any]
            if let array = json as? [Any] { objects = array } else { objects = [json] }
            for (i, obj) in objects.enumerated() {
                guard let dict = obj as? [String: Any] else {
                    issues.append(LintIssue(severity: .error, file: rel, ruleID: nil, message: "Phần tử \(i) không phải object rule"))
                    continue
                }
                let id = dict["id"] as? String
                for key in dict.keys where !Self.ruleKeys.contains(key) {
                    issues.append(LintIssue(severity: .error, file: rel, ruleID: id, message: "Trường không biết: \(key)"))
                }
                if let match = dict["match"] as? [String: Any] {
                    for key in match.keys where !Self.matchKeys.contains(key) {
                        issues.append(LintIssue(severity: .error, file: rel, ruleID: id, message: "Trường không biết trong match: \(key)"))
                    }
                }
                if let cond = dict["conditions"] as? [String: Any] {
                    for key in cond.keys where !Self.conditionKeys.contains(key) {
                        issues.append(LintIssue(severity: .error, file: rel, ruleID: id, message: "Trường không biết trong conditions: \(key)"))
                    }
                }
                do {
                    let one = try JSONSerialization.data(withJSONObject: dict)
                    rules.append(SourceRule(rule: try decoder.decode(Rule.self, from: one), file: rel))
                } catch {
                    issues.append(LintIssue(severity: .error, file: rel, ruleID: id, message: "Sai schema: \(Self.describe(error))"))
                }
            }
        }
        let versionURL = directory.appendingPathComponent("version.txt")
        if let text = try? String(contentsOf: versionURL, encoding: .utf8) {
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if let v = UInt64(trimmed), Self.isValidRulesVersion(v) {
                version = v
            } else {
                issues.append(LintIssue(severity: .error, file: "version.txt", ruleID: nil, message: "Phiên bản phải có dạng yyyymmddNN, nhận \"\(trimmed)\""))
            }
        }
    }

    private mutating func checkUnknownKeys(_ data: Data, file: String) {
        guard let dict = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return }
        for key in dict.keys where !Self.knowledgeKeys.contains(key) {
            issues.append(LintIssue(severity: .error, file: file, ruleID: nil, message: "Trường không biết trong knowledge: \(key)"))
        }
    }

    static func jsonFiles(in directory: URL) -> [URL] {
        guard let e = FileManager.default.enumerator(at: directory, includingPropertiesForKeys: nil) else { return [] }
        var files: [URL] = []
        while let url = e.nextObject() as? URL {
            if url.pathExtension == "json" { files.append(url) }
        }
        return files.sorted { $0.path < $1.path }
    }

    static func relative(_ url: URL, to base: URL) -> String {
        let b = base.standardizedFileURL.path
        let p = url.standardizedFileURL.path
        return p.hasPrefix(b + "/") ? String(p.dropFirst(b.count + 1)) : p
    }

    static func describe(_ error: any Error) -> String {
        switch error {
        case let DecodingError.dataCorrupted(ctx): return "\(path(ctx.codingPath)): \(ctx.debugDescription)"
        case let DecodingError.keyNotFound(key, ctx): return "thiếu trường \(path(ctx.codingPath + [key]))"
        case let DecodingError.typeMismatch(_, ctx): return "\(path(ctx.codingPath)): sai kiểu (\(ctx.debugDescription))"
        case let DecodingError.valueNotFound(_, ctx): return "\(path(ctx.codingPath)): thiếu giá trị"
        default: return String(describing: error)
        }
    }

    private static func path(_ keys: [any CodingKey]) -> String {
        keys.map { $0.intValue.map { "[\($0)]" } ?? $0.stringValue }.joined(separator: ".")
    }

    /// `yyyymmddNN` với ngày hợp lệ.
    static func isValidRulesVersion(_ v: UInt64) -> Bool {
        guard (2000_01_01_00...2999_12_31_99).contains(v) else { return false }
        let day = (v / 100) % 100, month = (v / 10_000) % 100, year = v / 1_000_000
        var c = DateComponents()
        c.year = Int(year); c.month = Int(month); c.day = Int(day)
        guard (1...12).contains(month), (1...31).contains(day) else { return false }
        return Calendar(identifier: .gregorian).date(from: c).map { Calendar(identifier: .gregorian).component(.day, from: $0) == Int(day) } ?? false
    }
}

/// Kiểm tra bộ rule (mục 8.5, 23.8): schema, id, glob, biến, root, an toàn, provider, ngôn ngữ, vùng cấm (mục 15.1).
struct RuleLinter {
    /// Provider cài trong code (`SystemJunkTasks.providers`).
    static let knownProviders: Set<String> = [
        "simctl.unavailableDevices", "simctl.unusedRuntimes", "docker.prune", "languageFiles", "iosBackups", "trash",
    ]
    /// Remover chuyên biệt có trong `CleanEngine` (mục 7.3).
    static let knownRemovers: Set<String> = Set([
        RemoverID.simulator, .simulatorRuntime, .launchItem, .localSnapshot, .app, .docker, .maintenance, "pkgForget",
    ].map(\.rawValue))
    static let allowedVariables: Set<String> = ["bundleID", "teamID", "appName", "userHome"]
    static let appVariables: Set<String> = ["bundleID", "teamID", "appName"]
    static let requiredLanguages = ["en", "vi"]

    /// Home giả dùng để mở rộng `~` khi kiểm tra vùng cấm.
    static let fakeHome = "/Users/rulepack-lint"
    static let sampleApp = RuleAppInfo(bundleID: "com.example.rulepack", teamID: "ABCDE12345", appName: "RulepackLint", url: nil)

    let userPolicy = PathPolicy.user(home: URL(fileURLWithPath: RuleLinter.fakeHome))
    let rootPolicy = PathPolicy.root

    /// Gốc vùng cấm (mục 15.1) — kể cả bí danh qua symlink (`/etc`, `/var`, `/tmp`) và dữ liệu cá nhân nhạy cảm.
    let forbiddenRoots: [String] = {
        let h = RuleLinter.fakeHome
        return [
            "/System", "/usr", "/bin", "/sbin", "/dev", "/cores", "/etc", "/var/db", "/var/vm", "/var/protected",
            "/private/var/db", "/private/var/vm", "/private/etc", "/private/var/protected",
            "/Library/Apple", "/Library/Keychains", "/Library/Application Support/com.apple.TCC", "/Library/Security",
            "/Library/SystemExtensions", "/var/root",
            "\(h)/Library/Keychains", "\(h)/Library/Mobile Documents", "\(h)/Library/Messages", "\(h)/Library/Photos",
            "\(h)/Library/Application Support/com.apple.TCC", "\(h)/Library/Accounts", "\(h)/Library/CloudStorage",
            "\(h)/Library/Mail", "\(h)/Library/Safari", "\(h)/Library/Calendars", "\(h)/Library/Cookies",
            "\(h)/Library/Application Support/AddressBook", "\(h)/Library/Application Support/MobileSync",
            "\(h)/Library/Group Containers/group.com.apple.notes", "\(h)/Library/Containers/com.apple.Notes",
            "\(h)/.ssh", "\(h)/.gnupg", "\(h)/Documents", "\(h)/Desktop", "\(h)/Pictures", "\(h)/Movies", "\(h)/Music",
        ]
    }()

    /// Chính thư mục này không được khớp (nội dung thì được).
    let protectedExact: [String] = {
        let h = RuleLinter.fakeHome
        return [
            "/", "/Applications", "/Library", "/Users", "/Volumes", "/private", "/private/var", "/private/tmp", "/tmp", "/var",
            "/Applications/Utilities", "/Users/Shared", "/opt", "/opt/homebrew", "/usr/local",
            h, "\(h)/Downloads", "\(h)/Library", "\(h)/Applications", "\(h)/Public", "\(h)/Sites",
        ]
    }()

    /// Thư mục "vỏ": chỉ được khớp với `deleteContents`.
    let shells: [String] = {
        let h = RuleLinter.fakeHome
        return [
            "/Library/Caches", "/Library/Logs", "/private/var/log", "/Library/LaunchAgents", "/Library/LaunchDaemons",
            "/Library/Application Support", "/Library/Preferences",
            "\(h)/Library/Caches", "\(h)/Library/Logs", "\(h)/Library/Application Support", "\(h)/Library/Preferences",
            "\(h)/Library/Containers", "\(h)/Library/Group Containers", "\(h)/Library/LaunchAgents",
            "\(h)/Library/Logs/DiagnosticReports", "\(h)/.Trash", "\(h)/Library/Developer", "\(h)/Library/Developer/Xcode",
            "\(h)/Library/Developer/Xcode/DerivedData", "\(h)/Library/Developer/Xcode/Archives",
            "\(h)/Library/Developer/CoreSimulator", "\(h)/Library/Saved Application State", "\(h)/Library/HTTPStorages",
        ]
    }()

    func lint(_ source: RuleSource) -> [LintIssue] {
        var issues = source.issues
        var seen: [String: String] = [:]
        for s in source.rules {
            let id = s.rule.id.rawValue
            if let other = seen[id] {
                issues.append(LintIssue(severity: .error, file: s.file, ruleID: id, message: "Trùng id với rule trong \(other)"))
            } else {
                seen[id] = s.file
            }
            issues += lint(s.rule, file: s.file)
        }
        if source.knowledge == nil {
            issues.append(LintIssue(severity: .error, file: "knowledge.json", ruleID: nil, message: "Không có knowledge.json hợp lệ"))
        } else if let k = source.knowledge {
            if !k.appleBundlePrefixes.contains("com.apple.") {
                issues.append(LintIssue(severity: .error, file: "knowledge.json", ruleID: nil, message: "appleBundlePrefixes phải chứa com.apple."))
            }
            if !k.isWhitelistedOrphan("com.apple.Safari") {
                issues.append(LintIssue(severity: .error, file: "knowledge.json", ruleID: nil, message: "orphanWhitelist phải bao com.apple.*"))
            }
            for lang in ["Base", "en"] where !k.keepLanguages.contains(lang) {
                issues.append(LintIssue(severity: .error, file: "knowledge.json", ruleID: nil, message: "keepLanguages phải chứa \(lang)"))
            }
        }
        if source.version == nil {
            issues.append(LintIssue(severity: .warning, file: "version.txt", ruleID: nil, message: "Không có version.txt, cần truyền --version khi build"))
        }
        let used = Set(source.rules.map(\.rule.category))
        for c in RuleCategory.all where !used.contains(c) {
            issues.append(LintIssue(severity: .warning, file: "-", ruleID: nil, message: "Nhóm \(c) chưa có rule nào"))
        }
        return issues
    }

    func lint(_ rule: Rule, file: String) -> [LintIssue] {
        var out: [LintIssue] = []
        let id = rule.id.rawValue
        func error(_ m: String) { out.append(LintIssue(severity: .error, file: file, ruleID: id, message: m)) }
        func warning(_ m: String) { out.append(LintIssue(severity: .warning, file: file, ruleID: id, message: m)) }

        // id dạng chấm, ổn định
        if id.range(of: #"^[a-z][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+)+$"#, options: .regularExpression) == nil {
            error("id phải dạng chấm, ví dụ devtools.npm.cache")
        }
        if (rule.version ?? 0) < 1 { error("Thiếu version (số nguyên >= 1)") }
        if !RuleCategory.all.contains(rule.category) { error("category không hợp lệ: \(rule.category)") }

        for (field, text) in [("title", rule.title), ("reason", rule.reason)] {
            guard let text else { error("Thiếu \(field)"); continue }
            for lang in Self.requiredLanguages where (text.values[lang] ?? "").trimmingCharacters(in: .whitespaces).isEmpty {
                error("\(field) thiếu bản \(lang)")
            }
        }

        // Chiến lược xoá
        if case let .custom(remover) = rule.removal, !Self.knownRemovers.contains(remover.rawValue) {
            error("Remover không biết: custom:\(remover.rawValue)")
        }
        let requiresRoot = rule.requiresRoot ?? false
        if requiresRoot && rule.removal == .moveToTrash { error("Rule cần root không thể moveToTrash (helper không có Thùng rác của người dùng)") }
        if rule.category == RuleCategory.oldDownloads && rule.removal != .moveToTrash { error("File người dùng tải về phải moveToTrash (mục 15.2)") }
        if rule.safety == .safe && rule.category == RuleCategory.languageFiles { error("Gói ngôn ngữ phải ở mức risky (mục 8.4)") }

        // Provider
        if let provider = rule.match.provider {
            if !Self.knownProviders.contains(provider) { error("Provider không biết: \(provider)") }
            if !rule.match.paths.isEmpty { error("Rule dùng provider không được khai báo paths") }
            if case .custom = rule.removal {} else if ["simctl.unavailableDevices", "simctl.unusedRuntimes", "docker.prune"].contains(provider) {
                error("Provider \(provider) cần removal custom:<remover>")
            }
        } else {
            if rule.match.paths.isEmpty { error("match.paths rỗng") }
            if case .custom = rule.removal { error("removal custom chỉ dùng với provider") }
        }

        // Số
        if let d = rule.match.minAgeDays, d < 0 { error("minAgeDays phải >= 0") }
        if let s = rule.match.minSizeBytes, s < 0 { error("minSizeBytes phải >= 0") }
        if let os = rule.conditions?.minOS, OSVersion(os) == nil { error("minOS không hợp lệ: \(os)") }

        // Biến theo app
        let vars = Set((rule.match.paths + (rule.exclude ?? [])).flatMap(Self.variables))
        let usesAppVars = !vars.isDisjoint(with: Self.appVariables)
        let perApp = (rule.match.forEachInstalledApp ?? false) || !(rule.match.bundleIDs ?? []).isEmpty
        if usesAppVars && !perApp { error("Dùng biến theo app nhưng không có forEachInstalledApp hoặc bundleIDs") }
        if (rule.match.forEachInstalledApp ?? false) && !rule.match.paths.contains(where: { !Set(Self.variables($0)).isDisjoint(with: Self.appVariables) }) {
            warning("forEachInstalledApp nhưng paths không dùng biến theo app")
        }
        for b in rule.match.bundleIDs ?? [] where b.isEmpty || b.contains("/") || b == "*" {
            error("bundleIDs không hợp lệ: \"\(b)\"")
        }
        for a in rule.conditions?.appNotRunning ?? [] {
            if a.isEmpty { error("appNotRunning có phần tử rỗng") }
            let v = Self.variables(a)
            if !v.allSatisfy({ $0 == "bundleID" }) { error("appNotRunning chỉ được dùng biến ${bundleID}") }
            if !v.isEmpty && !perApp { error("appNotRunning dùng ${bundleID} nhưng rule không theo app") }
        }
        if rule.category == RuleCategory.appLeftovers {
            if !(id.hasPrefix("app.") && id.hasSuffix(".leftovers")) { error("Rule appLeftovers phải có id dạng app.<bundleID>.leftovers") }
            if (rule.match.bundleIDs ?? []).isEmpty { error("Rule appLeftovers phải khai báo match.bundleIDs") }
        }

        // Đường dẫn
        for p in rule.match.paths { out += lintPath(p, rule: rule, file: file, isExclude: false) }
        for p in rule.exclude ?? [] { out += lintPath(p, rule: rule, file: file, isExclude: true) }
        return out
    }

    func lintPath(_ pattern: String, rule: Rule, file: String, isExclude: Bool) -> [LintIssue] {
        var out: [LintIssue] = []
        let id = rule.id.rawValue
        let label = isExclude ? "exclude" : "path"
        func error(_ m: String) { out.append(LintIssue(severity: .error, file: file, ruleID: id, message: "\(label) \"\(pattern)\": \(m)")) }

        for v in Self.variables(pattern) where !Self.allowedVariables.contains(v) { error("biến không được phép ${\(v)}") }
        if pattern.contains("$") && pattern.replacingOccurrences(of: #"\$\{[A-Za-z]+\}"#, with: "", options: .regularExpression).contains("$") {
            error("ký tự $ không thuộc biến hợp lệ")
        }
        guard pattern.hasPrefix("~/") || pattern.hasPrefix("/") || pattern.hasPrefix("${userHome}/") else {
            error("phải bắt đầu bằng ~/, / hoặc ${userHome}/")
            return out
        }
        if pattern.hasSuffix("/") { error("không được kết thúc bằng /") }
        let comps = pattern.split(separator: "/", omittingEmptySubsequences: false).dropFirst()
        if comps.contains(where: { $0.isEmpty }) && !pattern.hasSuffix("/") { error("có thành phần rỗng (//)") }
        if comps.contains(where: { $0 == "." || $0 == ".." }) { error("không được chứa . hoặc .. (chống path traversal)") }
        for c in comps where !Self.bracketsBalanced(c) { error("ngoặc [] không cân bằng trong \"\(c)\"") }
        if !isExclude && comps.contains(where: { $0 == "**" }) { error("không dùng ** trong match.paths (quá rộng, chậm)") }
        if comps.contains(where: { $0.contains("**") && $0 != "**" }) { error("** phải là một thành phần riêng") }
        if pattern.contains(".photoslibrary") { error("chạm thư viện Ảnh (vùng cấm)") }
        guard out.isEmpty else { return out }

        // Mở rộng biến với app mẫu và home giả.
        guard let substituted = RuleEvaluator.substitute(pattern, app: Self.sampleApp) else {
            error("không thay được biến")
            return out
        }
        let resolver = PathResolver(home: Self.fakeHome, rootPrefix: "")
        let resolved = resolver.resolve(substituted)
        let glob = Glob(resolved, home: Self.fakeHome)
        let prefix = glob.literalPrefix
        let underHome = resolved == Self.fakeHome || resolved.hasPrefix(Self.fakeHome + "/")
        let userArea = underHome || resolved.hasPrefix("/Users/")

        if isExclude { return out }

        // Độ sâu tối thiểu: không cho glob quá nông như `~/*` hay `/Library/*`.
        let prefixDepth = prefix.split(separator: "/").count
        if underHome {
            if prefix == Self.fakeHome || !prefix.hasPrefix(Self.fakeHome + "/") { error("phần cố định phải nằm sâu hơn thư mục home") }
        } else if prefixDepth < 2 {
            error("phần cố định quá nông (\(prefix))")
        }

        // requiresRoot khớp đường dẫn hệ thống.
        let requiresRoot = rule.requiresRoot ?? false
        if !userArea && !requiresRoot { error("đường dẫn hệ thống cần requiresRoot: true") }
        if underHome && requiresRoot { error("đường dẫn trong home không được requiresRoot: true") }

        // Vùng cấm (mục 15.1).
        for root in forbiddenRoots where glob.matches(root) || glob.couldMatchDescendant(of: root) {
            error("chạm vùng cấm \(root.replacingOccurrences(of: Self.fakeHome, with: "~"))")
        }
        // Thư mục được bảo vệ đã nằm trong exclude thì không tính (exclude luôn thắng match).
        let excludes = GlobSet(globs: (rule.exclude ?? []).compactMap { RuleEvaluator.substitute($0, app: Self.sampleApp) }.map(resolver.glob))
        for p in protectedExact where glob.matches(p) && !excludes.matchesSelfOrAncestor(p) {
            error("khớp chính thư mục được bảo vệ \(p.replacingOccurrences(of: Self.fakeHome, with: "~"))")
        }
        if rule.removal != .deleteContents {
            for p in shells where glob.matches(p) && !excludes.matchesSelfOrAncestor(p) {
                error("khớp thư mục vỏ \(p.replacingOccurrences(of: Self.fakeHome, with: "~")): chỉ được deleteContents")
            }
        }

        // Kiểm tra bằng chính PathPolicy của app và helper với một đường dẫn mẫu.
        let sample = Self.samplePath(for: glob)
        for (name, policy) in [("user", userPolicy), ("root", rootPolicy)] {
            if let v = policy.isForbidden(sample) { error("PathPolicy.\(name) chặn \(sample): \(v.kind)") }
        }
        let intent: PathPolicy.Intent = rule.removal == .deleteContents ? .removeContents : .removeItem
        let policy = requiresRoot ? rootPolicy : userPolicy
        do {
            try policy.check(URL(fileURLWithPath: sample), intent: intent, allowedRoots: [prefix], checkOwnership: false)
        } catch {
            if case .readOnlyVolume = error.kind {} else {
                out.append(LintIssue(severity: .error, file: file, ruleID: id,
                                     message: "\(label) \"\(pattern)\": PathPolicy.\(requiresRoot ? "root" : "user") từ chối \(sample): \(error.kind)"))
            }
        }
        return out
    }

    /// Đường dẫn cụ thể khớp glob: thay ký tự đại diện bằng tên mẫu.
    static func samplePath(for glob: Glob) -> String {
        var parts: [String] = []
        for seg in glob.segments {
            switch seg {
            case let .literal(s): parts.append(s)
            case .globstar: continue
            case let .wildcard(p):
                var s = p.replacingOccurrences(of: #"\[[^\]]*\]"#, with: "a", options: .regularExpression)
                s = s.replacingOccurrences(of: "*", with: "rulepack").replacingOccurrences(of: "?", with: "x")
                parts.append(s)
            }
        }
        return "/" + parts.joined(separator: "/")
    }

    static func variables(_ s: String) -> [String] {
        guard let re = try? NSRegularExpression(pattern: #"\$\{([^}]*)\}"#) else { return [] }
        let ns = s as NSString
        return re.matches(in: s, range: NSRange(location: 0, length: ns.length)).map { ns.substring(with: $0.range(at: 1)) }
    }

    static func bracketsBalanced(_ s: Substring) -> Bool {
        var open = false
        for ch in s {
            if ch == "[" { if open { return false }; open = true }
            if ch == "]" { if !open { return false }; open = false }
        }
        return !open
    }
}
