import AppCatalog
import CleanEngine
import Foundation
import NodeTree
import ScanEngine
import SweepCore
import UninstallerScanning

/// Feature Uninstaller (mục 11.3): màn riêng và thẻ "Ứng dụng" của Smart Scan (số app có file sót).
public struct UninstallerFeature: FeatureScanProvider {
    public let featureID: FeatureID = .uninstaller
    public let includeInSmartScan = true
    public let appScanner: AppScanner

    public init(appScanner: AppScanner) { self.appScanner = appScanner }

    /// Task của tab "File sót của app đã gỡ".
    public func scanTasks() -> [any ScanTask] { [InstalledAppsTask(scanner: appScanner), OrphanedLeftoversTask()] }

    public func smartScanTasks() -> [any ScanTask] { scanTasks() }

    public var taskIDs: Set<ScanTaskID> { [.orphanedLeftovers] }

    /// Thẻ "Ứng dụng": đếm số app đã gỡ còn để lại file.
    public func summarize(_ nodes: [Node]) -> FeatureSummary {
        let groups = nodes.filter { $0.category == UninstallerCategory.orphanedLeftovers }.flatMap { $0.isContainer ? $0.children : [$0] }
        let bytes = groups.sum(\.size)
        let names = groups.prefix(3).map { $0.title.components(separatedBy: " (").first ?? $0.title }
        return FeatureSummary(featureID: featureID, card: .applications, title: String(localized: "File sót của app đã gỡ"),
                              subtitle: groups.isEmpty ? String(localized: "Không có app nào để lại file sót") : "\(groups.count) app: " + names.joined(separator: ", "),
                              bytes: bytes, itemCount: groups.count)
    }

    /// Đăng ký remover riêng của Uninstaller vào CleanEngine (app chính gọi khi khởi động).
    public static func registerRemovers(in cleanEngine: CleanEngine) {
        cleanEngine.register(PackageForgetRemover())
    }
}

/// `pkgutil --forget <pkgid>` qua helper, sau khi đã xoá file của gói (mục 11.3 bước 6).
public struct PackageForgetRemover: Remover {
    public let id: RemoverID = .pkgForget
    public init() {}

    public func remove(_ items: [CleanPlan.Item], context: RemoveContext) async -> [RemoveResult] {
        var results: [RemoveResult] = []
        for item in items {
            guard case let .packageReceipt(pkgID)? = item.virtual else {
                results.append(.skipped(item.url?.path ?? item.title, .unsupported))
                continue
            }
            let label = "pkg:\(pkgID)"
            if context.dryRun {
                results.append(.skipped(label, .dryRun))
                continue
            }
            guard let helper = context.helper else {
                results.append(.failed(label, errno: EPERM, String(localized: "Cần helper để quên package receipt")))
                continue
            }
            do {
                let r = try await helper.forgetPackage(pkgID)
                results.append(r.success ? .ok(label, freed: 0) : .failed(label, errno: EIO, r.message))
            } catch {
                results.append(.failed(label, errno: EIO, String(describing: error)))
            }
        }
        return results
    }
}
