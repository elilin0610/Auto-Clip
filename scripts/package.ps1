<#
=====================================================================================
package.ps1 — 打包發佈檔：publish → （可選）簽章 → 壓縮 → SHA256SUMS.txt
              Build release files: publish → (optional) sign → zip → SHA256SUMS.txt

CI（.github/workflows/release.yml）和本機都用這支。
Used both by CI (.github/workflows/release.yml) and on your own PC.

用法 / Usage:
  # 不簽章（只給自己用或測試）/ unsigned (own use, testing)
  powershell -NoProfile -ExecutionPolicy RemoteSigned -File scripts\package.ps1 -Version 0.3.0

  # 用插在電腦上的 OV 憑證 USB token 簽章 / sign with an OV certificate on a USB token
  powershell -NoProfile -ExecutionPolicy RemoteSigned -File scripts\package.ps1 -Version 0.3.0 -CertThumbprint <SHA1 thumbprint>

  # CI 分兩段，中間用 Artifact Signing 簽 / CI runs it in two halves with Artifact Signing in between
  ... -PublishOnly      然後簽 out\publish / then sign out\publish
  ... -PackOnly         再壓縮並算 SHA256 / then zip and hash

輸出 / Output: out\publish\{app,cli}\, out\release\ApexHighlighter-<ver>-{app,cli}.zip, out\release\SHA256SUMS.txt
=====================================================================================
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-dev",
    [string]$CertThumbprint = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$PublishOnly,
    [switch]$PackOnly
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $Root "out"
$Pub = Join-Path $Out "publish"
$Rel = Join-Path $Out "release"
$Projects = [ordered]@{ app = "src\ApexHighlighter.App"; cli = "src\ApexHighlighter.Cli" }

function Invoke-Checked([string]$exe, [string[]]$argList) {
    & $exe @argList
    if ($LASTEXITCODE -ne 0) { throw "$exe exited $LASTEXITCODE" }
}

if (-not $PackOnly) {
    if (Test-Path $Pub) { Remove-Item -Recurse -Force $Pub }
    foreach ($k in $Projects.Keys) {
        Invoke-Checked "dotnet" @("publish", (Join-Path $Root $Projects[$k]), "-c", "Release", "-o", (Join-Path $Pub $k),
            "-p:Version=$Version", "-nologo", "-v", "q")
    }

    if ($CertThumbprint) {
        # 只簽我們自己的檔案（ApexHighlighter*.dll/exe、apexhl*）；第三方 DLL 保留原本的簽章
        # Sign only our own files; third-party DLLs keep their own signatures.
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\" } | Sort-Object FullName | Select-Object -Last 1
        if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK. 找不到 signtool.exe，請安裝 Windows SDK。" }
        $files = Get-ChildItem $Pub -Recurse -Include "ApexHighlighter*.dll", "ApexHighlighter*.exe", "apexhl.dll", "apexhl.exe"
        Invoke-Checked $signtool.FullName (@("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256", "/sha1", $CertThumbprint) + @($files.FullName))
    }
}
if ($PublishOnly) { return }

# ---- 檢查簽章（有簽的話）/ check signatures (when signed)
$own = Get-ChildItem $Pub -Recurse -Include "ApexHighlighter*.dll", "ApexHighlighter*.exe", "apexhl.dll", "apexhl.exe"
$signed = @($own | Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq "Valid" })
$state = if ($signed.Count -eq $own.Count -and $own.Count -gt 0) { "signed" } elseif ($signed.Count -eq 0) { "unsigned" } else { "partly signed" }
if ($state -eq "partly signed") { throw "Only $($signed.Count) of $($own.Count) files are signed. 只有部分檔案簽到章。" }
Write-Host "Signature check: $state ($($own.Count) files). 簽章檢查：$state。"

# ---- 壓縮 + SHA256 / zip + SHA256
if (Test-Path $Rel) { Remove-Item -Recurse -Force $Rel }
New-Item -ItemType Directory -Force $Rel | Out-Null
foreach ($k in $Projects.Keys) {
    Compress-Archive -Path (Join-Path $Pub "$k\*") -DestinationPath (Join-Path $Rel "ApexHighlighter-$Version-$k.zip")
}
$lines = Get-ChildItem $Rel -Filter *.zip | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
# sha256sum 格式，不帶 BOM / sha256sum format, no BOM
[IO.File]::WriteAllText((Join-Path $Rel "SHA256SUMS.txt"), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
Get-Content (Join-Path $Rel "SHA256SUMS.txt")
Write-Host "Release files in $Rel ($state). 發佈檔在 $Rel（$state）。" -ForegroundColor Green
