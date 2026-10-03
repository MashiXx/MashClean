import AppKit
import CleanEngineCore
import FileSystemKit
import Foundation
import NodeTree
import os
import ScanEngine
import SweepCore
import SweepIPC
import SweepLogging
import SweepStorage

/// Mức memory pressure của hệ thống (`kern.memorystatus_vm_pressure_level`).
public enum MemoryPressure: Int, Sendable, Comparable {
    case normal = 1
    case warning = 2
    case critical = 4

    public static func < (l: MemoryPressure, r: MemoryPressure) -> Bool { l.rawValue < r.rawValue }

    public static var current: MemoryPressure {
        var level: Int32 = 0
        var size = MemoryLayout<Int32>.size
        guard sysctlbyname("kern.memorystatus_vm_pressure_level", &level, &size, nil, 0) == 0 else { return .normal }
        return MemoryPressure(rawValue: Int(level)) ?? .normal
    }

    public var title: String {
        switch self {
        case .normal: String(localized: "Bình thường")
        case .warning: String(localized: "Cao")
        case .critical: String(localized: "Nghiêm trọng")
        }
    }
}

/// Snapshot Time Machine cục bộ.
public struct LocalSnapshot: Sendable, Hashable, Identifiable {
    public var id: String { name }
    public let name: String     // com.apple.TimeMachine.2026-10-01-101500.local
    public let date: String     // 2026-10-01-101500
    public let volume: String

    public var displayDate: String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd-HHmmss"
        guard let d = f.date(from: date) else { return date }
        return d.formatted(date: .abbreviated, time: .shortened)
    }
}

/// Trạng thái hệ thống dùng để gợi ý tác vụ nên chạy (mục 11.5).
public struct MaintenanceStatus: Sendable {
    public var memoryPressure: MemoryPressure
    public var purgeableBytes: ByteCount
    public var localSnapshots: [LocalSnapshot]
    public var mailRunning: Bool
    public var mailEnvelopeIndexBytes: ByteCount?
    public var spotlightEnabled: Bool?
    public var lastRuns: [MaintenanceTaskName: Date]
    public var helperAvailable: Bool

    public struct Recommendation: Sendable, Hashable {
        public var task: MaintenanceTaskName
        public var recommended: Bool
        public var safety: SafetyLevel
        public var why: String
    }

    /// Quy tắc gợi ý. Tác vụ nhẹ, vô hại thì `safe` (Smart Scan chạy luôn); tác vụ nặng thì `review`.
    public func recommendation(for task: MaintenanceTaskName, now: Date = Date()) -> Recommendation {
        let last = lastRuns[task]
        func olderThan(_ days: Double) -> Bool { last.map { now.timeIntervalSince($0) > days * 86_400 } ?? true }
        switch task {
        case .freeRAM:
            return .init(task: task, recommended: memoryPressure >= .warning, safety: .safe,
                         why: memoryPressure >= .warning ? String(localized: "Memory pressure đang ở mức \(memoryPressure.title.lowercased()).") : String(localized: "Memory pressure bình thường."))
        case .runPeriodic:
            return .init(task: task, recommended: olderThan(7), safety: .safe,
                         why: olderThan(7) ? String(localized: "Chưa chạy trong 7 ngày qua.") : String(localized: "Đã chạy gần đây."))
        case .flushDNS:
            return .init(task: task, recommended: olderThan(30), safety: .safe,
                         why: olderThan(30) ? String(localized: "Chưa xoá cache DNS trong 30 ngày.") : String(localized: "Đã chạy gần đây."))
        case .thinSnapshots:
            let rec = !localSnapshots.isEmpty && purgeableBytes > .gigabytes(5)
            return .init(task: task, recommended: rec, safety: .review,
                         why: localSnapshots.isEmpty ? String(localized: "Không có snapshot cục bộ.") : String(localized: "\(localSnapshots.count) snapshot, khoảng \(purgeableBytes.formatted) purgeable."))
        case .reindexSpotlight:
            return .init(task: task, recommended: spotlightEnabled == false, safety: .review,
                         why: spotlightEnabled == false ? String(localized: "Chỉ mục Spotlight đang tắt hoặc lỗi.") : String(localized: "Chỉ chạy khi Spotlight tìm sai hoặc chậm."))
        case .rebuildLaunchServices:
            return .init(task: task, recommended: false, safety: .review, why: String(localized: "Chỉ chạy khi menu \"Open With\" bị trùng lặp."))
        case .speedUpMail:
            let big = (mailEnvelopeIndexBytes ?? .zero) > .megabytes(500)
            return .init(task: task, recommended: big && !mailRunning, safety: .review,
                         why: mailRunning ? String(localized: "Hãy tắt Mail trước.") : big ? String(localized: "Chỉ mục Mail lớn (\(mailEnvelopeIndexBytes?.formatted ?? ""))."): String(localized: "Chỉ mục Mail bình thường."))
        }
    }

