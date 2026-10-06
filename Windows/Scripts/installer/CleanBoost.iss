; Bộ cài Clean Boost cho Windows (Inno Setup 6).
; Cài vào Program Files (chỉ quản trị viên ghi được) để helper quản trị tin được CleanBoost.exe cùng thư mục.
; Biên dịch qua Scripts\publish.ps1 (truyền AppVersion, SourceDir, OutputDir, Runtime).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\CleanBoost-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif
#ifndef Runtime
  #define Runtime "win-x64"
#endif

[Setup]
AppId={{6B0C7F1E-4A8E-4C8B-9E36-0C1E2B5C7A11}
AppName=Clean Boost
AppVersion={#AppVersion}
AppPublisher=Clean Boost
AppPublisherURL=https://clean.mashtool.com
AppSupportURL=https://clean.mashtool.com/support
DefaultDirName={autopf}\Clean Boost
DefaultGroupName=Clean Boost
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir={#OutputDir}
OutputBaseFilename=CleanBoost-{#AppVersion}-{#Runtime}-setup
SetupIconFile=..\..\src\CleanBoost.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\CleanBoost.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
#if Runtime == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Clean Boost"; Filename: "{app}\CleanBoost.exe"
Name: "{autodesktop}\Clean Boost"; Filename: "{app}\CleanBoost.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CleanBoost.exe"; Description: "{cm:LaunchProgram,Clean Boost}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Dọn đăng ký theo người dùng (menu chuột phải, URL cleanboost://, khởi động cùng Windows) do app tạo.
Filename: "{app}\CleanBoost.exe"; Parameters: "--unregister-shell"; Flags: runasoriginaluser waituntilterminated skipifdoesntexist; RunOnceId: "UnregisterShell"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Rules"
