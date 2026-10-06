<#
.SYNOPSIS
    Đóng gói bộ rule Windows (SYSTEM_DESIGN mục 8.5, 23.8): lint → test → build → sign → verify.

.DESCRIPTION
    Kết quả: Windows\src\CleanBoost.App\Rules\rules.bundle (đã ký, đã kiểm tra bằng public key nhúng trong app).
    Chạy được trên Windows PowerShell 5.1 và PowerShell 7+. Dừng ngay ở bước đầu tiên bị lỗi.

    Biến môi trường:
      CB_BUILD_SLOT           hậu tố thư mục build (.artifacts<slot>) để build song song không giẫm nhau
      RULES_SIGNING_KEY_FILE  file base64 khoá riêng Ed25519 (mặc định <repo>\Secrets\windows_rules_signing_key.b64; CI dùng secret)
      RULES_VERSION           ghi đè Rules\version.txt (yyyymmddNN)
      RULES_MIN_APP           phiên bản app tối thiểu (mặc định 1.0.0)
      RULES_CONFIGURATION     cấu hình build rulepack (mặc định Release)

.EXAMPLE
    pwsh -File Windows\Scripts\build-rules.ps1
    powershell -ExecutionPolicy Bypass -File Windows\Scripts\build-rules.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$WindowsRoot = Split-Path -Parent $PSScriptRoot
$RepoRoot = Split-Path -Parent $WindowsRoot
$RulesDir = Join-Path $WindowsRoot 'Rules'
$FixturesDir = Join-Path (Join-Path (Join-Path $WindowsRoot 'tests') 'Fixtures') 'rules'
$OutDir = Join-Path (Join-Path (Join-Path $WindowsRoot 'src') 'CleanBoost.App') 'Rules'
$Project = Join-Path (Join-Path (Join-Path $WindowsRoot 'src') 'CleanBoost.RulePack') 'CleanBoost.RulePack.csproj'

if ([string]::IsNullOrEmpty($env:RULES_SIGNING_KEY_FILE)) {
    $KeyFile = Join-Path (Join-Path $RepoRoot 'Secrets') 'windows_rules_signing_key.b64'
} else {
    $KeyFile = $env:RULES_SIGNING_KEY_FILE
}
if ([string]::IsNullOrEmpty($env:RULES_MIN_APP)) { $MinApp = '1.0.0' } else { $MinApp = $env:RULES_MIN_APP }
if ([string]::IsNullOrEmpty($env:RULES_CONFIGURATION)) { $Configuration = 'Release' } else { $Configuration = $env:RULES_CONFIGURATION }
if ([string]::IsNullOrEmpty($env:CB_BUILD_SLOT)) { $Slot = '' } else { $Slot = $env:CB_BUILD_SLOT }

# Chạy một lệnh ngoài, dừng script nếu mã thoát khác 0. Tạm hạ ErrorActionPreference để stderr của tiến trình con
# (cảnh báo lint...) không bị Windows PowerShell 5.1 biến thành lỗi khi đầu ra bị chuyển hướng.
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    Write-Host "==> $Title" -ForegroundColor Cyan
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) { throw "$Title thất bại (mã thoát $code)" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Không tìm thấy dotnet trong PATH (cần .NET SDK 9)' }
foreach ($required in @($RulesDir, $FixturesDir, $Project)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Không tìm thấy: $required" }
}
if (-not (Test-Path -LiteralPath $KeyFile -PathType Leaf)) { throw "Không tìm thấy khoá ký: $KeyFile (đặt RULES_SIGNING_KEY_FILE để chỉ file khác)" }

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ('cleanboost-rules-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
New-Item -ItemType Directory -Path $Work -Force | Out-Null
try {
    # CB_BUILD_SLOT là biến môi trường nên MSBuild tự đọc thành thuộc tính $(CB_BUILD_SLOT) (Directory.Build.props).
    Invoke-Native -Title "Build rulepack ($Configuration, .artifacts$Slot)" -FilePath 'dotnet' `
        -Arguments @('build', $Project, '-c', $Configuration, '-v', 'q', '-nologo')
    $ArtifactsRoot = Join-Path $WindowsRoot ('.artifacts' + $Slot)
    $RulePack = Join-Path (Join-Path (Join-Path (Join-Path $ArtifactsRoot 'bin') 'CleanBoost.RulePack') $Configuration.ToLowerInvariant()) 'rulepack.dll'
    if (-not (Test-Path -LiteralPath $RulePack -PathType Leaf)) { throw "Build xong nhưng không thấy $RulePack" }

    Invoke-Native -Title 'Lint' -FilePath 'dotnet' -Arguments @($RulePack, 'lint', $RulesDir)
    Invoke-Native -Title 'Test trên fixture' -FilePath 'dotnet' -Arguments @($RulePack, 'test', $RulesDir, '--fixtures', $FixturesDir)

    $Unsigned = Join-Path $Work 'rules.unsigned'
    $Signed = Join-Path $Work 'rules.bundle'
    $BuildArgs = @($RulePack, 'build', $RulesDir, '--out', $Unsigned, '--min-app', $MinApp)
    if (-not [string]::IsNullOrEmpty($env:RULES_VERSION)) { $BuildArgs += @('--version', $env:RULES_VERSION) }
    Invoke-Native -Title 'Build' -FilePath 'dotnet' -Arguments $BuildArgs

    Invoke-Native -Title 'Sign' -FilePath 'dotnet' -Arguments @($RulePack, 'sign', $Unsigned, '--key', $KeyFile, '--out', $Signed)
    Invoke-Native -Title 'Verify bằng public key nhúng trong app' -FilePath 'dotnet' -Arguments @($RulePack, 'verify', $Signed)

    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $Destination = Join-Path $OutDir 'rules.bundle'
    Copy-Item -LiteralPath $Signed -Destination $Destination -Force
    Write-Host "==> Xong: $Destination" -ForegroundColor Green
} finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}
