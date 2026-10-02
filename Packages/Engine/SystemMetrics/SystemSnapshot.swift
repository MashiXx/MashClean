import Foundation

/// Mức memory pressure của hệ thống (`kern.memorystatus_vm_pressure_level`: 1, 2, 4).
public enum MemoryPressureLevel: Int, Sendable, Comparable, Codable {
    case normal = 1
    case warning = 2
    case critical = 4

    public static func < (l: MemoryPressureLevel, r: MemoryPressureLevel) -> Bool { l.rawValue < r.rawValue }

    public var title: String {
        switch self {
        case .normal: "Bình thường"
        case .warning: "Cao"
        case .critical: "Nguy cấp"
        }
    }
}

public struct CPUStats: Sendable, Hashable {
    /// Tỉ lệ sử dụng 0...1 (trung bình mọi lõi), tính theo delta giữa 2 lần lấy mẫu.
    public var usage: Double
    public var user: Double
    public var system: Double
    public var coreCount: Int
}

public struct MemoryStats: Sendable, Hashable {
    public var total: UInt64
    /// active + wired + compressed (mục 22.3).
    public var used: UInt64
    public var wired: UInt64
    public var compressed: UInt64
    public var free: UInt64
    public var pressure: MemoryPressureLevel

    public var usedFraction: Double { total > 0 ? min(1, Double(used) / Double(total)) : 0 }
}

public struct DiskStats: Sendable, Hashable {
    public var mountPoint: String
    public var name: String
    public var total: Int64
    /// `volumeAvailableCapacityForImportantUsageKey`.
    public var available: Int64

    public var freeFraction: Double { total > 0 ? Double(available) / Double(total) : 1 }
    public var usedFraction: Double { 1 - freeFraction }
}

public struct BatteryStats: Sendable, Hashable {
    public var hasBattery: Bool
    /// 0...1
    public var level: Double
    public var isCharging: Bool
    public var isOnACPower: Bool
    /// Thời gian còn lại (phút) tới cạn hoặc tới đầy khi đang sạc; `nil` khi hệ thống đang tính.
    public var minutesRemaining: Int?

    public static let none = BatteryStats(hasBattery: false, level: 0, isCharging: false, isOnACPower: true, minutesRemaining: nil)
}

public struct NetworkStats: Sendable, Hashable {
    /// Tổng byte vào/ra từ lúc khởi động (mọi interface trừ loopback).
    public var totalIn: UInt64
    public var totalOut: UInt64
    /// Byte/giây, tính theo delta với lần lấy mẫu trước.
    public var inPerSecond: Double
    public var outPerSecond: Double
}

/// Ảnh chụp số liệu hệ thống tại một thời điểm (mục 11.9).
public struct SystemSnapshot: Sendable, Hashable {
    public var date: Date
    public var cpu: CPUStats?
    public var memory: MemoryStats?
    public var disk: DiskStats?
    public var battery: BatteryStats
    public var network: NetworkStats?
    /// Dung lượng `~/.Trash`; `nil` khi không đọc được (thiếu Full Disk Access) hoặc chưa đo.
    public var trashBytes: Int64?
    public var uptime: TimeInterval

    public init(date: Date = Date(), cpu: CPUStats? = nil, memory: MemoryStats? = nil, disk: DiskStats? = nil,
                battery: BatteryStats = .none, network: NetworkStats? = nil, trashBytes: Int64? = nil, uptime: TimeInterval = 0) {
        self.date = date
        self.cpu = cpu
        self.memory = memory
        self.disk = disk
        self.battery = battery
        self.network = network
        self.trashBytes = trashBytes
        self.uptime = uptime
    }
}
