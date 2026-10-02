import Foundation

/// Tác vụ bảo trì có tên (mục 9.3, 11.5). Helper chỉ nhận đúng các tên này, không nhận lệnh shell tự do.
public enum MaintenanceTaskName: String, Sendable, Codable, CaseIterable, Identifiable {
    case flushDNS
    case reindexSpotlight
    case freeRAM
    case runPeriodic
    case thinSnapshots
    case rebuildLaunchServices
    case speedUpMail

    public var id: String { rawValue }

    /// Tác vụ cần quyền root (chạy qua helper).
    public var requiresRoot: Bool {
        switch self {
        case .rebuildLaunchServices, .speedUpMail: false
        default: true
        }
    }

    /// Lệnh cố định: đường dẫn tuyệt đối và mảng tham số không đổi (mục 9.3).
    public var commands: [(tool: String, args: [String])] {
        switch self {
        case .flushDNS:
            [("/usr/bin/dscacheutil", ["-flushcache"]), ("/usr/bin/killall", ["-HUP", "mDNSResponder"])]
        case .reindexSpotlight:
            [("/usr/bin/mdutil", ["-E", "/"])]
        case .freeRAM:
            [("/usr/sbin/purge", [])]
        case .runPeriodic:
            [("/usr/sbin/periodic", ["daily", "weekly", "monthly"])]
        case .thinSnapshots:
            [("/usr/bin/tmutil", ["thinlocalsnapshots", "/", "999999999999", "4"])]
        case .rebuildLaunchServices:
            [("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
              ["-kill", "-r", "-domain", "local", "-domain", "system", "-domain", "user"])]
        case .speedUpMail:
            [] // xoá Envelope Index khi Mail đã tắt; làm ở app, không phải lệnh
        }
    }

    public var title: String {
        switch self {
        case .flushDNS: String(localized: "Xoá cache DNS")
        case .reindexSpotlight: String(localized: "Đánh lại chỉ mục Spotlight")
        case .freeRAM: String(localized: "Giải phóng RAM")
        case .runPeriodic: String(localized: "Chạy script bảo trì định kỳ")
        case .thinSnapshots: String(localized: "Xoá bớt snapshot Time Machine cục bộ")
        case .rebuildLaunchServices: String(localized: "Dựng lại Launch Services")
        case .speedUpMail: String(localized: "Tối ưu Mail")
        }
    }

    public var whenToRun: String {
        switch self {
        case .flushDNS: String(localized: "Khi gặp lỗi mạng hoặc vừa đổi DNS.")
        case .reindexSpotlight: String(localized: "Khi Spotlight tìm sai hoặc chậm.")
        case .freeRAM: String(localized: "Khi memory pressure cao.")
        case .runPeriodic: String(localized: "Khi máy hay tắt vào ban đêm nên script định kỳ không chạy.")
        case .thinSnapshots: String(localized: "Khi dung lượng purgeable lớn.")
        case .rebuildLaunchServices: String(localized: "Khi menu \"Open With\" bị trùng lặp.")
        case .speedUpMail: String(localized: "Khi Mail chậm. Mail sẽ tự dựng lại chỉ mục.")
        }
    }

    public var symbol: String {
        switch self {
        case .flushDNS: "network"
        case .reindexSpotlight: "magnifyingglass"
        case .freeRAM: "memorychip"
        case .runPeriodic: "calendar.badge.clock"
        case .thinSnapshots: "clock.arrow.circlepath"
        case .rebuildLaunchServices: "square.stack.3d.up"
        case .speedUpMail: "envelope"
        }
    }
}

/// Kết quả chạy một tác vụ bảo trì, trả về JSON qua XPC.
public struct MaintenanceResult: Sendable, Codable, Hashable {
    public var task: String
    public var success: Bool
    public var message: String
    public var durationSeconds: Double

    public init(task: String, success: Bool, message: String, durationSeconds: Double) {
        self.task = task
        self.success = success
        self.message = message
        self.durationSeconds = durationSeconds
    }
}
