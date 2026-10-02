import Foundation
import SweepCore
import SweepLogging

/// Package receipt của app cài bằng pkg (mức Trung bình, mục 11.3): `pkgutil --pkgs`, `--pkg-info-plist`, `--files`.
public enum PackageReceipts {
    public static let pkgutil = "/usr/sbin/pkgutil"

    public struct Info: Sendable, Hashable {
        public var id: String
        public var volume: String
        public var installLocation: String

        public init(id: String, volume: String = "/", installLocation: String = "/") {
            self.id = id
            self.volume = volume
            self.installLocation = installLocation
        }

        /// Đường dẫn tuyệt đối của một mục trong `--files` (tương đối với volume + install-location).
        public func absolutePath(_ relative: String) -> String {
            var base = volume.hasSuffix("/") ? volume : volume + "/"
            let loc = installLocation.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
            if !loc.isEmpty { base += loc + "/" }
            return (base + relative.trimmingCharacters(in: CharacterSet(charactersIn: "/"))).replacingOccurrences(of: "//", with: "/")
        }
    }

    /// Một receipt thuộc app, kèm các mục nó cài ra ngoài bundle app.
    public struct Receipt: Sendable, Hashable {
        public var info: Info
        public var ownedPaths: [String]
    }

    /// Thư mục chuẩn chứa mục do pkg cài; chỉ lấy **một cấp** con trực tiếp của chúng, không bao giờ lấy chính thư mục chuẩn.
    public static let containerDirectories = [
        "/Library/Application Support", "/Library/LaunchAgents", "/Library/LaunchDaemons", "/Library/PrivilegedHelperTools",
        "/Library/Preferences", "/Library/Caches", "/Library/Logs", "/Library/Internet Plug-Ins", "/Library/PreferencePanes",
        "/Library/Input Methods", "/Library/Audio/Plug-Ins/HAL", "/Library/Audio/Plug-Ins/Components",
        "/Library/Audio/Plug-Ins/VST", "/Library/Audio/Plug-Ins/VST3",
    ]

    // MARK: Phân tích đầu ra (hàm thuần)

    public static func parseLines(_ output: String) -> [String] {
        output.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    /// `pkgutil --file-info <path>`: các dòng `pkgid: <id>`.
    public static func parseFileInfo(_ output: String) -> [String] {
        parseLines(output).compactMap { line in
            guard line.hasPrefix("pkgid:") else { return nil }
            let id = line.dropFirst("pkgid:".count).trimmingCharacters(in: .whitespaces)
            return id.isEmpty ? nil : id
        }
    }

    public static func parseInfoPlist(_ data: Data) -> Info? {
        guard let dict = (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String: Any],
              let id = dict["pkgid"] as? String else { return nil }
        return Info(id: id, volume: dict["volume"] as? String ?? "/", installLocation: dict["install-location"] as? String ?? "/")
    }

    /// Mục cấp một trong thư mục chuẩn mà pkg đã cài (bỏ mục nằm trong bundle app, đã gỡ cùng app).
    public static func ownedTopPaths(files: [String], appPath: String) -> [String] {
        var result: [String] = []
        var seen = Set<String>()
        for file in files {
            if LeftoverMatcher.isPath(file, inside: appPath) { continue }
            for dir in containerDirectories where file.hasPrefix(dir + "/") {
                let rest = file.dropFirst(dir.count + 1)
                guard let first = rest.split(separator: "/").first, !first.isEmpty, first != ".", first != ".." else { continue }
                let top = dir + "/" + first
                if seen.insert(top).inserted { result.append(top) }
            }
        }
        return result
    }

    /// pkg có cài chính bundle app không.
    public static func installsApp(files: [String], appPath: String) -> Bool {
        files.contains { $0 == appPath || $0 == appPath + "/" }
    }

    // MARK: Gọi pkgutil

    /// Receipt thuộc app: pkg cài bundle app (`--file-info`), hoặc pkg cùng tiền tố bundle ID/hãng có cài bundle app.
    public static func receipts(appPath: String, bundleID: String, runner: ProcessRunner = ProcessRunner()) async -> [Receipt] {
        guard ProcessRunner.exists(pkgutil) else { return [] }
        var ids: [String] = []
        if let out = try? await runner.run(pkgutil, ["--file-info", appPath], timeout: 15), out.succeeded {
            // Không bao giờ đụng tới receipt của Apple.
            ids = parseFileInfo(out.stdoutString).filter { !$0.lowercased().hasPrefix("com.apple.") }
        }
        var verified = Set(ids)
        if let out = try? await runner.run(pkgutil, ["--pkgs"], timeout: 20), out.succeeded {
            let vendor = ReverseDNS.vendorPrefix(bundleID)
            let candidates = parseLines(out.stdoutString).filter { id in
                let l = id.lowercased()
                guard !l.hasPrefix("com.apple."), !verified.contains(id) else { return false }
                return l.hasPrefix(bundleID.lowercased()) || (vendor.map { l.hasPrefix($0 + ".") } ?? false)
            }
            ids += candidates.prefix(20)
        }

        var receipts: [Receipt] = []
        for id in ids {
            if Task.isCancelled { break }
            guard let infoOut = try? await runner.run(pkgutil, ["--pkg-info-plist", id], timeout: 15), infoOut.succeeded,
                  let info = parseInfoPlist(infoOut.stdout),
                  let filesOut = try? await runner.run(pkgutil, ["--files", id], timeout: 30), filesOut.succeeded else { continue }
            let files = parseLines(filesOut.stdoutString).map(info.absolutePath)
            if !verified.contains(id) {
                guard installsApp(files: files, appPath: appPath) else { continue }
                verified.insert(id)
            }
            receipts.append(Receipt(info: info, ownedPaths: ownedTopPaths(files: files, appPath: appPath)))
        }
        if !receipts.isEmpty { Log.info(.scan, "uninstaller", "Tìm thấy \(receipts.count) package receipt cho \(bundleID)") }
        return receipts
    }
}
