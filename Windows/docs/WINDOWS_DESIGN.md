# Clean Boost cho Windows: thiết kế và quy ước

> Bản Windows của Clean Boost, port từ bản macOS theo [docs/SYSTEM_DESIGN.md](../../docs/SYSTEM_DESIGN.md).
> Mục tiêu: cùng triết lý (an toàn, minh bạch, nhanh), cùng giao diện, cùng bộ khung Scan Engine → Node Tree → Clean Engine,
> nhưng dùng API và quy ước của Windows.

## 1. Stack

| Bản Mac | Bản Windows |
|---|---|
| Swift 6, SwiftUI + AppKit | C# 13 / .NET 9, WPF (`net9.0-windows10.0.19041.0`, chạy từ Windows 10 1809) |
| Swift Package (Foundation/Engine/Features/UI) | Các project: `CleanBoost.Core`, `CleanBoost.Engine`, `CleanBoost.Features`, `CleanBoost.App`, `CleanBoost.Helper`, `CleanBoost.RulePack` |
| GRDB (SQLite) | Microsoft.Data.Sqlite, cùng schema |
| UserDefaults App Group | `%LOCALAPPDATA%\CleanBoost\settings.json` (`AppSettings`, giữ tên khoá như bản Mac) |
| CryptoKit Ed25519 + LZFSE | BouncyCastle Ed25519 + Brotli; magic `MSRW` để không lẫn với bộ rule Mac |
| xxHash (C) | System.IO.Hashing `XxHash3` |
| SF Symbols | Segoe Fluent Icons / Segoe MDL2 Assets (`UI\Icons.cs` ánh xạ tên SF → glyph) |
| os_log | Log file xoay vòng `%LOCALAPPDATA%\CleanBoost\Logs` |

## 2. Tiến trình

| Tiến trình | Quyền | Vai trò |
|---|---|---|
| `CleanBoost.exe` | Người dùng (`asInvoker`) | Giao diện, quét, lập kế hoạch, xoá file của người dùng, khay hệ thống (thay app menu bar riêng của Mac) |
| `CleanBoost.Helper.exe` | Quản trị (`requireAdministrator`) | Chạy qua UAC khi lần đầu cần; xoá file hệ thống, chạy tác vụ bảo trì cần quyền, sửa mục khởi động của máy |

**Bảo mật helper** (tương đương XPC + code signing hai chiều):
- Kênh là named pipe tên ngẫu nhiên, `PipeOptions.CurrentUserOnly`.
- App kiểm tra tiến trình nối vào đúng PID helper vừa chạy và đúng đường dẫn exe.
- Helper kiểm tra tiến trình tạo pipe là PID cha khai báo và là `CleanBoost.exe` cùng thư mục.
- Helper chỉ nhận lệnh có tên (`removeItems`, `runMaintenance`, `setStartupApproved`, `deleteRunValue`, `setScheduledTaskEnabled`), không bao giờ chạy lệnh shell tuỳ ý, kiểm tra lại `PathPolicy.Root` cho mọi đường dẫn, tối đa 10.000 đường dẫn/lần, 1 tác vụ bảo trì cùng lúc, ghi log mọi lệnh kèm PID.
- Helper tự thoát khi pipe đóng; app đóng helper sau 15 phút rảnh.

## 3. An toàn dữ liệu

