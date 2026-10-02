import Darwin
import Foundation
import IOKit
import IOKit.storage
import SweepCore

/// Thông tin volume (dung lượng trống, loại ổ...).
public struct VolumeInfo: Sendable, Hashable, Identifiable {
    public var id: String { mountPoint }
    public let mountPoint: String
    public let name: String
    public let totalCapacity: Int64
    /// `volumeAvailableCapacityForImportantUsageKey`: con số dùng để đo dung lượng thực giải phóng (mục 7.5).
    public let availableForImportantUsage: Int64
    public let available: Int64
    public let isInternal: Bool
    public let isReadOnly: Bool
    public let isRemovable: Bool
    public let isRoot: Bool
    public let isLocal: Bool

    public init?(url: URL) {
        let keys: Set<URLResourceKey> = [.volumeURLKey, .volumeNameKey, .volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey,
                                         .volumeAvailableCapacityKey, .volumeIsInternalKey, .volumeIsReadOnlyKey, .volumeIsRemovableKey,
                                         .volumeIsRootFileSystemKey, .volumeIsLocalKey]
        var probe = url
        var values: URLResourceValues?
        while values == nil {
            values = try? probe.resourceValues(forKeys: keys)
            if values == nil {
                let parent = probe.deletingLastPathComponent()
                if parent.path == probe.path { return nil }
                probe = parent
            }
        }
        guard let v = values else { return nil }
        mountPoint = v.volume?.path ?? "/"
        name = v.volumeName ?? mountPoint
        totalCapacity = Int64(v.volumeTotalCapacity ?? 0)
        availableForImportantUsage = v.volumeAvailableCapacityForImportantUsage ?? Int64(v.volumeAvailableCapacity ?? 0)
        available = Int64(v.volumeAvailableCapacity ?? 0)
        isInternal = v.volumeIsInternal ?? true
        isReadOnly = v.volumeIsReadOnly ?? false
        isRemovable = v.volumeIsRemovable ?? false
        isRoot = v.volumeIsRootFileSystem ?? (mountPoint == "/")
        isLocal = v.volumeIsLocal ?? true
    }

    public var usedFraction: Double {
        guard totalCapacity > 0 else { return 0 }
        return 1 - Double(availableForImportantUsage) / Double(totalCapacity)
    }

    /// Volume người dùng nhìn thấy (ổ gốc + ổ ngoài đã gắn).
    public static func mounted() -> [VolumeInfo] {
        let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: nil, options: [.skipHiddenVolumes]) ?? []
        return urls.compactMap { VolumeInfo(url: $0) }
    }
}

/// Loại ổ, phát hiện qua IOKit `Medium Type` (mục 5.3).
public enum StorageMedium: Sendable {
    case solidState
    case rotational
    case unknown

    private static let cache = Locked<[String: StorageMedium]>([:])

    public static func detect(for url: URL) -> StorageMedium {
        var fs = statfs()
        guard statfs(url.path, &fs) == 0 else { return .unknown }
        let from = withUnsafePointer(to: &fs.f_mntfromname) {
            String(cString: UnsafeRawPointer($0).assumingMemoryBound(to: CChar.self))
        }
        guard from.hasPrefix("/dev/") else { return .unknown }
        let bsd = String(from.dropFirst("/dev/".count))
        if let cached = cache.withLock({ $0[bsd] }) { return cached }
        let result = lookup(bsdName: bsd)
        cache.withLock { $0[bsd] = result }
        return result
    }

    private static func lookup(bsdName: String) -> StorageMedium {
        guard let matching = IOBSDNameMatching(kIOMainPortDefault, 0, bsdName) else { return .unknown }
        let service = IOServiceGetMatchingService(kIOMainPortDefault, matching)
        guard service != 0 else { return .unknown }
        defer { IOObjectRelease(service) }

        // Đi ngược lên cây IORegistry tìm "Device Characteristics" → "Medium Type".
        var current = service
        IOObjectRetain(current)
        for _ in 0..<12 {
            if let props = IORegistryEntryCreateCFProperty(current, kIOPropertyDeviceCharacteristicsKey as CFString, kCFAllocatorDefault, 0)?
                .takeRetainedValue() as? [String: Any],
                let medium = props[kIOPropertyMediumTypeKey as String] as? String {
                IOObjectRelease(current)
                if medium == (kIOPropertyMediumTypeSolidStateKey as String) { return .solidState }
                if medium == (kIOPropertyMediumTypeRotationalKey as String) { return .rotational }
                return .unknown
            }
            var parent: io_registry_entry_t = 0
            let kr = IORegistryEntryGetParentEntry(current, kIOServicePlane, &parent)
            IOObjectRelease(current)
            guard kr == KERN_SUCCESS else { return .unknown }
            current = parent
        }
        IOObjectRelease(current)
        return .unknown
    }
}
