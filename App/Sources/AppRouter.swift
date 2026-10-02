import Foundation
import SweepCore
import SwiftUI

/// Mục điều hướng trên sidebar.
enum SidebarItem: String, CaseIterable, Identifiable, Hashable {
    case smartScan
    case systemJunk, largeOldFiles, duplicates
    case uninstaller, loginItems
    case maintenance
    case spaceLens
    case history, diagnostics

    var id: String { rawValue }

    var title: String {
        switch self {
        case .smartScan: "Smart Scan"
        case .systemJunk: "Rác hệ thống"
        case .largeOldFiles: "File lớn & cũ"
        case .duplicates: "File trùng lặp"
        case .uninstaller: "Gỡ cài đặt"
        case .loginItems: "Login Items"
        case .maintenance: "Bảo trì"
        case .spaceLens: "Space Lens"
        case .history: "Lịch sử"
        case .diagnostics: "Báo cáo lỗi"
        }
    }

    var symbol: String {
        switch self {
        case .smartScan: "sparkles"
        case .systemJunk: "trash.circle"
        case .largeOldFiles: "doc.badge.clock"
        case .duplicates: "doc.on.doc"
        case .uninstaller: "xmark.app"
        case .loginItems: "power.circle"
        case .maintenance: "wrench.and.screwdriver"
        case .spaceLens: "circle.hexagongrid"
        case .history: "clock.arrow.circlepath"
        case .diagnostics: "ladybug"
        }
    }

    static let sections: [(String, [SidebarItem])] = [
        ("", [.smartScan]),
        ("Dọn dẹp", [.systemJunk, .largeOldFiles, .duplicates]),
        ("Ứng dụng", [.uninstaller, .loginItems]),
        ("Tốc độ", [.maintenance]),
        ("Dung lượng", [.spaceLens]),
        ("Khác", [.history, .diagnostics]),
    ]

    init?(feature: FeatureID) {
        switch feature {
        case .smartScan: self = .smartScan
        case .systemJunk: self = .systemJunk
        case .uninstaller: self = .uninstaller
        case .spaceLens: self = .spaceLens
        case .maintenance: self = .maintenance
        case .loginItems: self = .loginItems
        case .largeOldFiles: self = .largeOldFiles
        case .duplicates: self = .duplicates
        default: return nil
        }
    }
}

/// Xử lý deep link `mashclean://` từ menu bar, FinderSync, App Intents và thông báo (mục 13, 22.3):
/// - `mashclean://scan?feature=smartScan|systemJunk`
/// - `mashclean://spacelens?path=/Users/...`
/// - `mashclean://uninstall?path=/Applications/Foo.app`
/// - `mashclean://maintenance?task=freeRAM`
@MainActor
final class AppRouter: ObservableObject {
    @Published var selection: SidebarItem = .smartScan
    /// Tăng mỗi lần cần tự bắt đầu quét màn hiện tại.
    @Published var autoStartToken = 0
    @Published var autoStartTarget: SidebarItem?
    @Published var spaceLensPath: String?
    @Published var uninstallPath: String?
    @Published var maintenanceTask: MaintenanceTaskName?
    /// Đổi id để dựng lại view khi deep link mang tham số mới.
    @Published var spaceLensID = UUID()
    @Published var uninstallerID = UUID()
    @Published var maintenanceID = UUID()

    func open(_ item: SidebarItem, autoStart: Bool = false) {
        selection = item
        if autoStart {
            autoStartTarget = item
            autoStartToken += 1
        }
        NSApp.activate(ignoringOtherApps: true)
    }

    func handle(_ url: URL) {
        guard url.scheme == MashCleanIdentifiers.urlScheme else { return }
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }

        switch url.host {
        case "scan":
            let feature = value("feature").map { FeatureID(rawValue: $0) } ?? .smartScan
            open(SidebarItem(feature: feature) ?? .smartScan, autoStart: true)
        case "spacelens":
            if let path = value("path"), FileManager.default.fileExists(atPath: path) {
                spaceLensPath = path
                spaceLensID = UUID()
            }
            open(.spaceLens)
        case "uninstall":
            if let path = value("path"), path.hasSuffix(".app") {
                uninstallPath = path
                uninstallerID = UUID()
            }
            open(.uninstaller)
        case "maintenance":
            maintenanceTask = value("task").flatMap(MaintenanceTaskName.init(rawValue:))
            maintenanceID = UUID()
            open(.maintenance)
        default:
            open(.smartScan)
        }
    }
}
