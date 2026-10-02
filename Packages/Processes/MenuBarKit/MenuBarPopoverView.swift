import Charts
import DesignSystem
import SwiftUI
import SweepCore
import SystemMetrics

/// Nội dung popover menu bar (mục 11.9).
public struct MenuBarPopoverView: View {
    @ObservedObject var model: MenuBarModel
    let onQuit: () -> Void
    let onAction: () -> Void

    /// - Parameters:
    ///   - onAction: gọi sau khi bấm một nút mở app (để đóng popover).
    public init(model: MenuBarModel, onAction: @escaping () -> Void = {}, onQuit: @escaping () -> Void = { NSApp.terminate(nil) }) {
        self.model = model
        self.onAction = onAction
        self.onQuit = onQuit
    }

    private let columns = [GridItem(.flexible(), spacing: 10), GridItem(.flexible(), spacing: 10)]

    public var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            LazyVGrid(columns: columns, spacing: 10) {
                cpuTile
                memoryTile
                diskTile
                if model.snapshot.battery.hasBattery { batteryTile } else { trashTile }
            }
            networkTile
            freedRow
            actions
            if !model.externalVolumes.isEmpty { volumes }
            footer
        }
        .padding(16)
        .frame(width: 360)
        .fixedSize(horizontal: false, vertical: true)
        .background(Theme.background(for: .smartScan))
        .foregroundStyle(Theme.primaryText)
    }

    // MARK: Phần đầu

    private var header: some View {
        HStack(spacing: 10) {
            Image(systemName: "sparkles")
                .font(.system(size: 18, weight: .bold))
                .frame(width: 32, height: 32)
                .background(Circle().fill(Color.white.opacity(0.18)))
            VStack(alignment: .leading, spacing: 1) {
                Text("MashClean").font(Theme.Font.headline)
                Text("Đã bật máy \(Self.duration(model.snapshot.uptime))")
                    .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            if let memory = model.snapshot.memory, memory.pressure != .normal {
                Pill("RAM: \(memory.pressure.title)", color: memory.pressure == .critical ? Theme.risky : Theme.review)
            }
        }
    }

    // MARK: Ô số liệu

    private var cpuTile: some View {
        let cpu = model.snapshot.cpu
        return MetricTile(symbol: "cpu", title: "CPU",
                          value: cpu.map { Self.percent($0.usage) } ?? "–",
                          detail: cpu.map { "Người dùng \(Self.percent($0.user)) · Hệ thống \(Self.percent($0.system))" } ?? "Đang đo…",
                          history: model.cpuHistory, upperBound: 1, tint: Color(hex: 0x7CE7FF))
    }

    private var memoryTile: some View {
        let memory = model.snapshot.memory
        return MetricTile(symbol: "memorychip", title: "RAM",
                          value: memory.map { Self.percent($0.usedFraction) } ?? "–",
                          detail: memory.map { "\(ByteCount($0.used).formatted) / \(ByteCount($0.total).formatted)" } ?? "",
                          history: model.memoryHistory, upperBound: 1,
                          tint: memory?.pressure == .critical ? Theme.risky : Color(hex: 0xFFB86C))
    }

    private var diskTile: some View {
        let disk = model.snapshot.disk
        let low = disk.map { $0.freeFraction < 0.10 || $0.available < 10_000_000_000 } ?? false
        return MetricTile(symbol: "internaldrive", title: "Ổ đĩa",
                          value: disk.map { ByteCount($0.available).formatted } ?? "–",
                          detail: disk.map { "trống / \(ByteCount($0.total).formatted)" } ?? "",
                          history: model.diskHistory, upperBound: 1, tint: low ? Theme.risky : Theme.safe)
    }

    private var batteryTile: some View {
        let b = model.snapshot.battery
        let detail: String = {
            if b.isCharging { return b.minutesRemaining.map { "Đang sạc · đầy sau \(Self.minutes($0))" } ?? "Đang sạc" }
            if b.isOnACPower { return "Đang cắm sạc" }
            return b.minutesRemaining.map { "Còn \(Self.minutes($0))" } ?? "Đang tính…"
        }()
        return MetricTile(symbol: b.isCharging ? "battery.100.bolt" : Self.batterySymbol(b.level), title: "Pin",
                          value: Self.percent(b.level), detail: detail,
                          history: model.batteryHistory, upperBound: 1, tint: b.level < 0.2 && !b.isCharging ? Theme.risky : Theme.safe)
    }

    private var trashTile: some View {
        MetricTile(symbol: "trash", title: "Thùng rác",
                   value: model.snapshot.trashBytes.map { ByteCount($0).formatted } ?? "–",
                   detail: model.trashUnreadable ? "Cần Full Disk Access" : "Dung lượng Thùng rác",
                   history: nil, upperBound: nil, tint: Theme.review)
    }

    private var networkTile: some View {
        let net = model.snapshot.network
        return VStack(alignment: .leading, spacing: 6) {
            HStack {
                Label("Mạng", systemImage: "network").font(Theme.Font.caption.weight(.semibold)).foregroundStyle(Theme.secondaryText)
                Spacer()
                Label(net.map { Self.rate($0.inPerSecond) } ?? "–", systemImage: "arrow.down")
                    .font(.system(size: 12, weight: .semibold).monospacedDigit()).foregroundStyle(Color(hex: 0x7CE7FF))
                Label(net.map { Self.rate($0.outPerSecond) } ?? "–", systemImage: "arrow.up")
                    .font(.system(size: 12, weight: .semibold).monospacedDigit()).foregroundStyle(Color(hex: 0xFF8AD8))
            }
            NetworkChart(incoming: model.networkInHistory, outgoing: model.networkOutHistory)
                .frame(height: 34)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Theme.cardBackground))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(Theme.cardStroke))
    }

    // MARK: Thống kê và hành động

    private var freedRow: some View {
        HStack {
            Image(systemName: "checkmark.seal.fill").foregroundStyle(Theme.safe)
            Text("Đã dọn tháng này").font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            Spacer()
            Text(ByteCount(model.freedThisMonth).formatted).font(.system(size: 15, weight: .bold, design: .rounded).monospacedDigit())
        }
        .padding(.horizontal, 4)
    }

    private var actions: some View {
        VStack(spacing: 8) {
            Button {
                MenuBarLinks.open(MenuBarLinks.smartScan)
                onAction()
            } label: {
                Label("Smart Scan", systemImage: "sparkle.magnifyingglass").frame(maxWidth: .infinity)
            }
            .buttonStyle(PrimaryButtonStyle(accent: .smartScan))

            HStack(spacing: 8) {
                Button {
                    MenuBarLinks.open(MenuBarLinks.systemJunk)
                    onAction()
                } label: {
                    Label("Dọn rác", systemImage: "trash.circle").frame(maxWidth: .infinity)
                }
                Button {
                    MenuBarLinks.open(MenuBarLinks.freeRAM)
                    onAction()
                } label: {
                    Label("Giải phóng RAM", systemImage: "memorychip").frame(maxWidth: .infinity)
                }
            }
            .buttonStyle(GlassButtonStyle())
        }
    }

    private var volumes: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Ổ đĩa ngoài").font(Theme.Font.caption.weight(.semibold)).foregroundStyle(Theme.secondaryText)
            ForEach(model.externalVolumes) { volume in
                HStack(spacing: 8) {
                    Image(systemName: "externaldrive.fill").foregroundStyle(Theme.secondaryText)
                    VStack(alignment: .leading, spacing: 1) {
                        Text(volume.name).font(Theme.Font.body).lineLimit(1)
                        Text("\(ByteCount(volume.available).formatted) trống / \(ByteCount(volume.total).formatted)")
                            .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                    }
                    Spacer()
                    if model.ejecting.contains(volume.id) {
                        ProgressView().controlSize(.small)
                    } else {
                        Button { model.eject(volume) } label: { Image(systemName: "eject.fill") }
                            .buttonStyle(.plain)
                            .help("Tháo \(volume.name)")
                    }
                }
            }
            if let error = model.ejectError {
                Text(error).font(Theme.Font.caption).foregroundStyle(Theme.risky)
            }
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Theme.cardBackground))
    }

    private func styleBinding(_ option: StatusItemStyle) -> Binding<Bool> {
        Binding(
            get: { model.statusStyle.contains(option) },
            set: { on in
                if on { model.statusStyle.insert(option) } else { model.statusStyle.remove(option) }
            }
        )
    }

    private var footer: some View {
        HStack {
            Button {
                MenuBarLinks.openMainApp()
                onAction()
            } label: {
                Label("Mở MashClean", systemImage: "macwindow")
            }
            .buttonStyle(GlassButtonStyle())
            Spacer()
            Menu {
                Section("Hiện trên thanh menu") {
                    Toggle("CPU (%)", isOn: styleBinding(.cpu))
                    Toggle("RAM (%)", isOn: styleBinding(.memory))
                    Toggle("Mạng (tải về / tải lên)", isOn: styleBinding(.network))
                }
            } label: {
                Image(systemName: "gearshape.fill")
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help("Tuỳ chọn hiển thị")
            Button("Thoát", action: onQuit)
                .buttonStyle(GlassButtonStyle())
        }
    }

    // MARK: Định dạng

    static func percent(_ value: Double) -> String { "\(Int((value * 100).rounded()))%" }

    static func rate(_ bytesPerSecond: Double) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(bytesPerSecond), countStyle: .file) + "/s"
    }

    static func minutes(_ m: Int) -> String { m >= 60 ? "\(m / 60) giờ \(m % 60) phút" : "\(m) phút" }

    static func duration(_ seconds: TimeInterval) -> String {
        let total = Int(seconds)
        let days = total / 86_400
        let hours = total % 86_400 / 3_600
        let mins = total % 3_600 / 60
        if days > 0 { return "\(days) ngày \(hours) giờ" }
        if hours > 0 { return "\(hours) giờ \(mins) phút" }
        return "\(mins) phút"
    }

    static func batterySymbol(_ level: Double) -> String {
        switch level {
        case ..<0.13: "battery.0"
        case ..<0.38: "battery.25"
        case ..<0.63: "battery.50"
        case ..<0.88: "battery.75"
        default: "battery.100"
        }
    }
}

