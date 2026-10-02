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
        didSet { StatusItemStyle.current = statusStyle }
    }

    /// Chu kỳ lấy mẫu: 2 giây khi popover mở, 30 giây khi đóng (mục 13).
    public static let openInterval: TimeInterval = 2
    public static let closedInterval: TimeInterval = 30
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
        let interval = isPopoverOpen ? Self.openInterval : Self.closedInterval
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
            raise(.lowDisk, body: "Chỉ còn \(ByteCount(disk.available).formatted) trống trên \(disk.name). Bấm để quét và dọn dẹp.")
        }
        if let since = criticalSince, Date().timeIntervalSince(since) >= Self.criticalPressureDuration {
            raise(.memoryPressure, body: "Memory pressure ở mức nguy cấp hơn 5 phút. Hãy đóng bớt ứng dụng hoặc kiểm tra với MashClean.")
        }
    }

    private func evaluateTrash(_ bytes: Int64) {
        let limitGB = settings.trashAlertGB
        guard limitGB > 0, bytes > Int64(limitGB) * 1_000_000_000 else { return }
        raise(.largeTrash, body: "Thùng rác đang chiếm \(ByteCount(bytes).formatted). Bấm để dọn dẹp.")
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
                failure = "Không tháo được \(name): \(error.localizedDescription)"
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

    public var statusText: String? {
        let cpu = snapshot.cpu.map { "\(Int(($0.usage * 100).rounded()))%" } ?? "–"
        let ram = snapshot.memory.map { "\(Int(($0.usedFraction * 100).rounded()))%" } ?? "–"
        switch statusStyle {
        case .iconOnly: return nil
        case .cpu: return cpu
        case .memory: return ram
        case .cpuAndMemory: return "\(cpu) · \(ram)"
        }
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
