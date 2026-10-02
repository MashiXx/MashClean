import CleanEngine
import DesignSystem
import MaintenanceDomain
import MaintenanceScanning
import SharedUI
import SweepCore
import SweepIPC
import SwiftUI

@MainActor
final class MaintenanceViewModel: ObservableObject {
    enum TaskRunState: Equatable {
        case idle, running, done(Bool, String)
    }

    @Published var status: MaintenanceStatus?
    @Published var selected: Set<MaintenanceTaskName> = []
    @Published var states: [MaintenanceTaskName: TaskRunState] = [:]
    @Published var isRunning = false
    @Published var loading = false
    @Published var snapshotMessage: String?

    let feature: MaintenanceFeature
    let services: ScanServices

    init(feature: MaintenanceFeature, services: ScanServices, initialTask: MaintenanceTaskName?) {
        self.feature = feature
        self.services = services
        if let initialTask { selected = [initialTask] }
    }

    func refresh() {
        loading = true
        Task {
            let s = await feature.statusReader.read()
            status = s
            if selected.isEmpty {
                selected = Set(s.recommendations.filter { $0.recommended && $0.safety == .safe }.map(\.task))
            }
            loading = false
        }
    }

    func runSelected() {
        guard !isRunning else { return }
        isRunning = true
        let tasks = MaintenanceTaskName.allCases.filter { selected.contains($0) }
        for t in tasks { states[t] = .idle }
        Task {
            if tasks.contains(where: \.requiresRoot), HelperInstaller.status != .enabled {
                _ = try? HelperInstaller.ensureRegistered()
            }
            for t in tasks {
                states[t] = .running
                let r = await feature.runner.run(t)
                states[t] = .done(r.success, r.message)
            }
            isRunning = false
            refresh()
        }
    }

    func delete(_ snapshot: LocalSnapshot) {
        let plan = services.cleanEngine.makePlan(nodes: [feature.snapshotNode(snapshot)])
        Task {
            let report = await services.cleanEngine.run(plan, dryRun: services.settings.dryRun)
            if let e = report.entries.first, case let .failed(_, message) = e.result.outcome {
                snapshotMessage = String(localized: "Không xoá được snapshot: \(message)")
            } else {
                snapshotMessage = String(localized: "Đã xoá snapshot \(snapshot.displayDate)")
            }
            refresh()
        }
    }
}

/// Màn Bảo trì (mục 11.5): danh sách tác vụ, lần chạy gần nhất, gợi ý theo trạng thái hệ thống.
public struct MaintenanceView: View {
    @StateObject private var model: MaintenanceViewModel

    public init(services: ScanServices, feature: MaintenanceFeature, initialTask: MaintenanceTaskName? = nil) {
        _model = StateObject(wrappedValue: MaintenanceViewModel(feature: feature, services: services, initialTask: initialTask))
    }

