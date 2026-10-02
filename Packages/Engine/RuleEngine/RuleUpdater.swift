import Foundation
import os
import SweepCore
import SweepLogging

/// Manifest trên CDN (mục 14.2).
public struct RuleManifest: Sendable, Codable, Equatable {
    public var latest: UInt64
    public var minApp: String
    public var url: String
    public var sha256: String
    /// Kill switch: tắt ngay một rule lỗi.
    public var disabledRules: [String]?

    public init(latest: UInt64, minApp: String, url: String, sha256: String, disabledRules: [String]? = nil) {
        self.latest = latest
        self.minApp = minApp
        self.url = url
        self.sha256 = sha256
        self.disabledRules = disabledRules
    }
}

/// Kiểm tra rule mới mỗi 24 giờ (và khi khởi động nếu lần trước đã quá 24 giờ).
public final class RuleUpdater: Sendable {
    public enum Outcome: Sendable, Equatable {
        case skippedRecentlyChecked
        case notConfigured
        case upToDate
        case installed(version: UInt64)
        case appTooOld(required: String)
        case failed(String)
    }

    public static let checkInterval: TimeInterval = 24 * 3600

    private let store: RuleStore
    private let manifestURL: URL?
    private let defaultsSuite: String
    private let session: URLSession

    private enum Keys {
        static let lastCheck = "rules.lastCheck"
        static let etag = "rules.manifestETag"
        static let channel = "updates.channel"
    }

    public init(store: RuleStore, manifestURL: URL?, defaultsSuite: String = MashCleanIdentifiers.appGroup, session: URLSession = .shared) {
        self.store = store
        self.manifestURL = manifestURL
        self.defaultsSuite = defaultsSuite
        self.session = session
    }

    private var defaults: UserDefaults { UserDefaults(suiteName: defaultsSuite) ?? .standard }

    /// URL manifest theo kênh (`stable`/`beta`): đọc `MashCleanRulesManifestURL` trong Info.plist, thay `{channel}`.
    public static func manifestURL(channel: String) -> URL? {
        guard let template = Bundle.main.object(forInfoDictionaryKey: "MashCleanRulesManifestURL") as? String, !template.isEmpty else { return nil }
        return URL(string: template.replacingOccurrences(of: "{channel}", with: channel))
    }

    public var lastCheck: Date? { defaults.object(forKey: Keys.lastCheck) as? Date }

    @discardableResult
    public func checkIfNeeded(force: Bool = false) async -> Outcome {
        guard let manifestURL else { return .notConfigured }
        if !force, let last = lastCheck, Date().timeIntervalSince(last) < Self.checkInterval { return .skippedRecentlyChecked }

        var request = URLRequest(url: manifestURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        if let etag = defaults.string(forKey: Keys.etag) { request.setValue(etag, forHTTPHeaderField: "If-None-Match") }
        do {
            let (data, response) = try await session.data(for: request)
            defaults.set(Date(), forKey: Keys.lastCheck)
            guard let http = response as? HTTPURLResponse else { return .failed("Phản hồi không phải HTTP") }
            if http.statusCode == 304 { return .upToDate }
            guard http.statusCode == 200 else { return .failed("HTTP \(http.statusCode)") }
            if let etag = http.value(forHTTPHeaderField: "ETag") { defaults.set(etag, forKey: Keys.etag) }
            let manifest = try JSONDecoder().decode(RuleManifest.self, from: data)
            store.applyDisabledRules(manifest.disabledRules ?? [])

            guard manifest.latest > store.snapshot.version else { return .upToDate }
            if let required = OSVersion(manifest.minApp), required > AppVersion.current { return .appTooOld(required: manifest.minApp) }
            guard let bundleURL = URL(string: manifest.url, relativeTo: manifestURL) else { return .failed("URL bundle sai") }
            let (bundleData, bundleResponse) = try await session.data(from: bundleURL)
            guard (bundleResponse as? HTTPURLResponse)?.statusCode == 200 else { return .failed("Không tải được bundle") }
            let snap = try store.install(bundleData, expectedSHA256: manifest.sha256, disabledRules: manifest.disabledRules ?? [])
            return .installed(version: snap.version)
        } catch {
            // Chữ ký sai hoặc lỗi mạng: giữ bộ cũ, ghi log.
            Log.error(.rules, "rules", "Cập nhật rule thất bại: \(error)")
            return .failed(String(describing: error))
        }
    }
}
