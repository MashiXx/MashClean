import SweepCore
import SweepIPC
import SweepPermissions
import SweepStorage
import SwiftUI

/// Cài đặt: chung, quyền & helper, rule, danh sách bỏ qua, nâng cao.
struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettings().tabItem { Label("Chung", systemImage: "gearshape") }
            PermissionSettings().tabItem { Label("Quyền", systemImage: "lock.shield") }
            IgnoreListSettings().tabItem { Label("Bỏ qua", systemImage: "eye.slash") }
            AdvancedSettings().tabItem { Label("Nâng cao", systemImage: "slider.horizontal.3") }
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

    var body: some View {
        Form {
            Section("Thanh menu") {
                Toggle("Hiện MashClean trên thanh menu", isOn: $menuBar)
                    .onChange(of: menuBar) { MenuBarLoginItem.setEnabled($0) }
                Toggle("Cảnh báo khi ổ đĩa sắp đầy (dưới 10% hoặc 10 GB)", isOn: $lowDisk)
                Stepper("Cảnh báo khi Thùng rác lớn hơn \(trashGB) GB", value: $trashGB, in: 1...200)
            }
            Section("Quét") {
                Stepper("File lớn: từ \(largeMB) MB", value: $largeMB, in: 10...10_000, step: 50)
                Stepper("File cũ: không dùng \(oldDays) ngày", value: $oldDays, in: 30...3650, step: 30)
                Toggle("Hiện mục rủi ro (ví dụ gói ngôn ngữ)", isOn: $showRisky)
            }
            Section("Cập nhật") {
                Picker("Kênh cập nhật", selection: $channel) {
                    Text("Ổn định").tag("stable")
                    Text("Beta").tag("beta")
                }
                .pickerStyle(.segmented)
            }
        }
        .formStyle(.grouped)
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
                    Text(holder.hasFullDiskAccess ? "Đã cấp" : "Chưa cấp — chế độ hạn chế")
                    Spacer()
                    Button("Mở System Settings") { Permissions.openFullDiskAccessSettings() }
                }
            }
            Section("Helper quản trị") {
                HStack {
                    statusDot(holder.helperStatus == .enabled)
                    Text(helperText)
                    Spacer()
                    Button("Cài / bật") {
                        do { holder.helperStatus = try HelperInstaller.ensureRegistered() } catch { message = error.localizedDescription }
                    }
                    Button("Gỡ helper") {
                        Task {
                            do { try await HelperInstaller.unregister(); holder.refreshPermissions(); message = "Đã gỡ helper" } catch { message = error.localizedDescription }
                        }
                    }
                }
                Text("Helper chỉ nhận lệnh có tên từ MashClean đã ký đúng Team ID, và kiểm tra lại mọi đường dẫn trước khi xoá.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Thông báo") {
                Button("Cho phép thông báo") { Task { await Permissions.requestNotifications() } }
            }
            if let message { Text(message).font(.caption).foregroundStyle(.orange) }
        }
        .formStyle(.grouped)
        .onAppear { holder.refreshPermissions() }
    }

    private var helperText: String {
        switch holder.helperStatus {
        case .enabled: "Đang hoạt động"
        case .requiresApproval: "Cần duyệt trong Login Items"
        case .notRegistered: "Chưa cài"
        case .notFound: "Không tìm thấy trong bundle"
        case .unreachable: "Không kết nối được"
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
            Text("Các mục bạn đã chọn \"Không bao giờ đề xuất\". Xoá khỏi danh sách để MashClean đề xuất lại.")
                .font(.callout).foregroundStyle(.secondary)
            Table(entries) {
                TableColumn("Loại") { e in Text(kindTitle(e.kind)) }.width(90)
                TableColumn("Giá trị") { e in Text(e.value.abbreviatingHome).lineLimit(1).truncationMode(.middle) }
                TableColumn("") { e in
                    Button("Xoá") {
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
        case .path: "Đường dẫn"
        case .rule: "Rule"
        case .bundleID: "Ứng dụng"
        }
    }

    private func reload() { entries = (try? holder.environment?.storage?.ignoreEntries()) ?? [] }
}

struct AdvancedSettings: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @AppStorage(AppSettings.Keys.analyticsEnabled, store: AppSettings.shared.defaults) private var analytics = false

    var body: some View {
        Form {
            Section("Bộ rule") {
                LabeledContent("Phiên bản rule", value: "\(holder.rulesVersion)")
                LabeledContent("Nguồn", value: sourceTitle)
                HStack {
                    Button("Kiểm tra rule mới") { Task { await holder.checkRuleUpdates(force: true) } }
                    if let last = holder.lastRuleUpdate { Text(last).font(.caption).foregroundStyle(.secondary) }
                }
            }
            Section("Quyền riêng tư") {
                Toggle("Gửi thống kê ẩn danh", isOn: $analytics)
                Text("Chỉ số liệu tổng hợp (dung lượng dọn theo nhóm, thời gian quét, rule hay lỗi). Không bao giờ gửi đường dẫn hay tên file. Tắt mặc định.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Chế độ thử") {
                Toggle("Dry run: chạy toàn bộ luồng nhưng không xoá gì", isOn: Binding(get: { holder.dryRun }, set: { holder.setDryRun($0) }))
                Text("Cũng bật được bằng biến môi trường MASHCLEAN_DRY_RUN=1 hoặc tham số --dry-run.").font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private var sourceTitle: String {
        switch holder.environment?.ruleStore.snapshot.source {
        case .bundled: "Đi kèm ứng dụng"
        case .downloaded: "Đã tải về"
        case .development: "Thư mục phát triển (chưa ký)"
        case .empty, .none: "Không có"
        }
    }
}
