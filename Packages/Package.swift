// swift-tools-version: 6.0
// Clean Boost: toàn bộ mã nguồn nằm trong package này (SYSTEM_DESIGN mục 4, 19.1).
// Xcode project (project.yml) chỉ chứa các target app, menu bar, helper và extension.

import PackageDescription

let swiftSettings: [SwiftSetting] = [
    .enableUpcomingFeature("ExistentialAny"),
]

func feature(_ name: String, scanningDeps: [Target.Dependency], domainDeps: [Target.Dependency] = [], uiDeps: [Target.Dependency] = []) -> [Target] {
    [
        .target(
            name: "\(name)Scanning",
            dependencies: scanningDeps,
            path: "Features/\(name)/\(name)Scanning",
            swiftSettings: swiftSettings
        ),
        .target(
            name: "\(name)Domain",
            dependencies: [.target(name: "\(name)Scanning")] + domainDeps,
            path: "Features/\(name)/\(name)Domain",
            swiftSettings: swiftSettings
        ),
        .target(
            name: "\(name)UI",
            dependencies: [.target(name: "\(name)Domain"), "DesignSystem", "SharedUI"] + uiDeps,
            path: "Features/\(name)/\(name)UI",
            swiftSettings: swiftSettings
        ),
    ]
}

let engine: [Target.Dependency] = ["SweepCore", "SweepLogging", "FileSystemKit", "NodeTree", "ScanEngine", "RuleEngine", "AppCatalog"]
let commonDomain: [Target.Dependency] = engine + ["CleanEngine", "SweepIPC", "SweepStorage"]
let commonUI: [Target.Dependency] = commonDomain + ["SweepPermissions"]

