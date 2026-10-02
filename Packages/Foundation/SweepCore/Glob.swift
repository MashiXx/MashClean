import Darwin
import Foundation

/// Glob đã biên dịch (mục 8.3, 8.6): hỗ trợ `~`, `*`, `?`, `[...]` trong một thành phần đường dẫn và `**`
/// cho không hoặc nhiều thành phần. Biên dịch một lần khi nạp rule, không biên dịch lại mỗi lần quét.
public struct Glob: Sendable, Hashable, CustomStringConvertible {
    public enum Segment: Sendable, Hashable {
        case literal(String)
        case wildcard(String)   // pattern fnmatch cho một thành phần
        case globstar           // **
    }

    public let pattern: String
    public let segments: [Segment]

    /// - Parameters:
    ///   - pattern: glob, có thể bắt đầu bằng `~`.
    ///   - home: thư mục home dùng để mở rộng `~` và `${userHome}`.
    public init(_ pattern: String, home: String = NSHomeDirectory()) {
        var p = pattern
        if p == "~" { p = home } else if p.hasPrefix("~/") { p = home + p.dropFirst(1) }
        p = p.replacingOccurrences(of: "${userHome}", with: home)
        self.pattern = p
        segments = p.split(separator: "/", omittingEmptySubsequences: true).map { comp -> Segment in
            let s = String(comp)
            if s == "**" { return .globstar }
            if Glob.hasMagic(s) { return .wildcard(s) }
            return .literal(s)
        }
    }

    public var description: String { pattern }

    public static func hasMagic(_ s: some StringProtocol) -> Bool {
        s.contains(where: { $0 == "*" || $0 == "?" || $0 == "[" })
    }

    public var isLiteral: Bool { segments.allSatisfy { if case .literal = $0 { true } else { false } } }

    /// Phần đường dẫn cố định trước thành phần wildcard đầu tiên, ví dụ `~/Library/Caches` với `~/Library/Caches/*`.
    public var literalPrefix: String {
        var parts: [String] = []
        for seg in segments {
            guard case let .literal(s) = seg else { break }
            parts.append(s)
        }
        return "/" + parts.joined(separator: "/")
    }

    /// Đường dẫn có khớp toàn bộ glob không.
    public func matches(_ path: String) -> Bool {
        let comps = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        return Self.match(segments[...], comps[...])
    }

    /// Đường dẫn có thể là tổ tiên của một đường dẫn khớp không (dùng để cắt nhánh sớm khi duyệt).
    public func couldMatchDescendant(of path: String) -> Bool {
        let comps = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        return Self.prefixMatch(segments[...], comps[...])
    }

    /// Đường dẫn khớp glob hoặc nằm bên trong một đường dẫn khớp glob.
    public func matchesSelfOrAncestor(_ path: String) -> Bool {
        var comps = path.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        while !comps.isEmpty {
            if Self.match(segments[...], comps[...]) { return true }
            comps.removeLast()
        }
        return false
    }

    static func componentMatches(_ pattern: String, _ name: String) -> Bool {
        fnmatch(pattern, name, 0) == 0
    }

    private static func match(_ segs: ArraySlice<Segment>, _ comps: ArraySlice<String>) -> Bool {
        guard let first = segs.first else { return comps.isEmpty }
        switch first {
        case .globstar:
            // ** khớp 0..n thành phần
            var rest = comps
            while true {
                if match(segs.dropFirst(), rest) { return true }
                guard !rest.isEmpty else { return false }
                rest = rest.dropFirst()
            }
        case let .literal(s):
            guard let c = comps.first, c == s else { return false }
            return match(segs.dropFirst(), comps.dropFirst())
        case let .wildcard(p):
            guard let c = comps.first, componentMatches(p, c) else { return false }
            return match(segs.dropFirst(), comps.dropFirst())
        }
    }

    private static func prefixMatch(_ segs: ArraySlice<Segment>, _ comps: ArraySlice<String>) -> Bool {
        guard let c = comps.first else { return true }
        guard let first = segs.first else { return false }
        switch first {
        case .globstar: return true
        case let .literal(s): return c == s && prefixMatch(segs.dropFirst(), comps.dropFirst())
        case let .wildcard(p): return componentMatches(p, c) && prefixMatch(segs.dropFirst(), comps.dropFirst())
        }
    }
}

/// Tập glob dùng cho `exclude` (luôn thắng `match`).
public struct GlobSet: Sendable, Hashable {
    public let globs: [Glob]
    public init(_ patterns: [String], home: String = NSHomeDirectory()) {
        globs = patterns.map { Glob($0, home: home) }
    }
    public init(globs: [Glob]) { self.globs = globs }
    public var isEmpty: Bool { globs.isEmpty }
    public func matches(_ path: String) -> Bool { globs.contains { $0.matches(path) } }
    public func matchesSelfOrAncestor(_ path: String) -> Bool { globs.contains { $0.matchesSelfOrAncestor(path) } }
}
