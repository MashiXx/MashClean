import Darwin
import Foundation
import IOKit.ps
import SweepCore

/// Đọc số liệu hệ thống bằng API cấp thấp, rẻ (mục 11.9, 22.3). Các hàm đều không chặn lâu, trừ `trashSize`.
public enum SystemMetrics {
    // MARK: CPU

    /// Tick CPU cộng dồn của mọi lõi (`host_processor_info`).
    public struct CPUTicks: Sendable, Hashable {
        public var user: UInt64
        public var system: UInt64
        public var idle: UInt64
        public var nice: UInt64
        public var coreCount: Int

        var total: UInt64 { user + system + idle + nice }
    }

    public static func cpuTicks() -> CPUTicks? {
        var cpuCount: natural_t = 0
        var info: processor_info_array_t?
        var infoCount: mach_msg_type_number_t = 0
        let kr = host_processor_info(mach_host_self(), PROCESSOR_CPU_LOAD_INFO, &cpuCount, &info, &infoCount)
        guard kr == KERN_SUCCESS, let info else { return nil }
        defer {
            vm_deallocate(mach_task_self_, vm_address_t(UInt(bitPattern: info)), vm_size_t(Int(infoCount) * MemoryLayout<integer_t>.stride))
        }
        var ticks = CPUTicks(user: 0, system: 0, idle: 0, nice: 0, coreCount: Int(cpuCount))
        for i in 0..<Int(cpuCount) {
            let base = i * Int(CPU_STATE_MAX)
            ticks.user += UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_USER)]))
            ticks.system += UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_SYSTEM)]))
            ticks.idle += UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_IDLE)]))
            ticks.nice += UInt64(UInt32(bitPattern: info[base + Int(CPU_STATE_NICE)]))
        }
        return ticks
    }

    /// % CPU giữa hai lần lấy mẫu.
    public static func cpuUsage(from old: CPUTicks, to new: CPUTicks) -> CPUStats {
        func d(_ a: UInt64, _ b: UInt64) -> Double { b >= a ? Double(b - a) : 0 }
        let user = d(old.user, new.user) + d(old.nice, new.nice)
        let system = d(old.system, new.system)
        let idle = d(old.idle, new.idle)
        let total = user + system + idle
        guard total > 0 else { return CPUStats(usage: 0, user: 0, system: 0, coreCount: new.coreCount) }
        return CPUStats(usage: (user + system) / total, user: user / total, system: system / total, coreCount: new.coreCount)
    }

    // MARK: RAM

    public static func memory() -> MemoryStats? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return nil }
        let page = UInt64(getpagesize())
        let active = UInt64(stats.active_count) * page
        let wired = UInt64(stats.wire_count) * page
        let compressed = UInt64(stats.compressor_page_count) * page
        let free = UInt64(stats.free_count) * page
        return MemoryStats(total: ProcessInfo.processInfo.physicalMemory, used: active + wired + compressed,
                           wired: wired, compressed: compressed, free: free, pressure: memoryPressureLevel())
    }

    /// `kern.memorystatus_vm_pressure_level`.
    public static func memoryPressureLevel() -> MemoryPressureLevel {
        var level: Int32 = 0
        var size = MemoryLayout<Int32>.size
        guard sysctlbyname("kern.memorystatus_vm_pressure_level", &level, &size, nil, 0) == 0 else { return .normal }
        return MemoryPressureLevel(rawValue: Int(level)) ?? .normal
    }

    // MARK: Ổ đĩa

    public static func disk(at url: URL = URL(fileURLWithPath: "/")) -> DiskStats? {
        let keys: Set<URLResourceKey> = [.volumeURLKey, .volumeNameKey, .volumeTotalCapacityKey,
                                         .volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey]
        guard let v = try? url.resourceValues(forKeys: keys) else { return nil }
        let available = v.volumeAvailableCapacityForImportantUsage ?? Int64(v.volumeAvailableCapacity ?? 0)
        return DiskStats(mountPoint: v.volume?.path ?? url.path, name: v.volumeName ?? url.lastPathComponent,
                         total: Int64(v.volumeTotalCapacity ?? 0), available: available)
    }

    // MARK: Pin

    public static func battery() -> BatteryStats {
        guard let blob = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let list = IOPSCopyPowerSourcesList(blob)?.takeRetainedValue() as? [CFTypeRef]
        else { return .none }
        for source in list {
            guard let desc = IOPSGetPowerSourceDescription(blob, source)?.takeUnretainedValue() as? [String: Any],
                  desc[kIOPSTypeKey] as? String == kIOPSInternalBatteryType,
                  desc[kIOPSIsPresentKey] as? Bool ?? true
            else { continue }
            let current = desc[kIOPSCurrentCapacityKey] as? Int ?? 0
            let max = desc[kIOPSMaxCapacityKey] as? Int ?? 100
            let charging = desc[kIOPSIsChargingKey] as? Bool ?? false
            let onAC = desc[kIOPSPowerSourceStateKey] as? String == kIOPSACPowerValue
            let minutesKey = charging ? kIOPSTimeToFullChargeKey : kIOPSTimeToEmptyKey
            let minutes = desc[minutesKey] as? Int
            return BatteryStats(hasBattery: true, level: max > 0 ? Double(current) / Double(max) : 0,
                                isCharging: charging, isOnACPower: onAC,
                                minutesRemaining: (minutes ?? -1) >= 0 ? minutes : nil)
        }
        return .none
    }

    // MARK: Mạng

    /// Bộ đếm byte theo interface (`getifaddrs` + `if_data`). Bộ đếm 32 bit nên có thể quay vòng.
    public static func interfaceCounters() -> [String: (rx: UInt32, tx: UInt32)] {
        var result: [String: (rx: UInt32, tx: UInt32)] = [:]
        var ifaddr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddr) == 0, let first = ifaddr else { return result }
        defer { freeifaddrs(ifaddr) }
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let p = cursor {
            defer { cursor = p.pointee.ifa_next }
            let ifa = p.pointee
            guard let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_LINK), let data = ifa.ifa_data else { continue }
            let flags = Int32(ifa.ifa_flags)
            guard flags & IFF_LOOPBACK == 0, flags & IFF_UP != 0 else { continue }
            let stats = data.assumingMemoryBound(to: if_data.self).pointee
            result[String(cString: ifa.ifa_name)] = (stats.ifi_ibytes, stats.ifi_obytes)
        }
        return result
    }

    // MARK: Thùng rác, uptime

    /// Dung lượng `~/.Trash`. Ném lỗi khi không có quyền đọc (cần Full Disk Access trên một số phiên bản macOS).
    public static func trashSize(home: URL = URL(fileURLWithPath: NSHomeDirectory())) throws -> Int64 {
        let trash = home.appendingPathComponent(".Trash", isDirectory: true)
        // Thử đọc trước để phân biệt "trống" với "không có quyền".
        _ = try FileManager.default.contentsOfDirectory(atPath: trash.path)
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isRegularFileKey]
        guard let e = FileManager.default.enumerator(at: trash, includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true })
        else { return 0 }
        var total: Int64 = 0
        for case let url as URL in e {
            guard let v = try? url.resourceValues(forKeys: Set(keys)), v.isRegularFile == true else { continue }
            total += Int64(v.totalFileAllocatedSize ?? v.fileAllocatedSize ?? 0)
        }
        return total
    }

    public static var uptime: TimeInterval {
        var boot = timeval()
        var size = MemoryLayout<timeval>.size
        var mib: [Int32] = [CTL_KERN, KERN_BOOTTIME]
        guard sysctl(&mib, 2, &boot, &size, nil, 0) == 0, boot.tv_sec > 0 else { return ProcessInfo.processInfo.systemUptime }
        return Date().timeIntervalSince1970 - (Double(boot.tv_sec) + Double(boot.tv_usec) / 1_000_000)
    }
}

