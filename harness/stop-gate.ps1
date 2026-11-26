<#
=====================================================================================
stop-gate.ps1 — loop 的關卡 / the gate that closes the loop

Claude Code 每次要「停下來、說做完了」之前會先跑這支（設定在 .claude\settings.json 的 Stop hook）。
Claude Code runs this every time it is about to stop and report it is done (Stop hook in .claude\settings.json).

  1. src\、tests\ 沒有改動，或這份改動已經驗證過 → 直接放行
     No changes under src\ or tests\, or this exact change already passed → allow stop.
  2. 跑 harness\verify.ps1。通過 → 放行，記下這份改動是綠的。
     Run harness\verify.ps1. Pass → allow stop and remember this change as green.
  3. 沒過 → 結束代碼 2，把失敗摘要交給 Claude，讓它繼續修（這就是 loop）。
     Fail → exit 2 and hand the failure summary to Claude so it keeps fixing (the loop).
  4. 連續 5 輪都沒過 → 放行，並提醒使用者要人來看。避免無限迴圈。
     Five failed rounds in a row → allow stop and tell the user a human is needed. No endless loops.

環境變數 / Environment:
  APEXHL_GATE=off    暫時關掉關卡 / disable the gate
  APEXHL_GATE=quick  只跑 build + 單元測試 / build + unit tests only
  APEXHL_GATE_ROUNDS 最多幾輪（預設 5）/ max rounds (default 5)
=====================================================================================
#>
$ErrorActionPreference = "Continue"
$Root = Split-Path -Parent $PSScriptRoot
$StateFile = Join-Path $PSScriptRoot ".loop-state.json"
$MaxRounds = 5
if ($env:APEXHL_GATE_ROUNDS) { $MaxRounds = [int]$env:APEXHL_GATE_ROUNDS }

try { [void][Console]::In.ReadToEnd() } catch { }   # hook 輸入（用不到，但要讀掉）/ hook input, unused

if ($env:APEXHL_GATE -eq "off") { exit 0 }

# ---- 這份改動的指紋 / fingerprint of the current change
Push-Location $Root
try {
    $diff = (& git diff HEAD -- src tests ApexHighlighter.slnx 2>$null) -join "`n"
    $untracked = (& git ls-files --others --exclude-standard -- src tests 2>$null)
    foreach ($f in @($untracked)) { if ($f) { $diff += "`n?? $f " + (Get-Item -LiteralPath $f).LastWriteTimeUtc.Ticks } }
} finally { Pop-Location }

if (-not $diff.Trim()) { exit 0 }   # 沒改程式 / no code changes

$sha = New-Object System.Security.Cryptography.SHA256Managed
$fp = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($diff))).Replace("-", "").Substring(0, 16)

$state = [pscustomobject]@{ green = ""; rounds = 0 }
if (Test-Path $StateFile) { try { $state = Get-Content $StateFile -Raw | ConvertFrom-Json } catch { } }
if ($state.green -eq $fp) { exit 0 }   # 這份改動已經驗證過 / already verified

# ---- 跑驗證 / run the harness
$verify = Join-Path $PSScriptRoot "verify.ps1"
$psArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $verify)
if ($env:APEXHL_GATE -eq "quick") { $psArgs += "-Quick" }
$exe = (Get-Process -Id $PID).Path   # 用跟自己一樣的 PowerShell / same PowerShell as this script
& $exe @psArgs *> (Join-Path $PSScriptRoot "out-gate.log")
$code = $LASTEXITCODE

if ($code -eq 0) {
    @{ green = $fp; rounds = 0 } | ConvertTo-Json | Set-Content $StateFile
    exit 0
}

$rounds = [int]$state.rounds + 1
@{ green = $state.green; rounds = $rounds } | ConvertTo-Json | Set-Content $StateFile

$report = Join-Path $PSScriptRoot "out\report.md"
if ($rounds -ge $MaxRounds) {
    # 放行，但讓使用者看到 / allow the stop, but tell the user
    @{ systemMessage = "Verify still failing after $rounds rounds; stopping the loop so a human can look. See harness/out/report.md. 驗證連續 $rounds 輪沒過，已停止自動修正，請人工查看 harness/out/report.md。" } | ConvertTo-Json -Compress
    @{ green = $state.green; rounds = 0 } | ConvertTo-Json | Set-Content $StateFile
    exit 0
}

$summary = ""
if (Test-Path $report) { $summary = (Get-Content $report -Raw -Encoding UTF8) }
if ($summary.Length -gt 6000) { $summary = $summary.Substring(0, 6000) + "`n…(truncated, read harness/out/report.md)" }
[Console]::Error.WriteLine(@"
harness/verify.ps1 FAILED (round $rounds of $MaxRounds). 驗證沒過（第 $rounds / $MaxRounds 輪）。
Read harness/out/report.md and the logs it points to, find the root cause, fix it, then stop again so the gate re-runs.
Do not weaken tests, golden expectations or the harness to make it pass. If an expectation itself is wrong, say so to the user instead.
讀 harness/out/report.md 找出根本原因修好，再停一次讓關卡重跑。不要為了過關去放寬測試、golden 期望值或改 harness；若期望值本身有錯，請告訴使用者。

$summary
"@)
exit 2
