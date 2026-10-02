import AppKit
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
        case .systemJunk: String(localized: "Rác hệ thống")
        case .largeOldFiles: String(localized: "File lớn & cũ")
        case .duplicates: String(localized: "File trùng lặp")
        case .uninstaller: String(localized: "Gỡ cài đặt")
        case .loginItems: "Login Items"
        case .maintenance: String(localized: "Bảo trì")
        case .spaceLens: "Space Lens"
        case .history: String(localized: "Lịch sử")
        case .diagnostics: String(localized: "Báo cáo lỗi")
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
        (String(localized: "Dọn dẹp"), [.systemJunk, .largeOldFiles, .duplicates]),
        (String(localized: "Ứng dụng"), [.uninstaller, .loginItems]),
        (String(localized: "Tốc độ"), [.maintenance]),
        (String(localized: "Dung lượng"), [.spaceLens]),
        (String(localized: "Khác"), [.history, .diagnostics]),
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

/// Xử lý deep link `cleanboost://` từ menu bar, FinderSync, App Intents và thông báo (mục 13, 22.3):
/// - `cleanboost://scan?feature=smartScan|systemJunk`
/// - `cleanboost://spacelens?path=/Users/...`
/// - `cleanboost://uninstall?path=/Applications/Foo.app`
/// - `cleanboost://maintenance?task=freeRAM`
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
        #if DEBUG
        case "debug":
            // Chỉ bản Debug: vẽ cửa sổ chính ra PNG để kiểm tra giao diện khi không chụp được màn hình.
            if let target = value("target").flatMap(SidebarItem.init(rawValue:)) { open(target) }
            let path = value("path") ?? NSTemporaryDirectory() + "cleanboost-snapshot.png"
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { DebugSnapshot.write(to: path) }
            return
        #endif
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

#if DEBUG
@MainActor
enum DebugSnapshot {
    /// `CGWindowListCreateImage` bị đánh dấu không dùng được từ SDK macOS 15 nhưng vẫn có lúc chạy; app được chụp cửa sổ
    /// của chính mình mà không cần quyền Screen Recording, và ảnh giữ nguyên vibrancy, bóng đổ, sheet đang mở.
    private typealias WindowListCreateImage = @convention(c) (CGRect, UInt32, UInt32, UInt32) -> Unmanaged<CGImage>?

    static func write(to path: String) {
        guard let window = NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil && !($0 is NSPanel) }) else { return }
        if let symbol = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "CGWindowListCreateImage") {
            let create = unsafeBitCast(symbol, to: WindowListCreateImage.self)
            // optionIncludingWindow = 1 << 3; boundsIgnoreFraming = 1 << 0, bestResolution = 1 << 3
            if let image = create(.null, 1 << 3, UInt32(window.windowNumber), (1 << 0) | (1 << 3))?.takeRetainedValue(),
               let data = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) {
                try? data.write(to: URL(fileURLWithPath: path))
                return
            }
        }
        guard let view = window.contentView?.superview ?? window.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { return }
        view.cacheDisplay(in: view.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
    }
}
#endif
