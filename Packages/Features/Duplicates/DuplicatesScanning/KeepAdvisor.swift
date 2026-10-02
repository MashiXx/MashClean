import Foundation

/// Gợi ý bản nên giữ trong một nhóm trùng lặp (mục 11.8): thư mục quan trọng hơn,
/// tên không có dấu hiệu bản sao, bản cũ hơn, đường dẫn ngắn hơn.
public enum KeepAdvisor {
    /// Documents > Desktop > Pictures/Movies/Music > thư mục khác > Downloads.
    public static func importance(of path: String, home: String) -> Int {
        let prefix = home.hasSuffix("/") ? home : home + "/"
        guard path.hasPrefix(prefix) else { return 2 }
        let first = path.dropFirst(prefix.count).split(separator: "/").first.map(String.init) ?? ""
        switch first {
        case "Documents": return 5
        case "Desktop": return 4
        case "Pictures", "Movies", "Music": return 3
        case "Downloads": return 1
        default: return 2
        }
    }

    /// Tên (bỏ đuôi) chứa "copy", "bản sao", kết thúc bằng "(1)" hoặc " 2".
    public static func hasCopyMarker(_ fileName: String) -> Bool {
        let base = ((fileName as NSString).deletingPathExtension).lowercased()
        if base.contains("copy") || base.contains("bản sao") || base.contains("ban sao") { return true }
        if base.range(of: #"\(\d+\)$"#, options: .regularExpression) != nil { return true }
        if base.range(of: #" \d{1,2}$"#, options: .regularExpression) != nil { return true }
        return false
    }

    public struct Candidate: Sendable, Hashable {
        public var path: String
        public var date: Date
        public init(path: String, date: Date) {
            self.path = path
            self.date = date
        }
    }

    /// Chỉ số bản nên giữ.
    public static func suggestKeep(_ files: [Candidate], home: String) -> Int? {
        guard !files.isEmpty else { return nil }
        return files.indices.min { a, b in isBetter(files[a], than: files[b], home: home) }
    }

    static func isBetter(_ a: Candidate, than b: Candidate, home: String) -> Bool {
        let ia = importance(of: a.path, home: home), ib = importance(of: b.path, home: home)
        if ia != ib { return ia > ib }
        let ca = hasCopyMarker((a.path as NSString).lastPathComponent), cb = hasCopyMarker((b.path as NSString).lastPathComponent)
        if ca != cb { return !ca }
        if a.date != b.date { return a.date < b.date }
        if a.path.count != b.path.count { return a.path.count < b.path.count }
        return a.path < b.path
    }
}
