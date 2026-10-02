import Foundation
import SweepCore

/// Cài đặt dùng chung qua `UserDefaults(suiteName: "group.com.cleanboost.mac")` (mục 12.1).
public final class AppSettings: @unchecked Sendable {
    public static let shared = AppSettings()

    public let defaults: UserDefaults

    public init(suiteName: String = MashCleanIdentifiers.appGroup) {
        defaults = UserDefaults(suiteName: suiteName) ?? .standard
        defaults.register(defaults: [
            Keys.updateChannel: "stable",
            Keys.menuBarEnabled: true,
            Keys.analyticsEnabled: false,
            Keys.dryRun: false,
            Keys.showRiskyItems: false,
            Keys.lowDiskAlerts: true,
            Keys.trashAlertGB: 5,
            Keys.largeFileThresholdMB: 100,
            Keys.oldFileDays: 365,
            Keys.onboardingCompleted: false,
        ])
    }

    public enum Keys {
        public static let updateChannel = "updates.channel"
        public static let menuBarEnabled = "menuBar.enabled"
        public static let analyticsEnabled = "analytics.enabled"
        public static let dryRun = "debug.dryRun"
        public static let showRiskyItems = "scan.showRisky"
        public static let lowDiskAlerts = "alerts.lowDisk"
        public static let trashAlertGB = "alerts.trashGB"
        public static let largeFileThresholdMB = "largeFiles.thresholdMB"
        public static let oldFileDays = "largeFiles.oldDays"
        public static let onboardingCompleted = "onboarding.completed"
        public static let fdaSkipped = "onboarding.fdaSkipped"
        public static let lastAlert = "alerts.last."
    }

    /// Kênh cập nhật `stable` hoặc `beta` (mục 14.1).
    public var updateChannel: String {
        get { defaults.string(forKey: Keys.updateChannel) ?? "stable" }
        set { defaults.set(newValue, forKey: Keys.updateChannel); notify() }
    }

    public var menuBarEnabled: Bool {
        get { defaults.bool(forKey: Keys.menuBarEnabled) }
        set { defaults.set(newValue, forKey: Keys.menuBarEnabled); notify() }
    }

    /// Thống kê ẩn danh: tắt mặc định (mục 17).
    public var analyticsEnabled: Bool {
        get { defaults.bool(forKey: Keys.analyticsEnabled) }
        set { defaults.set(newValue, forKey: Keys.analyticsEnabled); notify() }
    }

    /// Chế độ thử (mục 15.4). Bật bằng biến môi trường `MASHCLEAN_DRY_RUN=1`, `--dry-run`, hoặc menu Debug.
    public var dryRun: Bool {
        get { defaults.bool(forKey: Keys.dryRun) || DryRun.isEnabledByEnvironment }
        set { defaults.set(newValue, forKey: Keys.dryRun); notify() }
    }

    public var showRiskyItems: Bool {
        get { defaults.bool(forKey: Keys.showRiskyItems) }
        set { defaults.set(newValue, forKey: Keys.showRiskyItems); notify() }
    }

    public var lowDiskAlerts: Bool {
        get { defaults.bool(forKey: Keys.lowDiskAlerts) }
        set { defaults.set(newValue, forKey: Keys.lowDiskAlerts); notify() }
    }

    public var trashAlertGB: Int {
        get { defaults.integer(forKey: Keys.trashAlertGB) }
        set { defaults.set(newValue, forKey: Keys.trashAlertGB); notify() }
    }

    public var largeFileThresholdMB: Int {
        get { defaults.integer(forKey: Keys.largeFileThresholdMB) }
        set { defaults.set(newValue, forKey: Keys.largeFileThresholdMB) }
    }

    public var oldFileDays: Int {
        get { defaults.integer(forKey: Keys.oldFileDays) }
        set { defaults.set(newValue, forKey: Keys.oldFileDays) }
    }

    public var onboardingCompleted: Bool {
        get { defaults.bool(forKey: Keys.onboardingCompleted) }
        set { defaults.set(newValue, forKey: Keys.onboardingCompleted) }
    }

    public var fdaSkipped: Bool {
        get { defaults.bool(forKey: Keys.fdaSkipped) }
        set { defaults.set(newValue, forKey: Keys.fdaSkipped) }
    }

    /// Mỗi loại cảnh báo tối đa 1 lần mỗi 24 giờ (mục 11.9).
    public func shouldAlert(_ kind: String, now: Date = Date(), interval: TimeInterval = 24 * 3600) -> Bool {
        guard let last = defaults.object(forKey: Keys.lastAlert + kind) as? Date else { return true }
        return now.timeIntervalSince(last) >= interval
    }

    public func markAlerted(_ kind: String, at date: Date = Date()) {
        defaults.set(date, forKey: Keys.lastAlert + kind)
    }

    private func notify() {
        DistributedNotificationCenter.default().postNotificationName(MashCleanIdentifiers.settingsChangedNotification, object: nil, userInfo: nil, deliverImmediately: true)
    }
}
