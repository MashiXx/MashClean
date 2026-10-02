import AppCatalog
import Foundation
import RuleEngine
import ScanEngine

/// Danh sách task của System Junk (mục 11.2).
public enum SystemJunkTasks {
    public static let providers: [any JunkProvider] = [
        UnavailableSimulatorsProvider(), UnusedSimulatorRuntimesProvider(), DockerProvider(),
        LanguageFilesProvider(), IOSBackupsProvider(), TrashProvider(),
    ]

    public struct Spec: Sendable {
        public let id: ScanTaskID
        public let category: String
        public let title: String
        public let weight: Double
        public let needsApps: Bool
        public let groupByApp: Bool
        /// Có chạy trong Smart Scan không (nhóm `risky` như gói ngôn ngữ thì không).
        public let inSmartScan: Bool
    }

    public static let specs: [Spec] = [
        Spec(id: "userCaches", category: RuleCategory.userCaches, title: String(localized: "Cache người dùng"), weight: 4, needsApps: true, groupByApp: true, inSmartScan: true),
        Spec(id: "systemCaches", category: RuleCategory.systemCaches, title: String(localized: "Cache hệ thống"), weight: 2, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "userLogs", category: RuleCategory.userLogs, title: String(localized: "Log người dùng"), weight: 1, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "systemLogs", category: RuleCategory.systemLogs, title: String(localized: "Log hệ thống"), weight: 1, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "crashReports", category: RuleCategory.crashReports, title: String(localized: "Báo cáo crash"), weight: 0.5, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "xcodeJunk", category: RuleCategory.xcodeJunk, title: String(localized: "Rác Xcode"), weight: 3, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "devToolCaches", category: RuleCategory.devToolCaches, title: String(localized: "Cache công cụ lập trình"), weight: 3, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "languageFiles", category: RuleCategory.languageFiles, title: String(localized: "Gói ngôn ngữ"), weight: 2, needsApps: true, groupByApp: false, inSmartScan: false),
        Spec(id: "iosBackups", category: RuleCategory.iosBackups, title: String(localized: "Bản sao lưu iOS"), weight: 1, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "iosSoftwareUpdates", category: RuleCategory.iosSoftwareUpdates, title: String(localized: "Bản cài iOS cũ"), weight: 0.5, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "trash", category: RuleCategory.trash, title: String(localized: "Thùng rác"), weight: 1, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "oldDownloads", category: RuleCategory.oldDownloads, title: String(localized: "Bản tải về cũ"), weight: 0.5, needsApps: false, groupByApp: false, inSmartScan: true),
        Spec(id: "mailAttachments", category: RuleCategory.mailAttachments, title: String(localized: "Tệp đính kèm Mail"), weight: 0.5, needsApps: false, groupByApp: false, inSmartScan: true),
    ]

    public static func tasks(appScanner: AppScanner, smartScanOnly: Bool = false) -> [any ScanTask] {
        var tasks: [any ScanTask] = [InstalledAppsTask(scanner: appScanner)]
        for spec in specs where !smartScanOnly || spec.inSmartScan {
            tasks.append(RuleCategoryTask(id: spec.id, category: spec.category, title: spec.title, weight: spec.weight,
                                          needsInstalledApps: spec.needsApps, groupByApp: spec.groupByApp, providers: providers))
        }
        return tasks
    }

    public static var taskIDs: Set<ScanTaskID> { Set(specs.map(\.id)) }
}
