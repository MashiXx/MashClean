import AppKit
import Combine
import Foundation
import os
import SweepCore
import SweepLogging
import SweepStorage
import SystemMetrics

/// Một điểm trong biểu đồ mini.
public struct MetricPoint: Identifiable, Sendable, Hashable {
    public let id: Int
    public let value: Double
}

/// Lịch sử ~60 mẫu gần nhất của một chỉ số.
public struct MetricHistory: Sendable, Hashable {
    public private(set) var points: [MetricPoint] = []
    private var counter = 0
    public let capacity: Int

    public init(capacity: Int = 60) { self.capacity = capacity }

    public mutating func append(_ value: Double) {
        points.append(MetricPoint(id: counter, value: value))
        counter += 1
        if points.count > capacity { points.removeFirst(points.count - capacity) }
    }

    public var maxValue: Double { points.map(\.value).max() ?? 0 }
}

/// Ổ đĩa ngoài đang gắn, có thể tháo.
public struct ExternalVolume: Identifiable, Sendable, Hashable {
    public var id: String { url.path }
    public let url: URL
    public let name: String
    public let total: Int64
    public let available: Int64
}

/// Trạng thái menu bar (mục 11.9, 13, 23.7): lấy mẫu số liệu, cảnh báo, "Đã dọn tháng này".
/// Lấy mẫu trên hàng đợi nền; main thread chỉ cập nhật giá trị đã tính.
@MainActor
public final class MenuBarModel: ObservableObject {
    @Published public private(set) var snapshot = SystemSnapshot()
    @Published public private(set) var cpuHistory = MetricHistory()
    @Published public private(set) var memoryHistory = MetricHistory()
    @Published public private(set) var diskHistory = MetricHistory()
    @Published public private(set) var batteryHistory = MetricHistory()
    @Published public private(set) var networkInHistory = MetricHistory()
    @Published public private(set) var networkOutHistory = MetricHistory()
    @Published public private(set) var freedThisMonth: Int64 = 0
    @Published public private(set) var externalVolumes: [ExternalVolume] = []
    @Published public private(set) var trashUnreadable = false
    @Published public var ejectError: String?
    @Published public private(set) var ejecting: Set<String> = []
    @Published public var statusStyle: StatusItemStyle = .current {
        didSet {
            StatusItemStyle.current = statusStyle
            if statusStyle != oldValue, timer != nil { schedule() }
        }
    }

    /// Chu kỳ lấy mẫu: 2 giây khi popover mở, 30 giây khi đóng (mục 13). Khi thanh menu có hiện số thì
    /// cập nhật nhanh hơn để số không bị cũ: mạng 3 giây, CPU/RAM 5 giây.
    public static let openInterval: TimeInterval = 2
    public static let closedInterval: TimeInterval = 30
    public static let liveNetworkInterval: TimeInterval = 3
    public static let liveMetricsInterval: TimeInterval = 5
    private static let trashInterval: TimeInterval = 10 * 60
    private static let criticalPressureDuration: TimeInterval = 5 * 60

    public private(set) var isPopoverOpen = false

    private let sampler = SystemSampler()
    private let samplingQueue = DispatchQueue(label: "com.mashclean.menu.sampling", qos: .utility)
    private let freedReader = FreedStatsReader()
    private let settings = AppSettings.shared
    private var timer: Timer?
    private var observers: [(NotificationCenter, any NSObjectProtocol)] = []
    private var pressureMonitor: MemoryPressureMonitor?
    private var trashBytes: Int64?
    private var trashMeasuredAt: Date?
    private var measuringTrash = false
    private var criticalSince: Date?
    private var sampling = false

    public init() {}

    // MARK: Vòng đời

    public func start() {
        guard timer == nil else { return }
        observeNotifications()
        let monitor = MemoryPressureMonitor { [weak self] level in
            Task { @MainActor in self?.pressureChanged(level) }
        }
        monitor.start()
        pressureMonitor = monitor
        refreshVolumes()
        refreshFreed()
        tick()
        schedule()
    }

    public func stop() {
        timer?.invalidate()
        timer = nil
        pressureMonitor?.cancel()
        pressureMonitor = nil
        for (center, token) in observers { center.removeObserver(token) }
        observers.removeAll()
    }

    public func setPopoverOpen(_ open: Bool) {
        guard open != isPopoverOpen else { return }
        isPopoverOpen = open
        if open {
            tick()
            refreshVolumes()
            refreshFreed()
        }
        schedule()
    }