`CleanBoost.Core\PathPolicy.cs` là nơi duy nhất quyết định đường dẫn nào được xoá:
- **Luôn cấm:** `%WINDIR%` (trừ danh sách ngoại lệ đã review: Temp, SoftwareDistribution\Download, Logs, Minidump, LiveKernelReports, MEMORY.DMP, Panther, cache Delivery Optimization, SystemTemp), `System Volume Information`, `$Recycle.Bin` (đổ Thùng rác chỉ qua API), Recovery/Boot/EFI, pagefile/hiberfil/swapfile, `Program Files\Windows*`, `WindowsApps`, `ProgramData\Microsoft\{Crypto,Protect,Vault,Windows Defender…}`, hồ sơ Default, hive registry `NTUSER*`/`UsrClass.dat*`, Credentials/DPAPI/Vault/SystemCertificates, OneDrive/iCloud/Dropbox/Google Drive, mọi `*.pst`/`*.ost`.
- **Thư mục được bảo vệ (chính nó):** gốc ổ, `%WINDIR%`, `Users`, Program Files, ProgramData, Start Menu, hồ sơ người dùng và Documents/Desktop/Pictures/Videos/Music/Downloads (kể cả khi đã chuyển sang ổ khác), AppData và các nhánh chính.
- **Thư mục "vỏ":** chỉ xoá nội dung (`deleteContents`), không xoá chính nó: `%TEMP%`, `%WINDIR%\Temp`, INetCache, CrashDumps, WER…
- Chuẩn hoá: bỏ `.`/`..`, từ chối luồng dữ liệu phụ (`a:b`), ký tự đại diện, thành phần kết thúc bằng dấu chấm/khoảng trắng; giải symlink/junction/tên 8.3 của tổ tiên bằng `GetFinalPathNameByHandle`; thành phần cuối không giải (xoá liên kết chứ không xoá đích). Ổ mạng không hỗ trợ xoá.
- Chủ sở hữu (chế độ người dùng): file phải thuộc người dùng hiện tại, hoặc thuộc nhóm Administrators nhưng nằm trong hồ sơ của họ. Còn lại chuyển sang helper nếu `PathPolicy.Root` cho phép (vùng hệ thống trong danh sách, Program Files, ProgramData, `Users\*\AppData`).
- `SecureRemover` không bao giờ đi theo reparse point; giữ handle thư mục cha không chia sẻ quyền xoá trong lúc xoá để chống tráo đổi (TOCTOU).
- **Thùng rác:** `RecycleBin.MoveToTrash` dùng `SHFileOperation` với `FOF_ALLOWUNDO | FOF_WANTNUKEWARNING` (Windows hỏi nếu định xoá vĩnh viễn), rồi đọc file `$I` để biết đường dẫn `$R` → màn Lịch sử khôi phục được. Ổ không có Thùng rác (mạng, chỉ đọc) → hỏi người dùng có xoá vĩnh viễn không.

## 4. Ánh xạ tính năng

| Bản Mac | Bản Windows |
|---|---|
| Rác hệ thống: cache, log, Xcode, dev tool, iOS backup, Thùng rác | Tệp tạm, tệp tạm hệ thống, cache người dùng/trình duyệt/hệ thống, file Windows Update + Delivery Optimization, log, crash dump/WER, cache công cụ lập trình, bản sao lưu iOS (iTunes), Thùng rác mọi ổ, bản tải về cũ |
| Gỡ cài đặt (.app → Thùng rác, file sót theo bundle ID/Team ID) | Chương trình trong khoá Uninstall (HKLM 64/32, HKCU) + app Microsoft Store; chạy trình gỡ của app rồi tìm file sót theo tên/nhà phát hành/thư mục cài, lối tắt, khoá registry `HKCU\Software\<Hãng>\<App>` (sao lưu .reg trước khi xoá) |
| Login Items (LaunchAgent/Daemon) | Mục khởi động: khoá Run/RunOnce (HKCU, HKLM, HKLM 32-bit), thư mục Startup, tác vụ lập lịch chạy lúc đăng nhập/khởi động; bật/tắt bằng `StartupApproved` như Task Manager; nhãn **hỏng** khi chương trình không còn |
| Bảo trì: flush DNS, Spotlight, purge, periodic, thin snapshot, Launch Services, Mail | Xoá cache DNS, giải phóng RAM (working set + standby list), dựng lại cache biểu tượng, đánh lại chỉ mục Windows Search, dọn WinSxS (DISM), tối ưu ổ (TRIM/defrag), `sfc /scannow`, khởi động lại Explorer, xoá điểm khôi phục cũ nhất |
| Space Lens (getattrlistbulk, FSEvents) | `FindFirstFileEx` (LARGE_FETCH) song song, `FileSystemWatcher` |
| File lớn & cũ (Spotlight) | Duyệt trực tiếp thư mục người dùng và các ổ khác |
| File trùng lặp (bỏ qua APFS clone) | Kích thước → XxHash3 64 KB đầu+cuối → SHA-256; bỏ hard link (cùng file index) và file đám mây chưa tải |
| Menu bar (app riêng) | Biểu tượng khay hệ thống + popup (CPU, RAM, ổ, pin, mạng, Thùng rác), cảnh báo bằng thông báo Windows |
| Finder Sync | Menu chuột phải trong File Explorer ("Phân tích bằng Clean Boost") |
| App Intents / Shortcuts | Tham số dòng lệnh, URL `cleanboost://`, Jump List |
| Full Disk Access | Không có; thư mục cần quyền quản trị thì báo "Cần quyền quản trị" |