/// Lấy mẫu định kỳ, giữ trạng thái lần trước để tính % CPU và tốc độ mạng theo delta.
/// Gọi từ hàng đợi nền (không phải main thread).
public final class SystemSampler: @unchecked Sendable {
    private struct State {
        var cpu: SystemMetrics.CPUTicks?
        var net: [String: (rx: UInt32, tx: UInt32)]?
        var netTotals: (rx: UInt64, tx: UInt64) = (0, 0)
        var lastDate: Date?
    }

    private let state = Locked(State())

    public init() {}

    /// Lấy một ảnh chụp. `trashBytes` truyền từ ngoài vì đo Thùng rác tốn hơn, chỉ làm thưa.
    public func sample(trashBytes: Int64? = nil, diskURL: URL = URL(fileURLWithPath: "/")) -> SystemSnapshot {
        let now = Date()
        let ticks = SystemMetrics.cpuTicks()
        let counters = SystemMetrics.interfaceCounters()

        let (cpu, network): (CPUStats?, NetworkStats?) = state.withLock { s in
            var cpu: CPUStats?
            if let old = s.cpu, let ticks { cpu = SystemMetrics.cpuUsage(from: old, to: ticks) }
            s.cpu = ticks

            var dRx: UInt64 = 0
            var dTx: UInt64 = 0
            if let prev = s.net {
                for (name, c) in counters {
                    guard let p = prev[name] else { continue }
                    dRx += UInt64(c.rx &- p.rx) // trừ có quay vòng cho bộ đếm 32 bit
                    dTx += UInt64(c.tx &- p.tx)
                }
            }
            s.net = counters
            s.netTotals.rx &+= dRx
            s.netTotals.tx &+= dTx
            var network: NetworkStats?
            if let last = s.lastDate {
                let dt = max(0.001, now.timeIntervalSince(last))
                network = NetworkStats(totalIn: s.netTotals.rx, totalOut: s.netTotals.tx, inPerSecond: Double(dRx) / dt, outPerSecond: Double(dTx) / dt)
            }
            s.lastDate = now
            return (cpu, network)
        }

        return SystemSnapshot(date: now, cpu: cpu, memory: SystemMetrics.memory(), disk: SystemMetrics.disk(at: diskURL),
                              battery: SystemMetrics.battery(), network: network, trashBytes: trashBytes, uptime: SystemMetrics.uptime)
    }
}

/// Theo dõi memory pressure bằng `DispatchSource.makeMemoryPressureSource` (mục 11.9).
public final class MemoryPressureMonitor: @unchecked Sendable {
    private let source: any DispatchSourceMemoryPressure

    public init(queue: DispatchQueue = .global(qos: .utility), handler: @escaping @Sendable (MemoryPressureLevel) -> Void) {
        source = DispatchSource.makeMemoryPressureSource(eventMask: [.normal, .warning, .critical], queue: queue)
        let src = source
        source.setEventHandler {
            let event = src.data
            let level: MemoryPressureLevel = event.contains(.critical) ? .critical : event.contains(.warning) ? .warning : .normal
            handler(level)
        }
    }

    public func start() { source.resume() }
    public func cancel() { source.cancel() }
}
