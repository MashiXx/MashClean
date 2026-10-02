import AppKit
import Foundation
import os
import SweepCore
import SweepLogging

/// Quyền đọc/ghi thư mục do người dùng cấp cho bản Mac App Store (App Sandbox).
/// Người dùng chọn đúng thư mục trong `NSOpenPanel` một lần; app lưu security-scoped bookmark và mở lại mỗi lần khởi động.
/// Bản Developer ID không sandbox: mọi thư mục coi như đã có quyền.
public enum FolderAccess {
    public enum Folder: String, CaseIterable, Sendable {
        /// Home: cache, log, file lớn, trùng lặp, thùng rác, file sót của app.
        case home
        /// /Applications: gỡ cài đặt app.
        case applications

        public var url: URL {
            switch self {
            case .home: .userHome
            case .applications: URL(fileURLWithPath: "/Applications", isDirectory: true)
            }
        }

        var defaultsKey: String { "folderAccess.\(rawValue)" }
    }

    /// Thư mục đang mở quyền trong phiên hiện tại.
    private static let active = OSAllocatedUnfairLock(initialState: [Folder: URL]())

    /// Bookmark nằm trong defaults riêng của app (container), không dùng App Group vì bookmark gắn với app tạo ra nó.
    private static var defaults: UserDefaults { .standard }

    public static var isRequired: Bool { AppEdition.isSandboxed }

    public static func isGranted(_ folder: Folder) -> Bool {
        guard isRequired else { return true }
        return active.withLock { $0[folder] != nil }
    }

    public static var missingFolders: [Folder] { Folder.allCases.filter { !isGranted($0) } }

    /// Mở lại các bookmark đã lưu (gọi sớm khi khởi động, trước khi quét).
    public static func restore() {
        guard isRequired else { return }
        for folder in Folder.allCases where !isGranted(folder) {
            guard let data = defaults.data(forKey: folder.defaultsKey) else { continue }
            var stale = false
            guard let url = try? URL(resolvingBookmarkData: data, options: .withSecurityScope, relativeTo: nil, bookmarkDataIsStale: &stale),
                  url.startAccessingSecurityScopedResource() else {
                Log.info(.permissions, "permissions", "Bookmark \(folder.rawValue) không còn hiệu lực")
                defaults.removeObject(forKey: folder.defaultsKey)
                continue
            }
            if stale, let fresh = try? url.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil) {
                defaults.set(fresh, forKey: folder.defaultsKey)
            }
            active.withLock { $0[folder] = url }
        }
    }

    /// Hỏi người dùng cấp quyền một thư mục. Trả về true khi đã có quyền.
    @MainActor
    @discardableResult
    public static func request(_ folder: Folder) -> Bool {
        guard isRequired else { return true }
        if isGranted(folder) { return true }
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = false
        panel.directoryURL = folder.url
        panel.prompt = String(localized: "Cấp quyền")
        panel.message = switch folder {
        case .home: String(localized: "Chọn thư mục Home (\(folder.url.lastPathComponent)) để Clean Boost quét cache, log và file lớn của bạn.")
        case .applications: String(localized: "Chọn thư mục Applications để Clean Boost gỡ cài đặt ứng dụng.")
        }
        guard panel.runModal() == .OK, let url = panel.url else { return false }
        guard url.standardizedFileURL.path == folder.url.standardizedFileURL.path else {
            Log.info(.permissions, "permissions", "Người dùng chọn \(url.path) thay vì \(folder.url.path)")
            return false
        }
        do {
            let data = try url.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil)
            defaults.set(data, forKey: folder.defaultsKey)
        } catch {
            Log.error(.permissions, "permissions", "Không lưu được bookmark \(folder.rawValue): \(error)")
        }
        _ = url.startAccessingSecurityScopedResource()
        active.withLock { $0[folder] = url }
        return true
    }
}
