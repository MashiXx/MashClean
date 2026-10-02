import CleanEngineCore
import Darwin
import Foundation
import os
import SweepCore
import SweepIPC
import SweepLogging

/// Hộp bọc reply block của XPC để gọi lại từ hàng đợi khác (reply block của NSXPC an toàn đa luồng).
private final class Reply<T>: @unchecked Sendable {
    private let block: (T) -> Void
    init(_ block: @escaping (T) -> Void) { self.block = block }
    func callAsFunction(_ value: T) { block(value) }
}

/// Cài đặt `HelperProtocol` phía root (mục 9.2–9.4). Mỗi kết nối có một instance riêng.
/// Mọi tham số đều được kiểm tra lại ở đây; mọi lệnh root đều ghi log kèm PID client.
public final class HelperService: NSObject, HelperProtocol, @unchecked Sendable {
    public let peerPID: pid_t
    private let maintenanceBusy = Locked(false)
    private let workQueue = DispatchQueue(label: "com.cleanboost.mac.helper.work", qos: .userInitiated, attributes: .concurrent)

    public init(peerPID: pid_t) {
        self.peerPID = peerPID
    }

    private func audit(_ message: String) {
        Logger.helper.notice("[pid \(self.peerPID, privacy: .public)] \(message, privacy: .public)")
    }

    // MARK: HelperProtocol

    public func protocolVersion(reply: @escaping (Int) -> Void) {
        reply(MashCleanIdentifiers.helperProtocolVersion)
    }

    public func removeItems(_ paths: [String], mode: Int, reply: @escaping (Data) -> Void) {
        let reply = Reply(reply)
        workQueue.async { [self] in
            reply(VersionedPayload<[RemoveResult]>.encode(performRemove(paths, mode: mode)))
        }
    }

    func performRemove(_ paths: [String], mode rawMode: Int) -> [RemoveResult] {
        guard let mode = RemoveMode(rawValue: rawMode) else {
            audit("removeItems từ chối: mode không hợp lệ \(rawMode)")
            return paths.prefix(HelperValidation.maxPathsPerCall).map { .failed($0, errno: EINVAL, "Chế độ xoá không hợp lệ") }
        }
        guard paths.count <= HelperValidation.maxPathsPerCall else {
            audit("removeItems từ chối: \(paths.count) đường dẫn vượt giới hạn \(HelperValidation.maxPathsPerCall)")
            return paths.prefix(HelperValidation.maxPathsPerCall).map {
                .failed($0, errno: E2BIG, "Quá \(HelperValidation.maxPathsPerCall) đường dẫn mỗi lần gọi")
            }
        }
        audit("removeItems bắt đầu: \(paths.count) đường dẫn, mode=\(mode)")
        var results: [RemoveResult] = []
        results.reserveCapacity(paths.count)
        var freed: Int64 = 0
        var failures = 0
        for path in paths {
            // Helper tự kiểm tra PathPolicy.root cho từng đường dẫn, không tin app (mục 9.4.2).
            let r = SecureRemover.remove(path: path, policy: .root, contentsOnly: mode == .deleteContents)
            Logger.helper.debug("[pid \(self.peerPID, privacy: .public)] remove \(path, privacy: .private) → \(r.logValue, privacy: .public)")
            freed += r.freedBytes
            if !r.isSuccess { failures += 1 }
            results.append(r)
        }
        audit("removeItems xong: \(paths.count - failures) thành công, \(failures) lỗi/bỏ qua, giải phóng \(freed) byte")
        return results
    }

    public func runMaintenance(_ task: String, reply: @escaping (Data) -> Void) {
        let reply = Reply(reply)
        guard let name = MaintenanceTaskName(rawValue: task), name.requiresRoot, !name.commands.isEmpty else {
            audit("runMaintenance từ chối tác vụ không hợp lệ: \(task)")
            return reply(VersionedPayload<MaintenanceResult>.encode(
                MaintenanceResult(task: task, success: false, message: "Tác vụ không được hỗ trợ", durationSeconds: 0)))
        }
        // Mỗi kết nối tối đa 1 tác vụ bảo trì cùng lúc (mục 9.4.4).
        let acquired = maintenanceBusy.withLock { busy -> Bool in
            guard !busy else { return false }
            busy = true
            return true
        }
        guard acquired else {
            audit("runMaintenance từ chối \(task): đang chạy tác vụ khác")
            return reply(VersionedPayload<MaintenanceResult>.encode(
                MaintenanceResult(task: task, success: false, message: "Đang chạy một tác vụ bảo trì khác", durationSeconds: 0)))
        }
        workQueue.async { [self] in
            defer { maintenanceBusy.withLock { $0 = false } }
            reply(VersionedPayload<MaintenanceResult>.encode(performMaintenance(name)))
        }
    }

