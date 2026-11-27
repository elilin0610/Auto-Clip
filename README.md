# Apex Highlighter (v1: local, landscape)

**English** | [繁體中文](README.zh-TW.md)

After your stream ends, drop in your Apex recording and the app finds up to 3 fights worth posting to YouTube. You can play them, fine-tune the start and end, skip the ones you don't want, then export them as 16:9 MP4s. The original recording is only read, never modified.

## Install (for most people)

1. Download `ApexHighlighter-<version>-setup.exe` from the [Releases](https://github.com/elilin0610/Auto-Clip/releases) page.
2. Run it. By default it installs for your user only (`%LOCALAPPDATA%\Programs\Apex Highlight Picker`), so no admin rights are needed. The first page lets you choose "Install for all users" instead, which asks for admin.
3. Type "Apex Highlight Picker" in Windows Search (Start menu) to open it. Each Windows user who installs it gets the Start menu entry automatically; nobody has to run a script.
4. To uninstall: Settings > Apps > Installed apps > Apex Highlight Picker > Uninstall.

The installer includes the .NET runtime, so you don't need to install .NET. You still need FFmpeg (see Requirements). Your settings stay in your user folder after uninstalling.

**Smart App Control:** releases are not signed yet. On a PC where Smart App Control (SAC) is on, Windows refuses to run the app and shows "Application Control policy has blocked this file", even after it is installed. The setup exe itself may also get a SmartScreen warning ("Windows protected your PC"). Until the app is signed, people with SAC on can only use the CLI, or turn SAC off, which is effectively permanent. Details are under [Running](#running) below. PCs where SAC is off (or was never on, which is common on PCs upgraded from older Windows) only see the SmartScreen warning: click More info > Run anyway.

## Requirements

- Windows 10/11 (64-bit). To build from source: .NET 11 SDK
- FFmpeg: `winget install Gyan.FFmpeg`. The app looks on PATH and in winget's install location automatically. You can also put `ffmpeg.exe` and `ffprobe.exe` in an `ffmpeg\` subfolder next to the app, or point `APEXHL_FFMPEG_DIR` at a folder. Only absolute folders are searched (relative PATH entries such as `.` are skipped), and in winget's folder only the `Gyan.FFmpeg` and `BtbN.FFmpeg` packages. The log shows which FFmpeg was found and its version, and warns if it is older than 6.0. FFmpeg reads every recording, so keep it updated (`winget upgrade Gyan.FFmpeg`).
- Windows built-in OCR: install the language pack for whatever language the game is in (Settings > Time & language > Language). No paid API is needed.
- Exports use NVENC when you have an NVIDIA GPU, and fall back to x264 otherwise.

## Running from source

<a id="running"></a>

```powershell
dotnet run --project src\ApexHighlighter.App -c Release
```

If Windows shows "Application Control policy has blocked this file", that is Smart App Control (SAC) blocking an unsigned program. **Keep SAC on if you can.** Turning it off removes that protection for every program on the PC, and on many Windows versions it can only be turned back on by resetting Windows.

Tested on 2026-11-27 with SAC on (`VerifiedAndReputablePolicyState = 1`):

| How it is started | Result |
|---|---|
| `ApexHighlighter.App.exe` | Blocked |
| `dotnet ApexHighlighter.App.dll` | Also blocked: SAC checks the DLL that `dotnet.exe` loads (CodeIntegrity events 3033/3077/3118) |
| CLI, `dotnet apexhl.dll` (also what `harness\verify.ps1` uses) | Works: scan and export passed |

So with SAC on:

- Use the CLI below for scanning and exporting; it works today.
- To use the window, the app has to be signed with a certificate from a trusted CA. See [docs/SIGNING.md](docs/SIGNING.md) (an OV certificate is the option available in Taiwan). A self-signed certificate does not satisfy SAC.
- Turning SAC off (Settings > Privacy & security > Windows Security > App & browser control > Smart App Control settings) also works, but think of it as permanent.

Releases come with a `SHA256SUMS.txt` file so people can check the download.

Security notes: [docs/SECURITY.md](docs/SECURITY.md).

If you build from source instead of using the installer, you can still add it to Windows Search (Start menu): build the Release version once and create a shortcut:

```powershell
dotnet build src\ApexHighlighter.App -c Release
powershell -NoProfile -File scripts\install-shortcut.ps1
```

This adds "Apex Highlight Picker" to your Start menu for the current Windows user only, so every other user would have to run it too; the installer above does not have that limitation. If you later run the installer, it replaces this shortcut. Run it again after the build location changes; add `-UseDotnet` if Smart App Control blocks the exe, or `-Remove` to delete it. If PowerShell says running scripts is disabled, allow local scripts once with `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned`.

CLI (same core as the UI):

```powershell
dotnet run --project src\ApexHighlighter.Cli -c Release -- scan "recording.mkv"
dotnet run --project src\ApexHighlighter.Cli -c Release -- export "recording.mkv" 12:30 13:15 "output.mp4" --track 0
dotnet run --project src\ApexHighlighter.Cli -c Release -- synccheck "recording.mkv" 12:30 "output.mp4"
dotnet run --project src\ApexHighlighter.Cli -c Release -- ocr "recording.mkv" 754.5
dotnet run --project src\ApexHighlighter.Cli -c Release -- export "recording.mkv" 12:30 13:15 "vertical.mp4" --format vfit
dotnet run --project src\ApexHighlighter.Cli -c Release -- thumb "recording.mkv" 754.5 "thumbnail.jpg"
dotnet run --project src\ApexHighlighter.Cli -c Release -- obswatch --port 4455
dotnet run --project src\ApexHighlighter.Cli -c Release -- batch "D:\Recordings" --out "D:\Highlights"
```

`batch` handles several recordings at once as two stations: scanning (CPU, OCR) and exporting (GPU, NVENC). As soon as one recording is scanned it goes to export, and the next one starts scanning, so both run at the same time. One failed recording does not stop the others. Options: `--scan-workers 1`, `--export-workers 2`, `--format`, `--no-export`.

Unit tests (no FFmpeg or recordings needed):

```powershell
dotnet test tests\ApexHighlighter.Tests
```

To check a change end to end (build, tests, scan and export of real sample recordings), see [harness/README.md](harness/README.md).

The built executable is `apexhl.exe` (in `src\ApexHighlighter.Cli\bin\<Debug or Release>\net11.0-windows10.0.19041.0\`), and you can run it directly, for example `apexhl scan "recording.mkv"`. Running it with no arguments lists every command. `recording.mkv` is just an example name; use the path to your own recording.

Put the OBS password for `obswatch` in the environment variable `APEXHL_OBS_PASSWORD`, not on the command line (it would stay in your command history).

## The interface

1. **Import recording…**, or drag an MKV/MP4 straight into the window. The app measures the loudness of each audio track and picks the one that has sound by default.
   - Choose or drop **several recordings** at once and they are scanned one after another.
   - **Reopening a recording you already scanned** (same file, unchanged) shows the last scan right away, together with your edits: start/end times, ticks, and clips you added by hand. If you changed the highlight settings or keyword rules since that scan, the log says so; press "Find highlights" to rescan with the new settings.
2. **Find highlights**: lists up to 3 clips. Each one shows a thumbnail, its time, what triggered it (for example "Knock ×2, Elimination"), when each prompt appeared, and how many seconds of lead-in were added. If the app isn't confident, it lists nothing.
   - While scanning, prompts appear live as yellow ticks on the timeline. Click a tick to jump there and look without waiting for the scan to finish.
3. Click a clip: the player jumps to its start, and you can adjust it:
   - drag the white handles at either end of the selection on the timeline
   - type the start/end time, or use ±0.2 / ±1 s and "Set here" (shortcuts I / O)
   - "▶ Play clip", "▶ Check ending"
   - untick = skip, "Remove" = take it off the list
4. **+ Add at playhead**: add a clip by hand for anything the scan missed.
5. **Export ticked clips**: one MP4 per clip, saved to `Videos\Apex精華` by default. After exporting, each file is checked automatically: full decode, aspect ratio, and audio/video start and length.
   - Formats: Landscape 16:9 / Vertical 9:16 (crop middle) / Vertical 9:16 (whole frame). Vertical is 1080×1920 for YouTube Shorts. "Crop middle" cuts off the kill feed and both sides; "whole frame" puts the full picture in the middle and fills above and below with a blurred background.
6. The output folder, format, and window size and position are remembered for next time.

## Auto-scan when OBS stops recording

Settings → "OBS": tick "When OBS stops recording, import and scan the recording automatically", fill in the address (default `localhost`), port (default 4455) and password, then press "Test connection" to check.

- In OBS, first go to Tools → WebSocket Server Settings → tick "Enable WebSocket server"; the password is under "Show Connect Info". OBS 28 or later is required.
- As soon as recording stops, the app imports that file and starts scanning. If it is busy, the recording is queued, so several recordings in a row are never lost.
- The app only listens for OBS notifications and never changes any OBS settings. If OBS isn't running it retries every 10 seconds; it stops retrying if the password is wrong.
- The password is encrypted with Windows DPAPI before being saved in `settings.json`, so only your Windows account can decrypt it. It is never stored in plain text.
- The OBS WebSocket connection is not encrypted. Keep the address on `localhost` (OBS on this PC). The settings window shows a warning if you enter another computer.
- The app only opens a path from OBS if it is an existing video file on a local drive (for example `D:\Videos\...mkv`). URLs, network shares (`\\server\share`) and other file types are ignored and noted in the log.

## Interface language

"⚙ Settings" (top right) → "Interface language": English / 繁體中文 / 日本語. The change applies immediately, no restart needed. Your choice is saved in `%LOCALAPPDATA%\ApexHighlighter\settings.json` and reused next time. On first launch the app follows the Windows display language (Chinese → Traditional Chinese, Japanese → Japanese, anything else → English).

- What switches: buttons, help text, the status bar, each clip's trigger and details, skip reasons, export check results and scan reports. Messages already written to the log area below are not rewritten.
- The game language needs no setting: every scan detects it from the prompts and HUD text in the recording, and shows the result at the top right (for example "Game language: English (detected)"). When there are too few prompts to tell, it says "probably …". The CLI can still force a language with `--lang`.
- All text lives in `src/ApexHighlighter.Core/Loc.cs`, one key per string with English, Traditional Chinese and Japanese. To add a language, add a column to each row and an entry to `Languages`.
- The CLI prints Traditional Chinese by default; add `--ui-lang en` or `--ui-lang ja` to change it.

## How detection works (no hard-coded language or HUD position)

- Takes 2 frames per second, scales them to 1280 wide, and runs Windows OCR on the **whole frame**. It doesn't assume where prompts appear; their position is measured from the recording after the scan.
- `keywords.json` holds prompt word lists for English, Traditional Chinese, Simplified Chinese, Japanese and German. At the start of a scan every language is tried; the one whose prompts plus always-on HUD words (such as SQUADS LEFT) match most often wins, and only that one is used from then on.
- The English rules were confirmed against real recordings:
  - Counted: `KNOCKED DOWN` (knock), `ELIMINATED` / `COMEBACK KILL` (elimination), `SQUAD WIPE`, `YOU ARE THE NEW KILL LEADER`
  - The text must be all caps, and only an icon may come before the keyword. So `Team Eliminated +5 RP`, subtitles and `Knockdown Shield` are not counted.
  - `SQUAD ELIMINATED` means your own squad was wiped, so it is not counted.
  - `ASSIST …` is an assist, weighted 0.5. A fight with only assists is not listed.
- The same prompt showing on several frames in a row is merged into one. Events within 25 seconds of each other count as the same fight.
- Start: from the first knock, search back for where loudness stays high (gunfire), limited to 8–25 seconds. End: 6 seconds after the last event (8 seconds more for a championship win).
- Score: knock 1, elimination 1.5, squad wipe 2.5, kill leader 1, champion 4, assist 0.5. A hit seen on only one frame counts half. Fights scoring under 2 are not listed, and at most 3 are kept.
- Second look: before a fight is suggested, prompts seen in fewer than 3 samples are read again around where they appeared, at 6 frames per second. Only prompts seen again count, at full weight. If the fight's score falls below 2 it is dropped ("second look failed") and the next fight is checked instead. A fight that just missed the threshold is promoted if its weak prompts turn out to be real. If the frames can't be read again, the first decision is kept. Turn it off with `--no-verify`.
- Every scan saves a full log to `%LOCALAPPDATA%\ApexHighlighter\scans\`, with every OCR hit's text and position, plus skipped fights and why they were skipped, so you can check false positives.

## Export

- Re-encodes from the start point, so cuts are frame-accurate; audio and video both start at 0.
- If the source isn't 16:9, it is not scaled or cropped; it is centered and padded with black bars to 16:9 (for example 1920×1200 → 2134×1200).
- Only one audio track is exported (the one selected in the UI), as AAC 192k.
- Writes to a `.part.mp4` first and renames it only when done; on cancel or failure the partial file is deleted. The output path can't be the same as the source.

## Project layout

```
src/ApexHighlighter.Core   detection, FFmpeg, export (shared by UI and CLI)
src/ApexHighlighter.App    WPF interface
src/ApexHighlighter.Cli    command line: scan, export, batch, OCR test, A/V sync check
tests/ApexHighlighter.Tests unit tests (keyword rules, clustering, second look, pipeline, export arguments, cache, queue…)
harness/                   end-to-end verify script and Claude's fix loop (opt-in: enable-gate.ps1)
scripts/package.ps1        release zips, installer, optional signing, SHA256SUMS.txt
installer/                 Inno Setup script for the setup exe (Start menu entry, uninstall)
.github/workflows/         release workflow (vulnerability check, tests, signing, checksums)
docs/                      SECURITY.md, SIGNING.md, screenshots
acceptance/                acceptance notes and scan reports
```

## Not in this version

Twitch download, YouTube upload, paid APIs.

- Private YouTube upload needs you to create an OAuth client in Google Cloud and authorize this app. That is your own account setup, so it can only be hooked up after you do it.
- Twitch download needs a separate download tool (such as yt-dlp).
