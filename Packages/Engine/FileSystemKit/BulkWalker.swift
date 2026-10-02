import Darwin
import Foundation
import System

/// Một mục đọc bằng `getattrlistbulk`: nhiều mục trong một syscall (mục 16.2, 22.3).
public struct BulkEntry: Sendable {
    public var name: String
    public var isDirectory: Bool
    public var isSymlink: Bool
    public var allocatedSize: UInt64
    public var logicalSize: UInt64
    public var modificationDate: Date
    public var accessDate: Date
    public var fileID: UInt64
    public var deviceID: Int32
    public var linkCount: UInt32
    public var flags: UInt32

    public var isDataless: Bool { flags & UInt32(SF_DATALESS) != 0 }
    public var isHidden: Bool { flags & UInt32(UF_HIDDEN) != 0 || name.hasPrefix(".") }
}

public enum BulkReader {
    /// Đọc một thư mục, trả về tên, loại, dung lượng thực chiếm và thời gian của từng mục.
    /// Mở thư mục bằng `O_NOFOLLOW` nên không theo symlink.
    public static func readDirectory(_ path: String, _ body: (BulkEntry) throws -> Void) throws {
        let fd = open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(fd) }
        try readDirectory(fd: fd, body)
    }

    public static func readDirectory(fd: Int32, _ body: (BulkEntry) throws -> Void) throws {
        var attrs = attrlist()
        attrs.bitmapcount = u_short(ATTR_BIT_MAP_COUNT)
        attrs.commonattr = attrgroup_t(ATTR_CMN_RETURNED_ATTRS) | attrgroup_t(ATTR_CMN_NAME) | attrgroup_t(ATTR_CMN_DEVID)
            | attrgroup_t(ATTR_CMN_OBJTYPE) | attrgroup_t(ATTR_CMN_MODTIME) | attrgroup_t(ATTR_CMN_ACCTIME)
            | attrgroup_t(ATTR_CMN_FLAGS) | attrgroup_t(ATTR_CMN_FILEID) | attrgroup_t(ATTR_CMN_ERROR)
        attrs.fileattr = attrgroup_t(ATTR_FILE_LINKCOUNT) | attrgroup_t(ATTR_FILE_TOTALSIZE) | attrgroup_t(ATTR_FILE_ALLOCSIZE)

        let bufferSize = 256 * 1024
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: bufferSize, alignment: 16)
        defer { buffer.deallocate() }

        while true {
            let count = getattrlistbulk(fd, &attrs, buffer, bufferSize, 0)
            if count == 0 { break }
            if count < 0 {
                if errno == EINTR { continue }
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
            var entryPtr = buffer
            for _ in 0..<count {
                let length = entryPtr.loadUnaligned(as: UInt32.self)
                defer { entryPtr += Int(length) }
                var field = entryPtr + MemoryLayout<UInt32>.size
                let returned = field.loadUnaligned(as: attribute_set_t.self)
                field += MemoryLayout<attribute_set_t>.size

                // Thứ tự trong buffer theo giá trị bit tăng dần (sys/attr.h).
                var entry = BulkEntry(name: "", isDirectory: false, isSymlink: false, allocatedSize: 0, logicalSize: 0,
                                      modificationDate: .distantPast, accessDate: .distantPast, fileID: 0, deviceID: 0,
                                      linkCount: 1, flags: 0)
                let cmn = returned.commonattr
                if cmn & attrgroup_t(ATTR_CMN_NAME) != 0 {
                    let ref = field.loadUnaligned(as: attrreference_t.self)
                    let namePtr = (field + Int(ref.attr_dataoffset)).assumingMemoryBound(to: CChar.self)
                    entry.name = String(cString: namePtr)
                    field += MemoryLayout<attrreference_t>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_DEVID) != 0 {
                    entry.deviceID = field.loadUnaligned(as: dev_t.self)
                    field += MemoryLayout<dev_t>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_OBJTYPE) != 0 {
                    let type = field.loadUnaligned(as: fsobj_type_t.self)
                    entry.isDirectory = type == VDIR.rawValue
                    entry.isSymlink = type == VLNK.rawValue
                    field += MemoryLayout<fsobj_type_t>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_MODTIME) != 0 {
                    let ts = field.loadUnaligned(as: timespec.self)
                    entry.modificationDate = Date(timeIntervalSince1970: Double(ts.tv_sec) + Double(ts.tv_nsec) / 1e9)
                    field += MemoryLayout<timespec>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_ACCTIME) != 0 {
                    let ts = field.loadUnaligned(as: timespec.self)
                    entry.accessDate = Date(timeIntervalSince1970: Double(ts.tv_sec) + Double(ts.tv_nsec) / 1e9)
                    field += MemoryLayout<timespec>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_FLAGS) != 0 {
                    entry.flags = field.loadUnaligned(as: UInt32.self)
                    field += MemoryLayout<UInt32>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_FILEID) != 0 {
                    entry.fileID = field.loadUnaligned(as: UInt64.self)
                    field += MemoryLayout<UInt64>.size
                }
                if cmn & attrgroup_t(ATTR_CMN_ERROR) != 0 {
                    let err = field.loadUnaligned(as: UInt32.self)
                    field += MemoryLayout<UInt32>.size
                    if err != 0 { continue }   // mục không đọc được thuộc tính: bỏ qua
                }
                let fileAttrs = returned.fileattr
                if fileAttrs & attrgroup_t(ATTR_FILE_LINKCOUNT) != 0 {
                    entry.linkCount = field.loadUnaligned(as: UInt32.self)
                    field += MemoryLayout<UInt32>.size
                }
                if fileAttrs & attrgroup_t(ATTR_FILE_TOTALSIZE) != 0 {
                    entry.logicalSize = UInt64(max(0, field.loadUnaligned(as: off_t.self)))
                    field += MemoryLayout<off_t>.size
                }
                if fileAttrs & attrgroup_t(ATTR_FILE_ALLOCSIZE) != 0 {
                    entry.allocatedSize = UInt64(max(0, field.loadUnaligned(as: off_t.self)))
                    field += MemoryLayout<off_t>.size
                }
                guard !entry.name.isEmpty else { continue }
                try body(entry)
            }
        }
    }
}

