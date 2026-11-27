# Verify harness (loop / graph / harness)

[繁體中文](README.zh-TW.md)

Lets Claude (or you) check a change end to end without watching it: build, unit tests, real-recording scan, test export, and a report. Inspired by the "loop, graph, harness" way of running agents.

| Layer | File | What it does |
|---|---|---|
| harness | `verify.ps1` | Runs every check and writes `out/report.md` and `out/report.json` |
| graph | inside `verify.ps1` | Steps run by dependency; independent ones run in parallel (`-MaxParallel`, default 3) |
| loop | `stop-gate.ps1` + `enable-gate.ps1` (opt-in) | Before Claude stops, runs the harness; on failure Claude keeps fixing, up to 5 rounds |

```
build ──┬── unit-tests
        ├── scan:A-no-kills
        └── scan:B-squad-wipe ── export:B-squad-wipe
```

## First-time setup (Eli's PC)

1. Copy `golden.example.json` to `golden.local.json` (not committed).
2. Set `file` paths to the sample recordings, or set the `APEXHL_SAMPLES` environment variable to the folder holding them.
3. Run once by hand:
   ```powershell
   powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\verify.ps1
   ```

Without `golden.local.json` the scan/export steps are skipped, and without a test project the unit-test step is skipped. Skips are listed in the report and do not count as failures.

## Golden cases

Each case says what a correct scan of a recording looks like:

| Field | Meaning |
|---|---|
| `minCandidates` / `maxCandidates` | How many clips the scan may suggest (`max 0` guards against false positives) |
| `mustCover` | Seconds that must fall inside a suggested clip (misses) |
| `mustNotCover` | Seconds that must not be inside any clip (false positives) |
| `language` | Detected game language |
| `maxClipSeconds`, `maxScanSeconds` | Clip length and scan time limits |
| `export` | Also export the first clip and run `apexhl export`'s own check (duration, A/V start) |

## The loop

The gate is **off by default**. Turn it on for this PC only:

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\enable-gate.ps1           # enable
powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\enable-gate.ps1 -Disable  # disable
```

This writes the Stop hook to `.claude/settings.local.json`, which is git-ignored, so cloning the repo or checking out a PR branch never turns it on by itself. Only enable it while you trust the checked-out code: the gate runs `dotnet build`, and MSBuild runs whatever the project files say.

Why `RemoteSigned` and not `Bypass`: scripts from your own git clone run normally, but a script that came from the internet (for example in a downloaded ZIP, which Windows marks) is refused unless it is signed. Read it first, then `Unblock-File` it if you trust it.

Once enabled, `stop-gate.ps1` runs whenever Claude Code is about to stop in this repo:

- No changes under `src/` or `tests/`, or the same change already passed: stop allowed.
- Otherwise it runs `verify.ps1`. A pass allows the stop. A failure blocks it and gives Claude the report to fix.
- After 5 failed rounds in a row it lets Claude stop and shows a message asking a human to look.

Switches: `APEXHL_GATE=off` disables the gate, `APEXHL_GATE=quick` runs only build and unit tests, `APEXHL_GATE_ROUNDS=N` changes the round limit. Use the `/loop-fix <task>` command to start a task with this loop.

The failure report handed back to Claude contains tool output (file names, OCR text, logs). The gate labels it as data so Claude does not follow text inside it as instructions.

## Notes

- CLI runs as `dotnet apexhl.dll`, so Smart App Control does not block an unsigned exe.
- Not yet run on Windows: these scripts were written in a cloud environment without PowerShell. The first run may need small fixes.
