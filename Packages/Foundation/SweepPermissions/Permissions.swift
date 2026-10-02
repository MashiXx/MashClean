import AppKit
import Carbon
import Foundation
import os
import ServiceManagement
import SweepCore
import SweepLogging
import UserNotifications

/// Kiểm tra và xin quyền (mục 10).
public enum Permissions {
    // MARK: Full Disk Access

    /// Thử đọc metadata một đường dẫn được TCC bảo vệ; lỗi `EPERM` nghĩa là chưa có quyền (mục 10.1).
    public static func hasFullDiskAccess(home: String = NSHomeDirectory()) -> Bool {
        let probes = [
            "\(home)/Library/Safari/Bookmarks.plist",
            "/Library/Application Support/com.apple.TCC/TCC.db",
            "\(home)/Library/Safari",
            "\(home)/Library/Mail",
        ]
        var sawPermissionError = false
        for path in probes {
            let fd = Darwin.open(path, O_RDONLY)
            if fd >= 0 {
                close(fd)
                return true
            }
            if errno == EPERM || errno == EACCES { sawPermissionError = true }
        }
        // Không có file nào để thử (máy chưa từng mở Safari/Mail): dùng thư mục được bảo vệ khác.
        if !sawPermissionError {
            let dir = "\(home)/Library/Containers/com.apple.stocks"
            if (try? FileManager.default.contentsOfDirectory(atPath: dir)) != nil { return true }
        }
        return false
    }

    public static func openFullDiskAccessSettings() {
        openURL("x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")
    }

    // MARK: Login Items / Background

    public static func openLoginItemsSettings() {
        SMAppService.openSystemSettingsLoginItems()
    }

    // MARK: Automation

    /// Kiểm tra quyền gửi Apple Event tới app khác (chỉ khi thật sự cần, mục 10.1).
    public static func automationStatus(for bundleID: String, askIfNeeded: Bool = false) -> OSStatus {
        let target = NSAppleEventDescriptor(bundleIdentifier: bundleID)
        guard let desc = target.aeDesc else { return OSStatus(procNotFound) }
        return AEDeterminePermissionToAutomateTarget(desc, typeWildCard, typeWildCard, askIfNeeded)
    }

    // MARK: Notifications

    public static func notificationsAuthorized() async -> Bool {
        let settings = await UNUserNotificationCenter.current().notificationSettings()
        return settings.authorizationStatus == .authorized || settings.authorizationStatus == .provisional
    }

    @discardableResult
    public static func requestNotifications() async -> Bool {
        (try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound, .badge])) ?? false
    }

    // MARK: Tổng hợp

    /// Kết quả kiểm tra quyền cho báo cáo chẩn đoán.
    public static func summary() async -> [String: String] {
        [
            "Full Disk Access": hasFullDiskAccess() ? String(localized: "có") : String(localized: "chưa"),
            "Helper": helperStatusDescription,
            "Menu bar login item": SMAppService.loginItem(identifier: MashCleanIdentifiers.menuBundleID).status.description,
            String(localized: "Thông báo"): await notificationsAuthorized() ? String(localized: "có") : String(localized: "chưa"),
        ]
    }

    static var helperStatusDescription: String {
        SMAppService.daemon(plistName: MashCleanIdentifiers.helperPlistName).status.description
    }

    private static func openURL(_ url: String) {
        guard let u = URL(string: url) else { return }
        NSWorkspace.shared.open(u)
    }
}

extension SMAppService.Status: @retroactive CustomStringConvertible {
    public var description: String {
        switch self {
        case .enabled: String(localized: "đã bật")
        case .requiresApproval: String(localized: "cần duyệt")
        case .notRegistered: String(localized: "chưa đăng ký")
        case .notFound: String(localized: "không tìm thấy")
        @unknown default: String(localized: "không rõ")
        }
    }
}

/// Login item menu bar (mục 13): `SMAppService.loginItem(identifier: "com.cleanboost.mac.menu")`.
public enum MenuBarLoginItem {
    public static var service: SMAppService { SMAppService.loginItem(identifier: MashCleanIdentifiers.menuBundleID) }

    public static var isEnabled: Bool { service.status == .enabled }

    /// Bảo đảm menu bar đang chạy khi người dùng đã bật. Đăng ký login item gắn với chữ ký của bản đã cài;
    /// cài bản mới (nhất là bản ký ad-hoc) làm đăng ký cũ hỏng và launchd không bật được nữa (EX_CONFIG).
    /// Khi không thấy menu bar chạy: đăng ký lại cho đúng bản hiện tại rồi mở menu bar đi kèm app.
    @MainActor
    public static func ensureRunning(appBundle: URL = Bundle.main.bundleURL) async {
        guard NSRunningApplication.runningApplications(withBundleIdentifier: MashCleanIdentifiers.menuBundleID).isEmpty else { return }
        do {
            if service.status == .enabled { try await service.unregister() }
            try service.register()
        } catch {
            Log.error(.permissions, "permissions", "Không đăng ký lại được login item menu bar: \(error)")
        }
        let menuApp = appBundle.appendingPathComponent("Contents/Library/LoginItems/CleanBoostMenu.app")
        guard FileManager.default.fileExists(atPath: menuApp.path),
              NSRunningApplication.runningApplications(withBundleIdentifier: MashCleanIdentifiers.menuBundleID).isEmpty else { return }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = false
        config.addsToRecentItems = false
        _ = try? await NSWorkspace.shared.openApplication(at: menuApp, configuration: config)
        Log.info(.permissions, "permissions", "Đã mở lại menu bar")
    }

    public static func setEnabled(_ enabled: Bool) {
        do {
            if enabled {
                if service.status != .enabled { try service.register() }
            } else if service.status == .enabled {
                try service.unregister()
            }
        } catch {
            Log.error(.permissions, "permissions", "Không đổi được login item menu bar: \(error)")
        }
    }
}