    private func schedule() {
        timer?.invalidate()
        let interval = isPopoverOpen ? Self.openInterval
            : statusStyle.contains(.network) ? Self.liveNetworkInterval
            : statusStyle.isEmpty ? Self.closedInterval : Self.liveMetricsInterval
        let t = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        t.tolerance = interval * 0.2 // cho hệ thống gom wake-up, tiết kiệm CPU/pin
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func observeNotifications() {
        let distributed = DistributedNotificationCenter.default()
        let didClean = distributed.addObserver(forName: MashCleanIdentifiers.didCleanNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.refreshFreed()
                self?.trashMeasuredAt = nil
            }
        }
        let settingsChanged = distributed.addObserver(forName: MashCleanIdentifiers.settingsChangedNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                let style = StatusItemStyle.current
                if style != self.statusStyle { self.statusStyle = style }
            }
        }
        observers.append((distributed, didClean))
        observers.append((distributed, settingsChanged))

        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.didMountNotification, NSWorkspace.didUnmountNotification, NSWorkspace.didRenameVolumeNotification] {
            let token = workspace.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.refreshVolumes() }
            }
            observers.append((workspace, token))
        }
    }

    // MARK: Lấy mẫu

    public func tick() {
        guard !sampling else { return }
        sampling = true
        let sampler = sampler
        let trash = trashBytes
        samplingQueue.async {
            let snap = sampler.sample(trashBytes: trash)
            Task { @MainActor [weak self] in self?.apply(snap) }
        }
        measureTrashIfNeeded()
    }

    private func apply(_ snap: SystemSnapshot) {
        sampling = false
        snapshot = snap
        if let cpu = snap.cpu { cpuHistory.append(cpu.usage) }
        if let memory = snap.memory { memoryHistory.append(memory.usedFraction) }
        if let disk = snap.disk { diskHistory.append(disk.usedFraction) }
        if snap.battery.hasBattery { batteryHistory.append(snap.battery.level) }
        if let net = snap.network {
            networkInHistory.append(net.inPerSecond)
            networkOutHistory.append(net.outPerSecond)
        }
        if let memory = snap.memory { pressureChanged(memory.pressure) }
        evaluateAlerts(snap)
    }

    private func measureTrashIfNeeded() {
        guard !measuringTrash else { return }
        if let at = trashMeasuredAt, Date().timeIntervalSince(at) < Self.trashInterval { return }
        measuringTrash = true
        DispatchQueue.global(qos: .background).async {
            let result: Int64? = try? SystemMetrics.trashSize()
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.measuringTrash = false
                self.trashMeasuredAt = Date()
                self.trashBytes = result
                self.trashUnreadable = result == nil
                self.snapshot.trashBytes = result
                if let result { self.evaluateTrash(result) }
            }
        }
    }

    // MARK: Cảnh báo (mục 11.9)

    private func pressureChanged(_ level: MemoryPressureLevel) {
        if level == .critical {
            if criticalSince == nil { criticalSince = Date() }
        } else {
            criticalSince = nil
        }
    }

    private func evaluateAlerts(_ snap: SystemSnapshot) {
        if settings.lowDiskAlerts, let disk = snap.disk, disk.total > 0,
           disk.freeFraction < 0.10 || disk.available < 10_000_000_000 {
            raise(.lowDisk, body: String(localized: "Chỉ còn \(ByteCount(disk.available).formatted) trống trên \(disk.name). Bấm để quét và dọn dẹp."))
        }
        if let since = criticalSince, Date().timeIntervalSince(since) >= Self.criticalPressureDuration {
            raise(.memoryPressure, body: String(localized: "Memory pressure ở mức nguy cấp hơn 5 phút. Hãy đóng bớt ứng dụng hoặc kiểm tra với MashClean."))
        }
    }

    private func evaluateTrash(_ bytes: Int64) {
        let limitGB = settings.trashAlertGB
        guard limitGB > 0, bytes > Int64(limitGB) * 1_000_000_000 else { return }
        raise(.largeTrash, body: String(localized: "Thùng rác đang chiếm \(ByteCount(bytes).formatted). Bấm để dọn dẹp."))
    }

    private func raise(_ alert: MenuBarAlert, body: String) {
        guard settings.shouldAlert(alert.rawValue) else { return }
        settings.markAlerted(alert.rawValue)
        Task { await AlertNotifier.shared.post(alert, body: body, url: MenuBarLinks.smartScan) }
    }

    // MARK: Đã dọn tháng này

    public func refreshFreed() {
        let reader = freedReader
        DispatchQueue.global(qos: .utility).async {
            let value = reader.freedThisMonth()
            Task { @MainActor [weak self] in
                if let value { self?.freedThisMonth = value }
            }
        }
    }

    // MARK: Ổ đĩa ngoài

    public func refreshVolumes() {
        DispatchQueue.global(qos: .utility).async {
            let volumes = Self.listExternalVolumes()
            Task { @MainActor [weak self] in self?.externalVolumes = volumes }
        }
    }

    nonisolated static func listExternalVolumes() -> [ExternalVolume] {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeIsEjectableKey, .volumeIsRemovableKey, .volumeIsInternalKey,
                                      .volumeIsRootFileSystemKey, .volumeTotalCapacityKey, .volumeAvailableCapacityKey,
                                      .volumeIsBrowsableKey]
        let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) ?? []
        return urls.compactMap { url in
            guard let v = try? url.resourceValues(forKeys: Set(keys)) else { return nil }
            if v.volumeIsRootFileSystem == true || v.volumeIsBrowsable == false { return nil }
            let external = v.volumeIsEjectable == true || v.volumeIsRemovable == true || v.volumeIsInternal == false
            guard external else { return nil }
            return ExternalVolume(url: url, name: v.volumeName ?? url.lastPathComponent,
                                  total: Int64(v.volumeTotalCapacity ?? 0), available: Int64(v.volumeAvailableCapacity ?? 0))
        }.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    public func eject(_ volume: ExternalVolume) {
        guard !ejecting.contains(volume.id) else { return }
        ejecting.insert(volume.id)
        ejectError = nil
        let url = volume.url
        let name = volume.name
        // unmountAndEjectDevice chặn tới khi tháo xong: chạy trên luồng nền.
        DispatchQueue.global(qos: .userInitiated).async {
            var failure: String?
            do {
                try NSWorkspace.shared.unmountAndEjectDevice(at: url)
            } catch {
                failure = String(localized: "Không tháo được \(name): \(error.localizedDescription)")
            }
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.ejecting.remove(url.path)
                self.ejectError = failure
                self.refreshVolumes()
            }
        }
    }

    // MARK: Chuỗi cho thanh menu

    /// Một chỉ số trên thanh menu: icon SF Symbol + giá trị, kèm tên đầy đủ cho tooltip.
    public struct StatusPart: Equatable, Sendable {
        public let symbol: String
        public let label: String
        public let value: String
    }

    public var statusParts: [StatusPart] {
        var parts: [StatusPart] = []
        if statusStyle.contains(.cpu) {
            parts.append(StatusPart(symbol: "cpu", label: "CPU", value: snapshot.cpu.map { "\(Int(($0.usage * 100).rounded()))%" } ?? "–"))
        }
        if statusStyle.contains(.memory) {
            parts.append(StatusPart(symbol: "memorychip", label: "RAM", value: snapshot.memory.map { "\(Int(($0.usedFraction * 100).rounded()))%" } ?? "–"))
        }
        if statusStyle.contains(.network) {
            let net = snapshot.network
            parts.append(StatusPart(symbol: "arrow.down", label: String(localized: "Tải về"), value: net.map { Self.compactRate($0.inPerSecond) } ?? "–"))
            parts.append(StatusPart(symbol: "arrow.up", label: String(localized: "Tải lên"), value: net.map { Self.compactRate($0.outPerSecond) } ?? "–"))
        }
        return parts
    }

    /// Tốc độ gọn cho thanh menu: "0 KB/s", "850 KB/s", "1,2 MB/s".
    static func compactRate(_ bytesPerSecond: Double) -> String {
        let kb = bytesPerSecond / 1000
        if kb < 1000 { return "\(Int(kb.rounded())) KB/s" }
        let mb = kb / 1000
        return mb < 100 ? String(format: "%.1f MB/s", mb).replacingOccurrences(of: ".", with: ",") : "\(Int(mb.rounded())) MB/s"
    }

    public var statusText: String? {
        let parts = statusParts
        return parts.isEmpty ? nil : parts.map { "\($0.label) \($0.value)" }.joined(separator: " · ")
    }
}

/// Đọc "Đã dọn tháng này" từ database dùng chung ở chế độ chỉ đọc (mục 12, 13).
/// Mở database lười: app chính có thể chưa tạo file lúc menu bar khởi động.
final class FreedStatsReader: Sendable {
    private let storage = Locked<Storage?>(nil)

    func freedThisMonth() -> Int64? {
        let db: Storage? = storage.withLock { current in
            if current == nil {
                let url = Storage.defaultURL
                if FileManager.default.fileExists(atPath: url.path) {
                    current = try? Storage(url: url, readOnly: true)
                }
            }
            return current
        }
        guard let db else { return nil }
        do {
            return try db.freedThisMonth()
        } catch {
            Log.warning(.menu, "menu", "Không đọc được thống kê dọn dẹp: \(error.localizedDescription)")
            return nil
        }
    }
}
