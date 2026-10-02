import CryptoKit
import Foundation
import os
import SweepCore
import SweepLogging

/// Public key Ed25519 nhúng trong app (mục 8.5). Private key chỉ nằm trong secret của CI
/// (bản dev: `Secrets/rules_signing_key.b64`, không commit).
public enum RulesPublicKey {
    public static let base64 = "NMxnU8M5Xe1El1fvbVAfLdJA/VaNtJj3pUYqQ01gdS0="
}

/// Quy đổi glob của rule thành đường dẫn thật. Cho phép đổi gốc khi chạy test trên fixture (mục 18).
public struct PathResolver: Sendable, Hashable {
    public var home: String
    /// Tiền tố thêm vào trước đường dẫn tuyệt đối (`/Library/...`). Rỗng khi chạy thật.
    public var rootPrefix: String

    public init(home: String = AppEdition.userHomePath, rootPrefix: String = "") {
        self.home = home
        self.rootPrefix = rootPrefix
    }

    public static var live: PathResolver { PathResolver() }

    public func resolve(_ pattern: String) -> String {
        var p = pattern.replacingOccurrences(of: "${userHome}", with: home)
        if p == "~" { p = home } else if p.hasPrefix("~/") { p = home + p.dropFirst(1) } else if p.hasPrefix("/"), !p.hasPrefix(home) { p = rootPrefix + p }
        return p
    }

    public func glob(_ pattern: String) -> Glob { Glob(resolve(pattern), home: home) }
}

/// Rule đã biên dịch: glob tĩnh được biên dịch một lần lúc nạp (mục 8.6 bước 2).
public struct CompiledRule: Sendable, Identifiable {
    public let rule: Rule
    /// `nil` khi rule có biến theo app (phải mở rộng theo từng app).
    public let staticGlobs: [Glob]?
    public let staticExcludes: GlobSet
    public let minOS: OSVersion?
    public let bundleIDFilter: [String]?

    public var id: RuleID { rule.id }

    public init(_ rule: Rule, resolver: PathResolver) {
        self.rule = rule
        let excludesHaveVars = (rule.exclude ?? []).contains { $0.contains("${bundleID}") || $0.contains("${teamID}") || $0.contains("${appName}") }
        staticGlobs = rule.usesAppVariables ? nil : rule.match.paths.map(resolver.glob)
        staticExcludes = excludesHaveVars ? GlobSet(globs: []) : GlobSet(globs: (rule.exclude ?? []).map(resolver.glob))
        minOS = rule.conditions?.minOS.flatMap(OSVersion.init)
        bundleIDFilter = rule.match.bundleIDs
    }

    public func applies(toBundleID bundleID: String) -> Bool {
        guard let filter = bundleIDFilter, !filter.isEmpty else { return true }
        return filter.contains { Glob.componentMatchesPublic($0, bundleID) }
    }
}

/// Ảnh chụp bất biến của bộ rule đang dùng; truyền vào `ScanContext`.
public struct RuleSnapshot: Sendable {
    public let version: UInt64
    public let rules: [CompiledRule]
    public let knowledge: Knowledge
    public let disabled: Set<RuleID>
    public let resolver: PathResolver
    public let source: Source

    public enum Source: Sendable, Equatable {
        case bundled, downloaded, development, empty
    }

    public init(version: UInt64, rules: [Rule], knowledge: Knowledge, disabled: Set<RuleID> = [], resolver: PathResolver = .live, source: Source) {
        self.version = version
        // Bản Mac App Store không có helper root: bỏ rule cần root ngay từ đầu để không quét/hiện mục không xoá được.
        self.rules = rules.filter { !(AppEdition.isSandboxed && $0.requiresRoot == true) }.map { CompiledRule($0, resolver: resolver) }
        self.knowledge = knowledge
        self.disabled = disabled
        self.resolver = resolver
        self.source = source
    }

    public static let empty = RuleSnapshot(version: 0, rules: [], knowledge: Knowledge(), source: .empty)

    /// Rule đang bật thuộc một nhóm.
    public func rules(in category: String) -> [CompiledRule] {
        rules.filter { $0.rule.category == category && !disabled.contains($0.id) }
    }

    public func rule(_ id: RuleID) -> CompiledRule? { rules.first { $0.id == id } }

