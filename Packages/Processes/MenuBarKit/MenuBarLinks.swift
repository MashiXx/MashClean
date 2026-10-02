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

/// Chỉ số hiện trên thanh menu, bật/tắt độc lập. Lưu trong App Group để app chính có thể đổi từ Cài đặt.
public struct StatusItemStyle: OptionSet, Hashable, Sendable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }

    public static let cpu = StatusItemStyle(rawValue: 1 << 0)
    public static let memory = StatusItemStyle(rawValue: 1 << 1)
    public static let network = StatusItemStyle(rawValue: 1 << 2)

    public static let defaultsKey = "menuBar.statusStyle"

    /// Lưu dạng `cpu,memory,network`; vẫn đọc được giá trị cũ (`iconOnly`, `cpu`, `memory`, `cpuAndMemory`).
    public init(storedValue: String) {
        var style: StatusItemStyle = []
        for part in storedValue.split(separator: ",") {
            switch part {
            case "cpu": style.insert(.cpu)
            case "memory": style.insert(.memory)
            case "network": style.insert(.network)
            case "cpuAndMemory": style.formUnion([.cpu, .memory])
            default: break
            }
        }
        self = style
    }

    public var storedValue: String {
        var parts: [String] = []
        if contains(.cpu) { parts.append("cpu") }
        if contains(.memory) { parts.append("memory") }
        if contains(.network) { parts.append("network") }
        return parts.isEmpty ? "iconOnly" : parts.joined(separator: ",")
    }

    public static var current: StatusItemStyle {
        get { AppSettings.shared.defaults.string(forKey: defaultsKey).map(StatusItemStyle.init(storedValue:)) ?? [] }
        set { AppSettings.shared.defaults.set(newValue.storedValue, forKey: defaultsKey) }
    }
}
