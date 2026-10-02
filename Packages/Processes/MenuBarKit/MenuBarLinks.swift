import AppKit
import Foundation
import SweepCore
import SweepStorage

/// URL scheme mở app chính (mục 13): menu bar không chạy quét nặng, chỉ chuyển việc sang app.
public enum MenuBarLinks {
    public static let smartScan = URL(string: "\(MashCleanIdentifiers.urlScheme)://scan?feature=\(FeatureID.smartScan.rawValue)")!
    public static let systemJunk = URL(string: "\(MashCleanIdentifiers.urlScheme)://scan?feature=\(FeatureID.systemJunk.rawValue)")!
    public static let freeRAM = URL(string: "\(MashCleanIdentifiers.urlScheme)://maintenance?task=\(MaintenanceTaskName.freeRAM.rawValue)")!

    @MainActor
    public static func open(_ url: URL) {
        NSWorkspace.shared.open(url)
    }

    /// Mở app chính theo bundle id; nếu không tìm thấy thì thử app chứa menu bar (`.../Contents/Library/LoginItems/`).
    @MainActor
    public static func openMainApp() {
        let workspace = NSWorkspace.shared
        var appURL = workspace.urlForApplication(withBundleIdentifier: MashCleanIdentifiers.appBundleID)
        if appURL == nil {
            // MashClean.app/Contents/Library/LoginItems/MashCleanMenu.app → MashClean.app
            let candidate = Bundle.main.bundleURL.deletingLastPathComponent().deletingLastPathComponent()
                .deletingLastPathComponent().deletingLastPathComponent()
            if candidate.pathExtension == "app" { appURL = candidate }
        }
        guard let appURL else { return }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        workspace.openApplication(at: appURL, configuration: config) { _, _ in }
    }
}

/// Cách hiển thị trên thanh menu. Lưu trong App Group để app chính có thể đổi từ Cài đặt.
public enum StatusItemStyle: String, CaseIterable, Sendable, Identifiable {
    case iconOnly
    case cpu
    case memory
    case cpuAndMemory

    public static let defaultsKey = "menuBar.statusStyle"

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .iconOnly: "Chỉ biểu tượng"
        case .cpu: "Hiện % CPU"
        case .memory: "Hiện % RAM"
        case .cpuAndMemory: "Hiện % CPU và RAM"
        }
    }

    public static var current: StatusItemStyle {
        get { AppSettings.shared.defaults.string(forKey: defaultsKey).flatMap(StatusItemStyle.init(rawValue:)) ?? .iconOnly }
        set { AppSettings.shared.defaults.set(newValue.rawValue, forKey: defaultsKey) }
    }
}