    public var activeRules: [CompiledRule] { rules.filter { !disabled.contains($0.id) } }

    public func withDisabled(_ ids: Set<RuleID>) -> RuleSnapshot {
        RuleSnapshot(version: version, rules: rules.map(\.rule), knowledge: knowledge, disabled: disabled.union(ids), resolver: resolver, source: source)
    }

    public func withResolver(_ resolver: PathResolver) -> RuleSnapshot {
        RuleSnapshot(version: version, rules: rules.map(\.rule), knowledge: knowledge, disabled: disabled, resolver: resolver, source: source)
    }
}

/// Nạp bộ rule mới nhất hợp lệ (mục 8.6 bước 1): bộ đã tải về (chữ ký hợp lệ, phiên bản cao hơn) hoặc bộ đi kèm app.
public final class RuleStore: Sendable {
    public let bundledURL: URL?
    public let cacheDirectory: URL
    private let verifier: RuleBundleVerifier
    private let resolver: PathResolver
    private let state: Locked<RuleSnapshot>
    private let appVersion: OSVersion

    public init(bundled: URL?, cache: URL, publicKeyBase64: String = RulesPublicKey.base64, resolver: PathResolver = .live,
                appVersion: OSVersion = AppVersion.current) throws {
        bundledURL = bundled
        cacheDirectory = cache
        verifier = try RuleBundleVerifier(publicKeyBase64: publicKeyBase64)
        self.resolver = resolver
        self.appVersion = appVersion
        state = Locked(.empty)
        state.withLock { $0 = load() }
    }

    public var snapshot: RuleSnapshot { state.current }

    /// Mặc định: `Resources/Rules/rules.bundle` trong app, cache tại `~/Library/Application Support/CleanBoost/Rules/` (mục 12.1).
    public static var defaultCacheDirectory: URL {
        URL.appLibrary.appendingPathComponent("Application Support/CleanBoost/Rules", isDirectory: true)
    }

    public static var defaultBundledURL: URL? {
        Bundle.main.url(forResource: "rules", withExtension: "bundle", subdirectory: "Rules")
            ?? Bundle.main.url(forResource: "rules", withExtension: "bundle")
    }

    private func load() -> RuleSnapshot {
        #if DEBUG
        // Rule tác giả đang viết: nạp thẳng thư mục JSON chưa ký (chỉ bản Debug).
        if let dir = ProcessInfo.processInfo.environment["MASHCLEAN_RULES_DIR"],
           let payload = try? RuleSourceLoader.load(directory: URL(fileURLWithPath: dir)) {
            Log.warning(.rules, "rules", "Đang dùng rule chưa ký từ MASHCLEAN_RULES_DIR (chỉ Debug)")
            return RuleSnapshot(version: payload.version, rules: payload.rules, knowledge: payload.knowledge, resolver: resolver, source: .development)
        }
        #endif
        var best: (RuleBundle, RuleSnapshot.Source)?
        if let bundledURL, let data = try? Data(contentsOf: bundledURL) {
            do {
                best = (try verifier.verify(data), .bundled)
            } catch {
                Log.error(.rules, "rules", "Bộ rule đi kèm không hợp lệ: \(error)")
            }
        }
        for url in cachedBundles() {
            guard let data = try? Data(contentsOf: url) else { continue }
            do {
                let bundle = try verifier.verify(data)
                guard OSVersion(packed: bundle.header.minAppVersion) <= appVersion else { continue }
                if bundle.version > (best?.0.version ?? 0) { best = (bundle, .downloaded) }
            } catch {
                Log.error(.rules, "rules", "Bỏ rule cache \(url.lastPathComponent): \(error)")
            }
        }
        guard let (bundle, source) = best else {
            Log.error(.rules, "rules", "Không có bộ rule hợp lệ nào")
            return .empty
        }
        Log.info(.rules, "rules", "Nạp rule phiên bản \(bundle.version) (\(source)), \(bundle.payload.rules.count) rule")
        return RuleSnapshot(version: bundle.version, rules: bundle.payload.rules, knowledge: bundle.payload.knowledge, resolver: resolver, source: source)
    }

    private func cachedBundles() -> [URL] {
        let items = (try? FileManager.default.contentsOfDirectory(at: cacheDirectory, includingPropertiesForKeys: nil)) ?? []
        return items.filter { $0.pathExtension == "bundle" }
    }

