<div align="center">

<img src="src/CleanBoost.App/Assets/AppIcon256.png" width="128" alt="Clean Boost">

# Clean Boost for Windows

**Clean your PC safely, transparently, and fast.**

![Windows 10 1809+ / 11](https://img.shields.io/badge/Windows-10%201809%2B%20%C2%B7%2011-0078D4?logo=windows)
![.NET 9](https://img.shields.io/badge/.NET-9-512BD4?logo=dotnet)
![English · Tiếng Việt](https://img.shields.io/badge/UI-English%20%C2%B7%20Ti%E1%BA%BFng%20Vi%E1%BB%87t-2BB39B)

</div>

The Windows edition of [Clean Boost](../README.md). It shares the Mac app's design: every suggestion is explained (which app it belongs to, why it's safe, a *Safe* / *Review* / *Risky* level), your own files go to the Recycle Bin and can be restored from **History**, and a single safety module decides what may ever be deleted.

## Features

| | Feature | What it does on Windows |
|---|---|---|
| ✨ | **Smart Scan** | One scan summarized as *Cleanup*, *Maintenance* and *Applications* cards; **Run** handles every safe item. |
| 🧹 | **System Junk** | Temporary files (yours and the system's), app and browser caches, shader caches, downloaded Windows Update and Delivery Optimization files, logs, crash dumps and Windows Error Reporting files, developer tool caches (npm, Yarn, pnpm, pip, NuGet, Gradle, Maven, Cargo, Go, JetBrains…), iTunes backups, the Recycle Bin on every drive, old downloads. |
| 📦 | **Uninstaller** | Desktop programs (from the Windows uninstall registry) and Microsoft Store apps. Runs the program's own uninstaller, then finds leftovers ranked by confidence: install folder remnants, AppData folders, shortcuts, `HKCU\Software` keys (backed up as `.reg` before removal). Also finds leftovers of programs removed long ago. |
| 🌀 | **Space Lens** | Interactive sunburst disk map, fast parallel scanning, updates while files change. |
| 🔧 | **Maintenance** | Flush DNS, free up RAM, rebuild the icon cache, re-index Windows Search, clean the component store (WinSxS), optimize drives, repair system files, restart Explorer, remove the oldest restore point — with suggestions based on your PC's state. |
| ⏻ | **Startup Items** | Run keys, Startup folders and logon/boot scheduled tasks; enable/disable exactly like Task Manager, flag **broken** entries whose program no longer exists. |
| 🐘 | **Large & Old Files** | Filter by type, size and last use. |
| 👯 | **Duplicates** | Size → xxHash3 of start and end → SHA-256; skips hard links and cloud placeholders (OneDrive files that aren't downloaded), suggests which copy to keep. |
| 📊 | **Tray icon** | CPU, RAM, disk, battery and network with small charts; low-disk, memory and large-Recycle-Bin alerts. |
| 🖱️ | **Explorer & shortcuts** | Right-click *Analyze with Clean Boost*, `cleanboost://` links, Jump List tasks. |

## Safety

- **Plan first, delete second**, with a confirmation listing anything that needs administrator rights, isn't marked safe, or was blocked.
- **Always off-limits:** Windows system folders (only a reviewed list such as `Windows\Temp` and the Windows Update download cache is allowed), `Program Files\WindowsApps`, registry hives, credentials and DPAPI keys, OneDrive/iCloud/Dropbox folders, Outlook `.pst`/`.ost` files, your profile and Documents/Desktop/Pictures/Videos/Music/Downloads folders themselves. Paths are re-checked right before deletion; junctions and symlinks are never followed.
- **A locked-down admin helper:** `CleanBoost.Helper.exe` runs only when needed (Windows asks via UAC), talks to the app over a private named pipe, checks the app process on the other end, accepts only named commands, and re-checks every path itself.
- **Dry run:** `MASHCLEAN_DRY_RUN=1` or `--dry-run`.

## Install

Download `CleanBoost-<version>-win-x64-setup.exe` (or the portable zip) from [Releases](https://github.com/MashiXx/MashClean/releases/latest). The installer puts the app in *Program Files* so the admin helper can trust the app next to it.

## For developers

Requires the .NET 9 SDK on Windows. Architecture and conventions: [docs/WINDOWS_DESIGN.md](docs/WINDOWS_DESIGN.md) (Vietnamese).

```powershell
cd Windows
dotnet build CleanBoost.sln
.artifacts\bin\CleanBoost.App\debug\CleanBoost.exe
dotnet test tests\CleanBoost.Tests
Scripts\build-rules.ps1      # lint → fixture tests → build → sign → verify the rule set
Scripts\publish.ps1          # Release, self-contained, portable zip (+ installer if Inno Setup 6 is installed)
```

- Rules live in `Rules\*.json` (signed with Ed25519; the key is `Secrets\windows_rules_signing_key.b64`, not committed — CI uses the `WINDOWS_RULES_SIGNING_KEY` secret). Debug builds can load unsigned rules with `MASHCLEAN_RULES_DIR=<path>`.
- UI strings are written in Vietnamese in code (`Loc.T` / `Loc.F`); English lives in `src\CleanBoost.App\Localization\en*.json`. `python Scripts\l10n\check.py` lists missing translations.
- UI snapshots for review: `CLEANBOOST_SNAPSHOT=<dir>`, `CLEANBOOST_SNAPSHOT_SCREENS=SmartScan,SystemJunk`, `CLEANBOOST_SNAPSHOT_SCAN=1`.
- Logs: `%LOCALAPPDATA%\CleanBoost\Logs`. Data: `%LOCALAPPDATA%\CleanBoost`.
