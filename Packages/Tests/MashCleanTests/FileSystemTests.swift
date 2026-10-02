import CleanEngineCore
import CryptoKit
import Darwin
import FileSystemKit
import Foundation
import SweepCore
import Testing

/// Xoá an toàn chống symlink (mục 9.4, 22.3).
@Suite struct SecureRemoverTests {
    func policy(_ dir: TemporaryDirectory) -> PathPolicy { .user(home: dir.url("home")) }

    @Test func symlinkInsideTreeIsNotFollowed() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("outside/precious.txt", size: 4096)
        try dir.file("victim/a/b/file", size: 8192)
        try dir.symlink("victim/a/escape", to: dir.path("outside"))
        try dir.symlink("victim/abs", to: "/System/Library")
        let r = SecureRemover.remove(path: dir.path("victim"), policy: policy(dir))
        #expect(r.outcome == .ok)
        #expect(!dir.exists("victim"))
        #expect(dir.exists("outside/precious.txt"))
        #expect(r.freedBytes >= 8192)
    }

    @Test func targetSymlinkRemovesOnlyTheLink() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("outside/precious.txt")
        try dir.symlink("cache/link", to: dir.path("outside"))
        let r = SecureRemover.remove(path: dir.path("cache/link"), policy: policy(dir))
        #expect(r.isSuccess)
        #expect(!dir.exists("cache/link"))
        #expect(dir.exists("outside/precious.txt"))
    }

    @Test func symlinkedParentEscapingAllowedRootIsBlocked() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("outside/precious.txt")
        try dir.symlink("cache/link", to: dir.path("outside"))
        let r = SecureRemover.remove(path: dir.path("cache/link/precious.txt"), policy: policy(dir), allowedRoots: [dir.path("cache")])
        #expect(r.outcome == .skipped(reason: .blockedByPolicy))
        #expect(dir.exists("outside/precious.txt"))
    }

    @Test func contentsOnlyKeepsDirectory() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("cache/x", size: 4096)
        try dir.file("cache/sub/y", size: 4096)
        let r = SecureRemover.remove(path: dir.path("cache"), policy: policy(dir), contentsOnly: true)
        #expect(r.outcome == .ok)
        #expect(dir.exists("cache"))
        #expect(try FileManager.default.contentsOfDirectory(atPath: dir.path("cache")).isEmpty)
    }

    /// `deleteContents` khớp file lẻ (ví dụ file nằm thẳng trong `~/Library/Caches`): xoá chính file.
    @Test func contentsOnlyOnFileRemovesTheFile() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("file.db", size: 8192)
        let r = SecureRemover.remove(path: dir.path("file.db"), policy: policy(dir), contentsOnly: true)
        #expect(r.outcome == .ok)
        #expect(r.freedBytes >= 8192)
        #expect(!dir.exists("file.db"))
    }

    @Test func forbiddenAndMissingPaths() throws {
        let dir = try TemporaryDirectory("secure")
        #expect(SecureRemover.remove(path: "/System/Library/Fonts", policy: policy(dir)).outcome == .skipped(reason: .blockedByPolicy))
        #expect(SecureRemover.remove(path: dir.path("missing"), policy: policy(dir)).outcome == .skipped(reason: .notFound))
    }

    @Test func worldWritableParentWithoutStickyIsRefused() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("shared/target")
        chmod(dir.path("shared"), 0o777)
        let r = SecureRemover.remove(path: dir.path("shared/target"), policy: policy(dir))
        #expect(r.outcome == .skipped(reason: .blockedByPolicy))
        #expect(dir.exists("shared/target"))
        chmod(dir.path("shared"), 0o1777)
        #expect(SecureRemover.remove(path: dir.path("shared/target"), policy: policy(dir)).isSuccess)
    }

    @Test func hardLinkedFileFreesNothingUntilLastLink() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("a/data", size: 64 * 1024)
        try FileManager.default.linkItem(atPath: dir.path("a/data"), toPath: dir.path("b-link"))
        let r = SecureRemover.remove(path: dir.path("a"), policy: policy(dir))
        #expect(r.outcome == .ok)
        #expect(r.freedBytes == 0)
        #expect(dir.exists("b-link"))
    }

    @Test func secureUnlinkChecksParent() throws {
        let dir = try TemporaryDirectory("secure")
        try dir.file("p/f")
        try SecureRemover.secureUnlink(parent: dir.path("p"), name: "f", isDirectory: false, policy: policy(dir))
        #expect(!dir.exists("p/f"))
    }
}

/// Duyệt thư mục và đo dung lượng (mục 6.4, 16.2).
@Suite struct WalkerTests {
    func makeTree(_ dir: TemporaryDirectory) throws {
        for d in 0..<3 {
            for f in 0..<5 { try dir.file("tree/d\(d)/f\(f)", size: 1000) }
        }
        try dir.file("tree/top", size: 1000)
        try dir.symlink("tree/link", to: "/System")
        try dir.file("tree/.hidden", size: 10)
    }

    @Test func bulkWalkerCountsEverything() throws {
        let dir = try TemporaryDirectory("walk")
        try makeTree(dir)
        var files = 0, dirs = 0, links = 0
        try BulkWalker().walkSync(dir.url("tree"), options: .default) { e in
            if e.isSymlink { links += 1 } else if e.isDirectory { dirs += 1 } else { files += 1 }
            return .continue
        }
        #expect(files == 17)
        #expect(dirs == 3)
        #expect(links == 1)
    }

