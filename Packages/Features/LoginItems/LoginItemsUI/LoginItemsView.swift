import AppKit
import DesignSystem
import LoginItemsDomain
import LoginItemsScanning
import NodeTree
import SharedUI
import SweepCore
import SweepLogging
import SweepPermissions
import SwiftUI

/// View model màn Login Items (mục 11.6).
@MainActor
final class LoginItemsViewModel: ObservableObject {
    @Published private(set) var items: [LaunchItem] = []
    @Published private(set) var isLoading = false
    @Published private(set) var busy: Set<String> = []
    @Published var message: String?
    @Published var pendingRemoval: LaunchItem?
    @Published var showOnlyProblems = false

    let services: ScanServices
    let feature: LoginItemsFeature
    private let controller: LoginItemsController
    private var icons: [String: NSImage] = [:]

    init(services: ScanServices, feature: LoginItemsFeature) {
        self.services = services
        self.feature = feature
        controller = LoginItemsController(cleanEngine: services.cleanEngine)
    }

    func loadIfNeeded() { if items.isEmpty && !isLoading { reload() } }

    func reload() {
        isLoading = true
        let scanner = feature.scanner
        let appScanner = services.appScanner
        Task {
            // Tra app sở hữu theo danh sách app (lấy từ cache `app_usage_cache`, nhanh).
            let apps = await appScanner.scan()
            items = await scanner.scan(installedApps: apps)
            isLoading = false
        }
    }

    func items(in domain: VirtualItem.LaunchDomain) -> [LaunchItem] {
        items.filter { $0.domain == domain && (!showOnlyProblems || $0.isBroken || $0.isDisabled) }
            .sorted { ($0.isBroken ? 0 : 1, $0.displayName.lowercased()) < ($1.isBroken ? 0 : 1, $1.displayName.lowercased()) }
    }

    var brokenCount: Int { items.filter(\.isBroken).count }
    var runningCount: Int { items.filter { if case .running = $0.state { true } else { false } }.count }

    func icon(for item: LaunchItem) -> NSImage? {
        guard let url = item.owner?.appURL, item.owner?.isInstalled == true else { return nil }
        if let cached = icons[url.path] { return cached }
        let image = NSWorkspace.shared.icon(forFile: url.path)
        icons[url.path] = image
        return image
    }

    func setEnabled(_ item: LaunchItem, _ on: Bool) {
        busy.insert(item.id)
        Task {
            do {
                if on { try await controller.enable(item) } else { try await controller.disable(item) }
            } catch {
                message = "\(on ? "Không bật được" : "Không tắt được") \(item.label): \(error)"
            }
            busy.remove(item.id)
            refresh(item)
        }
    }

    func confirmRemoval(_ item: LaunchItem) {
        pendingRemoval = nil
        busy.insert(item.id)
        let dryRun = services.settings.dryRun
        Task {
            let report = await controller.remove([item], dryRun: dryRun)
            busy.remove(item.id)
            if let failed = report.failed.first, case let .failed(_, msg) = failed.result.outcome {
                message = "Không xoá được \(item.label): \(msg)"
            } else if report.entries.isEmpty {
                message = "Không xoá được \(item.label): đường dẫn bị chặn bởi chính sách an toàn"
            } else if !report.dryRun {
                items.removeAll { $0.id == item.id }
                message = "Đã xoá \(item.displayName). Plist đã chuyển vào Thùng rác hoặc xoá qua helper."
            } else {
                message = "Chạy thử: sẽ xoá \(item.label)"
            }
        }
    }

    /// Đọc lại trạng thái sau khi bật/tắt.
    private func refresh(_ item: LaunchItem) {
        let scanner = feature.scanner
        let apps = items
        Task {
            let fresh = await scanner.scan()
            items = fresh.map { new in
                var n = new
                if let old = apps.first(where: { $0.id == new.id }), n.owner == nil { n.owner = old.owner }
                return n
            }
        }
    }
}

/// Màn Login Items và Background Items (mục 11.6).
public struct LoginItemsView: View {
    @StateObject private var model: LoginItemsViewModel

    public static let appearance = FeatureAppearance(
        accent: .maintenance, symbol: "power.circle",
        title: "Login Items",
        subtitle: "LaunchAgent và Daemon chạy nền: xem của app nào, đang chạy không, tắt tạm hoặc xoá mục hỏng."
    )

    public init(services: ScanServices, feature: LoginItemsFeature) {
        _model = StateObject(wrappedValue: LoginItemsViewModel(services: services, feature: feature))
    }

