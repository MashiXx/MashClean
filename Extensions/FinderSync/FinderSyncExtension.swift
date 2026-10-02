import AppKit
import FinderSync

/// Menu chuột phải trong Finder (mục 3.1, 22.3). Extension chỉ mở URL scheme của app chính,
/// không import package để giữ nhẹ.
final class FinderSyncExtension: FIFinderSync {
    private static let scheme = "cleanboost"

    override init() {
        super.init()
        FIFinderSyncController.default().directoryURLs = [URL(fileURLWithPath: "/")]
    }

    override func menu(for menuKind: FIMenuKind) -> NSMenu? {
        guard menuKind == .contextualMenuForItems || menuKind == .contextualMenuForContainer else { return nil }
        let menu = NSMenu(title: "Clean Boost")
        let analyze = NSMenuItem(title: String(localized: "Phân tích bằng Clean Boost"), action: #selector(analyze(_:)), keyEquivalent: "")
        analyze.image = NSImage(systemSymbolName: "chart.pie", accessibilityDescription: nil)
        menu.addItem(analyze)

        if menuKind == .contextualMenuForItems, let items = FIFinderSyncController.default().selectedItemURLs(),
           items.count == 1, items[0].pathExtension.lowercased() == "app" {
            let uninstall = NSMenuItem(title: String(localized: "Gỡ bằng Clean Boost"), action: #selector(uninstall(_:)), keyEquivalent: "")
            uninstall.image = NSImage(systemSymbolName: "trash", accessibilityDescription: nil)
            menu.addItem(uninstall)
        }
        return menu
    }

    @IBAction func analyze(_ sender: AnyObject?) {
        let controller = FIFinderSyncController.default()
        // Chọn mục thì phân tích mục đó, bấm vào nền cửa sổ thì phân tích thư mục đang mở.
        guard let url = controller.selectedItemURLs()?.first ?? controller.targetedURL() else { return }
        open(host: "spacelens", path: url.path)
    }

    @IBAction func uninstall(_ sender: AnyObject?) {
        guard let url = FIFinderSyncController.default().selectedItemURLs()?.first, url.pathExtension.lowercased() == "app" else { return }
        open(host: "uninstall", path: url.path)
    }

    private func open(host: String, path: String) {
        var components = URLComponents()
        components.scheme = Self.scheme
        components.host = host
        components.queryItems = [URLQueryItem(name: "path", value: path)]
        guard let url = components.url else { return }
        NSWorkspace.shared.open(url)
    }
}
