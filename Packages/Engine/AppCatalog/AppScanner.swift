import AppKit
import FileSystemKit
import Foundation
import os
import RuleEngine
import ScanEngine
import SweepCore
import SweepLogging
import SweepStorage

/// Tìm app đã cài (mục 11.3):
/// 1. Quét `/Applications`, `~/Applications`, `/Applications/Utilities` (độ sâu 2).
/// 2. Bổ sung từ Spotlight: `kMDItemContentType == "com.apple.application-bundle"`.
/// 3. Đọc bundle ID, tên, phiên bản, Team ID, dung lượng, lần mở cuối, nguồn cài.
/// Cache trong `app_usage_cache`; chỉ đọc lại app có `modificationDate` thay đổi (mục 16.3).
public final class AppScanner: Sendable {
    public let fileSystem: FileSystemService
    public let storage: Storage?
    public let searchRoots: [URL]
    public let useSpotlight: Bool
    private let knowledge: @Sendable () -> Knowledge

    public init(fileSystem: FileSystemService, storage: Storage?, searchRoots: [URL]? = nil, useSpotlight: Bool = true,
                knowledge: @escaping @Sendable () -> Knowledge = { Knowledge() }) {
        self.fileSystem = fileSystem
        self.storage = storage
        self.searchRoots = searchRoots ?? [
            URL(fileURLWithPath: "/Applications"),
            fileSystem.home.appendingPathComponent("Applications"),
            URL(fileURLWithPath: "/Applications/Utilities"),
        ]
        self.useSpotlight = useSpotlight
        self.knowledge = knowledge
    }