/// Ô số liệu có biểu đồ mini.
struct MetricTile: View {
    let symbol: String
    let title: String
    let value: String
    let detail: String
    let history: MetricHistory?
    let upperBound: Double?
    let tint: Color

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(title, systemImage: symbol)
                .font(Theme.Font.caption.weight(.semibold))
                .foregroundStyle(Theme.secondaryText)
            Text(value)
                .font(.system(size: 20, weight: .bold, design: .rounded).monospacedDigit())
                .lineLimit(1).minimumScaleFactor(0.7)
            Text(detail)
                .font(.system(size: 10))
                .foregroundStyle(Theme.tertiaryText)
                .lineLimit(1).minimumScaleFactor(0.8)
            if let history {
                Sparkline(history: history, upperBound: upperBound, tint: tint).frame(height: 26)
            } else {
                Spacer(minLength: 26)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Theme.cardBackground))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(Theme.cardStroke))
    }
}

/// Biểu đồ mini (Swift Charts), không trục, ~60 mẫu.
struct Sparkline: View {
    let history: MetricHistory
    let upperBound: Double?
    let tint: Color

    var body: some View {
        Chart(history.points) { point in
            AreaMark(x: .value("t", point.id), y: .value("v", point.value))
                .foregroundStyle(LinearGradient(colors: [tint.opacity(0.45), tint.opacity(0.02)], startPoint: .top, endPoint: .bottom))
                .interpolationMethod(.monotone)
            LineMark(x: .value("t", point.id), y: .value("v", point.value))
                .foregroundStyle(tint)
                .lineStyle(StrokeStyle(lineWidth: 1.5))
                .interpolationMethod(.monotone)
        }
        .chartXAxis(.hidden)
        .chartYAxis(.hidden)
        .chartYScale(domain: 0...max(upperBound ?? history.maxValue, 0.0001))
        .chartXScale(domain: xDomain)
    }

    private var xDomain: ClosedRange<Int> {
        let last = history.points.last?.id ?? 0
        return max(0, last - history.capacity + 1)...max(last, 1)
    }
}

/// Biểu đồ tốc độ mạng: hai đường vào/ra.
struct NetworkChart: View {
    let incoming: MetricHistory
    let outgoing: MetricHistory

    var body: some View {
        let top = max(incoming.maxValue, outgoing.maxValue, 1)
        Chart {
            ForEach(incoming.points) { p in
                LineMark(x: .value("t", p.id), y: .value("v", p.value), series: .value("s", "in"))
                    .foregroundStyle(Color(hex: 0x7CE7FF))
                    .interpolationMethod(.monotone)
            }
            ForEach(outgoing.points) { p in
                LineMark(x: .value("t", p.id), y: .value("v", p.value), series: .value("s", "out"))
                    .foregroundStyle(Color(hex: 0xFF8AD8))
                    .interpolationMethod(.monotone)
            }
        }
        .chartXAxis(.hidden)
        .chartYAxis(.hidden)
        .chartYScale(domain: 0...top)
        .chartLegend(.hidden)
    }
}