## 5. Cấu trúc mã

```
Windows/
├── Directory.Build.props      TFM, nullable, artifacts output (CB_BUILD_SLOT)
├── CleanBoost.sln
├── Rules/                     bộ rule nguồn JSON cho Windows (+ knowledge.json, version.txt)
├── tests/Fixtures/rules/      fixture cho rulepack test
├── Scripts/                   build, đóng gói rule, phát hành
├── src/
│   ├── CleanBoost.Core/       ByteCount, Glob, PathPolicy, SecureRemover, RecycleBin, Loc, Log, IPC, MaintenanceExecutor, StartupRegistry
│   ├── CleanBoost.Engine/     FileSystem, Nodes (Node/NodeTree/SelectionState), Scanning (DAG), Rules, Cleaning, Persistence, Apps, Ipc (HelperClient), Metrics
│   ├── CleanBoost.Features/   <Tính năng>/… phần quét + nghiệp vụ, không có UI
│   ├── CleanBoost.App/        WPF: UI/ (design system), Shared/ (FeatureFlowView, NodeOutlineView, ScanCleanViewModel, MessageDialog),
│   │                          Shell/ (MainWindow, ScreenFactory), Services/ (AppEnvironment, AppRouter, FeatureRegistry), Features/<Tính năng>/, Tray/, Screens/
│   ├── CleanBoost.Helper/     helper quản trị
│   └── CleanBoost.RulePack/   CLI rulepack
└── tests/CleanBoost.Tests/    xUnit
```

## 6. Quy ước cho từng tính năng

