# Phân tích kiến trúc CleanMyMac 5 (v5.7.0) — ghi chú học tập

Phương pháp: phân tích tĩnh bundle (Info.plist, entitlements, cấu trúc thư mục, tên type Swift/selector ObjC qua `nm` + `swift-demangle` + `strings`). Không disassemble logic, không đụng phần license/activation, không giải mã dữ liệu được mã hoá.

## 1. Cấu trúc tiến trình

| Thành phần | Vai trò |
|---|---|
| `MacOS/CleanMyMac_5` | App chính (UI) |
| `Library/LaunchServices/com.macpaw.CleanMyMac5.Agent` | Privileged helper chạy root, cài qua `SMPrivilegedExecutables` (SMJobBless), giao tiếp XPC |
| `LoginItems/CleanMyMac_5_Menu.app` | App menu bar |
| `LoginItems/CleanMyMac_5_HealthMonitor.app` | Giám sát nền (RAM, ổ đĩa, malware realtime) |
| `PlugIns/..._FinderSyncExtension.appex` | Tích hợp menu chuột phải trong Finder |
| `Extensions/..._AppIntentsExtension.appex` | Shortcuts / Siri |
| `XPCServices/MASUpdaterXPCService.xpc` | Cập nhật app Mac App Store |
| `MacOS/CleanMyMac_5_Updater.app` + `Sparkle.framework` | Tự cập nhật |

- App Group dùng chung giữa các tiến trình: `...CleanMyMac5`, `...CleanMyMac5.CLI` (có bản CLI), `...CleanMyMac4` (để migrate từ v4).
- Không sandbox, phân phối bằng Developer ID. Quyền truy cập file dựa vào Full Disk Access + các chuỗi `NS*UsageDescription`.

## 2. Kiến trúc module (~130 framework)

Mỗi tính năng chia thành các tầng: **Scanning** (quét), **Module** (logic/view model), **UI**, **Service** (I/O, mạng).

- **Core:** `ScanningCore` (task graph có dependency, progress, priority, cancel), `CleaningCore` (CleanQueue, nodes cleaner), `NodesCore` (cây kết quả), `ModuleCore`, `DatabaseKit` (GRDB/FMDB, SQLite)
- **Tính năng:** `SystemJunkScanning`, `TrashJunkScanning`, `MailJunkScanning`, `UninstallerScanning`, `UpdaterScanning`, `SpaceLensScanning`, `PerformanceScanning`, `PrivacyScanning`, `MalwareScanning` (+ engine `PANEngine` / "Moonlock"), `OrganizeScanning`, `DownloadsScanning`, `UnusedDiskImagesScanning`, `CloudStorageScanning` (Dropbox / Google Drive / OneDrive), `EmailCleanupModule` (Gmail / Outlook)
- **Hệ thống:** `IPCProtocol` (XPCClientEngine / XPCServerEngine, xác thực client bằng code requirement), `PrivilegedOperationsPerformerService`, `PermissionsKit`, `HealthMonitoring*`
- **Thư viện bên thứ 3:** CocoaLumberjack, SnapKit, Kingfisher, Rive (animation), Sparkle, GRDB, FMDB, MSAL, AppAuth, SwiftProtobuf, CombineExt

## 3. Các nhóm rác (từ tên type trong SystemJunkScanning)

Cache (người dùng/hệ thống), Logs, Broken Preferences, Broken Startup Items, Language files (localization không dùng), Document Versions, Deleted Users, iOS Mobile Backups, Universal Binary thinning (gỡ slice kiến trúc thừa), Xcode junk (DerivedData, Device Logs, CoreSimulator cache, Simulator devices/runtimes).

Uninstaller có filter riêng cho JetBrains và Blizzard, tìm app leftovers, audio plugins, app containers.

## 4. Maintenance (PerformanceScanning + Agent)

`flushDNSCache`, `freeUpPurgeableSpace`, `freeUpRAM`, `reindexSpotlight`, `repairDiskPermissions`, `speedUpMail`, `thinLocalSnapshots`, chạy `periodic` scripts. Helper gọi các tool hệ thống (`dscacheutil`, `mdutil`, `periodic`...), quản lý LaunchAgents/Daemons, kill process.

Background items: `LaunchAgentsKnowledgeBase`, `LoginItemsKnowledgeBase`.

## 5. Knowledge base

`Resources/info.cmmkb` (~290KB) có entropy 8.0, tức là đã mã hoá. Đây là chỗ chứa rule/tri thức về app (bundle ID, đường dẫn an toàn để xoá...). Không giải mã. Sản phẩm của mình cần tự xây KB riêng (rule JSON + nguồn mở như Mole/Pearcleaner).

## 6. Bài học cho sản phẩm của mình

1. Tách 3 tiến trình: UI app, privileged helper (XPC, xác thực bằng code signing requirement), menu bar/monitor (login item). Hiện nay dùng `SMAppService` thay cho SMJobBless (đã deprecated).
2. Scanning engine dạng task graph: mỗi scanner là một task có dependency/progress/cancel, kết quả là cây node, cleaner chạy theo queue.
3. Mỗi tính năng là một module độc lập (Scanning / Domain / UI) để dễ phát triển song song.
4. Knowledge base tách khỏi code, có thể cập nhật từ xa (`RemotePreferences`).
5. Nhóm rác dành cho dev (Xcode, simulator) là điểm khác biệt lớn với người dùng kỹ thuật.
