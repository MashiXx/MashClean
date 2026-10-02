import CryptoKit
import CXXHash
import Darwin
import Foundation

/// Thông tin clone APFS (mục 11.8): file clone dùng chung block nên xoá không giải phóng dung lượng.
public struct CloneInfo: Sendable, Hashable {
    public let cloneID: UInt64
    public let extendedFlags: UInt64

    public var mayShareBlocks: Bool { extendedFlags & UInt64(EF_MAY_SHARE_BLOCKS) != 0 }
    public var sharesAllBlocks: Bool { extendedFlags & UInt64(EF_SHARES_ALL_BLOCKS) != 0 }

    /// Đọc bằng `getattrlist` với `ATTR_CMNEXT_CLONEID` và `ATTR_CMNEXT_EXT_FLAGS` (macOS 10.15+).
    public static func read(path: String) -> CloneInfo? {
        var attrs = attrlist()
        attrs.bitmapcount = u_short(ATTR_BIT_MAP_COUNT)
        attrs.commonattr = attrgroup_t(ATTR_CMN_RETURNED_ATTRS)
        attrs.forkattr = attrgroup_t(ATTR_CMNEXT_CLONEID) | attrgroup_t(ATTR_CMNEXT_EXT_FLAGS)
        let size = 4 + MemoryLayout<attribute_set_t>.size + 16
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: size + 16, alignment: 8)
        defer { buffer.deallocate() }
        guard getattrlist(path, &attrs, buffer, size + 16, UInt32(FSOPT_ATTR_CMN_EXTENDED | FSOPT_NOFOLLOW)) == 0 else { return nil }
        var field = buffer + 4
        let returned = field.loadUnaligned(as: attribute_set_t.self)
        field += MemoryLayout<attribute_set_t>.size
        var cloneID: UInt64 = 0
        var flags: UInt64 = 0
        if returned.forkattr & attrgroup_t(ATTR_CMNEXT_CLONEID) != 0 {
            cloneID = field.loadUnaligned(as: UInt64.self)
            field += 8
        }
        if returned.forkattr & attrgroup_t(ATTR_CMNEXT_EXT_FLAGS) != 0 {
            flags = field.loadUnaligned(as: UInt64.self)
        }
        return CloneInfo(cloneID: cloneID, extendedFlags: flags)
    }
}

/// Băm file cho tìm trùng lặp (mục 11.8).
public enum FileHasher {
    public static let edgeChunk = 64 * 1024

    /// xxHash3 của 64 KB đầu + 64 KB cuối (bước 2: lọc sơ bộ, nhanh hơn SHA-256 nhiều lần).
    public static func edgeHash(path: String, size: UInt64) throws -> UInt64 {
        let fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(fd) }
        _ = fcntl(fd, F_NOCACHE, 1)
        let chunk = edgeChunk
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: chunk * 2, alignment: 16)
        defer { buffer.deallocate() }
        var total = 0
        let first = pread(fd, buffer, chunk, 0)
        guard first >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        total += first
        if size > UInt64(chunk) {
            let offset = off_t(max(UInt64(chunk), size - UInt64(chunk)))
            let last = pread(fd, buffer + total, chunk, offset)
            if last > 0 { total += last }
        }
        return XXH3_64bits(buffer, total)
    }

    /// SHA-256 toàn bộ file (bước 3), đọc theo khối 1 MB, có kiểm tra huỷ.
    public static func sha256(path: String) throws -> String {
        let fd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(fd) }
        _ = fcntl(fd, F_NOCACHE, 1)
        var hasher = SHA256()
        let size = 1 << 20
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: size, alignment: 16)
        defer { buffer.deallocate() }
        var rounds = 0
        while true {
            let n = read(fd, buffer, size)
            if n < 0 {
                if errno == EINTR { continue }
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
            if n == 0 { break }
            hasher.update(bufferPointer: UnsafeRawBufferPointer(start: buffer, count: n))
            rounds += 1
            if rounds % 64 == 0 { try Task.checkCancellation() }
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    public static func xxh3(_ data: Data) -> UInt64 {
        data.withUnsafeBytes { XXH3_64bits($0.baseAddress, $0.count) }
    }
}

/// Kiểm tra file có đang được tiến trình nào mở không (mục 15.1, 7.4), qua `proc_listpidspath`.
public enum OpenFileCheck {
    @_silgen_name("proc_listpidspath")
    private static func proc_listpidspath(_ type: UInt32, _ typeinfo: UInt32, _ path: UnsafePointer<CChar>, _ pathflags: UInt32,
                                          _ buffer: UnsafeMutableRawPointer?, _ buffersize: Int32) -> Int32

    private static let PROC_ALL_PIDS: UInt32 = 1
    private static let PROC_LISTPIDSPATH_PATH_IS_VOLUME: UInt32 = 1
    private static let PROC_LISTPIDSPATH_EXCLUDE_EVTONLY: UInt32 = 2

    /// Danh sách PID đang mở file (không tính tiến trình chỉ theo dõi sự kiện).
    public static func processesHolding(_ path: String) -> [pid_t] {
        let flags = PROC_LISTPIDSPATH_EXCLUDE_EVTONLY
        let needed = path.withCString { proc_listpidspath(PROC_ALL_PIDS, 0, $0, flags, nil, 0) }
        guard needed > 0 else { return [] }
        let capacity = Int(needed) / MemoryLayout<pid_t>.size + 16
        var pids = [pid_t](repeating: 0, count: capacity)
        let bytes = path.withCString { cpath in
            pids.withUnsafeMutableBytes { proc_listpidspath(PROC_ALL_PIDS, 0, cpath, flags, $0.baseAddress, Int32($0.count)) }
        }
        guard bytes > 0 else { return [] }
        return Array(pids.prefix(Int(bytes) / MemoryLayout<pid_t>.size)).filter { $0 > 0 }
    }

    public static func isOpen(_ path: String) -> Bool { !processesHolding(path).isEmpty }
}
