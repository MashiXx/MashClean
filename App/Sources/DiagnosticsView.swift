import AppKit
import DesignSystem
import SweepLogging
import SweepPermissions
import SwiftUI

/// "Gửi báo cáo lỗi" (mục 17): gom log 24 giờ, phiên bản app/rule/macOS, kết quả kiểm tra quyền.
/// Người dùng xem trước toàn bộ nội dung rồi mới chọn lưu hoặc gửi.
struct DiagnosticsView: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @State private var report: DiagnosticReport?
    @State private var savedURL: URL?

    var body: some View {
        ZStack {
            Theme.background(for: .neutral).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 14) {
                Text("Gửi báo cáo lỗi").font(Theme.Font.title).foregroundStyle(.white)
                Text("Xem trước nội dung bên dưới. Đường dẫn chi tiết không có trong log ở mức thông thường. Không có gì được gửi đi nếu bạn không bấm.")
                    .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
                if let report {
                    ReportTextView(text: report.rendered)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(RoundedRectangle(cornerRadius: 12).fill(Color.black.opacity(0.3)))
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                    HStack {
                        if let savedURL { Text("Đã lưu: \(savedURL.lastPathComponent)").font(Theme.Font.caption).foregroundStyle(Theme.secondaryText) }
                        Spacer()
                        Button("Lưu ra file…") { save(report) }.buttonStyle(GlassButtonStyle())
                        Button("Gửi qua email…") { share(report) }.buttonStyle(PrimaryButtonStyle(accent: .neutral))
                    }
                } else {
                    Spacer()
                    ProgressView("Đang gom thông tin…").frame(maxWidth: .infinity)
                    Spacer()
                }
            }
            .padding(24)
        }
        .task { await build() }
    }

    private func build() async {
        let permissions = await Permissions.summary()
        report = DiagnosticReport(rulesVersion: holder.rulesVersion, permissions: permissions)
    }

    private func save(_ report: DiagnosticReport) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "MashClean-Diagnostics.txt"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        try? report.rendered.write(to: url, atomically: true, encoding: .utf8)
        savedURL = url
    }

    private func share(_ report: DiagnosticReport) {
        guard let file = try? report.write() else { return }
        let service = NSSharingService(named: .composeEmail)
        service?.recipients = [Bundle.main.object(forInfoDictionaryKey: "MashCleanSupportEmail") as? String ?? ""].filter { !$0.isEmpty }
        service?.subject = "MashClean — báo cáo lỗi"
        service?.perform(withItems: ["Báo cáo chẩn đoán đính kèm.", file])
    }
}

/// Văn bản dài (log hàng trăm KB) hiển thị bằng NSTextView: chỉ đọc, chọn được, tự cuộn.
/// Một `Text` SwiftUI khổng lồ đo kích thước sai và tràn ra khỏi màn hình.
struct ReportTextView: NSViewRepresentable {
    let text: String

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        if let tv = scroll.documentView as? NSTextView {
            tv.isEditable = false
            tv.isSelectable = true
            tv.drawsBackground = false
            tv.textContainerInset = NSSize(width: 10, height: 10)
            tv.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
            tv.textColor = .white
        }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let tv = scroll.documentView as? NSTextView, tv.string != text else { return }
        tv.string = text
        tv.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        tv.textColor = .white
    }
}
