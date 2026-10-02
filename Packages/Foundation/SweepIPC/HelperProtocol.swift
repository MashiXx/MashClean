import Foundation
import SweepCore

/// Protocol XPC dùng chung cho app và helper (mục 9.2).
/// Tham số dùng kiểu cơ bản (`String`, `Int`, `Data`) để tránh rủi ro `NSSecureCoding` với class tuỳ biến.
/// Kết quả trả về dạng JSON có phiên bản (`VersionedPayload`).
@objc public protocol HelperProtocol {
    /// Phiên bản giao thức, để app phát hiện helper cũ cần cập nhật.
    func protocolVersion(reply: @escaping (Int) -> Void)

    /// Xoá danh sách đường dẫn. Helper tự kiểm tra từng đường dẫn với PathPolicy phía root.
    /// `Data` = JSON `[RemoveResult]`.
    func removeItems(_ paths: [String], mode: Int, reply: @escaping (Data) -> Void)

    /// Chạy một tác vụ bảo trì có tên. Không nhận lệnh shell tự do. `Data` = JSON `MaintenanceResult`.
    func runMaintenance(_ task: String, reply: @escaping (Data) -> Void)

    /// `launchctl bootout system/<label>` rồi xoá plist trong `/Library/LaunchDaemons` hoặc `/Library/LaunchAgents`.
    func bootoutLaunchDaemon(label: String, plistPath: String, reply: @escaping (Data) -> Void)

    /// `tmutil thinlocalsnapshots <volume> ...`
    func thinLocalSnapshots(volume: String, reply: @escaping (Data) -> Void)

    /// `tmutil deletelocalsnapshots <date>` (LocalSnapshotRemover).
    func deleteLocalSnapshot(date: String, reply: @escaping (Data) -> Void)

    /// `pkgutil --forget <pkgid>` khi gỡ app cài bằng pkg (mục 11.3 bước 6).
    func forgetPackage(_ packageID: String, reply: @escaping (Data) -> Void)

    /// Tự gỡ helper (khi người dùng gỡ app).
    func uninstallSelf(reply: @escaping (Bool) -> Void)
}

/// Kết quả chung cho các lệnh không phải xoá file.
public struct HelperCommandResult: Codable, Sendable, Hashable {
    public var success: Bool
    public var message: String

    public init(success: Bool, message: String) {
        self.success = success
        self.message = message
    }
}

/// Code signing requirement cho hai chiều XPC (mục 9.4).
/// Team ID lấy từ Info.plist `MashCleanTeamID` (điền lúc build từ `DEVELOPMENT_TEAM`).
public enum HelperRequirement {
    public static var teamID: String? {
        let value = Bundle.main.object(forInfoDictionaryKey: "MashCleanTeamID") as? String
        guard let value, !value.isEmpty, !value.hasPrefix("$(") else { return nil }
        return value
    }

    /// Requirement app dùng để kiểm tra helper.
    public static var helper: String {
        requirement(identifiers: [MashCleanIdentifiers.helperLabel])
    }

    /// Requirement helper dùng để kiểm tra client: chỉ app chính và app menu bar ký bởi Team ID của mình.
    public static var client: String {
        requirement(identifiers: [MashCleanIdentifiers.appBundleID, MashCleanIdentifiers.menuBundleID])
    }

    static func requirement(identifiers: [String]) -> String {
        let ids = identifiers.map { "identifier \"\($0)\"" }.joined(separator: " or ")
        if let teamID {
            return #"anchor apple generic and certificate leaf[subject.OU] = ""# + teamID + #"" and ("# + ids + ")"
        }
        // Bản dev ký ad-hoc (không có Team ID): chỉ kiểm tra identifier. Không dùng cho bản phát hành.
        return "(" + ids + ")"
    }
}
