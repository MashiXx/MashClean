import SweepCore
import SweepIPC
import SweepPermissions
import SweepStorage
import AppKit
import SwiftUI

/// Cài đặt: chung, quyền & helper, rule, danh sách bỏ qua, nâng cao.
struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettings().tabItem { Label(String(localized: "Chung"), systemImage: "gearshape") }
            PermissionSettings().tabItem { Label(String(localized: "Quyền"), systemImage: "lock.shield") }
            IgnoreListSettings().tabItem { Label(String(localized: "Bỏ qua"), systemImage: "eye.slash") }
            AdvancedSettings().tabItem { Label(String(localized: "Nâng cao"), systemImage: "slider.horizontal.3") }
        }
        .padding(20)
    }
}

struct GeneralSettings: View {
    @AppStorage(AppSettings.Keys.menuBarEnabled, store: AppSettings.shared.defaults) private var menuBar = true
    @AppStorage(AppSettings.Keys.updateChannel, store: AppSettings.shared.defaults) private var channel = "stable"
    @AppStorage(AppSettings.Keys.lowDiskAlerts, store: AppSettings.shared.defaults) private var lowDisk = true
    @AppStorage(AppSettings.Keys.trashAlertGB, store: AppSettings.shared.defaults) private var trashGB = 5
    @AppStorage(AppSettings.Keys.largeFileThresholdMB, store: AppSettings.shared.defaults) private var largeMB = 100
    @AppStorage(AppSettings.Keys.oldFileDays, store: AppSettings.shared.defaults) private var oldDays = 365
    @AppStorage(AppSettings.Keys.showRiskyItems, store: AppSettings.shared.defaults) private var showRisky = false

    @State private var language = AppLanguage.current
    @State private var needsRestart = false

    var body: some View {
        Form {
            // Tên ngôn ngữ luôn hiện song ngữ để nhận ra ở bất kỳ ngôn ngữ nào.
            Section("Ngôn ngữ · Language") {
                Picker("Ngôn ngữ · Language", selection: $language) {
                    ForEach(AppLanguage.allCases) { Text(verbatim: $0.displayName).tag($0) }
                }
                .onChange(of: language) { newValue in
                    AppLanguage.select(newValue)
                    needsRestart = true
                }
                if needsRestart {
                    HStack {
                        Text(String(localized: "Khởi động lại Clean Boost để áp dụng ngôn ngữ mới."))
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button(String(localized: "Khởi động lại")) { Self.restartForLanguage() }
                    }
                }
            }
            Section("Thanh menu") {
                Toggle(String(localized: "Hiện Clean Boost trên thanh menu"), isOn: $menuBar)
                    .onChange(of: menuBar) { MenuBarLoginItem.setEnabled($0) }
                Toggle(String(localized: "Cảnh báo khi ổ đĩa sắp đầy (dưới 10% hoặc 10 GB)"), isOn: $lowDisk)
                Stepper(String(localized: "Cảnh báo khi Thùng rác lớn hơn \(trashGB) GB"), value: $trashGB, in: 1...200)
            }
            Section(String(localized: "Quét")) {
                Stepper(String(localized: "File lớn: từ \(largeMB) MB"), value: $largeMB, in: 10...10_000, step: 50)
                Stepper(String(localized: "File cũ: không dùng \(oldDays) ngày"), value: $oldDays, in: 30...3650, step: 30)
                Toggle(String(localized: "Hiện mục rủi ro (ví dụ gói ngôn ngữ)"), isOn: $showRisky)
            }
            Section(String(localized: "Cập nhật")) {
                Picker(String(localized: "Kênh cập nhật"), selection: $channel) {
                    Text(String(localized: "Ổn định")).tag("stable")
                    Text("Beta").tag("beta")
                }
                .pickerStyle(.segmented)
            }
        }
        .formStyle(.grouped)
    }
}

extension GeneralSettings {
    /// Mở lại app chính và menu bar để ngôn ngữ mới có hiệu lực.
    @MainActor
    static func restartForLanguage() {
        AppRelauncher.restartForLanguageChange()
    }
}

