<#
=====================================================================================
package.ps1 — 打包發佈檔：publish → （可選）簽章 → 壓縮 + 安裝程式 →（可選）簽安裝程式 → SHA256SUMS.txt
              Build release files: publish → (optional) sign → zip + installer → (optional) sign installer → SHA256SUMS.txt

CI（.github/workflows/release.yml）和本機都用這支。
Used both by CI (.github/workflows/release.yml) and on your own PC.

用法 / Usage:
  # 不簽章（只給自己用或測試）/ unsigned (own use, testing)
  powershell -NoProfile -ExecutionPolicy RemoteSigned -File scripts\package.ps1 -Version 0.3.0

  # 用插在電腦上的 OV 憑證 USB token 簽章 / sign with an OV certificate on a USB token
  powershell -NoProfile -ExecutionPolicy RemoteSigned -File scripts\package.ps1 -Version 0.3.0 -CertThumbprint <SHA1 thumbprint>

  # CI 分三段，中間用 Artifact Signing 簽 / CI runs it in three parts with Artifact Signing in between
  ... -PublishOnly             然後簽 out\publish / then sign out\publish
  ... -PackOnly -NoChecksum    壓縮 + 做安裝程式，然後簽 out\release\*-setup.exe / zip + build installer, then sign the setup exe
  ... -ChecksumOnly            最後算 SHA256 / finally hash

  # 不做安裝程式 / skip the installer
  ... -NoInstaller

安裝程式需要 Inno Setup 6：winget install JRSoftware.InnoSetup
The installer needs Inno Setup 6: winget install JRSoftware.InnoSetup

輸出 / Output:
  out\publish\{app,cli}\        需要 .NET 11 的 zip 內容 / zip contents (need .NET 11 installed)
  out\publish\setup\            安裝程式內容，內含 .NET（self-contained, win-x64）/ installer payload with .NET included
  out\release\ApexHighlighter-<ver>-{app,cli}.zip
  out\release\ApexHighlighter-<ver>-setup.exe
  out\release\SHA256SUMS.txt
=====================================================================================
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-dev",
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$PublishOnly,
    [switch]$PackOnly,
    [switch]$NoChecksum,
    [switch]$ChecksumOnly,
    [switch]$NoInstaller
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root "out"
$Pub = Join-Path $Out "publish"
$Rel = Join-Path $Out "release"
$Projects = [ordered]@{ app = "src\ApexHighlighter.App"; cli = "src\ApexHighlighter.Cli" }
$OwnFiles = @("ApexHighlighter*.dll", "ApexHighlighter*.exe", "apexhl.dll", "apexhl.exe")

function Invoke-Checked([string]$exe, [string[]]$argList) {
    & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw "$exe exited $LASTEXITCODE" }
}

function Find-SignTool {
    $t = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\" } | Sort-Object FullName | Select-Object -Last 1
    if (-not $t) { throw "signtool.exe not found. Install the Windows SDK. 找不到 signtool.exe，請安裝 Windows SDK。" }
    $t.FullName
}

function Invoke-Sign([string[]]$paths) {
    Invoke-Checked (Find-SignTool) (@("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/sha1", $CertThumbprint) + $paths)
}

function Find-Iscc {
    $c = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $c) { throw "ISCC.exe (Inno Setup 6) not found. Install it: winget install JRSoftware.InnoSetup, or pass -NoInstaller. 找不到 Inno Setup 6，請先安裝或加 -NoInstaller。" }
    $c
}

# 安裝程式的 VersionInfo 只能是數字 a.b.c.d / the installer's VersionInfo must be numeric a.b.c.d
function Get-NumericVersion([string]$v) {
    $parts = @(([regex]::Match($v, '^\d+(\.\d+){0,3}').Value -split '\.') | Where-Object { $_ -ne "" })
    while ($parts.Count -lt 4) { $parts += "0" }
    $parts[0..3] -join "."
}

