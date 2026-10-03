import Foundation
import os
import ServiceManagement
import SweepCore
import SweepLogging

public enum HelperError: Error, Sendable, CustomStringConvertible {
    case unavailable
    case notInstalled
    case requiresApproval
    case connection(String)
    case badReply

    public var description: String {
        switch self {
        case .unavailable: String(localized: "Không kết nối được helper")
        case .notInstalled: String(localized: "Helper chưa được cài")
        case .requiresApproval: String(localized: "Cần bật Clean Boost trong System Settings > General > Login Items")
        case let .connection(m): String(localized: "Lỗi kết nối helper: \(m)")
        case .badReply: String(localized: "Helper trả về dữ liệu không hợp lệ")
        }
    }
}

/// Bảo đảm continuation chỉ được resume một lần dù cả `errorHandler` lẫn `reply` cùng được gọi (mục 22.3).
final class ResumeOnce<T: Sendable>: @unchecked Sendable {
    private let lock = OSAllocatedUnfairLock()
    private var continuation: CheckedContinuation<T, any Error>?

    init(_ c: CheckedContinuation<T, any Error>) { continuation = c }

    private func take() -> CheckedContinuation<T, any Error>? {
        lock.lock()
        defer { lock.unlock() }
        let c = continuation
        continuation = nil
        return c
    }

    func succeed(_ value: T) { take()?.resume(returning: value) }
    func fail(_ error: any Error) { take()?.resume(throwing: error) }
}

