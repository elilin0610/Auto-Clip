<#
=====================================================================================
enable-gate.ps1 — 選擇要不要啟用 loop 關卡 / opt in to (or out of) the loop gate

關卡（stop-gate.ps1）預設是關的。啟用後，Claude Code 在這個 repo 每次要停下來之前，
都會自動執行 harness\stop-gate.ps1 → verify.ps1 → dotnet build／test。
dotnet build 會執行專案檔裡的 MSBuild 指令，所以只在你信任這份程式碼（包含別人送來的 PR 分支）時才啟用。
The gate (stop-gate.ps1) is off by default. Once enabled, every time Claude Code is about to stop in this repo
it runs harness\stop-gate.ps1 → verify.ps1 → dotnet build/test automatically. dotnet build runs whatever the
project files tell MSBuild to run, so only enable it while you trust the code you have checked out,
including other people's PR branches.

設定只寫進 .claude\settings.local.json（只在這台電腦，不進 git）。
The hook is written only to .claude\settings.local.json (this PC only, never committed).

用法 / Usage:
    powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\enable-gate.ps1           # 啟用 / enable
    powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\enable-gate.ps1 -Disable  # 關閉 / disable
=====================================================================================
#>
[CmdletBinding()]
param([switch]$Disable)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Dir = Join-Path $Root ".claude"
$Path = Join-Path $Dir "settings.local.json"
$Command = "powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness/stop-gate.ps1"

New-Item -ItemType Directory -Force $Dir | Out-Null
$settings = [pscustomobject]@{}
if (Test-Path $Path) { $settings = Get-Content $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
if (-not $settings.PSObject.Properties["hooks"]) { $settings | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) }

# 先拿掉舊的關卡設定（不動其他 hook）/ drop any existing gate entry, keep other hooks
$stop = @()
if ($settings.hooks.PSObject.Properties["Stop"]) {
    $stop = @($settings.hooks.Stop | Where-Object { -not (@($_.hooks) | Where-Object { "$($_.command)" -like "*harness/stop-gate.ps1*" }) })
}
if (-not $Disable) {
    $stop += [pscustomobject]@{ hooks = @([pscustomobject]@{ type = "command"; command = $Command; timeout = 900 }) }
}
if ($settings.hooks.PSObject.Properties["Stop"]) { $settings.hooks.Stop = $stop }
else { $settings.hooks | Add-Member -NotePropertyName Stop -NotePropertyValue $stop }

# UTF-8 不帶 BOM，Claude Code 才讀得到 / UTF-8 without BOM so Claude Code can parse it
$json = $settings | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding $false))

if ($Disable) {
    Write-Host "Loop gate disabled. 已關閉 loop 關卡。($Path)" -ForegroundColor Yellow
} else {
    Write-Host "Loop gate enabled for this PC only. 已在這台電腦啟用 loop 關卡。($Path)" -ForegroundColor Green
    Write-Host "Restart Claude Code (or run /hooks) to pick it up. 重新開啟 Claude Code（或執行 /hooks）後生效。"
    Write-Host "Disable again with -Disable. 用 -Disable 可以再關掉。"
}
