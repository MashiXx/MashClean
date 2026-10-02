import Darwin
import Foundation
import SweepCore

/// Remover cấp thấp dùng chung cho app và helper (mục 4.2, 9.4, 22.3).
/// Chống tấn công symlink (TOCTOU): mọi thao tác đi qua file descriptor của thư mục cha mở bằng `O_NOFOLLOW`,
/// xoá bằng `unlinkat`; thư mục có nội dung thì xoá từ lá lên gốc, mỗi cấp mở fd mới bằng `openat(..., O_NOFOLLOW)`.
public enum SecureRemover {
    public struct Outcome: Sendable {
        public var freedBytes: Int64
        public var removedCount: Int
        public var firstError: Int32?
        public var firstErrorPath: String?
    }

    /// Xoá một đường dẫn (hoặc chỉ nội dung bên trong nếu `contentsOnly`), có kiểm tra PathPolicy.
    /// - Parameters:
    ///   - shouldContinue: gọi định kỳ, trả `false` để dừng (huỷ).
    ///   - progress: báo dung lượng đã giải phóng tăng dần.
    public static func remove(
        path: String,
        policy: PathPolicy,
        contentsOnly: Bool = false,
        allowedRoots: [String]? = nil,
        checkOwnership: Bool = true,
        shouldContinue: () -> Bool = { true },
        progress: ((Int64) -> Void)? = nil
    ) -> RemoveResult {
        let canonical: URL
        do {
            canonical = try policy.check(URL(fileURLWithPath: path), intent: contentsOnly ? .removeContents : .removeItem,
                                         allowedRoots: allowedRoots, checkOwnership: checkOwnership)
        } catch {
            var st = stat()
            if lstat(path, &st) != 0 && errno == ENOENT { return .skipped(path, .notFound) }
            return RemoveResult(path: path, outcome: .skipped(reason: .blockedByPolicy))
        }

        let target = canonical.path
        let parent = (target as NSString).deletingLastPathComponent
        let name = (target as NSString).lastPathComponent

        let dirfd = open(parent, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard dirfd >= 0 else { return result(for: errno, path: path) }
        defer { close(dirfd) }

        // Kiểm tra quyền sở hữu thư mục cha trước khi xoá.
        var parentStat = stat()
        guard fstat(dirfd, &parentStat) == 0 else { return result(for: errno, path: path) }
        do {
            try policy.checkParent(owner: parentStat.st_uid, mode: parentStat.st_mode, path: parent)
        } catch {
            return RemoveResult(path: path, outcome: .skipped(reason: .blockedByPolicy))
        }

        var st = stat()
        guard fstatat(dirfd, name, &st, AT_SYMLINK_NOFOLLOW) == 0 else { return result(for: errno, path: path) }

        var outcome = Outcome(freedBytes: 0, removedCount: 0, firstError: nil, firstErrorPath: nil)
        var counter = 0
        let isDir = st.st_mode & S_IFMT == S_IFDIR
        if contentsOnly && isDir {
            let fd = openat(dirfd, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard fd >= 0 else { return result(for: errno, path: path) }
            removeChildren(of: fd, path: target, outcome: &outcome, counter: &counter, shouldContinue: shouldContinue, progress: progress)
            close(fd)
        } else {
            // `deleteContents` nhắm vào một file thường (vd file nằm thẳng trong ~/Library/Caches): xoá chính file.
            removeTree(parentFD: dirfd, name: name, path: target, outcome: &outcome, counter: &counter, shouldContinue: shouldContinue, progress: progress)
        }

        if let code = outcome.firstError {
            if outcome.removedCount > 0 && code != EPERM && code != EACCES {
                // Xoá được một phần: vẫn tính dung lượng đã giải phóng nhưng báo lỗi
                return RemoveResult(path: path, outcome: .failed(code: code, message: "Xoá một phần: \(String(cString: strerror(code))) tại \(outcome.firstErrorPath ?? "")"), freedBytes: outcome.freedBytes)
            }
            var r = result(for: code, path: path, flags: st.st_flags)
            r.freedBytes = outcome.freedBytes
            return r
        }
        return .ok(path, freed: outcome.freedBytes)
    }

    /// Xoá `name` bên trong thư mục cha đã mở bằng O_NOFOLLOW (mục 22.3).
    public static func secureUnlink(parent: String, name: String, isDirectory: Bool, policy: PathPolicy) throws {
        let dirfd = open(parent, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard dirfd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        defer { close(dirfd) }
        var st = stat()
        guard fstat(dirfd, &st) == 0 else { throw POSIXError(.EIO) }
        try policy.checkParent(owner: st.st_uid, mode: st.st_mode, path: parent)
        let flags = isDirectory ? AT_REMOVEDIR : 0
        guard unlinkat(dirfd, name, flags) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }

    // MARK: Nội bộ

    private static func removeTree(parentFD: Int32, name: String, path: String, outcome: inout Outcome, counter: inout Int,
                                   shouldContinue: () -> Bool, progress: ((Int64) -> Void)?) {
        var st = stat()
        guard fstatat(parentFD, name, &st, AT_SYMLINK_NOFOLLOW) == 0 else {
            if errno != ENOENT { record(errno, path, &outcome) }
            return
        }
        if st.st_mode & S_IFMT == S_IFDIR {
            let fd = openat(parentFD, name, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard fd >= 0 else {
                record(errno, path, &outcome)
                return
            }
            removeChildren(of: fd, path: path, outcome: &outcome, counter: &counter, shouldContinue: shouldContinue, progress: progress)
            close(fd)
            if unlinkat(parentFD, name, AT_REMOVEDIR) == 0 {
                outcome.removedCount += 1
            } else if errno != ENOENT {
                record(errno, path, &outcome)
            }
        } else {
            // Hard link: chỉ giải phóng khi đây là liên kết cuối cùng.
            let size = st.st_nlink <= 1 && st.st_flags & UInt32(SF_DATALESS) == 0 ? Int64(st.st_blocks) * 512 : 0
            if unlinkat(parentFD, name, 0) == 0 {
                outcome.freedBytes += size
                outcome.removedCount += 1
                counter += 1
                if counter % 200 == 0 { progress?(outcome.freedBytes) }
            } else if errno != ENOENT {
                record(errno, path, &outcome)
            }
        }
    }

    private static func removeChildren(of fd: Int32, path: String, outcome: inout Outcome, counter: inout Int,
                                       shouldContinue: () -> Bool, progress: ((Int64) -> Void)?) {
        let dupFD = dup(fd)
        guard dupFD >= 0, let dir = fdopendir(dupFD) else {
            record(errno, path, &outcome)
            return
        }
        var names: [String] = []
        while let entry = readdir(dir) {
            let name = withUnsafePointer(to: &entry.pointee.d_name) {
                String(cString: UnsafeRawPointer($0).assumingMemoryBound(to: CChar.self))
            }
            if name == "." || name == ".." { continue }
            names.append(name)
        }
        closedir(dir)
        for name in names {
            if counter % 100 == 0 && !shouldContinue() { return }
            removeTree(parentFD: fd, name: name, path: path + "/" + name, outcome: &outcome, counter: &counter, shouldContinue: shouldContinue, progress: progress)
        }
    }

    private static func record(_ code: Int32, _ path: String, _ outcome: inout Outcome) {
        if outcome.firstError == nil {
            outcome.firstError = code
            outcome.firstErrorPath = path
        }
    }

    /// Chuyển errno thành kết quả theo bảng xử lý lỗi (mục 7.4).
    public static func result(for code: Int32, path: String, flags: UInt32 = 0) -> RemoveResult {
        switch code {
        case ENOENT:
            return .skipped(path, .notFound)    // coi như thành công, giải phóng 0
        case EBUSY, ETXTBSY:
            return .skipped(path, .inUse)
        case EPERM where flags & UInt32(SF_RESTRICTED) != 0:
            return .skipped(path, .protectedBySIP)
        default:
            return .failed(path, errno: code)
        }
    }
}