/// Bọc NSXPCConnection thành async/await (mục 22.3). Mỗi lời gọi tạo proxy với errorHandler riêng,
/// nên khi helper chết giữa chừng, lời gọi đó nhận lỗi thay vì treo mãi.
public actor HelperClient {
    private var connection: NSXPCConnection?
    private let machServiceName: String
    public let timeout: TimeInterval

    public init(machServiceName: String = MashCleanIdentifiers.helperLabel, timeout: TimeInterval = 300) {
        self.machServiceName = machServiceName
        self.timeout = timeout
    }

    private func currentConnection() -> NSXPCConnection {
        if let connection { return connection }
        let conn = NSXPCConnection(machServiceName: machServiceName, options: .privileged)
        conn.remoteObjectInterface = NSXPCInterface(with: (any HelperProtocol).self)
        conn.setCodeSigningRequirement(HelperRequirement.helper)
        conn.invalidationHandler = { [weak self] in Task { await self?.reset() } }
        conn.interruptionHandler = { [weak self] in Task { await self?.reset() } }
        conn.resume()
        connection = conn
        return conn
    }

    private func reset() {
        connection?.invalidationHandler = nil
        connection = nil
    }

    public func disconnect() {
        connection?.invalidate()
        reset()
    }

    private func call<T: Sendable>(timeout: TimeInterval? = nil,
                                   _ body: @escaping @Sendable (any HelperProtocol, @escaping @Sendable (T) -> Void) -> Void) async throws -> T {
        let conn = currentConnection()
        let timeout = timeout ?? self.timeout
        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<T, any Error>) in
            let once = ResumeOnce(cont)
            let proxy = conn.remoteObjectProxyWithErrorHandler { once.fail(HelperError.connection($0.localizedDescription)) }
            guard let helper = proxy as? any HelperProtocol else { return once.fail(HelperError.unavailable) }
            DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { once.fail(HelperError.connection("timeout")) }
            body(helper) { once.succeed($0) }
        }
    }

    // MARK: API

    /// Chỉ là lệnh ping: helper khoẻ thì trả lời ngay. Timeout ngắn để helper hỏng (đã đăng ký nhưng launchd
    /// không khởi động được) không làm treo màn quét/bảo trì tới hết timeout 300 giây của lệnh xoá.
    public func protocolVersion(timeout: TimeInterval = 5) async throws -> Int {
        try await call(timeout: timeout) { helper, reply in helper.protocolVersion { reply($0) } }
    }

    public func removeItems(_ paths: [String], mode: RemoveMode) async throws -> [RemoveResult] {
        let data: Data = try await call { helper, reply in helper.removeItems(paths, mode: mode.rawValue) { reply($0) } }
        return try VersionedPayload<[RemoveResult]>.decode(data)
    }

    public func runMaintenance(_ task: MaintenanceTaskName) async throws -> MaintenanceResult {
        let data: Data = try await call { helper, reply in helper.runMaintenance(task.rawValue) { reply($0) } }
        return try VersionedPayload<MaintenanceResult>.decode(data)
    }

    public func bootoutLaunchDaemon(label: String, plistPath: String) async throws -> HelperCommandResult {
        let data: Data = try await call { helper, reply in helper.bootoutLaunchDaemon(label: label, plistPath: plistPath) { reply($0) } }
        return try VersionedPayload<HelperCommandResult>.decode(data)
    }

    public func thinLocalSnapshots(volume: String) async throws -> HelperCommandResult {
        let data: Data = try await call { helper, reply in helper.thinLocalSnapshots(volume: volume) { reply($0) } }
        return try VersionedPayload<HelperCommandResult>.decode(data)
    }

    public func deleteLocalSnapshot(date: String) async throws -> HelperCommandResult {
        let data: Data = try await call { helper, reply in helper.deleteLocalSnapshot(date: date) { reply($0) } }
        return try VersionedPayload<HelperCommandResult>.decode(data)
    }

    public func forgetPackage(_ id: String) async throws -> HelperCommandResult {
        let data: Data = try await call { helper, reply in helper.forgetPackage(id) { reply($0) } }
        return try VersionedPayload<HelperCommandResult>.decode(data)
    }

    public func uninstallSelf() async throws -> Bool {
        try await call { helper, reply in helper.uninstallSelf { reply($0) } }
    }

    /// Kiểm tra phiên bản helper; cũ hơn app cần thì đăng ký lại (mục 9.5).
    /// `repair`: helper "enabled" nhưng không trả lời (thường do app bị thay bằng bản mới, launchd giữ đăng ký cũ
    /// và spawn lỗi EX_CONFIG) thì đăng ký lại một lần rồi ping lại.
    public func ensureCompatible(repair: Bool = true) async -> HelperStatus {
        let status = HelperInstaller.status
        guard status == .enabled else { return status }
        do {
            let version = try await protocolVersion()
            if version < MashCleanIdentifiers.helperProtocolVersion {
                Log.warning(.ipc, "ipc", "Helper cũ (v\(version)), đăng ký lại")
                disconnect()
                try await HelperInstaller.reinstall()
                return HelperInstaller.status
            }
            return .enabled
        } catch {
            Log.error(.ipc, "ipc", "Không gọi được helper: \(error)")
            guard repair else { return .unreachable }
            disconnect()
            do {
                try await HelperInstaller.reinstall()
            } catch {
                Log.error(.ipc, "ipc", "Đăng ký lại helper lỗi: \(error)")
                return .unreachable
            }
            try? await Task.sleep(for: .seconds(1))
            return await ensureCompatible(repair: false)
        }
    }
}

public enum HelperStatus: Sendable, Equatable {
    case enabled
    case requiresApproval
    case notRegistered
    case notFound
    case unreachable
}

/// Đăng ký helper bằng `SMAppService.daemon` (mục 9.1).
public enum HelperInstaller {
    public static var service: SMAppService { SMAppService.daemon(plistName: MashCleanIdentifiers.helperPlistName) }

    public static var status: HelperStatus {
        switch service.status {
        case .enabled: .enabled
        case .requiresApproval: .requiresApproval
        case .notRegistered: .notRegistered
        case .notFound: .notFound
        @unknown default: .notFound
        }
    }

    @discardableResult
    public static func ensureRegistered() throws -> HelperStatus {
        switch service.status {
        case .enabled:
            return .enabled
        case .requiresApproval:
            // Người dùng phải bật trong System Settings > General > Login Items
            SMAppService.openSystemSettingsLoginItems()
            return .requiresApproval
        case .notRegistered, .notFound:
            try service.register()
            return status
        @unknown default:
            return status
        }
    }

    public static func reinstall() async throws {
        try? await service.unregister()
        try service.register()
    }

    public static func unregister() async throws {
        try await service.unregister()
    }
}