1. **Phần quét/nghiệp vụ** trong `CleanBoost.Features\<Tên>\`: lớp `<Tên>Feature : IFeatureScanProvider` (task quét, `Summarize` cho Smart Scan), các `IScanTask`, remover chuyên biệt (`IRemover`), loại mục ảo riêng (kế thừa `VirtualItem`).
2. **Đăng ký** trong `CleanBoost.App\Features\<Tên>\<Tên>Registration.cs`: cài `partial void Register<Tên>()` của `FeatureRegistry` (gọi `Add(provider)`, `Add(remover)`).
3. **Màn hình** trong `CleanBoost.App\Features\<Tên>\`: cài `partial void Create<Tên>(ref FrameworkElement? view)` của `ScreenFactory`. Màn hình cài `IAccentScreen` (màu nền), `IAutoStartScreen` (tự quét khi mở bằng liên kết sâu), `IBusyScreen`.
4. Màn quét/dọn thông thường dùng `ScanCleanViewModel` + `FeatureFlowView` (idle → scanning → results → confirming → cleaning → done/error). Thân màn kết quả thay được bằng `CustomResultsBody`.
5. **Giao diện:** dùng style trong `UI\Styles.xaml` (`GlassButton`, `PrimaryButton`, `BigActionButton`, `Card`, `Panel`, `Box`, `TriCheck`, `SquareCheck`, `Switch`, `PlainListBox`, `DarkTextBox`, `DarkComboBox`, `ThinProgress`, kiểu chữ `HeroText`/`TitleText`/`HeadlineText`/`BodyText`/`CaptionText`), `ui:Icon Symbol="…"` cho biểu tượng, `Theme` cho màu. Nền gradient do cửa sổ chính vẽ — màn hình để nền trong suốt. Hộp thoại dùng `MessageDialog.Show`.
6. **Bản dịch:** chuỗi giao diện viết bằng tiếng Việt trong code qua `Loc.T("…")` / `Loc.F("… %@ … %lld", …)` (định dạng giống khoá bản Mac để dùng lại `Localization\en.json`). Tiếng Anh của khoá mới đặt trong `CleanBoost.App\Localization\en.<tính năng>.json` (JSON phẳng, khoá là chuỗi tiếng Việt). Ngôn ngữ đổi cần khởi động lại.
7. **Ghi đè an toàn:** mọi thao tác xoá đi qua `CleanEngine` (lập kế hoạch → xác nhận → thực thi) để có PathPolicy, Thùng rác, lịch sử.

## 7. Build, chạy, kiểm thử

```powershell
cd Windows
dotnet build CleanBoost.sln
.artifacts\bin\CleanBoost.App\debug\CleanBoost.exe
dotnet test tests\CleanBoost.Tests
Scripts\build-rules.ps1            # lint → test → build → ký → verify bộ rule
```

- `CB_BUILD_SLOT=-x` đổi thư mục build thành `.artifacts-x` (nhiều tiến trình build song song).
- Chạy thử không xoá: `MASHCLEAN_DRY_RUN=1` hoặc `--dry-run`.
- Debug nạp rule chưa ký: `MASHCLEAN_RULES_DIR=<thư mục Rules>` (chỉ bản Debug).
- Bản Debug **không** tự đăng ký menu chuột phải, URL `cleanboost://` và khởi động cùng Windows (tránh bản build thử nằm trong Run của máy lập trình); bật bằng `CLEANBOOST_REGISTER_SHELL=1`. Gỡ mọi đăng ký: `CleanBoost.exe --unregister-shell`.
- Ép ngôn ngữ giao diện: `CLEANBOOST_LANGUAGE=vi|en`.
- **Chụp ảnh giao diện tự động:** `CLEANBOOST_SNAPSHOT=<thư mục>`, `CLEANBOOST_SNAPSHOT_SCREENS=SmartScan,SystemJunk,…` (tên `SidebarItem`), `CLEANBOOST_SNAPSHOT_SCAN=1` để quét trước khi chụp, `CLEANBOOST_SNAPSHOT_WAIT=<giây>` thời gian chờ quét. App bỏ qua kiểm tra một phiên bản và không ghi registry ở chế độ này, tự thoát sau khi chụp.
  - `CLEANBOOST_SNAPSHOT_ONBOARDING=1`, `CLEANBOOST_SNAPSHOT_SETTINGS=1`: chụp từng bước onboarding / từng tab cài đặt (không lưu gì).
  - `CLEANBOOST_SNAPSHOT_TRAY=1`: chụp popup khay hệ thống.
  - `CLEANBOOST_HISTORY_DB=<file>`: màn Lịch sử và danh sách bỏ qua dùng database riêng; bản Debug thêm `CLEANBOOST_HISTORY_DEMO=1` để điền dữ liệu mẫu.

## 8. Liên kết sâu

- `CleanBoost.exe cleanboost://scan?feature=systemJunk` (cũng `smartScan`, `uninstaller`, `maintenance`, `loginItems`, `largeOldFiles`, `duplicates`, `spaceLens`)
- `cleanboost://spacelens?path=<thư mục>` · `cleanboost://uninstall?path=<exe hoặc lối tắt>` · `cleanboost://maintenance?task=freeRAM`
- Tham số: `--smart-scan`, `--clean-junk`, `--analyze <thư mục>`, `--uninstall <đường dẫn>`, `--background` (chỉ khay hệ thống), `--dry-run`.