    public var recommendations: [Recommendation] { MaintenanceTaskName.allCases.map { recommendation(for: $0) } }
}

/// Thu thập trạng thái hệ thống. Không cần root.
public struct MaintenanceStatusReader: Sendable {
    let storage: Storage?
    let helper: HelperClient?
    let home: URL

    public init(storage: Storage?, helper: HelperClient?, home: URL = .userHome) {
        self.storage = storage
        self.helper = helper
        self.home = home
    }

    public func read() async -> MaintenanceStatus {
        let runs = (try? storage?.lastMaintenanceRuns()) ?? [:]
        var lastRuns: [MaintenanceTaskName: Date] = [:]
        for (k, v) in runs { if let t = MaintenanceTaskName(rawValue: k) { lastRuns[t] = v.ranAt } }
        let volume = VolumeInfo(url: URL(fileURLWithPath: "/"))
        let purgeable = volume.map { max(0, $0.availableForImportantUsage - $0.available) } ?? 0
        // Các lệnh ngoài (tmutil, mdutil, ping helper) chạy song song: chờ lâu nhất bằng lệnh chậm nhất, không cộng dồn.
        async let snapshotsResult = Self.localSnapshots(volume: "/")
        async let spotlightResult = Self.spotlightEnabled()
        async let helperResult = Self.helperReachable(helper)
        let mailRunning = RunningAppsCheck.isRunning("com.apple.mail")
        let envelope = Self.envelopeIndexFiles(home: home).reduce(Int64(0)) { sum, url in
            sum + Int64((try? url.resourceValues(forKeys: [.totalFileAllocatedSizeKey]).totalFileAllocatedSize) ?? 0)
        }
        let (snapshots, spotlight, helperOK) = await (snapshotsResult, spotlightResult, helperResult)
        return MaintenanceStatus(memoryPressure: .current, purgeableBytes: ByteCount(purgeable), localSnapshots: snapshots, mailRunning: mailRunning,
                                 mailEnvelopeIndexBytes: envelope > 0 ? ByteCount(envelope) : nil, spotlightEnabled: spotlight,
                                 lastRuns: lastRuns, helperAvailable: helperOK)
    }

    static func helperReachable(_ helper: HelperClient?) async -> Bool {
        guard let helper, HelperInstaller.status == .enabled else { return false }
        return (try? await helper.protocolVersion()) != nil
    }

    /// `tmutil listlocalsnapshots /` không cần root.
    public static func localSnapshots(volume: String) async -> [LocalSnapshot] {
        guard let out = try? await ProcessRunner().run("/usr/bin/tmutil", ["listlocalsnapshots", volume], timeout: 15), out.succeeded else { return [] }
        return parseSnapshots(out.stdoutString, volume: volume)
    }

    public static func parseSnapshots(_ text: String, volume: String) -> [LocalSnapshot] {
        text.split(separator: "\n").compactMap { line in
            let name = line.trimmingCharacters(in: .whitespaces)
            guard name.hasPrefix("com.apple.TimeMachine."), let range = name.range(of: #"\d{4}-\d{2}-\d{2}-\d{6}"#, options: .regularExpression) else { return nil }
            return LocalSnapshot(name: name, date: String(name[range]), volume: volume)
        }
    }

    static func spotlightEnabled() async -> Bool? {
        guard let out = try? await ProcessRunner().run("/usr/bin/mdutil", ["-s", "/"], timeout: 10), out.succeeded else { return nil }
        return out.stdoutString.contains("enabled")
    }

    /// `~/Library/Mail/V*/MailData/Envelope Index*` (cần Full Disk Access để đọc).
    public static func envelopeIndexFiles(home: URL) -> [URL] {
        let mail = home.appendingPathComponent("Library/Mail")
        guard let versions = try? FileManager.default.contentsOfDirectory(at: mail, includingPropertiesForKeys: nil) else { return [] }
        var files: [URL] = []
        for v in versions where v.lastPathComponent.hasPrefix("V") {
            let data = v.appendingPathComponent("MailData")
            let items = (try? FileManager.default.contentsOfDirectory(at: data, includingPropertiesForKeys: nil)) ?? []
            files += items.filter { $0.lastPathComponent.hasPrefix("Envelope Index") }
        }
        return files
    }
}

enum RunningAppsCheck {
    static func isRunning(_ bundleID: String) -> Bool {
        !NSRunningApplication.runningApplications(withBundleIdentifier: bundleID).isEmpty
    }
}
