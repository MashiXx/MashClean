import Foundation
import System

/// Một mục trong hệ thống tệp (mục 16.2).
public struct Entry: Sendable, Hashable {
    public let path: FilePath
    public let isDirectory: Bool
    public let isSymlink: Bool
    public let allocatedSize: UInt64
    public let logicalSize: UInt64
    public let modificationDate: Date
    public let accessDate: Date?
    public let fileID: UInt64
    public let deviceID: Int32
    public let linkCount: UInt32
    public let isPackage: Bool
    /// File "dataless" của iCloud / File Provider (chưa tải về máy). Xoá chúng không giải phóng ổ (mục 6.4).
    public let isDataless: Bool
    public let isHidden: Bool
    public let depth: Int

    public init(path: FilePath, isDirectory: Bool, isSymlink: Bool = false, allocatedSize: UInt64, logicalSize: UInt64 = 0,
                modificationDate: Date, accessDate: Date?, fileID: UInt64, deviceID: Int32 = 0, linkCount: UInt32 = 1,
                isPackage: Bool, isDataless: Bool = false, isHidden: Bool = false, depth: Int = 0) {
        self.path = path
        self.isDirectory = isDirectory
        self.isSymlink = isSymlink
        self.allocatedSize = allocatedSize
        self.logicalSize = logicalSize
        self.modificationDate = modificationDate
        self.accessDate = accessDate
        self.fileID = fileID
        self.deviceID = deviceID
        self.linkCount = linkCount
        self.isPackage = isPackage
        self.isDataless = isDataless
        self.isHidden = isHidden
        self.depth = depth
    }

    public var url: URL { URL(fileURLWithPath: path.string) }
    public var name: String { path.lastComponent?.string ?? path.string }
    /// Thời điểm dùng gần nhất: lớn hơn giữa lần sửa và lần truy cập.
    public var lastUse: Date { max(modificationDate, accessDate ?? .distantPast) }
}

public enum WalkDecision: Sendable {
    case `continue`
    case skipDescendants
    case stop
}

public struct WalkOptions: Sendable {
    /// Độ sâu tối đa (0 = chỉ nội dung trực tiếp của root). `nil` = không giới hạn.
    public var maxDepth: Int?
    /// Không đi vào bên trong package (.app, .bundle, .photoslibrary...).
    public var skipPackageContents: Bool
    public var skipHidden: Bool
    /// Không đi qua ranh giới volume (mục 16.3).
    public var crossVolumes: Bool
    /// Gọi `Task.checkCancellation()` sau mỗi N mục (mục 5.3).
    public var cancellationCheckInterval: Int

    public init(maxDepth: Int? = nil, skipPackageContents: Bool = false, skipHidden: Bool = false,
                crossVolumes: Bool = false, cancellationCheckInterval: Int = 500) {
        self.maxDepth = maxDepth
        self.skipPackageContents = skipPackageContents
        self.skipHidden = skipHidden
        self.crossVolumes = crossVolumes
        self.cancellationCheckInterval = cancellationCheckInterval
    }

    public static let `default` = WalkOptions()
}

/// Interface chung cho các cách duyệt (mục 16.2). Không theo symlink.
public protocol DirectoryWalker: Sendable {
    func walk(
        _ root: URL,
        options: WalkOptions,
        visit: @Sendable (Entry) throws -> WalkDecision
    ) async throws
}

public enum WalkError: Error, Sendable {
    case stopped
}

/// Đuôi thư mục được coi là package khi duyệt (không cần LaunchServices, an toàn với mọi luồng).
public enum PackageExtensions {
    public static let all: Set<String> = [
        "app", "appex", "bundle", "framework", "plugin", "kext", "xpc", "qlgenerator", "mdimporter", "prefPane",
        "photoslibrary", "musiclibrary", "tvlibrary", "imovielibrary", "fcpbundle", "logicx", "band", "pages",
        "numbers", "key", "rtfd", "xcodeproj", "xcworkspace", "xcarchive", "playground", "lproj", "dSYM",
        "sparsebundle", "pkg", "mpkg", "component", "vst", "vst3", "aaxplugin", "saver", "wdgt", "docarchive",
        "scptd", "workflow", "action", "safariextension", "lrlibrary", "lrdata", "aplibrary", "migratedphotolibrary",
    ]

    public static func isPackage(name: String) -> Bool {
        guard let dot = name.lastIndex(of: ".") else { return false }
        let ext = name[name.index(after: dot)...]
        return all.contains(String(ext)) || all.contains(ext.lowercased())
    }
}
