import Foundation
import os
import SweepCore
import SweepIPC
import SweepLogging

/// Điểm vào của helper root (mục 9.4). Target Xcode `Helper` chỉ gọi `HelperMain.run()`.
public enum HelperMain {
    public static func run() -> Never {
        let idle = IdleExitMonitor(timeout: 60)
        let delegate = HelperDelegate(idle: idle)
        let listener = NSXPCListener(machServiceName: MashCleanIdentifiers.helperLabel)
        listener.delegate = delegate
        listener.resume()
        idle.start()
        Logger.helper.info("Helper khởi động, uid=\(getuid(), privacy: .public), pid=\(getpid(), privacy: .public)")
        withExtendedLifetime((listener, delegate, idle)) {
            dispatchMain()
        }
    }
}

/// Chấp nhận kết nối XPC: kiểm tra chữ ký client rồi gắn `HelperService` riêng cho từng kết nối (mục 9.4).
public final class HelperDelegate: NSObject, NSXPCListenerDelegate, @unchecked Sendable {
    private let idle: IdleExitMonitor

    init(idle: IdleExitMonitor) {
        self.idle = idle
    }

    public func listener(_ listener: NSXPCListener, shouldAcceptNewConnection conn: NSXPCConnection) -> Bool {
        let pid = conn.processIdentifier
        // Client ký sai thì kết nối bị huỷ ngay ở lời gọi đầu tiên (macOS 13+).
        conn.setCodeSigningRequirement(HelperRequirement.client)
        conn.exportedInterface = NSXPCInterface(with: (any HelperProtocol).self)
        conn.exportedObject = HelperService(peerPID: pid)

        let closed = Locked(false)
        let idle = idle
        let onClose: @Sendable () -> Void = {
            let first = closed.withLock { done -> Bool in
                defer { done = true }
                return !done
            }
            if first {
                Logger.helper.info("Kết nối đóng, pid=\(pid, privacy: .public)")
                idle.connectionClosed()
            }
        }
        conn.invalidationHandler = onClose
        conn.interruptionHandler = onClose

        idle.connectionOpened()
        Logger.helper.info("Nhận kết nối từ pid=\(pid, privacy: .public)")
        conn.resume()
        return true
    }
}

/// Tự thoát sau `timeout` giây không còn kết nối nào; launchd sẽ bật lại khi có kết nối mới (mục 9.4.6).
final class IdleExitMonitor: @unchecked Sendable {
    private let queue = DispatchQueue(label: "com.mashclean.helper.idle")
    private let timeout: TimeInterval
    private var openConnections = 0
    private var pending: DispatchWorkItem?

    init(timeout: TimeInterval) {
        self.timeout = timeout
    }

    func start() {
        queue.async { self.scheduleIfIdle() }
    }

    func connectionOpened() {
        queue.async {
            self.openConnections += 1
            self.pending?.cancel()
            self.pending = nil
        }
    }

    func connectionClosed() {
        queue.async {
            self.openConnections = max(0, self.openConnections - 1)
            self.scheduleIfIdle()
        }
    }

    private func scheduleIfIdle() {
        guard openConnections == 0 else { return }
        pending?.cancel()
        let item = DispatchWorkItem { [weak self] in
            guard let self, self.openConnections == 0 else { return }
            Logger.helper.info("Không có kết nối trong \(Int(self.timeout), privacy: .public) giây, helper thoát")
            exit(0)
        }
        pending = item
        queue.asyncAfter(deadline: .now() + timeout, execute: item)
    }
}
