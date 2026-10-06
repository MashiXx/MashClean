<#
.SYNOPSIS
    Đóng gói Clean Boost cho Windows: build Release self-contained (app + helper chung thư mục), kiểm tra bộ rule,
    nén bản portable và (nếu có Inno Setup) tạo bộ cài.

.EXAMPLE
    Scripts\publish.ps1                       # win-x64
    Scripts\publish.ps1 -Runtime win-arm64
    Scripts\publish.ps1 -SkipInstaller

.NOTES
    Biến MSBuild tuỳ chọn (giống các khoá MashClean* của bản Mac): -p:CleanBoostRulesManifestUrl=…,
    -p:CleanBoostSupportEmail=…, -p:CleanBoostUpdateFeedUrl=… truyền qua tham số -MSBuildArgs.
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string[]]$MSBuildArgs = @(),
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$out = Join-Path $dist "CleanBoost-$Runtime"
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Select-Object -First 1

Write-Host "==> Clean Boost $version ($Runtime, $Configuration)"

$bundle = Join-Path $root "src\CleanBoost.App\Rules\rules.bundle"
if (-not (Test-Path $bundle)) {
    throw "Chưa có bộ rule đã ký ($bundle). Chạy Scripts\build-rules.ps1 trước."
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

$common = @("-c", $Configuration, "-r", $Runtime, "--self-contained", "true", "-p:PublishReadyToRun=true", "-p:DebugType=none", "-o", $out) + $MSBuildArgs

Write-Host "==> Publish helper"
dotnet publish (Join-Path $root "src\CleanBoost.Helper\CleanBoost.Helper.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Publish helper lỗi" }

Write-Host "==> Publish app"
dotnet publish (Join-Path $root "src\CleanBoost.App\CleanBoost.App.csproj") @common
if ($LASTEXITCODE -ne 0) { throw "Publish app lỗi" }

foreach ($f in @("CleanBoost.exe", "CleanBoost.Helper.exe", "Rules\rules.bundle")) {
    if (-not (Test-Path (Join-Path $out $f))) { throw "Thiếu $f trong bản publish" }
}

Write-Host "==> Kiểm tra chữ ký bộ rule"
dotnet run --project (Join-Path $root "src\CleanBoost.RulePack\CleanBoost.RulePack.csproj") -c $Configuration -- verify (Join-Path $out "Rules\rules.bundle")
if ($LASTEXITCODE -ne 0) { throw "Bộ rule không qua verify" }

$zip = Join-Path $dist "CleanBoost-$version-$Runtime-portable.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "==> Bản portable: $zip"

if (-not $SkipInstaller) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($iscc) {
        Write-Host "==> Tạo bộ cài bằng Inno Setup"
        & $iscc "/DAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$dist" "/DRuntime=$Runtime" (Join-Path $PSScriptRoot "installer\CleanBoost.iss")
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup lỗi" }
    }
    else {
        Write-Warning "Không tìm thấy Inno Setup 6 (winget install JRSoftware.InnoSetup) — bỏ qua bộ cài."
    }
}

Write-Host "==> Xong: $out"
