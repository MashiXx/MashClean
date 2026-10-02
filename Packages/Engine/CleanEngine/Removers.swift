import AppKit
import CleanEngineCore
import FileSystemKit
import Foundation
import NodeTree
import os
import SweepCore
import SweepIPC
import SweepLogging

/// Ngữ cảnh truyền cho remover.
public struct RemoveContext: Sendable {
    public let fileSystem: FileSystemService
    public let helper: HelperClient?
    public let policy: PathPolicy
    public let dryRun: Bool
    /// Báo dung lượng giải phóng tăng dần của mục đang xử lý.
    public let progress: @Sendable (_ path: String, _ freedDelta: Int64) -> Void
    /// Thùng rác nằm trên ổ khác hoặc ổ mạng: hỏi người dùng có muốn xoá vĩnh viễn không (mục 7.4).
    public let confirmPermanentDelete: @Sendable (URL) async -> Bool

    public init(fileSystem: FileSystemService, helper: HelperClient?, policy: PathPolicy, dryRun: Bool,
                progress: @escaping @Sendable (String, Int64) -> Void = { _, _ in },
                confirmPermanentDelete: @escaping @Sendable (URL) async -> Bool = { _ in false }) {
        self.fileSystem = fileSystem
        self.helper = helper
        self.policy = policy
        self.dryRun = dryRun
        self.progress = progress
        self.confirmPermanentDelete = confirmPermanentDelete
    }
}

/// Interface `Remover` chung + remover chuyên biệt (mục 7.3).
public protocol Remover: Sendable {
    var id: RemoverID { get }
    func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult]
}

// MARK: - FileRemover

/// File và thư mục thông thường: `FileManager.trashItem` hoặc xoá an toàn (từ lá lên gốc để báo tiến độ).
public struct FileRemover: Remover {
    public let id: RemoverID = .file
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            if Task.isCancelled { break }
            guard let url = item.url else {
                results.append(.failed("", errno: EINVAL, String(localized: "Mục không có đường dẫn")))
                continue
            }
            results.append(await removeOne(item, url: url, context: context))
        }
        return results
    }

    func removeOne(_ item: CleanPlan.Item, url: URL, context: RemoveContext) async -> RemoveResult {
        let path = url.path
        guard context.fileSystem.exists(url) else { return .skipped(path, .notFound) }
        // File đang được mở: bỏ qua (mục 15.1 bước 4). Chỉ kiểm tra file thường để không tốn thời gian.
        if !context.fileSystem.isDirectory(url), OpenFileCheck.isOpen(path) { return .skipped(path, .inUse) }
        if context.dryRun { return RemoveResult(path: path, outcome: .skipped(reason: .dryRun), freedBytes: item.expectedSize.bytes) }

        switch item.strategy {
        case .moveToTrash:
            do {
                _ = try context.policy.check(url, allowedRoots: item.allowedRoots.isEmpty ? nil : item.allowedRoots)
            } catch {
                return .skipped(path, .blockedByPolicy)
            }
            var resulting: NSURL?
            do {
                try FileManager.default.trashItem(at: url, resultingItemURL: &resulting)
                context.progress(path, item.expectedSize.bytes)
                return .ok(path, freed: item.expectedSize.bytes, trashedPath: resulting?.path)
            } catch let error as NSError {
                if Self.isTrashUnavailable(error) {
                    guard await context.confirmPermanentDelete(url) else { return .skipped(path, .userDeclined) }
                    return secureDelete(item, path: path, contentsOnly: false, context: context)
                }
                let code = (error.userInfo[NSUnderlyingErrorKey] as? NSError)?.code ?? error.code
                return .failed(path, errno: Int32(truncatingIfNeeded: code == NSFileWriteNoPermissionError ? Int(EACCES) : code), error.localizedDescription)
            }
        case .delete:
            return secureDelete(item, path: path, contentsOnly: false, context: context)
        case .deleteContents:
            return secureDelete(item, path: path, contentsOnly: true, context: context)
        case .custom:
            return .skipped(path, .unsupported)
        }
    }

    private func secureDelete(_ item: CleanPlan.Item, path: String, contentsOnly: Bool, context: RemoveContext) -> RemoveResult {
        var last: Int64 = 0
        return SecureRemover.remove(
            path: path, policy: context.policy, contentsOnly: contentsOnly,
            allowedRoots: item.allowedRoots.isEmpty ? nil : item.allowedRoots,
            shouldContinue: { !Task.isCancelled },
            progress: { freed in
                context.progress(path, freed - last)
                last = freed
            }
        )
    }

    static func isTrashUnavailable(_ error: NSError) -> Bool {
        error.domain == NSCocoaErrorDomain && (error.code == NSFeatureUnsupportedError || error.code == NSFileWriteVolumeReadOnlyError)
    }
}

// MARK: - PrivilegedFileRemover

