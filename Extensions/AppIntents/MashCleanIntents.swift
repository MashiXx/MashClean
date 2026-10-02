import AppIntents
import AppKit
import Foundation

/// App Intents extension: Shortcut "Dung lượng trống", "Dọn rác", "Smart Scan" (mục 3.1, 22.3).
/// Không import package để extension nhẹ; mở app chính qua URL scheme.
@main
struct MashCleanIntentsExtension: AppIntentsExtension {}

enum MashCleanLink {
    static func url(host: String, query: [String: String]) -> URL? {
        var components = URLComponents()
        components.scheme = "cleanboost"
        components.host = host
        components.queryItems = query.map { URLQueryItem(name: $0.key, value: $0.value) }
        return components.url
    }

    @MainActor
    static func open(host: String, query: [String: String]) throws {
        guard let url = url(host: host, query: query), NSWorkspace.shared.open(url) else {
            throw MashCleanIntentError.cannotOpenApp
        }
    }
}

enum MashCleanIntentError: Error, CustomLocalizedStringResourceConvertible {
    case cannotOpenApp

    var localizedStringResource: LocalizedStringResource {
        switch self {
        case .cannotOpenApp: "Không mở được Clean Boost"
        }
    }
}

/// "Dung lượng trống còn bao nhiêu" (mục 22.3).
struct FreeSpaceIntent: AppIntent {
    static let title: LocalizedStringResource = "Dung lượng trống"
    static let description = IntentDescription("Cho biết ổ khởi động còn trống bao nhiêu.")

    func perform() async throws -> some IntentResult & ReturnsValue<String> & ProvidesDialog {
        let values = try URL(fileURLWithPath: "/").resourceValues(forKeys: [
            .volumeAvailableCapacityForImportantUsageKey, .volumeAvailableCapacityKey,
        ])
        let bytes = values.volumeAvailableCapacityForImportantUsage ?? Int64(values.volumeAvailableCapacity ?? 0)
        let text = ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
        return .result(value: text, dialog: "Ổ khởi động còn trống \(text).")
    }
}

/// "Dọn rác": mở app chính ở màn System Junk và bắt đầu quét.
struct CleanJunkIntent: AppIntent {
    static let title: LocalizedStringResource = "Dọn rác"
    static let description = IntentDescription("Mở Clean Boost và quét rác hệ thống.")

    @MainActor
    func perform() async throws -> some IntentResult {
        try MashCleanLink.open(host: "scan", query: ["feature": "systemJunk"])
        return .result()
    }
}

/// "Smart Scan": mở app chính và chạy Smart Scan.
struct OpenSmartScanIntent: AppIntent {
    static let title: LocalizedStringResource = "Smart Scan"
    static let description = IntentDescription("Mở Clean Boost và chạy Smart Scan.")

    @MainActor
    func perform() async throws -> some IntentResult {
        try MashCleanLink.open(host: "scan", query: ["feature": "smartScan"])
        return .result()
    }
}

struct MashCleanShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: FreeSpaceIntent(),
            phrases: [
                "Dung lượng trống trong \(.applicationName)",
                "\(.applicationName) còn trống bao nhiêu",
            ],
            shortTitle: "Dung lượng trống",
            systemImageName: "internaldrive"
        )
        AppShortcut(
            intent: CleanJunkIntent(),
            phrases: [
                "Dọn rác bằng \(.applicationName)",
                "\(.applicationName) dọn rác",
            ],
            shortTitle: "Dọn rác",
            systemImageName: "trash"
        )
        AppShortcut(
            intent: OpenSmartScanIntent(),
            phrases: [
                "Smart Scan trong \(.applicationName)",
                "Quét máy bằng \(.applicationName)",
            ],
            shortTitle: "Smart Scan",
            systemImageName: "sparkles"
        )
    }
}
