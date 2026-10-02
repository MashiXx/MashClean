import Foundation

/// Nội dung một plist launchd (mục 11.6). Hàm thuần, tách ra để test.
public struct LaunchdJob: Sendable, Hashable {
    public var label: String
    public var program: String?
    public var programArguments: [String]
    public var runAtLoad: Bool
    /// `KeepAlive` là `true` hoặc một dictionary điều kiện (vẫn coi là giữ chạy).
    public var keepAlive: Bool
    public var disabled: Bool
    /// Plist của `SMAppService` nằm trong app: đường dẫn tương đối với bundle.
    public var bundleProgram: String?
    public var associatedBundleIDs: [String]
    public var startInterval: Int?
    public var hasCalendarInterval: Bool

    public init(label: String, program: String? = nil, programArguments: [String] = [], runAtLoad: Bool = false, keepAlive: Bool = false,
                disabled: Bool = false, bundleProgram: String? = nil, associatedBundleIDs: [String] = [], startInterval: Int? = nil,
                hasCalendarInterval: Bool = false) {
        self.label = label
        self.program = program
        self.programArguments = programArguments
        self.runAtLoad = runAtLoad
        self.keepAlive = keepAlive
        self.disabled = disabled
        self.bundleProgram = bundleProgram
        self.associatedBundleIDs = associatedBundleIDs
        self.startInterval = startInterval
        self.hasCalendarInterval = hasCalendarInterval
    }

    /// `Program` hoặc `ProgramArguments[0]`.
    public var executablePath: String? {
        if let program, !program.isEmpty { return program }
        return programArguments.first.flatMap { $0.isEmpty ? nil : $0 }
    }

    public static func parse(_ data: Data) -> LaunchdJob? {
        guard let dict = (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String: Any] else { return nil }
        return parse(dictionary: dict)
    }

    public static func parse(dictionary dict: [String: Any]) -> LaunchdJob? {
        guard let label = dict["Label"] as? String, !label.isEmpty else { return nil }
        let keepAlive: Bool = {
            if let b = dict["KeepAlive"] as? Bool { return b }
            if let d = dict["KeepAlive"] as? [String: Any] { return !d.isEmpty }
            return false
        }()
        let associated = dict["AssociatedBundleIdentifiers"]
        return LaunchdJob(
            label: label,
            program: dict["Program"] as? String,
            programArguments: dict["ProgramArguments"] as? [String] ?? [],
            runAtLoad: dict["RunAtLoad"] as? Bool ?? false,
            keepAlive: keepAlive,
            disabled: dict["Disabled"] as? Bool ?? false,
            bundleProgram: dict["BundleProgram"] as? String,
            associatedBundleIDs: (associated as? [String]) ?? ((associated as? String).map { [$0] } ?? []),
            startInterval: dict["StartInterval"] as? Int,
            hasCalendarInterval: dict["StartCalendarInterval"] != nil
        )
    }

    /// Mô tả ngắn khi nào job chạy.
    public var scheduleDescription: String {
        var parts: [String] = []
        if runAtLoad { parts.append("Chạy khi đăng nhập/khởi động") }
        if keepAlive { parts.append("Luôn giữ chạy") }
        if let startInterval { parts.append("Mỗi \(startInterval) giây") }
        if hasCalendarInterval { parts.append("Theo lịch") }
        return parts.isEmpty ? "Chạy theo yêu cầu" : parts.joined(separator: " · ")
    }
}

/// Phân tích đầu ra `launchctl` (mục 11.6). Hàm thuần.
public enum LaunchctlParser {
    public struct ListEntry: Sendable, Hashable {
        public var pid: Int32?
        public var lastExitStatus: Int32?
    }

    /// `launchctl list`: dòng `PID\tStatus\tLabel`, PID là `-` khi không chạy.
    public static func parseList(_ output: String) -> [String: ListEntry] {
        var result: [String: ListEntry] = [:]
        for line in output.split(whereSeparator: \.isNewline) {
            let cols = line.split(separator: "\t", omittingEmptySubsequences: false)
            guard cols.count >= 3, cols[0] != "PID" else { continue }
            let label = cols[2...].joined(separator: "\t").trimmingCharacters(in: .whitespaces)
            guard !label.isEmpty else { continue }
            result[label] = ListEntry(pid: Int32(cols[0].trimmingCharacters(in: .whitespaces)), lastExitStatus: Int32(cols[1].trimmingCharacters(in: .whitespaces)))
        }
        return result
    }

    /// `launchctl print system/<label>`: lấy `state = running` và `pid = 123` ở cấp đầu.
    public static func parsePrint(_ output: String) -> (state: String?, pid: Int32?) {
        var state: String?
        var pid: Int32?
        for raw in output.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if state == nil, line.hasPrefix("state = ") { state = String(line.dropFirst("state = ".count)) }
            if pid == nil, line.hasPrefix("pid = ") { pid = Int32(line.dropFirst("pid = ".count)) }
            if state != nil && pid != nil { break }
        }
        return (state, pid)
    }

    /// `launchctl print-disabled <domain>`: dòng `"label" => disabled|enabled|true|false`.
    public static func parseDisabled(_ output: String) -> [String: Bool] {
        var result: [String: Bool] = [:]
        for raw in output.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard line.hasPrefix("\""), let arrow = line.range(of: "\" => ") else { continue }
            let label = String(line[line.index(after: line.startIndex)..<arrow.lowerBound])
            let value = line[arrow.upperBound...].trimmingCharacters(in: .whitespaces)
            switch value {
            case "disabled", "true": result[label] = true
            case "enabled", "false": result[label] = false
            default: continue
            }
        }
        return result
    }
}