/// `/Library/Caches`, `/private/var/log`, file của root: gửi sang helper theo batch (mục 7.3).
public struct PrivilegedFileRemover: Remover {
    public let id: RemoverID = .privilegedFile
    public static let batchSize = 200
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        guard let helper = context.helper else {
            return items.map { .failed($0.url?.path ?? "", errno: EPERM, HelperError.notInstalled.description) }
        }
        if context.dryRun {
            return items.map { RemoveResult(path: $0.url?.path ?? "", outcome: .skipped(reason: .dryRun), freedBytes: $0.expectedSize.bytes) }
        }
        var results: [RemoveResult] = []
        let groups = Dictionary(grouping: items) { $0.strategy == .deleteContents ? RemoveMode.deleteContents : RemoveMode.delete }
        for (mode, group) in groups {
            for chunk in stride(from: 0, to: group.count, by: Self.batchSize).map({ Array(group[$0..<min($0 + Self.batchSize, group.count)]) }) {
                if Task.isCancelled { break }
                let paths = chunk.compactMap { $0.url?.path }
                do {
                    let r = try await helper.removeItems(paths, mode: mode)
                    for x in r { context.progress(x.path, x.freedBytes) }
                    results += r
                } catch {
                    results += paths.map { .failed($0, errno: EIO, String(describing: error)) }
                }
            }
        }
        return results
    }
}

// MARK: - Simulator

/// Thiết bị simulator Xcode: `xcrun simctl delete <udid>` (mục 7.3).
public struct SimulatorRemover: Remover {
    public let id: RemoverID = .simulator
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard case let .simulatorDevice(udid, _, _, dataPath)? = item.virtual else {
                results.append(.skipped(item.url?.path ?? "", .unsupported))
                continue
            }
            let label = dataPath ?? udid
            if context.dryRun {
                results.append(RemoveResult(path: label, outcome: .skipped(reason: .dryRun), freedBytes: item.expectedSize.bytes))
                continue
            }
            do {
                let out = try await ProcessRunner().run("/usr/bin/xcrun", ["simctl", "delete", udid], timeout: 120)
                if out.succeeded {
                    context.progress(label, item.expectedSize.bytes)
                    results.append(.ok(label, freed: item.expectedSize.bytes))
                } else {
                    results.append(.failed(label, errno: out.status, out.stderrString.trimmingCharacters(in: .whitespacesAndNewlines)))
                }
            } catch {
                results.append(.failed(label, errno: ENOENT, String(describing: error)))
            }
        }
        return results
    }
}

/// Runtime simulator đã tải: `xcrun simctl runtime delete <id>` (mục 7.3).
public struct SimulatorRuntimeRemover: Remover {
    public let id: RemoverID = .simulatorRuntime
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard case let .simulatorRuntime(identifier, _, path)? = item.virtual else {
                results.append(.skipped(item.url?.path ?? "", .unsupported))
                continue
            }
            let label = path ?? identifier
            if context.dryRun {
                results.append(RemoveResult(path: label, outcome: .skipped(reason: .dryRun), freedBytes: item.expectedSize.bytes))
                continue
            }
            do {
                let out = try await ProcessRunner().run("/usr/bin/xcrun", ["simctl", "runtime", "delete", identifier], timeout: 300)
                if out.succeeded {
                    context.progress(label, item.expectedSize.bytes)
                    results.append(.ok(label, freed: item.expectedSize.bytes))
                } else {
                    results.append(.failed(label, errno: out.status, out.stderrString.trimmingCharacters(in: .whitespacesAndNewlines)))
                }
            } catch {
                results.append(.failed(label, errno: ENOENT, String(describing: error)))
            }
        }
        return results
    }
}

// MARK: - Launch items

/// LaunchAgent/Daemon hỏng hoặc của app đã gỡ: `launchctl bootout` rồi xoá plist (Daemon thì qua helper) (mục 7.3).
public struct LaunchItemRemover: Remover {
    public let id: RemoverID = .launchItem
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard case let .launchItem(label, plistPath, domain)? = item.virtual else {
                results.append(.skipped(item.url?.path ?? "", .unsupported))
                continue
            }
            if context.dryRun {
                results.append(RemoveResult(path: plistPath, outcome: .skipped(reason: .dryRun)))
                continue
            }
            switch domain {
            case .userAgent:
                _ = try? await ProcessRunner().run("/bin/launchctl", ["bootout", "gui/\(getuid())/\(label)"], timeout: 20)
                var resulting: NSURL?
                do {
                    _ = try context.policy.check(URL(fileURLWithPath: plistPath))
                    try FileManager.default.trashItem(at: URL(fileURLWithPath: plistPath), resultingItemURL: &resulting)
                    results.append(.ok(plistPath, freed: item.expectedSize.bytes, trashedPath: resulting?.path))
                } catch let v as PathPolicy.Violation {
                    _ = v
                    results.append(.skipped(plistPath, .blockedByPolicy))
                } catch {
                    results.append(.failed(plistPath, errno: EIO, error.localizedDescription))
                }
            case .globalAgent, .globalDaemon:
                guard let helper = context.helper else {
                    results.append(.failed(plistPath, errno: EPERM, HelperError.notInstalled.description))
                    continue
                }
                do {
                    let r = try await helper.bootoutLaunchDaemon(label: label, plistPath: plistPath)
                    results.append(r.success ? .ok(plistPath, freed: item.expectedSize.bytes) : .failed(plistPath, errno: EIO, r.message))
                } catch {
                    results.append(.failed(plistPath, errno: EIO, String(describing: error)))
                }
            }
        }
        return results
    }
}

