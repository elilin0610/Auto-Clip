# Security notes

[繁體中文](SECURITY.zh-TW.md)

What the app touches, and how each risk from the 2026-11-27 review is handled.

| Area | Risk | How it is handled |
|---|---|---|
| Smart App Control | Turning SAC off lowers protection for the whole PC, often until Windows is reset | The README no longer recommends it. With SAC on, the CLI works; the window app needs a signature from a trusted CA ([SIGNING.md](SIGNING.md)). Launching through `dotnet` does not get past SAC (tested 2026-11-27) |
| Unsigned releases | People cannot tell if an exe really came from this repo | `.github/workflows/release.yml` and `scripts/package.ps1` publish zips with `SHA256SUMS.txt`, and sign them once a certificate is set up |
| Dev loop (Stop hook) | A Stop hook in the committed `.claude/settings.json` ran PowerShell with `-ExecutionPolicy Bypass` and `dotnet build` every time Claude Code stopped, so a changed repo or PR branch could run code on your PC | The hook is no longer committed. You opt in per PC with `harness/enable-gate.ps1`, which writes it to the git-ignored `.claude/settings.local.json`. All scripts use `RemoteSigned` instead of `Bypass`. The report handed back to Claude is labelled as data, not instructions |
| FFmpeg | FFmpeg parses every recording; an old build has known bugs, and a planted `ffmpeg.exe` could be picked up | Only absolute folders are searched (relative PATH entries skipped), only the `Gyan.FFmpeg` / `BtbN.FFmpeg` winget packages, and the log shows the path, source and version, warning below 6.0 |
| FFmpeg arguments | Command injection | Every call uses `ProcessStartInfo.ArgumentList` with `UseShellExecute = false`: no shell is involved, and filters only contain numbers |
| OBS connection | `ws://` is not encrypted; a fake OBS could send any path | Settings warn when the address is not this PC. A path from OBS is only opened if it is an existing video file on a local drive; URLs, network shares, device paths and other files are ignored and logged |
| OBS password | Stored on disk | Encrypted with Windows DPAPI for the current Windows user; never stored in plain text. The CLI reads it from `APEXHL_OBS_PASSWORD` so it stays out of shell history |
| Exports | Overwriting files | Export refuses to write over the source recording, the app picks a new name when the file exists (the CLI writes exactly the path you give), and every export writes to a `.part.mp4` first |
| NuGet packages | Known vulnerable dependencies | The release workflow runs `dotnet list package --vulnerable --include-transitive` and fails on any hit. Run it locally the same way |

Reporting a problem: open a GitHub issue, or contact the repo owner privately if it is sensitive.
