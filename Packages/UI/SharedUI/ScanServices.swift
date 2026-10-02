import AppCatalog
import CleanEngine
import FileSystemKit
import Foundation
import NodeTree
import os
import RuleEngine
import ScanEngine
import SweepCore
import SweepLogging
import SweepPermissions
import SweepStorage

/// Gói phụ thuộc mà màn tính năng cần (lấy từ composition root `AppEnvironment`).
public struct ScanServices: Sendable {
    public let fileSystem: FileSystemService
    public let scanEngine: ScanEngine
    public let cleanEngine: CleanEngine
    public let ruleStore: RuleStore
    public let storage: Storage?
    public let settings: AppSettings
    public let appScanner: AppScanner

    public init(fileSystem: FileSystemService, scanEngine: ScanEngine, cleanEngine: CleanEngine, ruleStore: RuleStore,
                storage: Storage?, settings: AppSettings, appScanner: AppScanner) {
        self.fileSystem = fileSystem
        self.scanEngine = scanEngine
        self.cleanEngine = cleanEngine
        self.ruleStore = ruleStore
        self.storage = storage
        self.settings = settings
        self.appScanner = appScanner
    }

    public var rules: RuleSnapshot { ruleStore.snapshot }

    /// Môi trường cho một phiên quét: chính sách, danh sách bỏ qua, app đang chạy, quyền FDA.
    @MainActor
    public func makeEnvironment() -> ScanEnvironment {
        ScanEnvironment(
            policy: .user(home: fileSystem.home),
            ignore: (try? storage?.ignoreList()) ?? .empty,
            runningBundleIDs: RunningApps.bundleIDs,
            hasFullDiskAccess: Permissions.hasFullDiskAccess(home: fileSystem.home.path),
            showRiskyItems: settings.showRiskyItems
        )
    }

    /// Thêm vào danh sách "không bao giờ đề xuất".
    public func ignore(_ node: Node) {
        if let path = node.url?.path { try? storage?.addIgnore(kind: .path, value: path) }
    }

    public func ignoreRule(_ ruleID: RuleID) {
        try? storage?.addIgnore(kind: .rule, value: ruleID.rawValue)
    }
}