if (-not $PackOnly -and -not $ChecksumOnly) {
    if (Test-Path $Pub) { Remove-Item -Recurse -Force $Pub }
    foreach ($k in $Projects.Keys) {
        Invoke-Checked "dotnet" @("publish", (Join-Path $Root $Projects[$k]), "-c", "Release", "-o", (Join-Path $Pub $k),
            "-p:Version=$Version", "-nologo", "-v", "q")
    }
    if (-not $NoInstaller) {
        # 安裝程式用 self-contained，使用者不必先裝 .NET / self-contained for the installer, so users don't need .NET first
        Invoke-Checked "dotnet" @("publish", (Join-Path $Root $Projects["app"]), "-c", "Release", "-o", (Join-Path $Pub "setup"),
            "-r", "win-x64", "--self-contained", "true", "-p:Version=$Version", "-nologo", "-v", "q")
    }

    if ($CertThumbprint) {
        # 只簽我們自己的檔案（ApexHighlighter*.dll/exe、apexhl*）；第三方 DLL 保留原本的簽章
        # Sign only our own files; third-party DLLs keep their own signatures.
        $files = Get-ChildItem $Pub -Recurse -Include $OwnFiles
        Invoke-Sign @($files.FullName)
    }
}
if ($PublishOnly) { return }

# ---- 檢查簽章（有簽的話）/ check signatures (when signed)
function Get-SignState([object[]]$files) {
    $signed = @($files | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq "Valid" })
    $s = if ($signed.Count -eq $files.Count -and $files.Count -gt 0) { "signed" } elseif ($signed.Count -eq 0) { "unsigned" } else { "partly signed" }
    if ($s -eq "partly signed") { throw "Only $($signed.Count) of $($files.Count) files are signed. 只有部分檔案簽到章。" }
    $s
}

if (-not $ChecksumOnly) {
    $own = @(Get-ChildItem $Pub -Recurse -Include $OwnFiles)
    $state = Get-SignState $own
    Write-Host "Signature check: $state ($($own.Count) files). 簽章檢查：$state。"

    # ---- 壓縮 / zip
    if (Test-Path $Rel) { Remove-Item -Recurse -Force $Rel }
    New-Item -ItemType Directory -Force $Rel | Out-Null
    foreach ($k in $Projects.Keys) {
        Compress-Archive -Path (Join-Path $Pub "$k\*") -DestinationPath (Join-Path $Rel "ApexHighlighter-$Version-$k.zip")
    }

    # ---- 安裝程式 / installer
    if (-not $NoInstaller) {
        $setupSrc = Join-Path $Pub "setup"
        if (-not (Test-Path (Join-Path $setupSrc "ApexHighlighter.App.exe"))) { throw "Missing $setupSrc. Run without -PackOnly first. 缺少安裝程式內容，請先完整跑一次。" }
        Invoke-Checked (Find-Iscc) @("/Q", "/DAppVersion=$Version", "/DNumericVersion=$(Get-NumericVersion $Version)",
            "/DSourceDir=$setupSrc", "/DOutputDir=$Rel", (Join-Path $Root "installer\ApexHighlighter.iss"))
        $setup = Join-Path $Rel "ApexHighlighter-$Version-setup.exe"
        if ($CertThumbprint) { Invoke-Sign @($setup) }
        Write-Host "Installer: $setup"
    }
}
if ($NoChecksum) { return }

# ---- SHA256（簽章後才算 / computed after signing）
$setups = @(Get-ChildItem $Rel -Filter *-setup.exe)
if ($setups.Count -gt 0) { $state = Get-SignState $setups } elseif (-not $state) { $state = "unchecked" }
$lines = Get-ChildItem $Rel -File | Where-Object { $_.Extension -in ".zip", ".exe" } | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
# sha256sum 格式，不帶 BOM / sha256sum format, no BOM
[IO.File]::WriteAllText((Join-Path $Rel "SHA256SUMS.txt"), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
Get-Content (Join-Path $Rel "SHA256SUMS.txt")
Write-Host "Release files in $Rel ($state). 發佈檔在 $Rel（$state）。" -ForegroundColor Green
