import AppCatalog
import Foundation
import NodeTree
import SweepCore
import SweepLogging

/// App sở hữu một LaunchAgent/Daemon.
public struct LaunchItemOwner: Sendable, Hashable {
    public var name: String
    public var bundleID: String?
    public var appURL: URL?
    /// App còn trên máy không (bundle tồn tại).
    public var isInstalled: Bool

    public init(name: String, bundleID: String?, appURL: URL?, isInstalled: Bool) {
        self.name = name
        self.bundleID = bundleID
        self.appURL = appURL
        self.isInstalled = isInstalled
    }
}

public enum LaunchItemState: Sendable, Hashable {
    case running(pid: Int32)
    /// Đã nạp vào launchd nhưng chưa chạy (chờ lịch, chờ sự kiện).
    case loaded
    /// Không nạp (đã tắt bằng `bootout` hoặc bị vô hiệu hoá).
    case notLoaded
    /// Không đọc được trạng thái.
    case unknown

    public var isActive: Bool {
        switch self {
        case .running, .loaded: true
        default: false
        }
    }
}

/// Một LaunchAgent/Daemon (mục 11.6).
public struct LaunchItem: Sendable, Hashable, Identifiable {
    public var id: String { plistPath }
    public let plistPath: String
    public let domain: VirtualItem.LaunchDomain
    public let job: LaunchdJob
    public let plistSize: ByteCount
    /// `nil` khi không xác định được (đường dẫn tương đối, `BundleProgram`).
    public var executableExists: Bool?
    public var owner: LaunchItemOwner?
    public var state: LaunchItemState
    /// Bị vô hiệu hoá qua `launchctl disable` hoặc khoá `Disabled` trong plist.
    public var isDisabled: Bool

    public init(plistPath: String, domain: VirtualItem.LaunchDomain, job: LaunchdJob, plistSize: ByteCount, executableExists: Bool?,
                owner: LaunchItemOwner?, state: LaunchItemState, isDisabled: Bool) {
        self.plistPath = plistPath
        self.domain = domain
        self.job = job
        self.plistSize = plistSize
        self.executableExists = executableExists
        self.owner = owner
        self.state = state
        self.isDisabled = isDisabled
    }

    public var label: String { job.label }
    public var plistURL: URL { URL(fileURLWithPath: plistPath) }
    /// Nhãn **hỏng**: binary không còn tồn tại.
    public var isBroken: Bool { executableExists == false }
    public var isApple: Bool { label.hasPrefix("com.apple.") }
    public var displayName: String { owner?.name ?? label }
    public var virtualItem: VirtualItem { .launchItem(label: label, plistPath: plistPath, domain: domain) }

    /// App sở hữu được khai báo (`AssociatedBundleIdentifiers`) nhưng không còn app nào trong số đó trên máy.
    public func belongsToRemovedApp(installedBundleIDs: Set<String>) -> Bool {
        if let owner, !owner.isInstalled { return true }
        let declared = job.associatedBundleIDs
        guard owner == nil, !declared.isEmpty else { return false }
        return !declared.contains { installedBundleIDs.contains($0) }
    }
}

extension VirtualItem.LaunchDomain {
    public var title: String {
        switch self {
        case .userAgent: String(localized: "Agent người dùng")
        case .globalAgent: String(localized: "Agent hệ thống")
        case .globalDaemon: "Daemon"
        }
    }

    public var directory: String {
        switch self {
        case .userAgent: "~/Library/LaunchAgents"
        case .globalAgent: "/Library/LaunchAgents"
        case .globalDaemon: "/Library/LaunchDaemons"
        }
    }
}

/// Đọc `~/Library/LaunchAgents`, `/Library/LaunchAgents`, `/Library/LaunchDaemons` và trạng thái qua `launchctl` (mục 11.6).
public struct LaunchItemScanner: Sendable {
    public static let launchctl = "/bin/launchctl"
    public let home: URL
    /// Tiền tố cho đường dẫn hệ thống, để test trên fixture. Rỗng khi chạy thật.
    public let systemRoot: String
    public let queryLaunchctl: Bool

    public init(home: URL = .userHome, systemRoot: String = "", queryLaunchctl: Bool = true) {
        self.home = home
        self.systemRoot = systemRoot
        self.queryLaunchctl = queryLaunchctl
    }

    public var directories: [(URL, VirtualItem.LaunchDomain)] {
        [
            (home.appendingPathComponent("Library/LaunchAgents"), .userAgent),
            (URL(fileURLWithPath: systemRoot + "/Library/LaunchAgents"), .globalAgent),
            (URL(fileURLWithPath: systemRoot + "/Library/LaunchDaemons"), .globalDaemon),
        ]
    }

