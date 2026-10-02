import DesignSystem
import SweepCore
import SweepIPC
import SweepPermissions
import SweepStorage
import SwiftUI

/// Luồng onboarding (mục 10.2): giới thiệu → Full Disk Access (kiểm tra lại mỗi 2 giây, cho phép bỏ qua)
/// → helper (SMAppService.register, hướng dẫn duyệt trong Login Items nếu cần) → sẵn sàng.
struct OnboardingView: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @Environment(\.dismiss) private var dismiss
    @State private var step: Step = .welcome
    @State private var helperError: String?
    @State private var menuBar = AppSettings.shared.menuBarEnabled
    @State private var notifications = true
    private let timer = Timer.publish(every: 2, on: .main, in: .common).autoconnect()

    enum Step: Int, CaseIterable {
        case welcome, fullDiskAccess, helper, done
    }

    var body: some View {
        ZStack {
            Theme.background(for: .smartScan)
            VStack(spacing: 22) {
                stepIndicator
                Spacer(minLength: 0)
                content
                Spacer(minLength: 0)
            }
            .padding(32)
        }
        .frame(width: 640, height: 520)
        .onReceive(timer) { _ in poll() }
    }

    private var stepIndicator: some View {
        HStack(spacing: 8) {
            ForEach(Step.allCases, id: \.rawValue) { s in
                Capsule().fill(s.rawValue <= step.rawValue ? Color.white : Color.white.opacity(0.25)).frame(width: 36, height: 4)
            }
        }
    }

    @ViewBuilder private var content: some View {
        switch step {
        case .welcome:
            VStack(spacing: 18) {
                FeatureHeader(symbol: "sparkles", title: "Chào mừng đến với MashClean",
                              subtitle: "Dọn dẹp an toàn, minh bạch: mọi mục đề xuất xoá đều giải thích được, file của bạn mặc định đi qua Thùng rác.")
                Button("Bắt đầu") { step = holder.hasFullDiskAccess ? .helper : .fullDiskAccess }
                    .buttonStyle(PrimaryButtonStyle(accent: .smartScan))
            }
        case .fullDiskAccess:
            VStack(spacing: 16) {
                FeatureHeader(symbol: "lock.shield", title: "Cấp Full Disk Access",
                              subtitle: "MashClean cần quyền này để đọc cache, log và dữ liệu của app khác trong ~/Library (Mail, Safari, container).")
                VStack(alignment: .leading, spacing: 8) {
                    guideLine(1, "Bấm \"Mở System Settings\" bên dưới.")
                    guideLine(2, "Tìm MashClean trong danh sách Full Disk Access và bật công tắc.")
                    guideLine(3, "Nếu chưa có, bấm dấu + rồi chọn MashClean trong thư mục Applications (hoặc kéo app vào danh sách).")
                    guideLine(4, "Quay lại đây, MashClean tự nhận ra sau vài giây.")
                }
                .padding(14)
                .background(RoundedRectangle(cornerRadius: 12).fill(Color.black.opacity(0.2)))
                HStack(spacing: 12) {
                    Button("Bỏ qua") {
                        AppSettings.shared.fdaSkipped = true
                        step = .helper
                    }
                    .buttonStyle(GlassButtonStyle())
                    Button("Mở System Settings") { Permissions.openFullDiskAccessSettings() }
                        .buttonStyle(PrimaryButtonStyle(accent: .smartScan))
                }
                Text("Bỏ qua thì app chạy ở chế độ hạn chế: chỉ quét những gì đọc được, các nhóm khác hiện nhãn \"Cần Full Disk Access\".")
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).multilineTextAlignment(.center)
            }
        case .helper:
            VStack(spacing: 16) {
                FeatureHeader(symbol: "gearshape.2", title: "Cài thành phần quản trị",
                              subtitle: "Một helper nhỏ chạy với quyền root để xoá cache hệ thống và chạy tác vụ bảo trì. Helper chỉ nhận lệnh có tên cụ thể từ MashClean đã ký, không bao giờ chạy lệnh tuỳ ý.")
                switch holder.helperStatus {
                case .enabled:
                    Label("Helper đã sẵn sàng", systemImage: "checkmark.seal.fill").foregroundStyle(.white)
                case .requiresApproval:
                    VStack(spacing: 8) {
                        Text("Hãy bật MashClean trong System Settings > General > Login Items & Extensions (mục \"Allow in the Background\").")
                            .font(Theme.Font.body).foregroundStyle(.white).multilineTextAlignment(.center)
                        Button("Mở Login Items") { Permissions.openLoginItemsSettings() }.buttonStyle(GlassButtonStyle())
                    }
                default:
                    EmptyView()
                }
                if let helperError {
                    Text(helperError).font(Theme.Font.caption).foregroundStyle(Theme.review).multilineTextAlignment(.center)
                }
                HStack(spacing: 12) {
                    Button("Để sau") { step = .done }.buttonStyle(GlassButtonStyle())
                    if holder.helperStatus == .enabled {
                        Button("Tiếp tục") { step = .done }.buttonStyle(PrimaryButtonStyle(accent: .smartScan))
                    } else {
                        Button("Cài helper") { registerHelper() }.buttonStyle(PrimaryButtonStyle(accent: .smartScan))
                    }
                }
            }
        case .done:
            VStack(spacing: 18) {
                FeatureHeader(symbol: "checkmark.circle", title: "Sẵn sàng quét",
                              subtitle: holder.hasFullDiskAccess ? "Mọi thứ đã sẵn sàng." : "Đang ở chế độ hạn chế. Bạn có thể cấp Full Disk Access sau trong Cài đặt.")
                VStack(alignment: .leading, spacing: 8) {
                    Toggle("Hiện MashClean trên thanh menu (RAM, CPU, dung lượng trống)", isOn: $menuBar)
                    Toggle("Cảnh báo khi ổ đĩa sắp đầy", isOn: $notifications)
                }
                .toggleStyle(.switch).foregroundStyle(.white)
                Button("Bắt đầu dùng") { finish() }.buttonStyle(PrimaryButtonStyle(accent: .smartScan))
            }
        }
    }

    private func guideLine(_ n: Int, _ text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text("\(n)").font(.system(size: 12, weight: .bold)).frame(width: 20, height: 20).background(Circle().fill(Color.white.opacity(0.25)))
            Text(text).font(Theme.Font.body)
        }
        .foregroundStyle(.white)
    }

    private func poll() {
        holder.refreshPermissions()
        if step == .fullDiskAccess && holder.hasFullDiskAccess { step = .helper }
    }

    private func registerHelper() {
        do {
            holder.helperStatus = try HelperInstaller.ensureRegistered()
            helperError = nil
        } catch {
            helperError = "Không đăng ký được helper: \(error.localizedDescription). Bản build ký ad-hoc có thể không được launchd chấp nhận; cần ký bằng Developer ID."
        }
    }

    private func finish() {
        AppSettings.shared.menuBarEnabled = menuBar
        AppSettings.shared.lowDiskAlerts = notifications
        AppSettings.shared.onboardingCompleted = true
        MenuBarLoginItem.setEnabled(menuBar)
        if notifications { Task { await Permissions.requestNotifications() } }
        holder.showOnboarding = false
        dismiss()
    }
}
