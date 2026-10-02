import AppKit
import Sparkle
import SweepCore
import SweepLogging
import SweepStorage
import SwiftUI

@main
struct MashCleanApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @StateObject private var holder = AppEnvironmentHolder()
    @StateObject private var router = AppRouter()
    private let updater = UpdaterController()

    var body: some Scene {
        WindowGroup("MashClean", id: "main") {
            RootView()
                .environmentObject(holder)
                .environmentObject(router)
                .frame(minWidth: 980, minHeight: 640)
                .onOpenURL { router.handle($0) }
                .task { await holder.startBackgroundWork(updater: updater) }
        }
        .windowToolbarStyle(.unifiedCompact(showsTitle: false))
        .handlesExternalEvents(matching: ["*"])
        .commands {
            CommandGroup(after: .appInfo) {
                Button(String(localized: "Kiểm tra cập nhật…")) { updater.checkForUpdates() }
                    .disabled(!updater.canCheckForUpdates)
            }
            CommandGroup(replacing: .newItem) {}
            CommandMenu(String(localized: "Quét")) {
                Button("Smart Scan") { router.open(.smartScan, autoStart: true) }.keyboardShortcut("1", modifiers: [.command])
                Button(String(localized: "Rác hệ thống")) { router.open(.systemJunk, autoStart: true) }.keyboardShortcut("2", modifiers: [.command])
                Button("Space Lens") { router.open(.spaceLens) }.keyboardShortcut("3", modifiers: [.command])
                Button(String(localized: "Gỡ cài đặt")) { router.open(.uninstaller) }.keyboardShortcut("4", modifiers: [.command])
            }
            DebugCommands(holder: holder)
            CommandGroup(replacing: .help) {
                Button(String(localized: "Gửi báo cáo lỗi…")) { router.open(.diagnostics) }
                Button(String(localized: "Mở thư mục log")) { NSWorkspace.shared.open(FileLog.shared.directory) }
            }
        }

        Settings {
            SettingsView()
                .environmentObject(holder)
                .environmentObject(router)
                .frame(width: 620, height: 520)
        }
    }
}

/// Menu Debug: chế độ thử (mục 15.4) và tiện ích cho QA khi review rule mới.
struct DebugCommands: Commands {
    @ObservedObject var holder: AppEnvironmentHolder

    var body: some Commands {
        CommandMenu("Debug") {
            Toggle(String(localized: "Chế độ thử (dry run)"), isOn: Binding(
                get: { holder.dryRun },
                set: { holder.setDryRun($0) }
            ))
            Button(String(localized: "Nạp lại rule")) { holder.reloadRules() }
            Button(String(localized: "Kiểm tra rule mới ngay")) { Task { await holder.checkRuleUpdates(force: true) } }
            Divider()
            Button(String(localized: "Mở thư mục dữ liệu")) {
                if let url = holder.environment?.storage?.url.deletingLastPathComponent() { NSWorkspace.shared.open(url) }
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationWillFinishLaunching(_ notification: Notification) {
        // Cấu hình ngôn ngữ của tiến trình lệch với lựa chọn trong App Group: ghi lại và mở lại để có hiệu lực.
        if AppLanguage.syncAtLaunch() {
            AppRelauncher.relaunch()
            exit(0)
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        MetricKitCollector.shared.start()
        Analytics.shared.isEnabled = { AppSettings.shared.analyticsEnabled }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

/// Bọc Sparkle 2 (mục 14.1). `Info.plist` cần `SUFeedURL` và `SUPublicEDKey`.
/// Kênh `stable`/`beta` chọn trong cài đặt: kênh beta thêm `allowedChannels`.
@MainActor
final class UpdaterController: NSObject, SPUUpdaterDelegate {
    private var controller: SPUStandardUpdaterController!

    override init() {
        super.init()
        let hasFeed = (Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") as? String).map { !$0.isEmpty } ?? false
        controller = SPUStandardUpdaterController(startingUpdater: hasFeed, updaterDelegate: self, userDriverDelegate: nil)
    }

    var canCheckForUpdates: Bool { controller.updater.canCheckForUpdates }

    func checkForUpdates() { controller.checkForUpdates(nil) }

    nonisolated func allowedChannels(for updater: SPUUpdater) -> Set<String> {
        AppSettings.shared.updateChannel == "beta" ? ["beta"] : []
    }
}
