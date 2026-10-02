import AppKit
import Combine
import SweepStorage
import SwiftUI
import SweepCore
import SweepLogging

/// `NSStatusItem` + `NSPopover` chứa `MenuBarPopoverView` (mục 13).
@MainActor
public final class MenuBarController: NSObject, NSPopoverDelegate {
    public let model: MenuBarModel
    private let statusItem: NSStatusItem
    private let popover = NSPopover()
    private var cancellables = Set<AnyCancellable>()
    private var lastTitle: String?
    private var eventMonitor: Any?

    public init(model: MenuBarModel) {
        self.model = model
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        super.init()

        if let button = statusItem.button {
            let image = NSImage(systemSymbolName: "sparkles", accessibilityDescription: "Clean Boost")
            image?.isTemplate = true
            button.image = image
            button.imagePosition = .imageLeading
            button.target = self
            button.action = #selector(togglePopover(_:))
            button.toolTip = "Clean Boost"
        }

        popover.behavior = .transient
        popover.animates = true
        popover.delegate = self
        let hosting = NSHostingController(rootView: MenuBarPopoverView(
            model: model,
            onAction: { [weak self] in self?.closePopover() },
            onQuit: { NSApp.terminate(nil) }
        ))
        // Popover cao theo nội dung (danh sách ổ ngoài, pin... thay đổi lúc chạy), không cắt phần đầu.
        hosting.sizingOptions = [.preferredContentSize]
        popover.contentViewController = hosting

        // Chỉ cập nhật tiêu đề khi chuỗi thực sự đổi, tránh vẽ lại thanh menu vô ích.
        model.$snapshot.combineLatest(model.$statusStyle)
            .receive(on: RunLoop.main)
            .sink { [weak self] _, _ in
                MainActor.assumeIsolated { self?.updateTitle() }
            }
            .store(in: &cancellables)
    }

    private func updateTitle() {
        let text = model.statusText
        guard text != lastTitle, let button = statusItem.button else { return }
        lastTitle = text
        button.attributedTitle = Self.statusTitle(model.statusParts)
        button.toolTip = text.map { "Clean Boost — \($0)" } ?? "Clean Boost"
    }

    /// Mỗi chỉ số có icon riêng đứng trước (CPU: `cpu`, RAM: `memorychip`) để biết số nào là gì.
    static func statusTitle(_ parts: [MenuBarModel.StatusPart]) -> NSAttributedString {
        let font = NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .medium)
        let result = NSMutableAttributedString()
        for part in parts {
            result.append(NSAttributedString(string: result.length == 0 ? " " : "  ", attributes: [.font: font]))
            let config = NSImage.SymbolConfiguration(pointSize: 11, weight: .medium)
            if let image = NSImage(systemSymbolName: part.symbol, accessibilityDescription: part.label)?.withSymbolConfiguration(config) {
                image.isTemplate = true
                let attachment = NSTextAttachment()
                attachment.image = image
                // Căn giữa icon theo chiều cao chữ số.
                attachment.bounds = CGRect(x: 0, y: (font.capHeight - image.size.height) / 2, width: image.size.width, height: image.size.height)
                result.append(NSAttributedString(attachment: attachment))
            }
            result.append(NSAttributedString(string: " " + part.value, attributes: [.font: font]))
        }
        return result
    }

    @objc private func togglePopover(_ sender: Any?) {
        if popover.isShown {
            closePopover()
        } else {
            guard let button = statusItem.button else { return }
            popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
            popover.contentViewController?.view.window?.makeKey()
        }
    }

    public func closePopover() {
        popover.performClose(nil)
    }

    // MARK: NSPopoverDelegate

    public func popoverDidShow(_ notification: Notification) {
        model.setPopoverOpen(true)
    }

    public func popoverDidClose(_ notification: Notification) {
        model.setPopoverOpen(false)
    }
}

/// App delegate cho tiến trình menu bar (`LSUIElement`).
@MainActor
public final class MenuBarAppDelegate: NSObject, NSApplicationDelegate {
    private var model: MenuBarModel?
    private var controller: MenuBarController?

    override public init() {
        super.init()
    }

    public func applicationDidFinishLaunching(_ notification: Notification) {
        // Ngôn ngữ đổi từ app chính lúc menu bar chưa chạy: ghi lại cấu hình rồi mở lại cho có hiệu lực.
        if AppLanguage.syncAtLaunch() {
            AppRelauncher.relaunch()
            NSApp.terminate(nil)
            return
        }
        AlertNotifier.shared.activate()
        let model = MenuBarModel()
        controller = MenuBarController(model: model)
        model.start()
        self.model = model
        Log.info(.menu, "menu", "Menu bar khởi động")
    }

    public func applicationWillTerminate(_ notification: Notification) {
        model?.stop()
    }
}

/// Điểm vào: target Xcode `MenuBar` chỉ gọi `MenuBarMain.run()`.
public enum MenuBarMain {
    @MainActor
    public static func run() {
        let app = NSApplication.shared
        let delegate = MenuBarAppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(delegate) {
            app.run()
        }
    }
}
