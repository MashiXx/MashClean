# MashClean

Ứng dụng dọn dẹp và tối ưu macOS, xây theo [docs/SYSTEM_DESIGN.md](docs/SYSTEM_DESIGN.md) (tài liệu gọi tên tạm là MacSweep).

## Yêu cầu

- macOS 13 trở lên để chạy; Xcode 16+ (Swift 6) để build
- `brew install xcodegen`

## Build và chạy

```bash
Scripts/build-rules.sh          # lint → test → đóng gói → ký → kiểm tra bộ rule, ra App/Resources/Rules/rules.bundle
Scripts/build-app.sh            # sinh MashClean.xcodeproj bằng XcodeGen rồi build Debug
open .build/DerivedData/Build/Products/Debug/MashClean.app
```

Hoặc `xcodegen generate && open MashClean.xcodeproj` rồi chạy scheme `MashClean`.

Test của package: `cd Packages && swift test`.

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `App/` | Target app chính: composition root (`AppEnvironment`), sidebar, onboarding, lịch sử, cài đặt |
| `MenuBar/` | Target login item menu bar (logic ở `Packages/Processes/MenuBarKit`) |
| `Helper/` | Target helper root (logic ở `Packages/Processes/HelperCore`), plist LaunchDaemon |
| `Extensions/` | FinderSync và App Intents |
| `Packages/Foundation` | SweepCore, SweepLogging, SweepStorage (GRDB), SweepIPC (XPC), SweepPermissions |
| `Packages/Engine` | FileSystemKit, NodeTree, ScanEngine, RuleEngine, CleanEngine(+Core), AppCatalog, SystemMetrics |
| `Packages/Features` | SystemJunk, Uninstaller, SpaceLens, Maintenance, LoginItems, LargeOldFiles, Duplicates, SmartScan — mỗi cái chia `Scanning` / `Domain` / `UI` |
| `Packages/UI` | DesignSystem, SharedUI |
| `Packages/Tools/rulepack` | CLI lint/test/build/sign/verify rule |
| `Rules/` | Nguồn rule JSON + `knowledge.json` |
| `Tests/Fixtures` | Cây thư mục giả lập cho `rulepack test` |
| `Config/` | Cấu hình ký (`Signing.xcconfig`, bản local không commit) |
| `Scripts/` | build, DMG, notarize, phát hành, tạo icon |

## Ký và phát hành

Mặc định build ký **ad-hoc** để chạy thử trên máy. Ở chế độ này:

- Helper root (`SMAppService.daemon`) có thể bị launchd từ chối, và yêu cầu chữ ký XPC chỉ kiểm tra identifier (không có Team ID). Các tác vụ cần root (cache hệ thống, bảo trì) sẽ báo "cần cài helper".
- FinderSync có thể không được Finder nạp.

Để có đầy đủ tính năng, sao chép `Config/Signing.local.xcconfig.example` thành `Config/Signing.local.xcconfig`, điền `DEVELOPMENT_TEAM` và `CODE_SIGN_IDENTITY = Developer ID Application`. Phát hành: `Scripts/release.sh` (build Release universal → DMG → notarize → appcast Sparkle).

Khoá ký rule: private key Ed25519 nằm ở `Secrets/rules_signing_key.b64` (không commit; trên CI là secret `RULES_SIGNING_KEY`), public key nhúng tại `Packages/Engine/RuleEngine/RuleStore.swift` (`RulesPublicKey`).

## Chế độ thử

`MASHCLEAN_DRY_RUN=1` hoặc tham số `--dry-run` hoặc menu Debug → "Chế độ thử": chạy toàn bộ luồng nhưng không xoá gì (mục 15.4).

Bản Debug có thể nạp rule chưa ký từ thư mục nguồn: `MASHCLEAN_RULES_DIR=/path/to/Rules`.

## Xem log

```bash
log stream --predicate 'subsystem BEGINSWITH "com.mashclean"' --level debug
```

File log xoay vòng: `~/Library/Logs/MashClean/`.
