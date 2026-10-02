import AppCatalog
import FileSystemKit
import Foundation
import NodeTree
import RuleEngine
import ScanEngine
import SweepCore

// MARK: - Xcode simulator

struct SimDevice: Decodable, Sendable {
    let udid: String
    let name: String
    let isAvailable: Bool
    let dataPath: String?
    let availabilityError: String?
}

struct SimList: Decodable { let devices: [String: [SimDevice]] }

struct SimRuntime: Decodable, Sendable {
    let identifier: String
    let version: String?
    let platformIdentifier: String?
    let path: String?
    let sizeBytes: Int64?
    let runtimeIdentifier: String?
    let deletable: Bool?
    let state: String?
}

/// Thiết bị simulator không dùng được nữa (`isAvailable == false`, mục 8.4). Chưa cài Xcode thì bỏ qua.
public struct UnavailableSimulatorsProvider: JunkProvider {
    public let name = "simctl.unavailableDevices"
    public init() {}

    public func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning]) {
        guard let list = try await Self.list() else { return ([], []) }
        var nodes: [Node] = []
        for (runtimeKey, devices) in list.devices {
            let runtime = Self.prettyRuntime(runtimeKey)
            for d in devices where !d.isAvailable {
                let size = d.dataPath.flatMap { try? context.fileSystem.measure(URL(fileURLWithPath: $0).deletingLastPathComponent()) }?.allocatedSize ?? 0
                nodes.append(rule.makeVirtualNode(.simulatorDevice(udid: d.udid, name: d.name, runtime: runtime, dataPath: d.dataPath.map { URL(fileURLWithPath: $0).deletingLastPathComponent().path }),
                                                  size: ByteCount(size)))
                context.progress.addBytesFound(ByteCount(size))
            }
        }
        return (nodes, [])
    }

    static func list() async throws -> SimList? {
        guard FileManager.default.fileExists(atPath: "/Applications/Xcode.app") || FileManager.default.fileExists(atPath: "/Library/Developer/CommandLineTools") else { return nil }
        guard let out = try? await ProcessRunner().run("/usr/bin/xcrun", ["simctl", "list", "devices", "-j"], timeout: 30), out.succeeded else { return nil }
        return try? JSONDecoder().decode(SimList.self, from: out.stdout)
    }

    static func prettyRuntime(_ key: String) -> String {
        // com.apple.CoreSimulator.SimRuntime.iOS-17-2 → iOS 17.2
        guard let last = key.split(separator: ".").last else { return key }
        let parts = last.split(separator: "-")
        guard let os = parts.first else { return key }
        return "\(os) " + parts.dropFirst().joined(separator: ".")
    }
}

/// Runtime simulator đã tải mà không thiết bị nào dùng (review).
public struct UnusedSimulatorRuntimesProvider: JunkProvider {
    public let name = "simctl.unusedRuntimes"
    public init() {}

    public func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning]) {
        guard let devices = try await UnavailableSimulatorsProvider.list() else { return ([], []) }
        guard let out = try? await ProcessRunner().run("/usr/bin/xcrun", ["simctl", "runtime", "list", "-j"], timeout: 30), out.succeeded,
              let runtimes = try? JSONDecoder().decode([String: SimRuntime].self, from: out.stdout) else { return ([], []) }
        let usedKeys = Set(devices.devices.filter { $0.value.contains(where: \.isAvailable) }.keys)
        var nodes: [Node] = []
        for (_, rt) in runtimes {
            guard rt.deletable ?? true, let rid = rt.runtimeIdentifier, !usedKeys.contains(rid) else { continue }
            let title = "\(UnavailableSimulatorsProvider.prettyRuntime(rid)) \(rt.version.map { "(\($0))" } ?? "")"
            nodes.append(rule.makeVirtualNode(.simulatorRuntime(identifier: rt.identifier, name: title, path: rt.path),
                                              title: title, size: ByteCount(rt.sizeBytes ?? 0)))
        }
        return (nodes, [])
    }
}

// MARK: - Docker

/// Docker: không xoá trực tiếp; đề xuất `docker system prune` (review, mục 8.4).
public struct DockerProvider: JunkProvider {
    public let name = "docker.prune"
    public init() {}

    public func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning]) {
        guard let docker = DockerRemoverPaths.docker else { return ([], []) }
        // `docker system df` cho biết dung lượng thu hồi được; daemon chưa chạy thì bỏ qua.
        guard let out = try? await ProcessRunner().run(docker, ["system", "df", "--format", "{{.Reclaimable}}"], timeout: 15), out.succeeded else { return ([], []) }
        let reclaimable = out.stdoutString.split(separator: "\n").reduce(Int64(0)) { $0 + Self.parseSize(String($1)) }
        guard reclaimable > 0 else { return ([], []) }
        return ([rule.makeVirtualNode(.dockerPrune, title: String(localized: "Docker: image, container, cache không dùng"), size: ByteCount(reclaimable))], [])
    }

    /// "1.2GB (40%)" → byte.
    static func parseSize(_ s: String) -> Int64 {
        let value = s.split(separator: " ").first.map(String.init) ?? ""
        let units: [(String, Double)] = [("TB", 1e12), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("KB", 1e3), ("B", 1)]
        for (u, m) in units where value.hasSuffix(u) { return Int64((Double(value.dropLast(u.count)) ?? 0) * m) }
        return 0
    }
}

enum DockerRemoverPaths {
    static var docker: String? {
        ["/usr/local/bin/docker", "/opt/homebrew/bin/docker", "/Applications/Docker.app/Contents/Resources/bin/docker",
         "\(NSHomeDirectory())/.docker/bin/docker", "\(NSHomeDirectory())/.orbstack/bin/docker"].first { ProcessRunner.exists($0) }
    }
}