    public var body: some View {
        ZStack {
            Theme.background(for: .maintenance).ignoresSafeArea()
            VStack(alignment: .leading, spacing: 16) {
                header
                if let status = model.status {
                    if !status.helperAvailable && MaintenanceTaskName.allCases.contains(where: { model.selected.contains($0) && $0.requiresRoot }) {
                        NoticeBanner(symbol: "lock.shield", text: String(localized: "Tác vụ cần quyền quản trị sẽ yêu cầu cài helper của MashClean."),
                                     actionTitle: String(localized: "Cài helper")) { _ = try? HelperInstaller.ensureRegistered() }
                    }
                    statusRow(status)
                    ScrollView {
                        VStack(spacing: 8) {
                            ForEach(MaintenanceTaskName.allCases) { task in
                                taskRow(task, rec: status.recommendation(for: task), last: status.lastRuns[task])
                            }
                            if !status.localSnapshots.isEmpty { snapshotsSection(status.localSnapshots) }
                        }
                    }
                } else {
                    Spacer()
                    ProgressView().controlSize(.large).frame(maxWidth: .infinity)
                    Spacer()
                }
                footer
            }
            .padding(24)
        }
        .onAppear { model.refresh() }
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 4) {
                Text(String(localized: "Bảo trì")).font(Theme.Font.title).foregroundStyle(.white)
                Text(String(localized: "Chạy các tác vụ giúp máy ổn định hơn. Chỉ chạy khi cần; MashClean gợi ý dựa trên trạng thái hệ thống."))
                    .font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            }
            Spacer()
            Button { model.refresh() } label: { Label(String(localized: "Làm mới"), systemImage: "arrow.clockwise") }
                .buttonStyle(GlassButtonStyle()).disabled(model.loading)
        }
    }

    private func statusRow(_ s: MaintenanceStatus) -> some View {
        HStack(spacing: 28) {
            StatTile(value: s.memoryPressure.title, label: "Memory pressure", symbol: "memorychip")
            StatTile(value: s.purgeableBytes.formatted, label: "Purgeable", symbol: "internaldrive")
            StatTile(value: "\(s.localSnapshots.count)", label: String(localized: "Snapshot cục bộ"), symbol: "clock.arrow.circlepath")
            if let mail = s.mailEnvelopeIndexBytes { StatTile(value: mail.formatted, label: String(localized: "Chỉ mục Mail"), symbol: "envelope") }
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.black.opacity(0.18)))
    }

    private func taskRow(_ task: MaintenanceTaskName, rec: MaintenanceStatus.Recommendation, last: Date?) -> some View {
        let isOn = Binding(get: { model.selected.contains(task) }, set: { on in
            if on { model.selected.insert(task) } else { model.selected.remove(task) }
        })
        return HStack(spacing: 12) {
            Toggle("", isOn: isOn).toggleStyle(.checkbox).labelsHidden().disabled(model.isRunning)
            Image(systemName: task.symbol).font(.system(size: 20)).frame(width: 30).foregroundStyle(.white)
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 6) {
                    Text(task.title).font(Theme.Font.headline).foregroundStyle(.white)
                    if rec.recommended { Pill(String(localized: "Nên chạy"), color: Theme.safe) }
                    if task.requiresRoot { Image(systemName: "lock.fill").font(.system(size: 10)).foregroundStyle(Theme.tertiaryText) }
                }
                Text(task.whenToRun + " " + rec.why).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText)
                Text(last.map { String(localized: "Lần chạy gần nhất: \($0.formatted(date: .abbreviated, time: .shortened))") } ?? String(localized: "Chưa chạy lần nào"))
                    .font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
            }
            Spacer()
            switch model.states[task] ?? .idle {
            case .idle: EmptyView()
            case .running: ProgressView().controlSize(.small)
            case let .done(ok, message):
                Image(systemName: ok ? "checkmark.circle.fill" : "xmark.circle.fill")
                    .foregroundStyle(ok ? Theme.safe : Theme.risky).help(message)
            }
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.white.opacity(model.selected.contains(task) ? 0.12 : 0.06)))
    }

    private func snapshotsSection(_ snapshots: [LocalSnapshot]) -> some View {
        Card(padding: 12) {
            VStack(alignment: .leading, spacing: 8) {
                Label(String(localized: "Snapshot Time Machine cục bộ"), systemImage: "clock.arrow.circlepath").font(Theme.Font.headline).foregroundStyle(.white)
                if let msg = model.snapshotMessage { Text(msg).font(Theme.Font.caption).foregroundStyle(Theme.secondaryText) }
                ForEach(snapshots) { snap in
                    HStack {
                        Text(snap.displayDate).font(Theme.Font.body).foregroundStyle(.white)
                        Text(snap.volume).font(Theme.Font.caption).foregroundStyle(Theme.tertiaryText)
                        Spacer()
                        Button(String(localized: "Xoá")) { model.delete(snap) }.buttonStyle(GlassButtonStyle())
                    }
                }
            }
        }
    }

    private var footer: some View {
        HStack {
            Text(String(localized: "\(model.selected.count) tác vụ được chọn")).font(Theme.Font.body).foregroundStyle(Theme.secondaryText)
            Spacer()
            if model.services.settings.dryRun { Pill("DRY RUN", color: Theme.review) }
            Button(model.isRunning ? String(localized: "Đang chạy…") : String(localized: "Chạy")) { model.runSelected() }
                .buttonStyle(PrimaryButtonStyle(accent: .maintenance))
                .disabled(model.selected.isEmpty || model.isRunning)
                .keyboardShortcut(.defaultAction)
        }
    }
}
