import Charts
import CleanEngine
import DesignSystem
import SharedUI
import SweepCore
import SweepStorage
import SwiftUI

/// Màn "Lịch sử" (mục 15.3): mỗi lần dọn, từng mục đã xử lý, nút "Khôi phục" cho mục đã chuyển vào Thùng rác.
struct HistoryView: View {
    let services: ScanServices
    @State private var operations: [CleanOperation] = []
    @State private var selected: CleanOperation?
    @State private var items: [CleanItemLog] = []
    @State private var message: String?
    @State private var totalFreed: Int64 = 0

    var body: some View {
        ZStack {
            Theme.background(for: .neutral).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 14) {
                HStack(alignment: .firstTextBaseline) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Lịch sử dọn dẹp").font(Theme.Font.title).foregroundStyle(.white)
                        Text("Tổng đã giải phóng: \(ByteCount(totalFreed).formatted). Mục chuyển vào Thùng rác có thể khôi phục trong 90 ngày.")
                            .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
                    }
                    Spacer()
                    Button { reload() } label: { Label("Làm mới", systemImage: "arrow.clockwise") }.buttonStyle(GlassButtonStyle())
                }
                if operations.count > 1 { chart }
                HStack(spacing: 12) {
                    List(operations, selection: Binding(get: { selected?.id }, set: { id in select(operations.first { $0.id == id }) })) { op in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(op.startedAt.formatted(date: .abbreviated, time: .shortened)).font(.system(size: 13, weight: .semibold))
                            Text("\(ByteCount(op.freedBytes ?? 0).formatted) · \(op.itemsOk) thành công\(op.itemsFailed > 0 ? " · \(op.itemsFailed) lỗi" : "")")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        .tag(op.id)
                    }
                    .frame(width: 260)
                    .scrollContentBackground(.hidden)
                    .background(RoundedRectangle(cornerRadius: 12).fill(Color.black.opacity(0.2)))

                    detail
                }
                if let message { Text(message).font(Theme.Font.caption).foregroundStyle(Theme.review) }
            }
            .padding(24)
        }
        .onAppear(perform: reload)
        .onReceive(DistributedNotificationCenter.default().publisher(for: MashCleanIdentifiers.didCleanNotification)) { _ in reload() }
    }

    private var chart: some View {
        Chart(operations.prefix(30).reversed(), id: \.id) { op in
            BarMark(x: .value("Ngày", op.startedAt, unit: .day), y: .value("Đã dọn (MB)", Double(op.freedBytes ?? 0) / 1_000_000))
                .foregroundStyle(.white.opacity(0.8))
        }
        .chartYAxisLabel("MB")
        .frame(height: 110)
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.black.opacity(0.2)))
    }

    @ViewBuilder private var detail: some View {
        VStack(alignment: .leading, spacing: 8) {
            if selected == nil {
                Text("Chọn một lần dọn để xem chi tiết").foregroundStyle(Theme.secondaryText).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                Table(items) {
                    TableColumn("Mục") { item in
                        Text(item.path.abbreviatingHome).lineLimit(1).truncationMode(.middle).help(item.path)
                    }
                    TableColumn("Cách xoá") { item in Text(strategyTitle(item.strategy)) }.width(110)
                    TableColumn("Dung lượng") { item in Text(ByteCount(item.size).formatted) }.width(90)
                    TableColumn("Kết quả") { item in Text(resultTitle(item.result)) }.width(120)
                    TableColumn("") { item in
                        if item.isRestorable {
                            Button("Khôi phục") { restore(item) }
                        }
                    }
                    .width(90)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func strategyTitle(_ s: String) -> String {
        switch s {
        case "trash": "Thùng rác"
        case "delete": "Xoá vĩnh viễn"
        case "deleteContents": "Xoá nội dung"
        default: s.replacingOccurrences(of: "custom:", with: "")
        }
    }

    private func resultTitle(_ r: String) -> String {
        if r == "ok" { return "Thành công" }
        if r == "restored" { return "Đã khôi phục" }
        if r.hasPrefix("skipped:dryRun") { return "Chạy thử" }
        if r.hasPrefix("skipped") { return "Bỏ qua" }
        return "Lỗi (\(r.replacingOccurrences(of: "failed:", with: "")))"
    }

    private func reload() {
        operations = (try? services.storage?.operations()) ?? []
        totalFreed = (try? services.storage?.totalFreed()) ?? 0
        if let s = selected { select(operations.first { $0.id == s.id }) }
    }

    private func select(_ op: CleanOperation?) {
        selected = op
        items = op.flatMap { try? services.storage?.items(of: $0.id) } ?? []
    }

    private func restore(_ item: CleanItemLog) {
        do {
            try services.cleanEngine.restore(item)
            message = "Đã khôi phục \(item.path.abbreviatingHome)"
        } catch {
            message = String(describing: error)
        }
        select(selected)
    }
}
