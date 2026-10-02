import AppKit
import Foundation
import SweepCore

/// Ngôn ngữ giao diện. Lưu trong App Group để app chính và menu bar dùng chung.
/// Áp dụng bằng cách ghi `AppleLanguages` vào domain của từng tiến trình; có hiệu lực từ lần mở kế tiếp.
public enum AppLanguage: String, CaseIterable, Sendable, Identifiable {
    case system
    case vietnamese = "vi"
    case english = "en"

    public var id: String { rawValue }

    /// Tên luôn hiển thị song ngữ để người dùng nhận ra dù đang ở ngôn ngữ nào.
    public var displayName: String {
        switch self {
        case .system: "Theo hệ thống · System"
        case .vietnamese: "Tiếng Việt"
        case .english: "English"
        }
    }

    static let key = "app.language"

    /// Domain của các tiến trình có giao diện.
    static let bundleIDs = [MashCleanIdentifiers.appBundleID, MashCleanIdentifiers.menuBundleID]

    public static var current: AppLanguage {
        AppSettings.shared.defaults.string(forKey: key).flatMap(AppLanguage.init(rawValue:)) ?? .system
    }

    /// Ngôn ngữ đang thực sự hiển thị trong tiến trình này.
    public static var active: String {
        Bundle.main.preferredLocalizations.first ?? "en"
    }

    /// Lưu lựa chọn và ghi `AppleLanguages` cho app chính và menu bar.
    public static func select(_ language: AppLanguage) {
        AppSettings.shared.defaults.set(language.rawValue, forKey: key)
        for id in bundleIDs { write(language, to: id) }
        DistributedNotificationCenter.default().postNotificationName(MashCleanIdentifiers.settingsChangedNotification, object: nil, userInfo: nil, deliverImmediately: true)
    }

    /// Gọi sớm lúc khởi động: nếu lựa chọn trong App Group khác cấu hình của tiến trình này (vd đổi từ app chính
    /// khi menu bar chưa chạy) thì ghi lại. Trả `true` nếu cần mở lại để có hiệu lực.
    @discardableResult
    public static func syncAtLaunch(bundleID: String = Bundle.main.bundleIdentifier ?? MashCleanIdentifiers.appBundleID) -> Bool {
        // Tiến trình vừa được mở lại thì không đồng bộ nữa, tránh vòng mở lại liên tục.
        guard !ProcessInfo.processInfo.arguments.contains(AppRelauncher.relaunchedFlag) else { return false }
        let wanted = current
        // Chỉ đọc domain riêng của app: CFPreferencesCopyAppValue còn trả về ngôn ngữ chung của máy (NSGlobalDomain)
        // khi app chưa đặt gì, khiến lần mở nào cũng tưởng bị lệch và mở lại.
        let stored = CFPreferencesCopyValue("AppleLanguages" as CFString, bundleID as CFString,
                                            kCFPreferencesCurrentUser, kCFPreferencesAnyHost) as? [String]
        let expected = wanted == .system ? nil : [wanted.rawValue]
        guard stored != expected else { return false }
        write(wanted, to: bundleID)
        return true
    }

    private static func write(_ language: AppLanguage, to bundleID: String) {
        let value: CFPropertyList? = language == .system ? nil : [language.rawValue] as CFArray
        CFPreferencesSetAppValue("AppleLanguages" as CFString, value, bundleID as CFString)
        CFPreferencesAppSynchronize(bundleID as CFString)
    }
}

public enum AppRelauncher {
    public static let relaunchedFlag = "--relaunched"

    /// Áp dụng ngôn ngữ mới: đóng tiến trình MashClean còn lại (app chính hoặc menu bar) nếu đang chạy,
    /// rồi mở lại tất cả. Gọi được từ cả app chính lẫn menu bar.
    @MainActor
    public static func restartForLanguageChange() {
        var bundles = [Bundle.main.bundleURL]
        for id in AppLanguage.bundleIDs where id != Bundle.main.bundleIdentifier {
            for app in NSRunningApplication.runningApplications(withBundleIdentifier: id) {
                if let url = app.bundleURL, !bundles.contains(url) { bundles.append(url) }
                app.terminate()
            }
        }
        relaunch(bundleURLs: bundles)
        NSApp.terminate(nil)
    }

    /// Mở lại các bundle sau khi tiến trình hiện tại thoát.
    public static func relaunch(bundleURLs: [URL] = [Bundle.main.bundleURL], delay: Double = 1) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/sh")
        // Đường dẫn bundle đi qua tham số vị trí, không ghép vào chuỗi lệnh.
        let script = "sleep \(delay); for b in \"$@\"; do /usr/bin/open -n \"$b\" --args \(relaunchedFlag); done"
        process.arguments = ["-c", script, "relaunch"] + bundleURLs.map(\.path)
        try? process.run()
    }
}