// MARK: - Gói ngôn ngữ

/// `<App>.app/Contents/Resources/*.lproj` không thuộc ngôn ngữ đang dùng (risky, mặc định ẩn, mục 8.4).
/// Xoá `.lproj` bên trong app đã ký làm hỏng chữ ký code nên chỉ đề xuất với cảnh báo rõ.
public struct LanguageFilesProvider: JunkProvider {
    public let name = "languageFiles"
    public init() {}

    public func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning]) {
        var keep = Set(context.rules.knowledge.keepLanguages.map { $0.lowercased() })
        for lang in Locale.preferredLanguages {
            keep.insert(lang.lowercased())
            keep.insert(String(lang.prefix(2)).lowercased())
            keep.insert(lang.replacingOccurrences(of: "-", with: "_").lowercased())
        }
        keep.formUnion(["base", "en", "english"])
        var appNodes: [Node] = []
        for app in context.installedApps where !app.isApple && !app.isAppStore && !app.isSystemApp {
            try Task.checkCancellation()
            let resources = app.url.appendingPathComponent("Contents/Resources")
            var leaves: [Node] = []
            for entry in context.fileSystem.children(of: resources) where entry.isDirectory && entry.name.hasSuffix(".lproj") {
                let lang = String(entry.name.dropLast(6)).lowercased()
                let short = String(lang.prefix(2))
                if keep.contains(lang) || keep.contains(short) || keep.contains(lang.replacingOccurrences(of: "_", with: "-")) { continue }
                guard let m = try? context.fileSystem.measure(entry.url), m.allocatedSize > 0 else { continue }
                leaves.append(rule.makeFileNode(entry.url, measurement: m, title: Locale.current.localizedString(forIdentifier: lang) ?? entry.name,
                                                allowedRoot: resources.path, badges: [String(localized: "Làm hỏng chữ ký app")]))
            }
            guard !leaves.isEmpty else { continue }
            var n = Node(kind: .application(bundleID: app.bundleID, url: app.url), title: app.name, safety: .risky, category: rule.category, children: leaves)
            n.badges = [String(localized: "Làm hỏng chữ ký app")]
            n.recomputeAggregates()
            appNodes.append(n)
        }
        return (appNodes, [])
    }
}

// MARK: - iOS backup

/// Bản sao lưu thiết bị iOS: đọc `Info.plist` trong từng thư mục backup để lấy tên thiết bị và ngày (mục 11.2).
public struct IOSBackupsProvider: JunkProvider {
    public let name = "iosBackups"
    public init() {}

    public func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning]) {
        let root = URL(fileURLWithPath: context.rules.resolver.resolve("~/Library/Application Support/MobileSync/Backup"))
        if AccessProbe.isBlockedByTCC(root.path) {
            return ([], [ScanWarning(taskID: "iosBackups", kind: .needsFullDiskAccess, message: String(localized: "Cần Full Disk Access để đọc bản sao lưu iOS"))])
        }
        var nodes: [Node] = []
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        for entry in context.fileSystem.children(of: root) where entry.isDirectory {
            try Task.checkCancellation()
            let info = entry.url.appendingPathComponent("Info.plist")
            let plist = (try? Data(contentsOf: info)).flatMap { try? PropertyListSerialization.propertyList(from: $0, format: nil) as? [String: Any] }
            let device = plist?["Device Name"] as? String ?? plist?["Display Name"] as? String ?? entry.name
            let product = plist?["Product Type"] as? String
            let date = plist?["Last Backup Date"] as? Date ?? entry.modificationDate
            guard let m = try? context.fileSystem.measure(entry.url, tracker: context.environment.tracker, counter: context.progress.fileCounter) else { continue }
            var title = "\(device) — \(formatter.string(from: date))"
            if let product { title += " (\(product))" }
            nodes.append(rule.makeFileNode(entry.url, measurement: m, title: title, allowedRoot: root.path))
            context.progress.addBytesFound(ByteCount(m.allocatedSize))
        }
        return (nodes, [])
    }
}

// MARK: - Thùng rác

/// `~/.Trash` và thùng rác trên ổ ngoài `/Volumes/*/.Trashes/<uid>` (mục 11.2).
public struct TrashProvider: JunkProvider {
    public let name = "trash"
    public init() {}

    public func nodes(for rule: Rule, context: ScanContext) async throws -> (nodes: [Node], warnings: [ScanWarning]) {
        var bins: [(URL, String)] = [(URL(fileURLWithPath: context.rules.resolver.resolve("~/.Trash")), String(localized: "Thùng rác"))]
        let uid = getuid()
        for vol in VolumeInfo.mounted() where !vol.isRoot && !vol.isReadOnly {
            bins.append((URL(fileURLWithPath: vol.mountPoint).appendingPathComponent(".Trashes/\(uid)"), String(localized: "Thùng rác trên \(vol.name)")))
        }
        var nodes: [Node] = []
        var warnings: [ScanWarning] = []
        for (url, title) in bins {
            if AccessProbe.isBlockedByTCC(url.path) {
                warnings.append(ScanWarning(taskID: "trash", kind: .needsFullDiskAccess, message: String(localized: "Cần Full Disk Access để đọc \(title)")))
                continue
            }
            guard context.fileSystem.isDirectory(url),
                  let m = try? context.fileSystem.measure(url, counter: context.progress.fileCounter), m.itemCount > 0 else { continue }
            var n = rule.makeFileNode(url, measurement: m, title: title, allowedRoot: url.path)
            n.removal = .deleteContents
            nodes.append(n)
            context.progress.addBytesFound(ByteCount(m.allocatedSize))
        }
        return (nodes, warnings)
    }
}
