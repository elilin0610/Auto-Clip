<#
.SYNOPSIS
  在開始功能表建立「Apex Highlight Picker」捷徑，讓 Windows 搜尋找得到 App。
  Creates an "Apex Highlight Picker" Start menu shortcut so Windows Search can find the app.

.DESCRIPTION
  捷徑放在目前使用者的開始功能表（%APPDATA%\Microsoft\Windows\Start Menu\Programs），不需要系統管理員權限。
  再跑一次會覆蓋舊捷徑，所以組建位置改了也可以重跑。
  The shortcut goes into the current user's Start menu, so no admin rights are needed.
  Running it again overwrites the old shortcut, e.g. after the build moved.

.PARAMETER ExePath
  ApexHighlighter.App.exe 的路徑。預設是這個 repo 的 Release 組建。
  Path to ApexHighlighter.App.exe. Defaults to this repo's Release build.

.PARAMETER UseDotnet
  捷徑改成用 `dotnet ApexHighlighter.App.dll` 啟動（Smart App Control 擋 exe 時用）。
  Launch through `dotnet ApexHighlighter.App.dll` instead (for when Smart App Control blocks the exe).

.PARAMETER Remove
  刪除捷徑。 / Delete the shortcut.

.EXAMPLE
  powershell -NoProfile -File scripts\install-shortcut.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\src\ApexHighlighter.App\bin\Release\net11.0-windows10.0.19041.0\ApexHighlighter.App.exe'),
    [switch]$UseDotnet,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$name = 'Apex Highlight Picker'
$programs = [Environment]::GetFolderPath('Programs')   # %APPDATA%\Microsoft\Windows\Start Menu\Programs
$lnk = Join-Path $programs "$name.lnk"

if ($Remove) {
    if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk; Write-Host "Removed $lnk" }
    else { Write-Host "No shortcut at $lnk" }
    return
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "找不到 / Not found: $ExePath`n請先組建 / Build first: dotnet build src\ApexHighlighter.App -c Release"
}
$exe = (Resolve-Path -LiteralPath $ExePath).Path
$dir = Split-Path -Parent $exe

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
if ($UseDotnet) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $s.TargetPath = $dotnet
    $s.Arguments = '"' + [IO.Path]::ChangeExtension($exe, '.dll') + '"'
    $s.WindowStyle = 7   # minimized, so no console flashes up
} else {
    $s.TargetPath = $exe
    $s.Arguments = ''
}
$s.WorkingDirectory = $dir
$s.IconLocation = "$exe,0"   # the app icon embedded in the exe (Assets\app.ico)
$s.Description = 'Pick Apex Legends highlights from recordings / 從錄影挑出 Apex 精華'
$s.Save()
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

Write-Host "Created $lnk"
Write-Host "  -> $($s.TargetPath) $($s.Arguments)"