// MARK: - Local snapshots

/// Snapshot Time Machine cục bộ: helper chạy `tmutil deletelocalsnapshots <date>` (mục 7.3).
public struct LocalSnapshotRemover: Remover {
    public let id: RemoverID = .localSnapshot
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard case let .localSnapshot(volume, date)? = item.virtual else { continue }
            let label = "\(volume)@\(date)"
            if context.dryRun { results.append(.skipped(label, .dryRun)); continue }
            guard let helper = context.helper else {
                results.append(.failed(label, errno: EPERM, HelperError.notInstalled.description))
                continue
            }
            do {
                let r = try await helper.deleteLocalSnapshot(date: date)
                results.append(r.success ? .ok(label, freed: item.expectedSize.bytes) : .failed(label, errno: EIO, r.message))
            } catch {
                results.append(.failed(label, errno: EIO, String(describing: error)))
            }
        }
        return results
    }
}

// MARK: - App

/// Gỡ app: app phải đã tắt (UI hỏi buộc tắt trước), chuyển `.app` vào Thùng rác (mục 7.3, 11.3).
public struct AppRemover: Remover {
    public let id: RemoverID = .app
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard let url = item.url else { continue }
            let path = url.path
            if let bundleID = Bundle(url: url)?.bundleIdentifier,
               NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).contains(where: { $0.bundleURL?.standardizedFileURL == url.standardizedFileURL }) {
                results.append(.skipped(path, .appRunning))
                continue
            }
            if context.dryRun {
                results.append(RemoveResult(path: path, outcome: .skipped(reason: .dryRun), freedBytes: item.expectedSize.bytes))
                continue
            }
            var resulting: NSURL?
            do {
                _ = try context.policy.check(url, checkOwnership: false)
                try FileManager.default.trashItem(at: url, resultingItemURL: &resulting)
                context.progress(path, item.expectedSize.bytes)
                results.append(.ok(path, freed: item.expectedSize.bytes, trashedPath: resulting?.path))
            } catch is PathPolicy.Violation {
                results.append(.skipped(path, .blockedByPolicy))
            } catch {
                // App của root (cài bằng pkg): Thùng rác không dùng được, xoá qua helper sau khi người dùng đồng ý.
                if let helper = context.helper, await context.confirmPermanentDelete(url) {
                    let r = (try? await helper.removeItems([path], mode: .delete))?.first ?? .failed(path, errno: EIO)
                    results.append(r)
                } else {
                    results.append(.failed(path, errno: EACCES, error.localizedDescription))
                }
            }
        }
        return results
    }
}

// MARK: - Docker

/// Docker: không xoá file `Docker.raw` trực tiếp; chạy `docker system prune -f` khi người dùng chủ động chọn (mục 8.4).
public struct DockerRemover: Remover {
    public let id: RemoverID = .docker
    public init() {}

    public static let candidates = [
        "/usr/local/bin/docker", "/opt/homebrew/bin/docker",
        "/Applications/Docker.app/Contents/Resources/bin/docker", "\(AppEdition.userHomePath)/.docker/bin/docker",
        "\(AppEdition.userHomePath)/.orbstack/bin/docker",
    ]

    public static var dockerPath: String? { candidates.first { ProcessRunner.exists($0) } }

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        guard let docker = Self.dockerPath else { return items.map { _ in .failed("docker", errno: ENOENT, String(localized: "Không tìm thấy docker CLI")) } }
        if context.dryRun { return items.map { _ in .skipped("docker system prune", .dryRun) } }
        do {
            let out = try await ProcessRunner().run(docker, ["system", "prune", "-f"], timeout: 600)
            let freed = Self.parseReclaimed(out.stdoutString)
            return [out.succeeded ? .ok("docker system prune", freed: freed) : .failed("docker system prune", errno: out.status, out.stderrString)]
        } catch {
            return [.failed("docker system prune", errno: EIO, String(describing: error))]
        }
    }

    /// Đọc dòng "Total reclaimed space: 1.2GB".
    static func parseReclaimed(_ output: String) -> Int64 {
        guard let line = output.split(separator: "\n").first(where: { $0.contains("reclaimed space") }),
              let value = line.split(separator: ":").last?.trimmingCharacters(in: .whitespaces) else { return 0 }
        let units: [(String, Double)] = [("TB", 1e12), ("GB", 1e9), ("MB", 1e6), ("kB", 1e3), ("KB", 1e3), ("B", 1)]
        for (unit, mult) in units where value.hasSuffix(unit) {
            return Int64((Double(value.dropLast(unit.count)) ?? 0) * mult)
        }
        return 0
    }
}