    public func scan(installedApps: [InstalledApp] = []) async -> [LaunchItem] {
        let fm = FileManager.default
        var items: [LaunchItem] = []
        for (dir, domain) in directories {
            guard let names = try? fm.contentsOfDirectory(atPath: dir.path) else { continue }
            for name in names.sorted() where name.hasSuffix(".plist") {
                let url = dir.appendingPathComponent(name)
                guard let data = try? Data(contentsOf: url), let job = LaunchdJob.parse(data) else { continue }
                let size = (try? fm.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? Int64(data.count)
                items.append(LaunchItem(plistPath: url.path, domain: domain, job: job, plistSize: ByteCount(size),
                                        executableExists: executableExists(job), owner: Self.resolveOwner(job: job, installedApps: installedApps),
                                        state: .unknown, isDisabled: job.disabled))
            }
        }
        guard queryLaunchctl else { return items }
        return await withStates(items)
    }

    func executableExists(_ job: LaunchdJob) -> Bool? {
        guard let exe = job.executablePath else { return job.bundleProgram == nil ? false : nil }
        guard exe.hasPrefix("/") else { return nil }
        return FileManager.default.fileExists(atPath: systemRoot + exe)
    }

    /// App sở hữu: binary nằm trong `.app` → bundle đó; nếu không thì `AssociatedBundleIdentifiers` hoặc bundle ID trong label.
    public static func resolveOwner(job: LaunchdJob, installedApps: [InstalledApp]) -> LaunchItemOwner? {
        if let exe = job.executablePath, let appPath = appBundle(containing: exe) {
            let appURL = URL(fileURLWithPath: appPath)
            if let app = installedApps.first(where: { $0.url.standardizedFileURL.path == appURL.standardizedFileURL.path }) {
                return LaunchItemOwner(name: app.name, bundleID: app.bundleID, appURL: app.url, isInstalled: true)
            }
            if let bundle = Bundle(url: appURL), FileManager.default.fileExists(atPath: appPath) {
                let name = bundle.object(forInfoDictionaryKey: "CFBundleDisplayName") as? String
                    ?? bundle.object(forInfoDictionaryKey: "CFBundleName") as? String ?? appURL.deletingPathExtension().lastPathComponent
                return LaunchItemOwner(name: name, bundleID: bundle.bundleIdentifier, appURL: appURL, isInstalled: true)
            }
            return LaunchItemOwner(name: appURL.deletingPathExtension().lastPathComponent, bundleID: nil, appURL: appURL, isInstalled: false)
        }
        for id in job.associatedBundleIDs {
            if let app = installedApps.first(where: { $0.bundleID == id }) {
                return LaunchItemOwner(name: app.name, bundleID: app.bundleID, appURL: app.url, isInstalled: true)
            }
        }
        // Bundle ID trong label: chọn app có bundle ID dài nhất là tiền tố của label.
        let label = job.label.lowercased()
        let match = installedApps
            .filter { let id = $0.bundleID.lowercased(); return label == id || label.hasPrefix(id + ".") }
            .max { $0.bundleID.count < $1.bundleID.count }
        if let match { return LaunchItemOwner(name: match.name, bundleID: match.bundleID, appURL: match.url, isInstalled: true) }
        return nil
    }

    /// `/Applications/Foo.app/Contents/MacOS/foo` → `/Applications/Foo.app`.
    public static func appBundle(containing path: String) -> String? {
        if let r = path.range(of: ".app/") { return String(path[..<path.index(before: r.upperBound)]) }
        return path.hasSuffix(".app") ? path : nil
    }

    // MARK: Trạng thái

    private func withStates(_ items: [LaunchItem]) async -> [LaunchItem] {
        let runner = ProcessRunner()
        let uid = getuid()
        var userList: [String: LaunchctlParser.ListEntry]?
        if let out = try? await runner.run(Self.launchctl, ["list"], timeout: 10), out.succeeded {
            userList = LaunchctlParser.parseList(out.stdoutString)
        }
        var disabled: [String: Bool] = [:]
        for domain in ["gui/\(uid)", "system"] {
            if let out = try? await runner.run(Self.launchctl, ["print-disabled", domain], timeout: 10), out.succeeded {
                disabled.merge(LaunchctlParser.parseDisabled(out.stdoutString)) { a, _ in a }
            }
        }

        var result = items
        for i in result.indices {
            let label = result[i].label
            if disabled[label] == true { result[i].isDisabled = true } else if disabled[label] == false { result[i].isDisabled = false }
            switch result[i].domain {
            case .userAgent, .globalAgent:
                guard let userList else { continue }
                if let entry = userList[label] {
                    result[i].state = entry.pid.map { .running(pid: $0) } ?? .loaded
                } else {
                    result[i].state = .notLoaded
                }
            case .globalDaemon:
                // Đọc trạng thái daemon không cần root.
                guard let out = try? await runner.run(Self.launchctl, ["print", "system/\(label)"], timeout: 5) else { continue }
                if out.succeeded {
                    let parsed = LaunchctlParser.parsePrint(out.stdoutString)
                    result[i].state = parsed.pid.map { .running(pid: $0) } ?? (parsed.state == "running" ? .running(pid: 0) : .loaded)
                } else {
                    result[i].state = out.timedOut ? .unknown : .notLoaded
                }
            }
        }
        return result
    }
}