    private func performMaintenance(_ name: MaintenanceTaskName) -> MaintenanceResult {
        let start = Date()
        var messages: [String] = []
        var success = true
        for command in name.commands {
            let outcome = runCommand(command.tool, command.args, timeout: 1800)
            if !outcome.success {
                // `killall -HUP mDNSResponder` có thể báo lỗi khi tiến trình vừa khởi động lại: không coi là thất bại cả tác vụ.
                if !(name == .flushDNS && command.tool == "/usr/bin/killall") { success = false }
                messages.append(outcome.message)
            }
        }
        let duration = Date().timeIntervalSince(start)
        audit("runMaintenance \(name.rawValue) \(success ? "thành công" : "thất bại") sau \(String(format: "%.1f", duration)) giây")
        return MaintenanceResult(task: name.rawValue, success: success,
                                 message: success ? "Đã hoàn tất" : messages.joined(separator: "\n"),
                                 durationSeconds: duration)
    }

    public func bootoutLaunchDaemon(label: String, plistPath: String, reply: @escaping (Data) -> Void) {
        let reply = Reply(reply)
        workQueue.async { [self] in
            reply(VersionedPayload<HelperCommandResult>.encode(performBootout(label: label, plistPath: plistPath)))
        }
    }

    func performBootout(label: String, plistPath: String) -> HelperCommandResult {
        let validated: (path: String, directory: String)
        switch HelperValidation.validateLaunchPlist(label: label, plistPath: plistPath) {
        case let .success(v): validated = v
        case let .failure(error):
            audit("bootoutLaunchDaemon từ chối label=\(label): \(error)")
            return HelperCommandResult(success: false, message: error.description)
        }

        var notes: [String] = []
        for domain in launchdDomains(forDirectory: validated.directory) {
            let outcome = runCommand("/bin/launchctl", ["bootout", "\(domain)/\(label)"], timeout: 30)
            if !outcome.success && !Self.isNotLoaded(outcome) {
                notes.append(outcome.message)
            }
        }

        let result = SecureRemover.remove(path: validated.path, policy: .root, allowedRoots: [validated.directory])
        audit("bootoutLaunchDaemon label=\(label) xoá plist → \(result.logValue)")
        guard result.isSuccess else {
            let message: String = switch result.outcome {
            case let .failed(_, m): m
            case let .skipped(reason): "Bỏ qua: \(reason.rawValue)"
            case .ok: ""
            }
            return HelperCommandResult(success: false, message: (notes + [message]).joined(separator: "\n"))
        }
        return HelperCommandResult(success: true, message: notes.isEmpty ? "Đã gỡ \(label)" : notes.joined(separator: "\n"))
    }

    /// LaunchDaemon chạy trong domain `system`; LaunchAgent toàn máy chạy trong `gui/<uid>` của người đang đăng nhập.
    private func launchdDomains(forDirectory directory: String) -> [String] {
        if directory == "/Library/LaunchDaemons" { return ["system"] }
        var st = stat()
        if stat("/dev/console", &st) == 0, st.st_uid != 0 { return ["gui/\(st.st_uid)"] }
        return []
    }

    private static func isNotLoaded(_ outcome: CommandOutcome) -> Bool {
        // launchctl trả 3 (No such process), 36 hoặc 113 (Could not find specified service) khi job chưa nạp.
        if [3, 36, 113].contains(outcome.status) { return true }
        let text = outcome.message.lowercased()
        return text.contains("no such process") || text.contains("could not find") || text.contains("not loaded")
    }

    public func thinLocalSnapshots(volume: String, reply: @escaping (Data) -> Void) {
        let reply = Reply(reply)
        guard HelperValidation.isMountPoint(volume) else {
            audit("thinLocalSnapshots từ chối volume không hợp lệ")
            return reply(VersionedPayload<HelperCommandResult>.encode(HelperCommandResult(success: false, message: "Volume không hợp lệ")))
        }
        workQueue.async { [self] in
            let outcome = runCommand("/usr/bin/tmutil", ["thinlocalsnapshots", volume, "999999999999", "4"], timeout: 600)
            reply(VersionedPayload<HelperCommandResult>.encode(
                HelperCommandResult(success: outcome.success, message: outcome.success ? outcome.stdout : outcome.message)))
        }
    }

