import Foundation

/// Gọi tool hệ thống theo nguyên tắc chung (mục 22.3): đường dẫn tuyệt đối, tham số dạng mảng,
/// có timeout, coi tool không tồn tại là trường hợp bình thường. Không bao giờ gọi qua `/bin/sh -c`.
public struct ProcessRunner: Sendable {
    public struct Output: Sendable {
        public var status: Int32
        public var stdout: Data
        public var stderr: Data
        public var timedOut: Bool

        public var stdoutString: String { String(decoding: stdout, as: UTF8.self) }
        public var stderrString: String { String(decoding: stderr, as: UTF8.self) }
        public var succeeded: Bool { status == 0 && !timedOut }
    }

    public enum RunError: Error, Sendable, CustomStringConvertible {
        case notAbsolute(String)
        case toolMissing(String)
        case launchFailed(String)

        public var description: String {
            switch self {
            case let .notAbsolute(p): String(localized: "Tool không phải đường dẫn tuyệt đối: \(p)")
            case let .toolMissing(p): String(localized: "Không tìm thấy tool: \(p)")
            case let .launchFailed(m): String(localized: "Không chạy được tool: \(m)")
            }
        }
    }

    public init() {}

    public static func exists(_ tool: String) -> Bool {
        FileManager.default.isExecutableFile(atPath: tool)
    }

    /// Chạy tool và chờ kết thúc. Hàm async, chạy trên thread riêng để không chặn cooperative pool.
    public func run(
        _ tool: String,
        _ arguments: [String],
        timeout: TimeInterval = 120,
        environment: [String: String]? = nil,
        currentDirectory: URL? = nil
    ) async throws -> Output {
        guard tool.hasPrefix("/") else { throw RunError.notAbsolute(tool) }
        guard Self.exists(tool) else { throw RunError.toolMissing(tool) }

        return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Output, any Error>) in
            DispatchQueue.global(qos: .utility).async {
                do {
                    cont.resume(returning: try Self.runSync(tool, arguments, timeout: timeout, environment: environment, currentDirectory: currentDirectory))
                } catch {
                    cont.resume(throwing: error)
                }
            }
        }
    }

    /// Phiên bản đồng bộ, dùng trong helper (chạy trên hàng đợi XPC riêng).
    public static func runSync(
        _ tool: String,
        _ arguments: [String],
        timeout: TimeInterval = 120,
        environment: [String: String]? = nil,
        currentDirectory: URL? = nil
    ) throws -> Output {
        guard tool.hasPrefix("/") else { throw RunError.notAbsolute(tool) }
        guard exists(tool) else { throw RunError.toolMissing(tool) }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        if let environment { process.environment = environment }
        if let currentDirectory { process.currentDirectoryURL = currentDirectory }
        let outPipe = Pipe()
        let errPipe = Pipe()
        process.standardOutput = outPipe
        process.standardError = errPipe
        process.standardInput = FileHandle.nullDevice

        let outBox = DataBox()
        let errBox = DataBox()
        let group = DispatchGroup()
        group.enter()
        group.enter()
        DispatchQueue.global(qos: .utility).async {
            outBox.set(outPipe.fileHandleForReading.readDataToEndOfFile())
            group.leave()
        }
        DispatchQueue.global(qos: .utility).async {
            errBox.set(errPipe.fileHandleForReading.readDataToEndOfFile())
            group.leave()
        }

        do {
            try process.run()
        } catch {
            throw RunError.launchFailed(error.localizedDescription)
        }

        let exited = DispatchSemaphore(value: 0)
        process.terminationHandler = { _ in exited.signal() }
        var timedOut = false
        if exited.wait(timeout: .now() + timeout) == .timedOut {
            timedOut = true
            process.terminate()
            if exited.wait(timeout: .now() + 3) == .timedOut {
                kill(process.processIdentifier, SIGKILL)
                _ = exited.wait(timeout: .now() + 2)
            }
        }
        _ = group.wait(timeout: .now() + 5)
        return Output(status: process.terminationStatus, stdout: outBox.get(), stderr: errBox.get(), timedOut: timedOut)
    }
}

private final class DataBox: @unchecked Sendable {
    private let lock = NSLock()
    private var data = Data()
    func set(_ d: Data) { lock.lock(); data = d; lock.unlock() }
    func get() -> Data { lock.lock(); defer { lock.unlock() }; return data }
}
