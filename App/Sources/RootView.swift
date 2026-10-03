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
import SweepPermissions
import SweepStorage
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
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .opacity(router.selection == item ? 1 : 0)
                            .allowsHitTesting(router.selection == item)
                            .accessibilityHidden(router.selection != item)
                    }
                }
                // Gradient của từng màn liền mạch lên tới mép trên, không bị nền thanh tiêu đề che.
                .toolbarBackground(.hidden, for: .windowToolbar)
                // Mọi màn đều có nền gradient tối: control và chữ hệ thống (List, Table, Toggle) dùng giao diện tối cho dễ đọc.
                .environment(\.colorScheme, .dark)
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
            StartupErrorView(message: holder.startupError ?? String(localized: "Không rõ lỗi"))
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
            LargeOldFilesView(services: env.services, feature: env.largeOldFiles, autoStart: router.autoStartTarget == .largeOldFiles)
                .id(router.autoStartTarget == .largeOldFiles ? router.autoStartToken : 0)
        case .duplicates:
            DuplicatesView(services: env.services, feature: env.duplicates, autoStart: router.autoStartTarget == .duplicates)
                .id(router.autoStartTarget == .duplicates ? router.autoStartToken : 0)
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
    @State private var pendingLanguage: AppLanguage?

    var body: some View {
        // List đứng đầu để macOS tự chừa chỗ cho thanh tiêu đề và nút cửa sổ; phần dưới nằm ngoài vùng cuộn
        // (không dùng safeAreaInset vì nội dung cuộn sẽ chui bên dưới và chữ chồng lên nhau).
        VStack(spacing: 0) {
            List(selection: $selection) {
                HStack(spacing: 8) {
                    Image(nsImage: NSImage(named: "AppIcon") ?? NSApp.applicationIconImage).resizable().interpolation(.high).frame(width: 26, height: 26)
                    Text("Clean Boost").font(.system(size: 16, weight: .bold, design: .rounded))
                }
                .padding(.vertical, 4)

                ForEach(SidebarItem.sections, id: \.0) { section in
                    Section(section.0) {
                        ForEach(section.1) { item in
                            Label(item.title, systemImage: item.symbol).tag(item)
                        }
                    }
                }
            }
            .listStyle(.sidebar)
            Divider()
            footer
        }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            volume = VolumeInfo(url: URL(fileURLWithPath: "/"))
        }
        .onReceive(DistributedNotificationCenter.default().publisher(for: MashCleanIdentifiers.didCleanNotification)) { _ in
            volume = VolumeInfo(url: URL(fileURLWithPath: "/"))
        }
    }

    /// Đổi ngôn ngữ ngay ở sidebar (cũng có trong Cài đặt và menu bánh răng của thanh menu).
    private var languageMenu: some View {
        Menu {
            ForEach(AppLanguage.allCases) { language in
                Button {
                    if language != AppLanguage.current { pendingLanguage = language }
                } label: {
                    if language == AppLanguage.current {
                        Label(language.displayName, systemImage: "checkmark")
                    } else {
                        Text(verbatim: language.displayName)
                    }
                }
            }
        } label: {
            Label(AppLanguage.current.displayName, systemImage: "globe").font(.caption)
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help("Ngôn ngữ · Language")
        .confirmationDialog(
            "Khởi động lại Clean Boost để đổi ngôn ngữ? · Restart Clean Boost to change the language?",
            isPresented: Binding(get: { pendingLanguage != nil }, set: { if !$0 { pendingLanguage = nil } })
        ) {
            Button("Khởi động lại · Restart") {
                guard let language = pendingLanguage else { return }
                AppLanguage.select(language)
                AppRelauncher.restartForLanguageChange()
            }
            Button("Huỷ · Cancel", role: .cancel) { pendingLanguage = nil }
        }
    }

    private var footer: some View {
        VStack(alignment: .leading, spacing: 8) {
            if AppEdition.isAppStore {
                if !holder.missingFolders.isEmpty {
                    Button {
                        for folder in holder.missingFolders { FolderAccess.request(folder) }
                        holder.refreshPermissions()
                    } label: {
                        Label(String(localized: "Cấp quyền thư mục"), systemImage: "folder.badge.plus").font(.caption)
                    }
                    .buttonStyle(.link)
                }
            } else if !holder.hasFullDiskAccess {
                Button {
                    holder.showOnboarding = true
                } label: {
                    Label(String(localized: "Cấp Full Disk Access"), systemImage: "lock.open").font(.caption)
                }
                .buttonStyle(.link)
            }
            if holder.dryRun {
                Label(String(localized: "Đang ở chế độ thử"), systemImage: "testtube.2").font(.caption).foregroundStyle(.orange)
            }
            languageMenu
            if let v = volume {
                VStack(alignment: .leading, spacing: 4) {
                    Text(v.name).font(.caption.weight(.semibold))
                    ProgressView(value: v.usedFraction).tint(v.usedFraction > 0.9 ? .red : .accentColor)
                    Text(String(localized: "Còn trống \(ByteCount(v.availableForImportantUsage).formatted) / \(ByteCount(v.totalCapacity).formatted)"))
                        .font(.caption2).foregroundStyle(.secondary)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(14)
    }
}

struct StartupErrorView: View {
    let message: String
    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "exclamationmark.triangle.fill").font(.system(size: 48)).foregroundStyle(.orange)
            Text(String(localized: "Clean Boost không khởi động được")).font(.title2.bold())
            Text(message).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 480)
            Text(String(localized: "Thường do bộ rule đi kèm bị hỏng. Hãy cài lại ứng dụng.")).font(.caption).foregroundStyle(.secondary)
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