    public func deleteLocalSnapshot(date: String, reply: @escaping (Data) -> Void) {
        let reply = Reply(reply)
        guard HelperValidation.isValidSnapshotDate(date) else {
            audit("deleteLocalSnapshot từ chối ngày không hợp lệ: \(date)")
            return reply(VersionedPayload<HelperCommandResult>.encode(HelperCommandResult(success: false, message: "Ngày snapshot không hợp lệ")))
        }
        workQueue.async { [self] in
            let outcome = runCommand("/usr/bin/tmutil", ["deletelocalsnapshots", date], timeout: 600)
            reply(VersionedPayload<HelperCommandResult>.encode(
                HelperCommandResult(success: outcome.success, message: outcome.success ? "Đã xoá snapshot \(date)" : outcome.message)))
        }
    }

    public func forgetPackage(_ packageID: String, reply: @escaping (Data) -> Void) {
        let reply = Reply(reply)
        guard HelperValidation.isValidPackageID(packageID) else {
            audit("forgetPackage từ chối id không hợp lệ: \(packageID)")
            return reply(VersionedPayload<HelperCommandResult>.encode(HelperCommandResult(success: false, message: "Mã gói không hợp lệ")))
        }
        workQueue.async { [self] in
            let outcome = runCommand("/usr/sbin/pkgutil", ["--forget", packageID], timeout: 60)
            reply(VersionedPayload<HelperCommandResult>.encode(
                HelperCommandResult(success: outcome.success, message: outcome.success ? "Đã xoá receipt \(packageID)" : outcome.message)))
        }
    }

    /// Tự gỡ: xoá bản cài kiểu cũ (nếu có) rồi bootout chính mình sau khi đã trả lời.
    /// Binary và plist nằm trong bundle app (SMAppService), không xoá được từ đây mà không phá chữ ký app;
    /// app phải gọi `SMAppService.unregister()` để launchd không bật lại helper.
    public func uninstallSelf(reply: @escaping (Bool) -> Void) {
        audit("uninstallSelf")
        let label = MashCleanIdentifiers.helperLabel
        for legacy in ["/Library/LaunchDaemons/\(label).plist", "/Library/PrivilegedHelperTools/\(label)"] {
            var st = stat()
            guard lstat(legacy, &st) == 0 else { continue }
            let dir = (legacy as NSString).deletingLastPathComponent
            let r = SecureRemover.remove(path: legacy, policy: .root, allowedRoots: [dir])
            audit("uninstallSelf xoá bản cài cũ → \(r.logValue)")
        }
        reply(true)
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 1) { [self] in
            _ = runCommand("/bin/launchctl", ["bootout", "system/\(label)"], timeout: 10)
            // bootout thường kết thúc tiến trình này; nếu chưa thì tự thoát.
            exit(0)
        }
    }

    // MARK: Chạy lệnh

    struct CommandOutcome {
        var success: Bool
        var status: Int32
        var stdout: String
        var message: String
    }

    /// Chạy tool bằng đường dẫn tuyệt đối + mảng tham số, không qua shell (mục 9.3, 22.3).
    private func runCommand(_ tool: String, _ args: [String], timeout: TimeInterval) -> CommandOutcome {
        let commandLine = ([tool] + args).joined(separator: " ")
        audit("exec \(commandLine)")
        do {
            let out = try ProcessRunner.runSync(tool, args, timeout: timeout)
            let stderr = out.stderrString.trimmingCharacters(in: .whitespacesAndNewlines)
            let stdout = out.stdoutString.trimmingCharacters(in: .whitespacesAndNewlines)
            if out.succeeded {
                audit("exec xong: \(tool) status=0")
            } else {
                audit("exec lỗi: \(tool) status=\(out.status) timeout=\(out.timedOut) \(stderr.prefix(300))")
            }
            let message = out.timedOut ? "Hết thời gian chờ \(tool)" : (stderr.isEmpty ? stdout : stderr)
            return CommandOutcome(success: out.succeeded, status: out.status, stdout: stdout, message: message)
        } catch {
            audit("exec không chạy được \(tool): \(error)")
            return CommandOutcome(success: false, status: -1, stdout: "", message: "\(error)")
        }
    }
}
