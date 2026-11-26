<#
=====================================================================================
verify.ps1 — 一鍵驗證（harness + graph） / One-shot verification (harness + graph)

把「改完程式之後要人工檢查的事」全部自動做完，結果寫成報告給人和 Claude 讀。
Runs every check a human would do after a code change and writes a report that
both people and Claude can read.

驗證步驟是一張相依圖（graph），沒有相依的步驟會同時跑：
The checks form a dependency graph; steps whose dependencies are done run in parallel:

    build ──┬── unit-tests
            ├── scan:<case>  ── export:<case>
            └── scan:<case>  ── export:<case>   …（每支測試錄影一條 / one chain per sample）

用法 / Usage (Windows PowerShell 5.1 or pwsh 7):
    powershell -NoProfile -ExecutionPolicy Bypass -File harness\verify.ps1
    powershell -NoProfile -ExecutionPolicy Bypass -File harness\verify.ps1 -Quick
    ... -Golden harness\golden.local.json -MaxParallel 2 -KeepClips

結束代碼 / Exit code: 0 = 全部通過（略過的步驟不算失敗）/ all passed (skipped steps are not failures)
                      1 = 有步驟沒過 / at least one step failed
報告 / Report: harness\out\report.md（人看 / for people）、harness\out\report.json（程式讀 / for tools）
=====================================================================================
#>
[CmdletBinding()]
param(
    [switch]$Quick,                          # 只跑 build + 單元測試 / build + unit tests only
    [string]$Golden = "",                    # 測試錄影清單 / list of golden sample recordings
    [string]$Configuration = "Release",
    [int]$MaxParallel = 3,
    [switch]$KeepClips                       # 保留試剪出來的 mp4 / keep exported test clips
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Out = Join-Path $PSScriptRoot "out"
if ($Golden -eq "") { $Golden = Join-Path $PSScriptRoot "golden.local.json" }
if (Test-Path $Out) { Remove-Item -Recurse -Force (Join-Path $Out "*") -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $Out | Out-Null

# ------------------------------------------------------------------ graph definition
# 每個節點 / each node:
#   Id, Deps          相依 / dependencies
#   Prepare {ctx,node}      在主執行緒算出要跑的指令 / returns @{Exe;Args}, @{Skip="why"} or @{Fail="why"}
#   Check {ctx,run,node}    看結束代碼和輸出判斷過不過 / returns @{Ok;Summary;Data}
#   Case                    golden 清單裡的那一筆（scan/export 才有）/ the golden case, for scan/export nodes
$Nodes = New-Object System.Collections.ArrayList
function Add-Node($id, $deps, $prepare, $check, $case = $null) {
    [void]$Nodes.Add([pscustomobject]@{ Id = $id; Deps = @($deps); Prepare = $prepare; Check = $check; Case = $case;
        Status = "pending"; Summary = ""; Seconds = 0; LogTail = ""; Data = $null; Job = $null; Started = $null; LogFile = "" })
}

function Find-Apexhl {
    $dir = Join-Path $Root "src\ApexHighlighter.Cli\bin\$Configuration"
    if (-not (Test-Path $dir)) { return $null }
    $dll = Get-ChildItem $dir -Recurse -Filter "apexhl.dll" | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($dll) { return $dll.FullName } else { return $null }
}

Add-Node "build" @() {
    param($ctx, $node)
    $sln = Join-Path $Root "ApexHighlighter.slnx"
    @{ Exe = "dotnet"; Args = @("build", $sln, "-c", $Configuration, "-nologo", "-v", "q", "-clp:ErrorsOnly") }
} {
    param($ctx, $run, $node)
    if ($run.Exit -eq 0) { @{ Ok = $true; Summary = "build OK" } }
    else {
        $errs = ($run.Output -split "`r?`n" | Where-Object { $_ -match "error [A-Z]+\d+" } | Select-Object -First 5) -join "`n"
        @{ Ok = $false; Summary = "build failed / 組建失敗`n$errs" }
    }
}

Add-Node "unit-tests" @("build") {
    param($ctx, $node)
    $proj = Join-Path $Root "tests\ApexHighlighter.Tests\ApexHighlighter.Tests.csproj"
    if (-not (Test-Path $proj)) { return @{ Skip = "no test project yet / 還沒有單元測試專案" } }
    @{ Exe = "dotnet"; Args = @("test", $proj, "-c", $Configuration, "--no-build", "-nologo", "-v", "q") }
} {
    param($ctx, $run, $node)
    $line = ($run.Output -split "`r?`n" | Where-Object { $_ -match "(Passed!|Failed!|通過!|失敗!)" } | Select-Object -Last 1)
    $failed = ($run.Output -split "`r?`n" | Where-Object { $_ -match "^\s*(Failed|失敗)\s+\S" } | Select-Object -First 10) -join "`n"
    if ($run.Exit -eq 0) { @{ Ok = $true; Summary = "$line".Trim() } }
    else { @{ Ok = $false; Summary = ("tests failed / 測試沒過: " + "$line".Trim() + "`n" + $failed).Trim() } }
}

# ------------------------------------------------------------------ golden recordings
$Cases = @()
if (-not $Quick) {
    if (Test-Path $Golden) {
        $Cases = @((Get-Content $Golden -Raw -Encoding UTF8 | ConvertFrom-Json).cases)
    } else {
        Write-Host "[verify] $Golden not found; scan/export steps skipped. Copy harness\golden.example.json to golden.local.json." -ForegroundColor Yellow
    }
}

function Test-Expect($expect, $report) {
    # 依 golden 清單的 expect 比對掃描結果 / compare scan result against expectations
    $problems = @()
    $cands = @($report.Candidates)
    $n = $cands.Count
    if ($null -ne $expect.minCandidates -and $n -lt $expect.minCandidates) { $problems += "found $n clips, expected at least $($expect.minCandidates) / 片段太少" }
    if ($null -ne $expect.maxCandidates -and $n -gt $expect.maxCandidates) { $problems += "found $n clips, expected at most $($expect.maxCandidates) / 片段太多（可能誤判）" }
    if ($expect.language -and $report.Language -ne $expect.language) { $problems += "language $($report.Language), expected $($expect.language) / 語言判斷錯誤" }
    foreach ($t in @($expect.mustCover)) {
        if ($null -eq $t) { continue }
        $hit = $cands | Where-Object { $_.Start -le $t -and $_.End -ge $t }
        if (-not $hit) { $problems += "no clip covers ${t}s / 漏抓 ${t} 秒的事件" }
    }
    foreach ($t in @($expect.mustNotCover)) {
        if ($null -eq $t) { continue }
        $hit = $cands | Where-Object { $_.Start -le $t -and $_.End -ge $t }
        if ($hit) { $problems += "a clip covers ${t}s, which should be empty / ${t} 秒不該被剪進來（誤判）" }
    }
    if ($expect.maxClipSeconds) {
        foreach ($c in $cands) { if (($c.End - $c.Start) -gt $expect.maxClipSeconds) { $problems += ("clip {0:0}-{1:0}s is longer than {2}s / 片段太長" -f $c.Start, $c.End, $expect.maxClipSeconds) } }
    }
    if ($expect.maxScanSeconds -and $report.ScanSeconds -gt $expect.maxScanSeconds) {
        $problems += ("scan took {0:0.0}s, limit {1}s / 掃描太慢" -f $report.ScanSeconds, $expect.maxScanSeconds)
    }
    return ,$problems
}

foreach ($case in $Cases) {
    Add-Node "scan:$($case.id)" @("build") {
        param($ctx, $node)
        $c = $node.Case
        $file = [Environment]::ExpandEnvironmentVariables($c.file)
        if (-not (Test-Path -LiteralPath $file)) { return @{ Skip = "sample not found / 找不到測試錄影: $file" } }
        $dll = Find-Apexhl
        if (-not $dll) { return @{ Fail = "apexhl.dll not found after build / 組建後找不到 apexhl.dll" } }
        $repDir = Join-Path $Out ("scans\" + $c.id)
        New-Item -ItemType Directory -Force $repDir | Out-Null
        $a = @($dll, "scan", $file, "--ui-lang", "en", "--report-dir", $repDir) + @($c.args | Where-Object { $_ })
        @{ Exe = "dotnet"; Args = $a }
    } {
        param($ctx, $run, $node)
        $c = $node.Case
        if ($run.Exit -ne 0) { return @{ Ok = $false; Summary = "apexhl scan exited $($run.Exit) / 掃描失敗" } }
        $json = Get-ChildItem (Join-Path $Out ("scans\" + $c.id)) -Filter *.json | Sort-Object LastWriteTime | Select-Object -Last 1
        if (-not $json) { return @{ Ok = $false; Summary = "no scan report written / 沒有產生掃描報告" } }
        $rep = Get-Content $json.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        $problems = Test-Expect $c.expect $rep
        $clips = (@($rep.Candidates) | ForEach-Object { "{0:0.0}-{1:0.0}s score {2:0.0}" -f $_.Start, $_.End, $_.Score }) -join "; "
        if ($clips -eq "") { $clips = "none / 無" }
        $sum = ("{0} clip(s): {1}  ·  scan {2:0.0}s  ·  report {3}" -f @($rep.Candidates).Count, $clips, $rep.ScanSeconds, $json.FullName)
        if ($problems.Count -eq 0) { @{ Ok = $true; Summary = $sum; Data = $rep } }
        else { @{ Ok = $false; Summary = (($problems -join "`n") + "`n" + $sum); Data = $rep } }
    } $case

    if ($case.export -ne $false) {
        Add-Node "export:$($case.id)" @("scan:$($case.id)") {
            param($ctx, $node)
            $c = $node.Case
            $rep = $ctx["scan:$($c.id)"].Data
            $first = @($rep.Candidates) | Select-Object -First 1
            if (-not $first) { return @{ Skip = "no clip to export / 沒有片段可以試剪" } }
            $dll = Find-Apexhl
            $mp4 = Join-Path $Out ("clips\" + $c.id + ".mp4")
            New-Item -ItemType Directory -Force (Split-Path $mp4) | Out-Null
            $s = [string]::Format([Globalization.CultureInfo]::InvariantCulture, "{0:0.###}", $first.Start)
            $e = [string]::Format([Globalization.CultureInfo]::InvariantCulture, "{0:0.###}", $first.End)
            $file = [Environment]::ExpandEnvironmentVariables($c.file)
            @{ Exe = "dotnet"; Args = @($dll, "export", $file, $s, $e, $mp4, "--ui-lang", "en") }
        } {
            param($ctx, $run, $node)
            $c = $node.Case
            # apexhl export 會自己解碼檢查長度、音畫起點；3 = 檢查沒過
            # apexhl export verifies duration and A/V start itself; exit 3 = check failed
            $tail = ($run.Output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 2) -join " "
            if (-not $KeepClips) { Remove-Item (Join-Path $Out ("clips\" + $c.id + ".mp4")) -ErrorAction SilentlyContinue }
            if ($run.Exit -eq 0) { @{ Ok = $true; Summary = "export verified / 試剪驗收通過: $tail" } }
            elseif ($run.Exit -eq 3) { @{ Ok = $false; Summary = "export check failed / 試剪檢查沒過: $tail" } }
            else { @{ Ok = $false; Summary = "apexhl export exited $($run.Exit) / 試剪失敗: $tail" } }
        } $case
    }
}

# ------------------------------------------------------------------ graph runner
$Runner = {
    param($exe, $argList, $cwd, $log)
    $argList = @($argList | ForEach-Object { [string]$_ })
    Set-Location $cwd
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $env:NO_COLOR = "1"
    $o = & $exe @argList 2>&1 | Out-String
    $code = $LASTEXITCODE
    Set-Content -Path $log -Value $o -Encoding UTF8
    [pscustomobject]@{ Exit = $code; Output = $o }
}

$ctx = @{}
New-Item -ItemType Directory -Force (Join-Path $Out "logs") | Out-Null
function Get-Node($id) { $Nodes | Where-Object { $_.Id -eq $id } | Select-Object -First 1 }
function Finish($node, $status, $summary) {
    $node.Status = $status; $node.Summary = $summary
    if ($node.Started) { $node.Seconds = [math]::Round(((Get-Date) - $node.Started).TotalSeconds, 1) }
    $ctx[$node.Id] = $node
    $color = @{ pass = "Green"; fail = "Red"; skip = "DarkYellow"; blocked = "DarkGray" }[$status]
    Write-Host ("[{0,-7}] {1}  {2}" -f $status, $node.Id, ($summary -split "`n")[0]) -ForegroundColor $color
}

Write-Host "[verify] graph: $(($Nodes | ForEach-Object { if ($_.Deps.Count) { "$($_.Deps -join '+')->$($_.Id)" } else { $_.Id } }) -join ', ')"

while ($true) {
    $pending = @($Nodes | Where-Object { $_.Status -eq "pending" })
    $running = @($Nodes | Where-Object { $_.Status -eq "running" })
    if ($pending.Count -eq 0 -and $running.Count -eq 0) { break }

    # 相依失敗或略過 → 這個節點也跑不了 / a failed or skipped dependency blocks the node
    foreach ($n in $pending) {
        $bad = $n.Deps | Where-Object { (Get-Node $_).Status -in @("fail", "skip", "blocked") }
        if ($bad) { Finish $n "blocked" "waiting on $($bad -join ', ') / 前一步沒過" }
    }

    # 啟動所有相依都過了的節點 / launch every node whose dependencies passed
    foreach ($n in @($Nodes | Where-Object { $_.Status -eq "pending" })) {
        if (@($Nodes | Where-Object { $_.Status -eq "running" }).Count -ge $MaxParallel) { break }
        $ready = -not ($n.Deps | Where-Object { (Get-Node $_).Status -ne "pass" })
        if (-not $ready) { continue }
        $n.Started = Get-Date
        $p = & $n.Prepare $ctx $n
        if ($p.Skip) { Finish $n "skip" $p.Skip; continue }
        if ($p.Fail) { Finish $n "fail" $p.Fail; continue }
        $n.LogFile = Join-Path $Out ("logs\" + ($n.Id -replace '[:\\/]', '_') + ".log")
        $n.Job = Start-Job -ScriptBlock $Runner -ArgumentList $p.Exe, $p.Args, $Root, $n.LogFile
        $n.Status = "running"
        Write-Host ("[start  ] {0}" -f $n.Id) -ForegroundColor Cyan
    }

    # 收已完成的 / collect finished nodes
    foreach ($n in @($Nodes | Where-Object { $_.Status -eq "running" })) {
        if ($n.Job.State -notin @("Completed", "Failed", "Stopped")) { continue }
        $run = Receive-Job $n.Job -ErrorAction SilentlyContinue | Select-Object -Last 1
        Remove-Job $n.Job -Force
        if (-not $run) { $run = [pscustomobject]@{ Exit = -1; Output = "job produced no result" } }
        $n.LogTail = (($run.Output -split "`r?`n") | Select-Object -Last 40) -join "`n"
        try { $r = & $n.Check $ctx $run $n } catch { $r = @{ Ok = $false; Summary = "check crashed / 檢查程式出錯: $_" } }
        $n.Data = $r.Data
        if ($r.Ok) { Finish $n "pass" $r.Summary } else { Finish $n "fail" $r.Summary }
    }
    Start-Sleep -Milliseconds 300
}

# ------------------------------------------------------------------ report
$failed = @($Nodes | Where-Object { $_.Status -eq "fail" })
$overall = if ($failed.Count -eq 0) { "pass" } else { "fail" }
$result = [ordered]@{
    overall = $overall
    finishedAt = (Get-Date).ToString("s")
    quick = [bool]$Quick
    git = $(try { (& git -C $Root rev-parse --short HEAD 2>$null) } catch { "?" })
    nodes = @($Nodes | ForEach-Object { [ordered]@{ id = $_.Id; deps = $_.Deps; status = $_.Status; seconds = $_.Seconds; summary = $_.Summary; log = $_.LogFile; logTail = $(if ($_.Status -eq "fail") { $_.LogTail } else { "" }) } })
}
$result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Out "report.json") -Encoding UTF8

$md = New-Object System.Text.StringBuilder
[void]$md.AppendLine("# Verify report / 驗證報告: **$($overall.ToUpper())**")
[void]$md.AppendLine("")
[void]$md.AppendLine("$($result.finishedAt) · commit $($result.git)$(if ($Quick) { ' · quick' })")
[void]$md.AppendLine("")
[void]$md.AppendLine("| step / 步驟 | status / 結果 | sec | summary / 摘要 |")
[void]$md.AppendLine("|---|---|---|---|")
foreach ($n in $Nodes) { [void]$md.AppendLine("| $($n.Id) | $($n.Status) | $($n.Seconds) | $((($n.Summary -replace '\|', '/') -split "`r?`n") -join '<br>') |") }
foreach ($n in $failed) {
    [void]$md.AppendLine(""); [void]$md.AppendLine("## $($n.Id) — log tail / 最後 40 行")
    [void]$md.AppendLine('```'); [void]$md.AppendLine($n.LogTail); [void]$md.AppendLine('```')
}
Set-Content (Join-Path $Out "report.md") $md.ToString() -Encoding UTF8

Write-Host ""
Write-Host "[verify] $($overall.ToUpper())  ->  $(Join-Path $Out 'report.md')" -ForegroundColor $(if ($overall -eq "pass") { "Green" } else { "Red" })
if ($overall -eq "pass") { exit 0 } else { exit 1 }