    public var body: some View {
        ZStack {
            Theme.background(for: Self.appearance.accent).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 14) {
                header
                if let message = model.message {
                    NoticeBanner(symbol: "info.circle", text: message, actionTitle: "Đóng") { model.message = nil }
                }
                modernItemsNotice
                if model.isLoading && model.items.isEmpty {
                    Spacer()
                    HStack { Spacer(); ProgressView().controlSize(.large); Spacer() }
                    Spacer()
                } else {
                    list
                }
            }
            .padding(24)
        }
        .onAppear { model.loadIfNeeded() }
        .alert(item: $model.pendingRemoval) { item in
            Alert(title: Text("Xoá \(item.displayName)?"),
                  message: Text("Gỡ \(item.label) khỏi launchd và \(item.domain == .userAgent ? "chuyển plist vào Thùng rác" : "xoá plist qua helper (cần quyền quản trị)"). App sở hữu có thể tạo lại mục này khi mở."),
                  primaryButton: .destructive(Text("Xoá")) { model.confirmRemoval(item) },
                  secondaryButton: .cancel(Text("Huỷ")))
        }
    }

    private var header: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 4) {
                Text(Self.appearance.title).font(Theme.Font.title).foregroundStyle(.white)
                Text("\(model.items.count) mục · \(model.runningCount) đang chạy · \(model.brokenCount) hỏng")
                    .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            Toggle("Chỉ hiện mục hỏng/đã tắt", isOn: $model.showOnlyProblems).toggleStyle(.switch).font(Theme.Font.caption).foregroundStyle(.white)
            Button("Làm mới", action: model.reload).buttonStyle(GlassButtonStyle()).disabled(model.isLoading)
        }
    }

    /// Login item hiện đại (`SMAppService`) của app khác: không có API công khai để liệt kê.
    private var modernItemsNotice: some View {
        Card(padding: 12) {
            HStack(spacing: 12) {
                Image(systemName: "person.crop.circle.badge.checkmark").font(.system(size: 24)).foregroundStyle(.white)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Login item hiện đại").font(Theme.Font.headline).foregroundStyle(.white)
                    Text("App mở cùng hệ thống và mục \"Cho phép chạy nền\" đăng ký qua SMAppService chỉ quản lý được trong System Settings > General > Login Items.")
                        .font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).lineLimit(2)
                }
                // Không dùng fixedSize: khi đo kích thước tối thiểu, chữ bị ép rộng ~0 nên cao vọt và đẩy lệch cả cửa sổ.
                .frame(maxWidth: .infinity, alignment: .leading)
                Button("Mở Login Items") { Permissions.openLoginItemsSettings() }.buttonStyle(GlassButtonStyle())
            }
        }
    }

    private var list: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                ForEach([VirtualItem.LaunchDomain.userAgent, .globalAgent, .globalDaemon], id: \.self) { domain in
                    let rows = model.items(in: domain)
                    VStack(alignment: .leading, spacing: 6) {
                        HStack {
                            Text(domain.title).font(Theme.Font.headline).foregroundStyle(.white)
                            Text(domain.directory).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                            Spacer()
                            Text("\(rows.count)").font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                        }
                        if rows.isEmpty {
                            Text("Không có mục nào").font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).padding(.vertical, 4)
                        } else {
                            VStack(spacing: 2) {
                                ForEach(rows) { item in
                                    LaunchItemRow(item: item, icon: model.icon(for: item), isBusy: model.busy.contains(item.id),
                                                  onToggle: { model.setEnabled(item, $0) }, onRemove: { model.pendingRemoval = item })
                                }
                            }
                            .padding(6)
                            .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
                        }
                    }
                }
            }
        }
    }
}

struct LaunchItemRow: View {
    let item: LaunchItem
    let icon: NSImage?
    let isBusy: Bool
    let onToggle: (Bool) -> Void
    let onRemove: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Group {
                if let icon { Image(nsImage: icon).resizable() } else {
                    Image(systemName: item.isBroken ? "exclamationmark.triangle" : "gearshape.2").resizable().scaledToFit().padding(4)
                        .foregroundStyle(item.isBroken ? Theme.risky : .white.opacity(0.85))
                }
            }
            .frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(item.displayName).font(.system(size: 13, weight: .medium)).foregroundStyle(.white).lineLimit(1)
                    statusPill
                }
                Text(item.label).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText).lineLimit(1).textSelection(.enabled)
                Text(detailLine).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText).lineLimit(1)
                    .help(item.job.executablePath ?? item.plistPath)
            }
            Spacer()
            if isBusy { ProgressView().controlSize(.small) }
            Toggle("", isOn: Binding(get: { item.state.isActive }, set: { onToggle($0) }))
                .toggleStyle(.switch).labelsHidden()
                .disabled(!LoginItemsController.canToggle(item) || isBusy || item.isApple)
                .help(LoginItemsController.toggleUnavailableReason(item) ?? (item.state.isActive ? "Tắt tạm (bootout)" : "Bật lại (bootstrap)"))
            Button { NSWorkspace.shared.activateFileViewerSelecting([item.plistURL]) } label: { Image(systemName: "magnifyingglass") }
                .buttonStyle(.plain).foregroundStyle(Theme.secondaryText).help("Hiện plist trong Finder")
            Button(action: onRemove) { Image(systemName: "trash") }
                .buttonStyle(.plain).foregroundStyle(item.isApple ? Theme.tertiaryText : Theme.secondaryText)
                .disabled(item.isApple || isBusy)
                .help(item.isApple ? "Thành phần của Apple" : "Xoá hẳn: bootout rồi xoá plist")
        }
        .padding(.horizontal, 8).padding(.vertical, 6)
        .contextMenu {
            Button("Hiện plist trong Finder") { NSWorkspace.shared.activateFileViewerSelecting([item.plistURL]) }
            if let exe = item.job.executablePath, item.executableExists == true {
                Button("Hiện chương trình trong Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: exe)]) }
            }
            Button("Sao chép label") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(item.label, forType: .string)
            }
        }
    }

    private var detailLine: String {
        var parts: [String] = []
        if let owner = item.owner { parts.append(owner.isInstalled ? "Của \(owner.name)" : "Của \(owner.name) (đã gỡ)") }
        parts.append(item.job.scheduleDescription)
        return parts.joined(separator: " · ")
    }

    @ViewBuilder private var statusPill: some View {
        if item.isBroken {
            Pill("Hỏng", color: Theme.risky)
        } else {
            switch item.state {
            case .running: Pill("Đang chạy", color: Theme.safe)
            case .loaded: Pill(item.isDisabled ? "Đã vô hiệu" : "Đã nạp")
            case .notLoaded: Pill(item.isDisabled ? "Đã vô hiệu" : "Đã tắt", color: Theme.review)
            case .unknown: EmptyView()
            }
        }
    }
}