let package = Package(
    name: "MashCleanKit",
    defaultLocalization: "vi",
    platforms: [.macOS(.v13)],
    products: [
        // Dùng cho app chính
        .library(name: "MashCleanAppKit", targets: [
            "SweepCore", "SweepLogging", "SweepStorage", "SweepIPC", "SweepPermissions",
            "FileSystemKit", "NodeTree", "ScanEngine", "RuleEngine", "CleanEngine", "AppCatalog",
            "DesignSystem", "SharedUI",
            "SystemJunkUI", "UninstallerUI", "SpaceLensUI", "MaintenanceUI", "LoginItemsUI",
            "LargeOldFilesUI", "DuplicatesUI", "SmartScanUI",
        ]),
        // Dùng cho app menu bar: chỉ phần domain cần thiết (mục 3.2 nguyên tắc 3)
        .library(name: "MashCleanMenuKit", targets: ["MenuBarKit"]),
        // Helper chỉ link SweepIPC, SweepCore, SweepLogging và remover cấp thấp (mục 4.2)
        .library(name: "MashCleanHelperKit", targets: ["HelperCore"]),
        // Extension: chỉ phần lõi nhẹ
        .library(name: "MashCleanExtensionKit", targets: ["SweepCore"]),
        .executable(name: "rulepack", targets: ["rulepack"]),
    ],
    dependencies: [
        .package(url: "https://github.com/groue/GRDB.swift.git", from: "7.0.0"),
    ],
    targets: [
        // MARK: Foundation
        .target(name: "SweepCore", path: "Foundation/SweepCore", swiftSettings: swiftSettings),
        .target(name: "SweepLogging", dependencies: ["SweepCore"], path: "Foundation/SweepLogging", swiftSettings: swiftSettings),
        .target(
            name: "SweepStorage",
            dependencies: ["SweepCore", "SweepLogging", .product(name: "GRDB", package: "GRDB.swift")],
            path: "Foundation/SweepStorage",
            swiftSettings: swiftSettings
        ),
        .target(name: "SweepIPC", dependencies: ["SweepCore", "SweepLogging"], path: "Foundation/SweepIPC", swiftSettings: swiftSettings),
        .target(name: "SweepPermissions", dependencies: ["SweepCore", "SweepLogging"], path: "Foundation/SweepPermissions", swiftSettings: swiftSettings),

        // MARK: Engine
        .target(name: "CXXHash", path: "Vendor/CXXHash"),
        .target(name: "FileSystemKit", dependencies: ["SweepCore", "SweepLogging", "CXXHash"], path: "Engine/FileSystemKit", swiftSettings: swiftSettings),
        .target(name: "NodeTree", dependencies: ["SweepCore"], path: "Engine/NodeTree", swiftSettings: swiftSettings),
        .target(name: "RuleEngine", dependencies: ["SweepCore", "SweepLogging", "FileSystemKit"], path: "Engine/RuleEngine", swiftSettings: swiftSettings),
        .target(name: "AppCatalog", dependencies: ["SweepCore", "SweepLogging", "SweepStorage", "FileSystemKit", "RuleEngine", "ScanEngine"], path: "Engine/AppCatalog", swiftSettings: swiftSettings),
        .target(
            name: "ScanEngine",
            dependencies: ["SweepCore", "SweepLogging", "FileSystemKit", "NodeTree", "RuleEngine"],
            path: "Engine/ScanEngine",
            swiftSettings: swiftSettings
        ),
        .target(name: "CleanEngineCore", dependencies: ["SweepCore"], path: "Engine/CleanEngineCore", swiftSettings: swiftSettings),
        .target(
            name: "CleanEngine",
            dependencies: ["SweepCore", "SweepLogging", "SweepStorage", "SweepIPC", "FileSystemKit", "NodeTree", "CleanEngineCore"],
            path: "Engine/CleanEngine",
            swiftSettings: swiftSettings
        ),
        .target(name: "SystemMetrics", dependencies: ["SweepCore"], path: "Engine/SystemMetrics", swiftSettings: swiftSettings),

        // MARK: Tiến trình phụ (logic nằm trong package, target Xcode chỉ có entry point)
        .target(name: "HelperCore", dependencies: ["SweepCore", "SweepIPC", "SweepLogging", "CleanEngineCore"], path: "Processes/HelperCore", swiftSettings: swiftSettings),
        .target(
            name: "MenuBarKit",
            dependencies: ["SweepCore", "SweepLogging", "SweepStorage", "SweepPermissions", "FileSystemKit", "DesignSystem", "SystemMetrics"],
            path: "Processes/MenuBarKit",
            swiftSettings: swiftSettings
        ),

        // MARK: UI chung
        .target(name: "DesignSystem", dependencies: ["SweepCore"], path: "UI/DesignSystem", swiftSettings: swiftSettings),
        .target(
            name: "SharedUI",
            dependencies: ["DesignSystem", "SweepCore", "SweepLogging", "SweepStorage", "SweepPermissions", "NodeTree", "ScanEngine", "CleanEngine", "RuleEngine", "FileSystemKit", "AppCatalog"],
            path: "UI/SharedUI",
            swiftSettings: swiftSettings
        ),

        // MARK: Tools
        .executableTarget(name: "rulepack", dependencies: ["SweepCore", "RuleEngine", "FileSystemKit"], path: "Tools/rulepack", swiftSettings: swiftSettings),

        // MARK: Tests
        .testTarget(
            name: "MashCleanTests",
            dependencies: [
                "SweepCore", "SweepStorage", "FileSystemKit", "NodeTree", "ScanEngine", "RuleEngine", "CleanEngine", "CleanEngineCore",
                "AppCatalog", "SystemMetrics", "HelperCore", "SweepIPC",
                "SystemJunkScanning", "DuplicatesScanning", "SpaceLensScanning", "UninstallerScanning", "LoginItemsScanning",
                "LargeOldFilesScanning", "MaintenanceScanning", "SystemJunkScanning",
                .product(name: "GRDB", package: "GRDB.swift"),
            ],
            path: "Tests/MashCleanTests",
            resources: [.copy("Fixtures")],
            swiftSettings: swiftSettings
        ),
    ]
    + feature("SystemJunk", scanningDeps: engine, domainDeps: commonDomain, uiDeps: commonUI)
    + feature("Uninstaller", scanningDeps: engine + ["SweepStorage", "SweepIPC"], domainDeps: commonDomain, uiDeps: commonUI)
    + feature("SpaceLens", scanningDeps: engine, domainDeps: commonDomain, uiDeps: commonUI)
    + feature("Maintenance", scanningDeps: engine + ["SweepIPC", "SweepStorage"], domainDeps: commonDomain, uiDeps: commonUI)
    + feature("LoginItems", scanningDeps: engine + ["SweepIPC"], domainDeps: commonDomain, uiDeps: commonUI)
    + feature("LargeOldFiles", scanningDeps: engine, domainDeps: commonDomain, uiDeps: commonUI)
    + feature("Duplicates", scanningDeps: engine, domainDeps: commonDomain, uiDeps: commonUI)
    + feature("SmartScan", scanningDeps: engine, domainDeps: commonDomain, uiDeps: commonUI)
)
