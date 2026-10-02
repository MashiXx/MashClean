import Foundation
import MetricKit
import os
import SweepCore

/// Gom dữ liệu cho màn "Gửi báo cáo lỗi" (mục 17): log 24 giờ gần nhất, phiên bản app/rule/macOS,
/// kết quả kiểm tra quyền. Người dùng xem trước nội dung rồi mới gửi.
public struct DiagnosticReport: Sendable {
    public var appVersion: String
    public var rulesVersion: UInt64
    public var osVersion: String
    public var hardware: String
    public var permissions: [String: String]
    public var logExcerpt: String
    public var createdAt: Date

    public init(rulesVersion: UInt64, permissions: [String: String]) {
        appVersion = (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev")
            + " (" + (Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "0") + ")"
        self.rulesVersion = rulesVersion
        osVersion = ProcessInfo.processInfo.operatingSystemVersionString
        var size = 0
        sysctlbyname("hw.model", nil, &size, nil, 0)
        var model = [CChar](repeating: 0, count: max(size, 1))
        sysctlbyname("hw.model", &model, &size, nil, 0)
        hardware = String(decoding: model.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }, as: UTF8.self)
        self.permissions = permissions
        createdAt = Date()
        FileLog.shared.flush()
        var excerpt = ""
        for file in FileLog.shared.recentLogFiles() {
            guard let text = try? String(contentsOf: file, encoding: .utf8) else { continue }
            excerpt += "===== \(file.lastPathComponent) =====\n"
            excerpt += String(text.suffix(200_000)) + "\n"
        }
        logExcerpt = excerpt
    }

    public var rendered: String {
        var s = """
        Clean Boost — báo cáo chẩn đoán
        Thời điểm: \(ISO8601DateFormatter().string(from: createdAt))
        App: \(appVersion)
        Rule: \(rulesVersion)
        macOS: \(osVersion)
        Máy: \(hardware)

        Quyền:

        """
        for (k, v) in permissions.sorted(by: { $0.key < $1.key }) { s += "  - \(k): \(v)\n" }
        s += String(localized: "\nLog 24 giờ gần nhất:\n\(logExcerpt)")
        return s
    }

    /// Ghi ra file tạm để người dùng đính kèm hoặc xem trước.
    public func write(to directory: URL = FileManager.default.temporaryDirectory) throws -> URL {
        let url = directory.appendingPathComponent("CleanBoost-Diagnostics-\(Int(createdAt.timeIntervalSince1970)).txt")
        try rendered.write(to: url, atomically: true, encoding: .utf8)
        return url
    }
}

/// Nhận báo cáo crash/hang từ MetricKit (macOS 12+), lưu vào thư mục log; không dùng SDK bên thứ ba.
public final class MetricKitCollector: NSObject, MXMetricManagerSubscriber, @unchecked Sendable {
    public static let shared = MetricKitCollector()

    public func start() {
        MXMetricManager.shared.add(self)
    }

    public func didReceive(_ payloads: [MXDiagnosticPayload]) {
        for payload in payloads {
            let json = payload.jsonRepresentation()
            let url = FileLog.shared.directory.appendingPathComponent("diagnostic-\(Int(Date().timeIntervalSince1970)).json")
            try? FileManager.default.createDirectory(at: FileLog.shared.directory, withIntermediateDirectories: true)
            try? json.write(to: url)
            Log.warning(.ui, "metrickit", "Nhận \(payload.crashDiagnostics?.count ?? 0) crash diagnostic")
        }
    }
}

/// Thống kê ẩn danh (mục 17): tắt mặc định, chỉ bật khi người dùng đồng ý.
/// Chỉ gửi số liệu tổng hợp (dung lượng dọn theo nhóm, thời gian quét, rule hay lỗi), không gửi đường dẫn hay tên file.
public final class Analytics: @unchecked Sendable {
    public static let shared = Analytics()

    public struct Snapshot: Codable, Sendable {
        public var cleanedBytesByCategory: [String: Int64] = [:]
        public var scanDurations: [String: [Double]] = [:]
        public var failingRules: [String: Int] = [:]
        public var periodStart = Date()
    }

    private let state = Locked(Snapshot())
    public var isEnabled: () -> Bool = { false }
    /// Endpoint lấy từ Info.plist `MashCleanAnalyticsURL`; không cấu hình thì không gửi gì.
    public var endpoint: URL? = (Bundle.main.object(forInfoDictionaryKey: "MashCleanAnalyticsURL") as? String).flatMap(URL.init(string:))

    public func recordClean(category: String, bytes: Int64) {
        guard isEnabled() else { return }
        state.withLock { $0.cleanedBytesByCategory[category, default: 0] += bytes }
    }

    public func recordScan(kind: String, duration: Double) {
        guard isEnabled() else { return }
        state.withLock { $0.scanDurations[kind, default: []].append(duration) }
    }

    public func recordRuleFailure(_ ruleID: String) {
        guard isEnabled() else { return }
        state.withLock { $0.failingRules[ruleID, default: 0] += 1 }
    }

    public var snapshot: Snapshot { state.current }

    /// Gửi gói tổng hợp rồi đặt lại. Không làm gì nếu chưa đồng ý hoặc không có endpoint.
    public func flush() async {
        guard isEnabled(), let endpoint else { return }
        let payload = state.withLock { s -> Snapshot in
            let copy = s
            s = Snapshot()
            return copy
        }
        var request = URLRequest(url: endpoint)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try? JSONEncoder().encode(payload)
        _ = try? await URLSession.shared.data(for: request)
    }
}
