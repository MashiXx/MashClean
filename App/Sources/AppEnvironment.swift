import AppCatalog
import CleanEngine
import DuplicatesDomain
import FileSystemKit
import Foundation
import LargeOldFilesDomain
import LoginItemsDomain
import MaintenanceDomain
import os
import RuleEngine
import ScanEngine
import SharedUI
import SmartScanDomain
import SmartScanUI
import SpaceLensDomain
import SweepCore
import SweepIPC
import SweepLogging
import SweepPermissions
import SweepStorage
import SystemJunkDomain
import UninstallerDomain

/// Composition root (mục 4.3): container phụ thuộc tự viết, không cần thư viện DI.
@MainActor
final class AppEnvironment {
    let storage: Storage?
    let ruleStore: RuleStore
    let fileSystem: FileSystemService
    let helper: HelperClient
    let scanEngine: ScanEngine
    let cleanEngine: CleanEngine
    let appScanner: AppScanner
    let services: ScanServices
    let ruleUpdater: RuleUpdater
    let settings: AppSettings

    let systemJunk: SystemJunkFeature
    let uninstaller: UninstallerFeature
    let maintenance: MaintenanceFeature
    let loginItems: LoginItemsFeature
    let largeOldFiles: LargeOldFilesFeature
    let duplicates: DuplicatesFeature
    let spaceLens: SpaceLensFeature
    let smartScan: SmartScanFeature
    let features: [any FeatureScanProvider]

    /// Chỉ việc bắt buộc (database, rule đi kèm) chạy trước khi hiện UI (mục 23.1).
    init() throws {
        settings = AppSettings.shared
        do {
            storage = try Storage(url: Storage.defaultURL)
        } catch {
            Log.error(.storage, "storage", "Không mở được database: \(error)")
            storage = nil
        }
        ruleStore = try RuleStore(bundled: RuleStore.defaultBundledURL, cache: RuleStore.defaultCacheDirectory)
        fileSystem = FileSystemService()
        helper = HelperClient(machServiceName: MashCleanIdentifiers.helperLabel)
        scanEngine = ScanEngine(fileSystem: fileSystem)
        cleanEngine = CleanEngine(fileSystem: fileSystem, helper: helper, storage: storage)
        let store = ruleStore
        appScanner = AppScanner(fileSystem: fileSystem, storage: storage, knowledge: { store.snapshot.knowledge })
        services = ScanServices(fileSystem: fileSystem, scanEngine: scanEngine, cleanEngine: cleanEngine, ruleStore: ruleStore,
                                storage: storage, settings: settings, appScanner: appScanner)
        ruleUpdater = RuleUpdater(store: ruleStore, manifestURL: RuleUpdater.manifestURL(channel: settings.updateChannel))

        systemJunk = SystemJunkFeature(appScanner: appScanner)
        uninstaller = UninstallerFeature(appScanner: appScanner)
        maintenance = MaintenanceFeature(helper: helper, storage: storage)
        loginItems = LoginItemsFeature(appScanner: appScanner)
        largeOldFiles = LargeOldFilesFeature()
        duplicates = DuplicatesFeature()
        spaceLens = SpaceLensFeature()
        features = [systemJunk, maintenance, uninstaller, loginItems, largeOldFiles, duplicates, spaceLens]
        smartScan = SmartScanFeature(providers: features)

        maintenance.registerRemovers(in: cleanEngine)
        UninstallerFeature.registerRemovers(in: cleanEngine)
    }
}

/// Giữ environment và các view model sống suốt vòng đời app.
@MainActor
final class AppEnvironmentHolder: ObservableObject {
    @Published private(set) var environment: AppEnvironment?
    @Published private(set) var startupError: String?
    @Published var hasFullDiskAccess = Permissions.hasFullDiskAccess()
    @Published var helperStatus: HelperStatus = HelperInstaller.status
    @Published var showOnboarding = !AppSettings.shared.onboardingCompleted
    @Published var dryRun = AppSettings.shared.dryRun
    @Published var rulesVersion: UInt64 = 0
    @Published var lastRuleUpdate: String?
    private(set) var smartScanModel: SmartScanViewModel?
    private var started = false

    init() {
        do {
            let env = try AppEnvironment()
            environment = env
            smartScanModel = SmartScanViewModel(services: env.services, feature: env.smartScan)
            rulesVersion = env.ruleStore.snapshot.version
        } catch {
            startupError = String(describing: error)
            Log.error(.ui, "app", "Khởi tạo thất bại: \(error)")
        }
    }

    /// Việc cần mạng hoặc XPC chạy sau khi UI hiện, song song, không chặn UI (mục 23.1).
    func startBackgroundWork(updater: UpdaterController) async {
        guard !started, let env = environment else { return }
        started = true
        Log.info(.ui, "app", "Khởi động MashClean \(AppVersion.current), rule \(rulesVersion)")

        // Dọn dữ liệu cũ: clean_item_log 90 ngày, scan_session 1 năm (mục 12.2).
        if let storage = env.storage {
            Task.detached(priority: .utility) { try? storage.pruneOldData() }
        }
        hasFullDiskAccess = Permissions.hasFullDiskAccess()
        if AppSettings.shared.menuBarEnabled, AppSettings.shared.onboardingCompleted { MenuBarLoginItem.setEnabled(true) }

        async let helperCheck: HelperStatus = env.helper.ensureCompatible()
        async let ruleCheck: Void = checkRuleUpdates(force: false)
        helperStatus = await helperCheck
        _ = await ruleCheck
        await Analytics.shared.flush()
    }

    func checkRuleUpdates(force: Bool) async {
        guard let env = environment else { return }
        let outcome = await env.ruleUpdater.checkIfNeeded(force: force)
        rulesVersion = env.ruleStore.snapshot.version
        switch outcome {
        case let .installed(v): lastRuleUpdate = String(localized: "Đã cập nhật rule lên \(v)")
        case .upToDate: lastRuleUpdate = String(localized: "Rule đang là bản mới nhất")
        case .notConfigured: lastRuleUpdate = String(localized: "Chưa cấu hình máy chủ rule")
        case .skippedRecentlyChecked: break
        case let .appTooOld(v): lastRuleUpdate = String(localized: "Bộ rule mới cần app \(v)")
        case let .failed(m): lastRuleUpdate = String(localized: "Lỗi cập nhật rule: \(m)")
        }
    }

    func reloadRules() {
        guard let env = environment else { return }
        // RuleStore nạp lại khi tạo mới; ở đây chỉ đọc lại phiên bản đang dùng.
        rulesVersion = env.ruleStore.snapshot.version
    }

    func setDryRun(_ on: Bool) {
        AppSettings.shared.dryRun = on
        dryRun = AppSettings.shared.dryRun
    }

    func refreshPermissions() {
        hasFullDiskAccess = Permissions.hasFullDiskAccess()
        helperStatus = HelperInstaller.status
    }
}