/// Walker nhanh nhất, dùng cho Space Lens và tính dung lượng thư mục.
public struct BulkWalker: DirectoryWalker {
    public init() {}

    public func walk(_ root: URL, options: WalkOptions, visit: @Sendable (Entry) throws -> WalkDecision) async throws {
        try walkSync(root, options: options, visit: visit)
    }

    /// Duyệt theo hàng đợi thư mục (không đệ quy để tránh tràn stack với cây sâu).
    public func walkSync(_ root: URL, options: WalkOptions, visit: (Entry) throws -> WalkDecision) throws {
        var rootStat = stat()
        guard lstat(root.path, &rootStat) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .ENOENT) }
        guard rootStat.st_mode & S_IFMT == S_IFDIR else { return }
        let rootDevice = rootStat.st_dev

        var queue: [(path: String, depth: Int)] = [(root.path, 0)]
        var visited = 0
        while let (dir, depth) = queue.popLast() {
            var stop = false
            do {
                try BulkReader.readDirectory(dir) { raw in
                    if stop { return }
                    visited += 1
                    if visited % options.cancellationCheckInterval == 0 { try Task.checkCancellation() }
                    if options.skipHidden && raw.isHidden { return }
                    let full = dir == "/" ? "/" + raw.name : dir + "/" + raw.name
                    let isPackage = raw.isDirectory && PackageExtensions.isPackage(name: raw.name)
                    let entry = Entry(
                        path: FilePath(full), isDirectory: raw.isDirectory, isSymlink: raw.isSymlink,
                        allocatedSize: raw.isDataless ? 0 : raw.allocatedSize, logicalSize: raw.logicalSize,
                        modificationDate: raw.modificationDate, accessDate: raw.accessDate, fileID: raw.fileID,
                        deviceID: raw.deviceID, linkCount: raw.linkCount, isPackage: isPackage,
                        isDataless: raw.isDataless, isHidden: raw.isHidden, depth: depth
                    )
                    switch try visit(entry) {
                    case .stop: stop = true
                    case .skipDescendants: break
                    case .continue:
                        guard raw.isDirectory, !raw.isSymlink else { return }
                        if !options.crossVolumes && raw.deviceID != rootDevice { return }
                        if options.skipPackageContents && isPackage { return }
                        if let max = options.maxDepth, depth + 1 > max { return }
                        queue.append((full, depth + 1))
                    }
                }
            } catch is POSIXError where dir != root.path {
                // Thư mục con không đọc được (EACCES, EPERM do TCC...): bỏ qua, tiếp tục nhánh khác.
            }
            if stop { return }
        }
    }
}
