import Foundation
import os
import SweepCore

/// Logger theo module (mục 17): subsystem `com.mashclean`, category `scan`, `clean`, `helper`, `rules`, `ui`...
/// Không ghi đường dẫn đầy đủ ở mức `info`; dùng `privacy: .private` để macOS che khi xuất log.
extension Logger {
    public static let scan = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "scan")
    public static let clean = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "clean")
    public static let rules = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "rules")
    public static let ui = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "ui")
    public static let storage = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "storage")
    public static let ipc = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "ipc")
    public static let permissions = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "permissions")
    public static let menu = Logger(subsystem: MashCleanIdentifiers.logSubsystem, category: "menu")
    public static let helper = Logger(subsystem: MashCleanIdentifiers.helperLogSubsystem, category: "xpc")
}

/// Ghi song song ra file xoay vòng tại `~/Library/Logs/MashClean/` (mục 12.1) để gom khi gửi báo cáo lỗi.
/// Chỉ ghi thông điệp đã lược bỏ đường dẫn chi tiết.
public final class FileLog: Sendable {
    public static let shared = FileLog()

    public let directory: URL
    private let maxFileSize: UInt64 = 5 * 1024 * 1024
    private let maxFiles = 5
    private let queue = DispatchQueue(label: "com.mashclean.filelog", qos: .utility)
    private let processName: String

    public init(directory: URL? = nil, processName: String = ProcessInfo.processInfo.processName) {
        if let directory {
            self.directory = directory
        } else if getuid() == 0 {
            self.directory = URL(fileURLWithPath: "/Library/Logs/MashClean")
        } else {
            self.directory = URL.userHome.appendingPathComponent("Library/Logs/MashClean", isDirectory: true)
        }
        self.processName = processName
    }

    private var currentFile: URL { directory.appendingPathComponent("\(processName).log") }

    public func write(_ level: String, category: String, _ message: String) {
        let line = "\(ISO8601DateFormatter.logFormatter.string(from: Date())) [\(level)] [\(category)] \(message)\n"
        let dir = directory
        let file = currentFile
        let maxSize = maxFileSize
        let maxFiles = maxFiles
        queue.async {
            let fm = FileManager.default
            try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
            if let attrs = try? fm.attributesOfItem(atPath: file.path), let size = attrs[.size] as? UInt64, size > maxSize {
                Self.rotate(file: file, maxFiles: maxFiles)
            }
            if !fm.fileExists(atPath: file.path) { fm.createFile(atPath: file.path, contents: nil) }
            guard let handle = try? FileHandle(forWritingTo: file) else { return }
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: Data(line.utf8))
        }
    }

    private static func rotate(file: URL, maxFiles: Int) {
        let fm = FileManager.default
        let base = file.path
        try? fm.removeItem(atPath: "\(base).\(maxFiles - 1)")
        for i in stride(from: maxFiles - 2, through: 1, by: -1) {
            try? fm.moveItem(atPath: "\(base).\(i)", toPath: "\(base).\(i + 1)")
        }
        try? fm.moveItem(atPath: base, toPath: "\(base).1")
    }

    /// Các file log sửa đổi trong khoảng thời gian gần đây (cho màn "Gửi báo cáo lỗi").
    public func recentLogFiles(within interval: TimeInterval = 24 * 3600) -> [URL] {
        let fm = FileManager.default
        guard let items = try? fm.contentsOfDirectory(at: directory, includingPropertiesForKeys: [.contentModificationDateKey]) else { return [] }
        let cutoff = Date().addingTimeInterval(-interval)
        return items.filter {
            ((try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast) >= cutoff
        }.sorted { $0.lastPathComponent < $1.lastPathComponent }
    }

    public func flush() { queue.sync {} }
}

extension ISO8601DateFormatter {
    nonisolated(unsafe) static let logFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()
}

/// Facade ghi cùng lúc ra `os_log` và file log.
public enum Log {
    public static func info(_ logger: Logger, _ category: String, _ message: String) {
        logger.info("\(message, privacy: .public)")
        FileLog.shared.write("INFO", category: category, message)
    }

    public static func error(_ logger: Logger, _ category: String, _ message: String) {
        logger.error("\(message, privacy: .public)")
        FileLog.shared.write("ERROR", category: category, message)
    }

    public static func warning(_ logger: Logger, _ category: String, _ message: String) {
        logger.warning("\(message, privacy: .public)")
        FileLog.shared.write("WARN", category: category, message)
    }

    /// Đường dẫn chỉ ghi ở mức debug và được che trong os_log.
    public static func debugPath(_ logger: Logger, _ prefix: String, _ path: String) {
        logger.debug("\(prefix, privacy: .public): \(path, privacy: .private)")
    }
}
