import DesignSystem
import DuplicatesUI
import FileSystemKit
import LargeOldFilesUI
import LoginItemsUI
import MaintenanceUI
import SharedUI
import SmartScanUI
import SpaceLensUI
import SweepCore
import SweepIPC
import SwiftUI
import SystemJunkUI
import UninstallerUI

struct RootView: View {
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @EnvironmentObject private var router: AppRouter
    /// Màn đã mở được giữ sống để không mất kết quả quét khi chuyển màn.
    @State private var visited: [SidebarItem] = [.smartScan]

    var body: some View {
        if let env = holder.environment, let smartScan = holder.smartScanModel {
            NavigationSplitView {
                Sidebar(selection: $router.selection)
                    .navigationSplitViewColumnWidth(min: 200, ideal: 220, max: 260)
            } detail: {
                ZStack {
                    ForEach(visited) { item in
                        screen(item, env: env, smartScan: smartScan)
                            .opacity(router.selection == item ? 1 : 0)
                            .allowsHitTesting(router.selection == item)
                            .accessibilityHidden(router.selection != item)
                    }
                }
                .ignoresSafeArea()
            }
            .onChange(of: router.selection) { item in
                if !visited.contains(item) { visited.append(item) }
            }
            .onChange(of: router.autoStartToken) { _ in
                if router.autoStartTarget == .smartScan, !smartScan.isBusy { smartScan.startScan() }
            }
            .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
                holder.refreshPermissions()
            }
            .sheet(isPresented: $holder.showOnboarding) {
                OnboardingView().environmentObject(holder)
            }
        } else {
            StartupErrorView(message: holder.startupError ?? "Không rõ lỗi")
        }
    }

    @ViewBuilder
    private func screen(_ item: SidebarItem, env: AppEnvironment, smartScan: SmartScanViewModel) -> some View {
        switch item {
        case .smartScan:
            SmartScanView(model: smartScan) { feature in
                if let target = SidebarItem(feature: feature) { router.open(target) }
            }
        case .systemJunk:
            SystemJunkView(services: env.services, feature: env.systemJunk, autoStart: router.autoStartTarget == .systemJunk)
                .id(router.autoStartTarget == .systemJunk ? router.autoStartToken : 0)
        case .largeOldFiles:
            LargeOldFilesView(services: env.services, feature: env.largeOldFiles)
        case .duplicates:
            DuplicatesView(services: env.services, feature: env.duplicates)
        case .uninstaller:
            UninstallerView(services: env.services, feature: env.uninstaller, initialAppPath: router.uninstallPath)
                .id(router.uninstallerID)
        case .loginItems:
            LoginItemsView(services: env.services, feature: env.loginItems)
        case .maintenance:
            MaintenanceView(services: env.services, feature: env.maintenance, initialTask: router.maintenanceTask)
                .id(router.maintenanceID)
        case .spaceLens:
            SpaceLensView(services: env.services, feature: env.spaceLens, initialPath: router.spaceLensPath.map { URL(fileURLWithPath: $0) })
                .id(router.spaceLensID)
        case .history:
            HistoryView(services: env.services)
        case .diagnostics:
            DiagnosticsView()
        }
    }
}

struct Sidebar: View {
    @Binding var selection: SidebarItem
    @EnvironmentObject private var holder: AppEnvironmentHolder
    @State private var volume = VolumeInfo(url: URL(fileURLWithPath: "/"))

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 8) {
                Image(systemName: "sparkles.rectangle.stack.fill").font(.system(size: 20)).foregroundStyle(.purple)
                Text("MashClean").font(.system(size: 17, weight: .bold, design: .rounded))
                Spacer()
            }
            .padding(.horizontal, 16).padding(.top, 34).padding(.bottom, 8)

            List(selection: $selection) {
                ForEach(SidebarItem.sections, id: \.0) { section in
                    Section(section.0) {
                        ForEach(section.1) { item in
                            Label(item.title, systemImage: item.symbol).tag(item)
                        }
                    }
                }
            }
            .listStyle(.sidebar)

            VStack(alignment: .leading, spacing: 8) {
                if !holder.hasFullDiskAccess {
                    Button {
                        holder.showOnboarding = true
                    } label: {
                        Label("Cấp Full Disk Access", systemImage: "lock.open").font(.caption)
                    }
                    .buttonStyle(.link)
                }
                if holder.dryRun {
                    Label("Đang ở chế độ thử", systemImage: "testtube.2").font(.caption).foregroundStyle(.orange)
                }
                if let v = volume {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(v.name).font(.caption.weight(.semibold))
                        ProgressView(value: v.usedFraction).tint(v.usedFraction > 0.9 ? .red : .accentColor)
                        Text("Còn trống \(ByteCount(v.availableForImportantUsage).formatted) / \(ByteCount(v.totalCapacity).formatted)")
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }
            .padding(14)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            volume = VolumeInfo(url: URL(fileURLWithPath: "/"))
        }
        .onReceive(DistributedNotificationCenter.default().publisher(for: MashCleanIdentifiers.didCleanNotification)) { _ in
            volume = VolumeInfo(url: URL(fileURLWithPath: "/"))
        }
    }
}

struct StartupErrorView: View {
    let message: String
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 48)).foregroundStyle(.orange)
            Text("MashClean không khởi động được").font(.title2.bold())
            Text(message).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 480)
            Text("Thường do bộ rule đi kèm bị hỏng. Hãy cài lại ứng dụng.").font(.caption).foregroundStyle(.secondary)
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