    public func scan(progress: ProgressReporter? = nil) async -> [InstalledApp] {
        var bundles = Set<String>()
        for root in searchRoots {
            for path in Self.findAppBundles(in: root, maxDepth: 2) { bundles.insert(path) }
        }
        progress?.report(0.15)
        if useSpotlight {
            let predicate = NSPredicate(format: "kMDItemContentType == 'com.apple.application-bundle'")
            let items = await SpotlightQuery.run(predicate: predicate, scopes: [NSMetadataQueryLocalComputerScope], timeout: 8)
            for item in items {
                let p = item.url.path
                // Bỏ app hệ thống, app nằm bên trong app khác, app trong Thùng rác, bản sao lưu Time Machine
                if p.hasPrefix("/System/") || p.contains(".app/") || p.contains("/.Trash/") || p.hasPrefix("/Volumes/") && p.contains("Backups.backupdb") { continue }
                if p.hasPrefix("/Library/Developer/") || p.contains("/Xcode.app/") || p.contains("/DerivedData/") { continue }
                bundles.insert(p)
            }
        }
        progress?.report(0.3)

        let cache = (try? storage?.cachedApps()) ?? [:]
        let sorted = bundles.sorted()
        var apps: [InstalledApp] = []
        var updated: [AppUsageCache] = []
        let total = max(sorted.count, 1)
        for (i, path) in sorted.enumerated() {
            if Task.isCancelled { break }
            let url = URL(fileURLWithPath: path)
            let modified = fileSystem.entry(at: url)?.modificationDate
            if let cached = cache[path], let modified, let cachedMod = cached.bundleModifiedAt, abs(cachedMod.timeIntervalSince(modified)) < 1,
               let app = makeApp(from: cached, url: url) {
                apps.append(app)
            } else if let app = readApp(at: url, modified: modified) {
                apps.append(app)
                updated.append(AppUsageCache(bundleId: app.bundleID, path: path, teamId: app.teamID, version: app.version, size: app.size.bytes,
                                             lastUsedAt: app.lastUsed, bundleModifiedAt: modified, name: app.name, isAppStore: app.isAppStore))
            }
            if i % 10 == 0 { progress?.report(0.3 + 0.7 * Double(i) / Double(total)) }
        }
        if let storage {
            try? storage.upsertApps(updated)
            try? storage.removeApps(notIn: Set(sorted))
        }
        // Một bundle ID có thể có nhiều bản (vd bản beta); giữ tất cả, sắp theo tên.
        return apps.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// Tìm `.app` trong một thư mục, độ sâu tối đa, không đi vào bên trong `.app`.
    public static func findAppBundles(in root: URL, maxDepth: Int) -> [String] {
        var result: [String] = []
        var queue: [(String, Int)] = [(root.path, 0)]
        while let (dir, depth) = queue.popLast() {
            try? BulkReader.readDirectory(dir) { e in
                guard e.isDirectory, !e.isSymlink else { return }
                let full = dir + "/" + e.name
                if e.name.hasSuffix(".app") {
                    result.append(full)
                } else if depth + 1 < maxDepth, !PackageExtensions.isPackage(name: e.name) {
                    queue.append((full, depth + 1))
                }
            }
        }
        return result
    }

    private func makeApp(from cached: AppUsageCache, url: URL) -> InstalledApp? {
        let lastUsed = SpotlightQuery.lastUsedDate(of: url) ?? cached.lastUsedAt
        return InstalledApp(bundleID: cached.bundleId, name: cached.name ?? url.deletingPathExtension().lastPathComponent, version: cached.version,
                            url: url, teamID: cached.teamId, size: ByteCount(cached.size ?? 0), lastUsed: lastUsed,
                            isAppStore: cached.isAppStore ?? false, isApple: knowledge().isApple(cached.bundleId),
                            executableName: nil, bundleModified: cached.bundleModifiedAt, bundledUninstaller: Self.findBundledUninstaller(for: url))
    }

    /// Đọc thông tin một app từ Info.plist, chữ ký, Spotlight và đo dung lượng.
    public func readApp(at url: URL, modified: Date? = nil) -> InstalledApp? {
        let infoURL = url.appendingPathComponent("Contents/Info.plist")
        guard let data = try? Data(contentsOf: infoURL),
              let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let bundleID = info["CFBundleIdentifier"] as? String, !bundleID.isEmpty else { return nil }
        let name = (info["CFBundleDisplayName"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            ?? (info["CFBundleName"] as? String).flatMap { $0.isEmpty ? nil : $0 }
            ?? url.deletingPathExtension().lastPathComponent
        let version = info["CFBundleShortVersionString"] as? String ?? info["CFBundleVersion"] as? String
        let size = (try? fileSystem.measure(url))?.allocatedSize ?? 0
        let isAppStore = fileSystem.exists(url.appendingPathComponent("Contents/_MASReceipt"))
        return InstalledApp(
            bundleID: bundleID, name: name, version: version, url: url, teamID: CodeSignature.teamIdentifier(of: url),
            size: ByteCount(size), lastUsed: SpotlightQuery.lastUsedDate(of: url), isAppStore: isAppStore,
            isApple: knowledge().isApple(bundleID), executableName: info["CFBundleExecutable"] as? String,
            bundleModified: modified ?? fileSystem.entry(at: url)?.modificationDate, bundledUninstaller: Self.findBundledUninstaller(for: url)
        )
    }

    /// Tìm trình gỡ riêng: `Uninstall <App>.app` bên trong bundle hoặc cạnh app trong cùng thư mục.
    public static func findBundledUninstaller(for app: URL) -> URL? {
        let fm = FileManager.default
        let name = app.deletingPathExtension().lastPathComponent
        let candidates = [
            app.deletingLastPathComponent().appendingPathComponent("Uninstall \(name).app"),
            app.deletingLastPathComponent().appendingPathComponent("\(name) Uninstaller.app"),
            app.appendingPathComponent("Contents/Resources/Uninstall \(name).app"),
            app.appendingPathComponent("Contents/Resources/Uninstaller.app"),
        ]
        return candidates.first { fm.fileExists(atPath: $0.path) }
    }
}

extension ArtifactKey {
    /// `[InstalledApp]` do task `installedApps` tạo ra.
    public static let installedApps: ArtifactKey = "installedApps"
}

extension ScanTaskID {
    public static let installedApps: ScanTaskID = "installedApps"
}

/// Task chạy đầu tiên vì nhiều task cần biết app nào đang được cài (mục 5.5).
public struct InstalledAppsTask: ScanTask {
    public let id: ScanTaskID = .installedApps
    public let priority: ScanPriority = .high
    public let estimatedWeight: Double = 2
    public let title = "Tìm ứng dụng đã cài"
    let scanner: AppScanner

    public init(scanner: AppScanner) { self.scanner = scanner }

    public func run(context: ScanContext) async throws -> ScanOutput {
        let apps = await scanner.scan(progress: context.progress)
        try Task.checkCancellation()
        return ScanOutput(artifacts: [.installedApps: apps])
    }
}

extension InstalledApp {
    public var ruleInfo: RuleAppInfo { RuleAppInfo(bundleID: bundleID, teamID: teamID, appName: name, url: url) }
}

extension ScanContext {
    /// Danh sách app đã cài từ task `installedApps`.
    public var installedApps: [InstalledApp] { upstreamArtifact(.installedApps, as: [InstalledApp].self) ?? [] }
}

/// App đang chạy (để áp `conditions.appNotRunning`, mục 8.3).
public enum RunningApps {
    @MainActor
    public static var bundleIDs: Set<String> {
        Set(NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier))
    }

    public static func isRunning(_ bundleID: String) -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
    }
}