struct PermissionSettings: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @State private var message: String?

    var body: some View {
        Form {
            Section("Full Disk Access") {
                HStack {
                    statusDot(holder.hasFullDiskAccess)
                    Text(holder.hasFullDiskAccess ? String(localized: "Đã cấp") : String(localized: "Chưa cấp — chế độ hạn chế"))
                    Spacer()
                    Button(String(localized: "Mở System Settings")) { Permissions.openFullDiskAccessSettings() }
                }
            }
            Section(String(localized: "Helper quản trị")) {
                HStack {
                    statusDot(holder.helperStatus == .enabled)
                    Text(helperText)
                    Spacer()
                    Button(String(localized: "Cài / bật")) {
                        do { holder.helperStatus = try HelperInstaller.ensureRegistered() } catch { message = error.localizedDescription }
                    }
                    Button(String(localized: "Gỡ helper")) {
                        Task {
                            do { try await HelperInstaller.unregister(); holder.refreshPermissions(); message = String(localized: "Đã gỡ helper") } catch { message = error.localizedDescription }
                        }
                    }
                }
                Text(String(localized: "Helper chỉ nhận lệnh có tên từ Clean Boost đã ký đúng Team ID, và kiểm tra lại mọi đường dẫn trước khi xoá."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section(String(localized: "Thông báo")) {
                Button(String(localized: "Cho phép thông báo")) { Task { await Permissions.requestNotifications() } }
            }
            if let message { Text(message).font(.caption).foregroundStyle(.orange) }
        }
        .formStyle(.grouped)
        .onAppear { holder.refreshPermissions() }
    }

    private var helperText: String {
        switch holder.helperStatus {
        case .enabled: String(localized: "Đang hoạt động")
        case .requiresApproval: String(localized: "Cần duyệt trong Login Items")
        case .notRegistered: String(localized: "Chưa cài")
        case .notFound: String(localized: "Không tìm thấy trong bundle")
        case .unreachable: String(localized: "Không kết nối được")
        }
    }

    private func statusDot(_ ok: Bool) -> some View {
        Circle().fill(ok ? Color.green : Color.orange).frame(width: 9, height: 9)
    }
}

struct IgnoreListSettings: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @State private var entries: [IgnoreEntry] = []

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(String(localized: "Các mục bạn đã chọn \"Không bao giờ đề xuất\". Xoá khỏi danh sách để Clean Boost đề xuất lại."))
                .font(.callout).foregroundStyle(.secondary)
            Table(entries) {
                TableColumn(String(localized: "Loại")) { e in Text(kindTitle(e.kind)) }.width(90)
                TableColumn(String(localized: "Giá trị")) { e in Text(e.value.abbreviatingHome).lineLimit(1).truncationMode(.middle) }
                TableColumn("") { e in
                    Button(String(localized: "Xoá")) {
                        if let id = e.id { try? holder.environment?.storage?.removeIgnore(id: id) }
                        reload()
                    }
                }
                .width(60)
            }
        }
        .onAppear(perform: reload)
    }

    private func kindTitle(_ k: IgnoreEntry.Kind) -> String {
        switch k {
        case .path: String(localized: "Đường dẫn")
        case .rule: "Rule"
        case .bundleID: String(localized: "Ứng dụng")
        }
    }

    private func reload() { entries = (try? holder.environment?.storage?.ignoreEntries()) ?? [] }
}

struct AdvancedSettings: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @AppStorage(AppSettings.Keys.analyticsEnabled, store: AppSettings.shared.defaults) private var analytics = false

    var body: some View {
        Form {
            Section(String(localized: "Bộ rule")) {
                LabeledContent(String(localized: "Phiên bản rule"), value: "\(holder.rulesVersion)")
                LabeledContent(String(localized: "Nguồn"), value: sourceTitle)
                HStack {
                    Button(String(localized: "Kiểm tra rule mới")) { Task { await holder.checkRuleUpdates(force: true) } }
                    if let last = holder.lastRuleUpdate { Text(last).font(.caption).foregroundStyle(.secondary) }
                }
            }
            Section(String(localized: "Quyền riêng tư")) {
                Toggle(String(localized: "Gửi thống kê ẩn danh"), isOn: $analytics)
                Text(String(localized: "Chỉ số liệu tổng hợp (dung lượng dọn theo nhóm, thời gian quét, rule hay lỗi). Không bao giờ gửi đường dẫn hay tên file. Tắt mặc định."))
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section(String(localized: "Chế độ thử")) {
                Toggle(String(localized: "Dry run: chạy toàn bộ luồng nhưng không xoá gì"), isOn: Binding(get: { holder.dryRun }, set: { holder.setDryRun($0) }))
                Text(String(localized: "Cũng bật được bằng biến môi trường MASHCLEAN_DRY_RUN=1 hoặc tham số --dry-run.")).font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private var sourceTitle: String {
        switch holder.environment?.ruleStore.snapshot.source {
        case .bundled: String(localized: "Đi kèm ứng dụng")
        case .downloaded: String(localized: "Đã tải về")
        case .development: String(localized: "Thư mục phát triển (chưa ký)")
        case .empty, .none: String(localized: "Không có")
        }
    }
}
