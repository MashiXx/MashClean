import Darwin
import Foundation
import System

/// Walker dựa trên `fts_open`/`fts_read`: nhanh, phù hợp duyệt cây sâu (mục 16.2).
public struct FTSWalker: DirectoryWalker {
    public init() {}

    public func walk(_ root: URL, options: WalkOptions, visit: @Sendable (Entry) throws -> WalkDecision) async throws {
        try walkSync(root, options: options, visit: visit)
    }

    public func walkSync(_ root: URL, options: WalkOptions, visit: (Entry) throws -> WalkDecision) throws {
        var flags = FTS_PHYSICAL | FTS_NOCHDIR
        if !options.crossVolumes { flags |= FTS_XDEV }
        let pathC = strdup(root.path)
        defer { free(pathC) }
        var argv: [UnsafeMutablePointer<CChar>?] = [pathC, nil]
        guard let fts = fts_open(&argv, flags, nil) else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { fts_close(fts) }

        var visited = 0
        while let node = fts_read(fts) {
            let info = Int32(node.pointee.fts_info)
            let level = Int(node.pointee.fts_level)
            if level == 0 { continue }                           // bỏ qua chính root
            if info == FTS_DP || info == FTS_DNR || info == FTS_ERR || info == FTS_NS { continue }
            visited += 1
            if visited % options.cancellationCheckInterval == 0 { try Task.checkCancellation() }

            let path = String(cString: node.pointee.fts_path)
            let name = String(cString: withUnsafePointer(to: &node.pointee.fts_name) { UnsafeRawPointer($0).assumingMemoryBound(to: CChar.self) })
            if options.skipHidden && name.hasPrefix(".") {
                if info == FTS_D { fts_set(fts, node, FTS_SKIP) }
                continue
            }
            guard let st = node.pointee.fts_statp?.pointee else { continue }
            let isDir = info == FTS_D
            let isPackage = isDir && PackageExtensions.isPackage(name: name)
            let isDataless = st.st_flags & UInt32(SF_DATALESS) != 0
            let entry = Entry(
                path: FilePath(path), isDirectory: isDir, isSymlink: info == FTS_SL || info == FTS_SLNONE,
                allocatedSize: isDataless ? 0 : UInt64(st.st_blocks) * 512, logicalSize: UInt64(max(0, st.st_size)),
                modificationDate: Date(timeIntervalSince1970: Double(st.st_mtimespec.tv_sec)),
                accessDate: Date(timeIntervalSince1970: Double(st.st_atimespec.tv_sec)),
                fileID: st.st_ino, deviceID: st.st_dev, linkCount: UInt32(st.st_nlink), isPackage: isPackage,
                isDataless: isDataless, isHidden: name.hasPrefix(".") || st.st_flags & UInt32(UF_HIDDEN) != 0, depth: level - 1
            )
            switch try visit(entry) {
            case .stop: return
            case .skipDescendants: if isDir { fts_set(fts, node, FTS_SKIP) }
            case .continue:
                guard isDir else { break }
                if (options.skipPackageContents && isPackage) || (options.maxDepth.map { level - 1 >= $0 } ?? false) {
                    fts_set(fts, node, FTS_SKIP)
                }
            }
        }
    }
}

/// Walker dựa trên `FileManager.enumerator` có prefetch key: tốc độ trung bình, dùng khi quét theo rule (mục 16.2).
public struct FoundationWalker: DirectoryWalker {
    public init() {}

    static let keys: [URLResourceKey] = [
        .isDirectoryKey, .isSymbolicLinkKey, .totalFileAllocatedSizeKey, .fileSizeKey, .contentModificationDateKey,
        .contentAccessDateKey, .fileResourceIdentifierKey, .isPackageKey, .isHiddenKey, .volumeIdentifierKey,
        .linkCountKey, .ubiquitousItemDownloadingStatusKey,
    ]

    public func walk(_ root: URL, options: WalkOptions, visit: @Sendable (Entry) throws -> WalkDecision) async throws {
        var enumOptions: FileManager.DirectoryEnumerationOptions = []
        if options.skipHidden { enumOptions.insert(.skipsHiddenFiles) }
        if options.skipPackageContents { enumOptions.insert(.skipsPackageDescendants) }
        guard let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: Self.keys, options: enumOptions, errorHandler: { _, _ in true }) else { return }
        let rootVolume = try? root.resourceValues(forKeys: [.volumeIdentifierKey]).volumeIdentifier as? NSObject
        var visited = 0
        while let url = enumerator.nextObject() as? URL {
            visited += 1
            if visited % options.cancellationCheckInterval == 0 { try Task.checkCancellation() }
            guard let v = try? url.resourceValues(forKeys: Set(Self.keys)) else { continue }
            let depth = enumerator.level - 1
            let isDir = v.isDirectory ?? false
            if isDir, !options.crossVolumes, let rootVolume, let vol = v.volumeIdentifier as? NSObject, !vol.isEqual(rootVolume) {
                enumerator.skipDescendants()
                continue
            }
            let dataless = v.ubiquitousItemDownloadingStatus == .notDownloaded
            let fileID = (v.fileResourceIdentifier as? NSObject).map { UInt64(truncatingIfNeeded: $0.hash) } ?? 0
            let entry = Entry(
                path: FilePath(url.path), isDirectory: isDir, isSymlink: v.isSymbolicLink ?? false,
                allocatedSize: dataless ? 0 : UInt64(v.totalFileAllocatedSize ?? 0), logicalSize: UInt64(v.fileSize ?? 0),
                modificationDate: v.contentModificationDate ?? .distantPast, accessDate: v.contentAccessDate,
                fileID: fileID, linkCount: UInt32(v.linkCount ?? 1), isPackage: v.isPackage ?? false,
                isDataless: dataless, isHidden: v.isHidden ?? false, depth: depth
            )
            switch try visit(entry) {
            case .stop: return
            case .skipDescendants: enumerator.skipDescendants()
            case .continue:
                if let max = options.maxDepth, isDir, depth >= max { enumerator.skipDescendants() }
            }
        }
    }
}