    @Test func walkersAgree() async throws {
        let dir = try TemporaryDirectory("walk")
        try makeTree(dir)
        let bulk = Locked(Set<String>())
        let fts = Locked(Set<String>())
        try await BulkWalker().walk(dir.url("tree"), options: .default) { e in bulk.withLock { _ = $0.insert(e.path.string) }; return .continue }
        try await FTSWalker().walk(dir.url("tree"), options: .default) { e in fts.withLock { _ = $0.insert(e.path.string) }; return .continue }
        #expect(bulk.current == fts.current)
        #expect(bulk.current.count == 21)
    }

    @Test func walkOptions() throws {
        let dir = try TemporaryDirectory("walk")
        try makeTree(dir)
        var count = 0
        try BulkWalker().walkSync(dir.url("tree"), options: WalkOptions(maxDepth: 0, skipHidden: true)) { _ in count += 1; return .continue }
        #expect(count == 5)   // d0, d1, d2, top, link
        var visited = 0
        try BulkWalker().walkSync(dir.url("tree"), options: .default) { _ in visited += 1; return visited == 3 ? .stop : .continue }
        #expect(visited == 3)
    }

    @Test func measureCountsHardLinksOnce() throws {
        let dir = try TemporaryDirectory("walk")
        try dir.file("m/a", size: 64 * 1024)
        try dir.dir("other")
        try FileManager.default.linkItem(atPath: dir.path("m/a"), toPath: dir.path("m/b"))
        try FileManager.default.linkItem(atPath: dir.path("m/a"), toPath: dir.path("other/c"))
        let fs = FileSystemService(home: dir.url("home"))
        let single = try fs.measure(dir.url("m/a"))
        let tracker = HardLinkTracker()
        let m = try fs.measure(dir.url("m"), tracker: tracker)
        #expect(m.itemCount == 2)
        #expect(m.allocatedSize == single.allocatedSize)
        // Cùng phiên quét: đã đếm inode này rồi.
        let other = try fs.measure(dir.url("other"), tracker: tracker)
        #expect(other.allocatedSize == 0)
        #expect(try fs.measure(dir.url("missing")) == .missing)
    }

    @Test func measureDoesNotFollowSymlinks() throws {
        let dir = try TemporaryDirectory("walk")
        try dir.dir("m")
        try dir.symlink("m/sys", to: "/System/Library")
        let m = try FileSystemService(home: dir.url("home")).measure(dir.url("m"))
        #expect(m.itemCount == 1)
        #expect(m.allocatedSize < 64 * 1024)
    }

    @Test func measureParallelMatchesSerial() async throws {
        let dir = try TemporaryDirectory("walk")
        try makeTree(dir)
        let fs = FileSystemService(home: dir.url("home"))
        let serial = try fs.measure(dir.url("tree"))
        let parallel = try await fs.measureParallel(dir.url("tree"))
        #expect(serial.allocatedSize == parallel.allocatedSize)
        #expect(serial.itemCount == parallel.itemCount)
    }
}

/// Băm file cho Duplicates (mục 11.8).
@Suite struct FileHasherTests {
    @Test func sha256KnownVector() throws {
        let dir = try TemporaryDirectory("hash")
        try Data("abc".utf8).write(to: dir.url("abc"))
        #expect(try FileHasher.sha256(path: dir.path("abc")) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
    }

    @Test func sha256MatchesCryptoKitForLargeFile() throws {
        let dir = try TemporaryDirectory("hash")
        let data = Data((0..<(3 * 1024 * 1024 + 17)).map { UInt8(truncatingIfNeeded: $0 &* 7) })
        try data.write(to: dir.url("big"))
        let expected = SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
        #expect(try FileHasher.sha256(path: dir.path("big")) == expected)
    }

    @Test func edgeHash() throws {
        let dir = try TemporaryDirectory("hash")
        let small = Data("hello".utf8)
        try small.write(to: dir.url("small"))
        #expect(try FileHasher.edgeHash(path: dir.path("small"), size: UInt64(small.count)) == FileHasher.xxh3(small))

        var big = Data((0..<(300 * 1024)).map { UInt8(truncatingIfNeeded: $0) })
        try big.write(to: dir.url("a"))
        try big.write(to: dir.url("b"))
        let size = UInt64(big.count)
        let ha = try FileHasher.edgeHash(path: dir.path("a"), size: size)
        #expect(ha == (try FileHasher.edgeHash(path: dir.path("b"), size: size)))
        // Khác ở đuôi → khác edge hash; khác ở giữa → edge hash giống (chỉ lọc sơ bộ), SHA-256 khác.
        big[big.count - 1] ^= 0xFF
        try big.write(to: dir.url("tail"))
        #expect(ha != (try FileHasher.edgeHash(path: dir.path("tail"), size: size)))
        big[big.count - 1] ^= 0xFF
        big[150 * 1024] ^= 0xFF
        try big.write(to: dir.url("middle"))
        #expect(ha == (try FileHasher.edgeHash(path: dir.path("middle"), size: size)))
        #expect(try FileHasher.sha256(path: dir.path("a")) != FileHasher.sha256(path: dir.path("middle")))
    }

    @Test func hasherDoesNotFollowSymlink() throws {
        let dir = try TemporaryDirectory("hash")
        try dir.file("real")
        try dir.symlink("link", to: dir.path("real"))
        #expect(throws: (any Error).self) { try FileHasher.sha256(path: dir.path("link")) }
    }
}
