import AppKit
import Foundation
import os
import SweepCore
import SweepLogging
import UserNotifications

/// Loại cảnh báo của menu bar; mỗi loại tối đa 1 lần / 24 giờ (mục 11.9).
public enum MenuBarAlert: String, Sendable, CaseIterable {
    case lowDisk
    case largeTrash
    case memoryPressure

    var title: String {
        switch self {
        case .lowDisk: String(localized: "Ổ đĩa sắp đầy")
        case .largeTrash: String(localized: "Thùng rác đang chiếm nhiều dung lượng")
        case .memoryPressure: String(localized: "Máy đang thiếu bộ nhớ")
        }
    }
}

/// Gửi thông báo qua `UNUserNotificationCenter`; bấm thông báo thì mở URL đính kèm (mục 23.7).
public final class AlertNotifier: NSObject, UNUserNotificationCenterDelegate, @unchecked Sendable {
    public static let shared = AlertNotifier()
    private static let urlKey = "url"

    /// `UNUserNotificationCenter` chỉ dùng được khi chạy trong bundle app thật.
    private var isAvailable: Bool { Bundle.main.bundleIdentifier != nil && Bundle.main.bundleURL.pathExtension == "app" }

    /// Gọi sớm trong `applicationDidFinishLaunching` để nhận sự kiện bấm thông báo.
    public func activate() {
        guard isAvailable else { return }
        UNUserNotificationCenter.current().delegate = self
    }

    func requestAuthorizationIfNeeded() async -> Bool {
        guard isAvailable else { return false }
        let center = UNUserNotificationCenter.current()
        let settings = await center.notificationSettings()
        switch settings.authorizationStatus {
        case .authorized, .provisional: return true
        case .notDetermined: return (try? await center.requestAuthorization(options: [.alert, .sound])) ?? false
        default: return false
        }
    }

    func post(_ alert: MenuBarAlert, body: String, url: URL) async {
        guard await requestAuthorizationIfNeeded() else {
            Log.info(.menu, "menu", "Bỏ qua cảnh báo \(alert.rawValue): chưa có quyền thông báo")
            return
        }
        let content = UNMutableNotificationContent()
        content.title = alert.title
        content.body = body
        content.sound = .default
        content.userInfo = [Self.urlKey: url.absoluteString]
        let request = UNNotificationRequest(identifier: "com.cleanboost.alert.\(alert.rawValue)", content: content, trigger: nil)
        do {
            try await UNUserNotificationCenter.current().add(request)
            Log.info(.menu, "menu", "Đã gửi cảnh báo \(alert.rawValue)")
        } catch {
            Log.error(.menu, "menu", "Không gửi được cảnh báo \(alert.rawValue): \(error.localizedDescription)")
        }
    }

    // MARK: UNUserNotificationCenterDelegate

    public func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                       withCompletionHandler completionHandler: @escaping () -> Void) {
        let link = (response.notification.request.content.userInfo[Self.urlKey] as? String).flatMap(URL.init(string:))
        if let link {
            DispatchQueue.main.async { NSWorkspace.shared.open(link) }
        }
        completionHandler()
    }

    public func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                       withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound])
    }
}