    /// Cài bộ rule tải về (mục 14.2): kiểm tra SHA-256, chữ ký, phiên bản app, chống hạ cấp. Giữ 2 phiên bản gần nhất.
    @discardableResult
    public func install(_ data: Data, expectedSHA256: String?, disabledRules: [String] = []) throws -> RuleSnapshot {
        if let expectedSHA256 {
            let digest = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
            guard digest == expectedSHA256.lowercased() else { throw RuleError.checksumMismatch }
        }
        let bundle = try verifier.verify(data)
        let current = snapshot.version
        guard bundle.version > current else { throw RuleError.downgrade(current: current, offered: bundle.version) }
        let minApp = OSVersion(packed: bundle.header.minAppVersion)
        guard minApp <= appVersion else { throw RuleError.appTooOld(required: minApp.description) }

        try FileManager.default.createDirectory(at: cacheDirectory, withIntermediateDirectories: true)
        let file = cacheDirectory.appendingPathComponent("rules-\(bundle.version).bundle")
        try data.write(to: file, options: .atomic)
        pruneCache(keep: 2)

        let snap = RuleSnapshot(version: bundle.version, rules: bundle.payload.rules, knowledge: bundle.payload.knowledge,
                                disabled: Set(disabledRules.map { RuleID($0) }), resolver: resolver, source: .downloaded)
        state.withLock { $0 = snap }
        Log.info(.rules, "rules", "Đã cài rule phiên bản \(bundle.version), dùng từ lần quét kế tiếp")
        return snap
    }

    /// Kill switch: tắt ngay các rule lỗi theo manifest (mục 14.2).
    public func applyDisabledRules(_ ids: [String]) {
        guard !ids.isEmpty else { return }
        state.withLock { $0 = $0.withDisabled(Set(ids.map { RuleID($0) })) }
        Log.warning(.rules, "rules", "Tắt \(ids.count) rule theo manifest: \(ids.joined(separator: ", "))")
    }

    private func pruneCache(keep: Int) {
        let bundles = cachedBundles().sorted { a, b in
            let va = UInt64(a.deletingPathExtension().lastPathComponent.replacingOccurrences(of: "rules-", with: "")) ?? 0
            let vb = UInt64(b.deletingPathExtension().lastPathComponent.replacingOccurrences(of: "rules-", with: "")) ?? 0
            return va > vb
        }
        for old in bundles.dropFirst(keep) { try? FileManager.default.removeItem(at: old) }
    }
}

/// Đọc thư mục nguồn `Rules/*.json` (dùng cho `rulepack` và chế độ phát triển).
public enum RuleSourceLoader {
    /// Mỗi file JSON là một rule hoặc một mảng rule; `knowledge.json` chứa tri thức; `version.txt` (tuỳ chọn) chứa phiên bản.
    public static func load(directory: URL, version: UInt64? = nil) throws -> RulePayload {
        let fm = FileManager.default
        guard let enumerator = fm.enumerator(at: directory, includingPropertiesForKeys: nil) else { throw RuleError.badFormat }
        var rules: [Rule] = []
        var knowledge = Knowledge()
        let decoder = JSONDecoder()
        var files: [URL] = []
        while let url = enumerator.nextObject() as? URL {
            if url.pathExtension == "json" { files.append(url) }
        }
        for url in files.sorted(by: { $0.path < $1.path }) {
            let data = try Data(contentsOf: url)
            if url.lastPathComponent == "knowledge.json" {
                knowledge = try decoder.decode(Knowledge.self, from: data)
                continue
            }
            if url.lastPathComponent.hasPrefix("manifest") { continue }
            do {
                if let many = try? decoder.decode([Rule].self, from: data) {
                    rules += many
                } else {
                    rules.append(try decoder.decode(Rule.self, from: data))
                }
            } catch {
                throw RuleError.invalidJSON("\(url.lastPathComponent): \(error)")
            }
        }
        let resolvedVersion = version
            ?? (try? String(contentsOf: directory.appendingPathComponent("version.txt"), encoding: .utf8))
                .flatMap { UInt64($0.trimmingCharacters(in: .whitespacesAndNewlines)) }
            ?? 0
        return RulePayload(version: resolvedVersion, rules: rules, knowledge: knowledge)
    }
}
