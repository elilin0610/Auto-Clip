# Apex 精華挑片（第一版：本機、橫式）

[English](README.md) | **繁體中文**

關台後把 Apex 錄影丟進來，程式會找出最多 3 段值得發 YouTube 的交戰。你可以播放、微調起訖、跳過不要的，再匯出成 16:9 MP4。原始錄影只讀取，不會修改。

## 安裝（一般使用者）

1. 到 [Releases](https://github.com/elilin0610/Auto-Clip/releases) 頁面下載 `ApexHighlighter-<版本>-setup.exe`。
2. 執行它。預設只裝給目前的使用者（`%LOCALAPPDATA%\Programs\Apex Highlight Picker`），所以不需要系統管理員權限。第一頁也可以改選「Install for all users」，那樣才會要求管理員。
3. 在 Windows 搜尋（開始功能表）輸入「Apex Highlight Picker」就能打開。每個 Windows 使用者自己裝完就會有開始功能表項目，不用跑任何指令碼。
4. 解除安裝：Settings > Apps > Installed apps > Apex Highlight Picker > Uninstall。

安裝程式內含 .NET 執行階段，不用另外裝 .NET。FFmpeg 還是要裝（見「需要」）。解除安裝後，你的設定會留在使用者資料夾裡。

**Smart App Control：**目前發佈的版本還沒有簽章。開著 Smart App Control（SAC）的電腦，就算裝好了，Windows 也會拒絕執行並顯示「Application Control policy has blocked this file」。安裝程式本身也可能跳出 SmartScreen 警告（「Windows protected your PC」）。在 App 簽章之前，SAC 開著的人只能用 CLI，或是關掉 SAC（關了基本上就回不去）。詳見下面的[從原始碼執行](#running)。SAC 關著（或從來沒開過，從舊版 Windows 升級上來的電腦常是這樣）的電腦只會看到 SmartScreen 警告，按 More info > Run anyway 即可。

## 需要

- Windows 10/11（64 位元）。要從原始碼組建才需要 .NET 11 SDK
- FFmpeg：`winget install Gyan.FFmpeg`。程式會自動找 PATH 和 winget 的安裝位置，也可以把 `ffmpeg.exe`、`ffprobe.exe` 放到程式資料夾的 `ffmpeg\` 子資料夾，或用環境變數 `APEXHL_FFMPEG_DIR` 指定資料夾。只會搜尋絕對路徑（PATH 裡 `.` 這類相對路徑會跳過），winget 資料夾只找 `Gyan.FFmpeg` 和 `BtbN.FFmpeg` 兩個套件。log 會顯示找到哪一個 FFmpeg 和它的版本，比 6.0 舊會提醒。FFmpeg 會讀取每一支錄影，請保持更新（`winget upgrade Gyan.FFmpeg`）。
- Windows 內建 OCR：遊戲是什麼語言，就要裝那個語言包（設定 > 時間與語言 > 語言）。不需要付費 API。
- 有 NVIDIA 顯卡時用 NVENC 匯出，沒有就自動改用 x264。

## 從原始碼執行

<a id="running"></a>

```powershell
dotnet run --project src\ApexHighlighter.App -c Release
```

如果 Windows 跳出「Application Control policy has blocked this file」，那是 Smart App Control（SAC）在擋未簽章的程式。**能不關就不要關 SAC。**關掉後整台電腦的所有程式都失去這層保護，而且在不少 Windows 版本上要重設 Windows 才能再打開。

2026-11-27 在 SAC 開著（`VerifiedAndReputablePolicyState = 1`）的電腦上實測：

| 啟動方式 | 結果 |
|---|---|
| `ApexHighlighter.App.exe` | 被擋 |
| `dotnet ApexHighlighter.App.dll` | 一樣被擋：SAC 會檢查 `dotnet.exe` 載入的 DLL（CodeIntegrity 事件 3033／3077／3118） |
| CLI，`dotnet apexhl.dll`（`harness\verify.ps1` 也是這樣跑） | 可以用：掃描和匯出都通過 |

所以 SAC 開著時：

- 掃描和匯出先用下面的 CLI，現在就能用。
- 要用視窗介面，程式必須用受信任 CA 的憑證簽章，見 [docs/SIGNING.zh-TW.md](docs/SIGNING.zh-TW.md)（台灣可用的是 OV 憑證）。自簽憑證過不了 SAC。
- 關掉 SAC（Settings > Privacy & security > Windows Security > App & browser control > Smart App Control settings）也能用，但請當成關了就回不去。

發佈時會附上 `SHA256SUMS.txt`，讓下載的人可以核對檔案。

資安說明：[docs/SECURITY.zh-TW.md](docs/SECURITY.zh-TW.md)。

如果不用安裝程式、自己從原始碼組建，也可以讓 Windows 搜尋（開始功能表）找到 App：先組建一次 Release 版，再建立捷徑：

```powershell
dotnet build src\ApexHighlighter.App -c Release
powershell -NoProfile -File scripts\install-shortcut.ps1
```

開始功能表會多一個「Apex Highlight Picker」，但只有目前這個 Windows 使用者看得到，其他使用者要各自再跑一次；上面的安裝程式沒有這個限制。之後若改用安裝程式，會取代這個捷徑。組建位置改了就再跑一次；Smart App Control 擋 exe 時加 `-UseDotnet`，要刪掉加 `-Remove`。如果 PowerShell 說不能執行指令碼，先執行一次 `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` 允許本機指令碼。

CLI（和介面用同一套核心）：

```powershell
dotnet run --project src\ApexHighlighter.Cli -c Release -- scan "錄影.mkv"
dotnet run --project src\ApexHighlighter.Cli -c Release -- export "錄影.mkv" 12:30 13:15 "輸出.mp4" --track 0
dotnet run --project src\ApexHighlighter.Cli -c Release -- synccheck "錄影.mkv" 12:30 "輸出.mp4"
dotnet run --project src\ApexHighlighter.Cli -c Release -- ocr "錄影.mkv" 754.5
dotnet run --project src\ApexHighlighter.Cli -c Release -- export "錄影.mkv" 12:30 13:15 "直式.mp4" --format vfit
dotnet run --project src\ApexHighlighter.Cli -c Release -- thumb "錄影.mkv" 754.5 "縮圖.jpg"
dotnet run --project src\ApexHighlighter.Cli -c Release -- obswatch --port 4455
dotnet run --project src\ApexHighlighter.Cli -c Release -- batch "D:\錄影" --out "D:\精華"
```

`batch` 一次處理好幾支錄影，分成兩站：掃描（CPU，OCR）和剪片（GPU，NVENC）。一支掃完馬上去剪，下一支接著掃，兩站同時進行。某一支失敗不影響其他支。選項：`--scan-workers 1`、`--export-workers 2`、`--format`、`--no-export`。

單元測試（不需要 FFmpeg 或錄影）：

```powershell
dotnet test tests\ApexHighlighter.Tests
```

要從頭到尾驗證一次改動（組建、測試、用真實測試錄影掃描和試剪），請看 [harness/README.zh-TW.md](harness/README.zh-TW.md)。

組建後的執行檔叫 `apexhl.exe`（在 `src\ApexHighlighter.Cli\bin\<Debug或Release>\net11.0-windows10.0.19041.0\`），也可以直接執行它，例如 `apexhl scan "錄影.mkv"`。不加參數執行會顯示所有指令。`錄影.mkv` 只是範例檔名，請換成你自己的錄影路徑。

`obswatch` 的 OBS 密碼請放在環境變數 `APEXHL_OBS_PASSWORD`，不要打在指令裡（會留在命令歷史）。

## 介面

1. **匯入錄影…**，或直接把 MKV／MP4 拖進視窗。程式會量每條音軌的音量，預設選有聲音的那條。
   - 一次選或拖進**好幾支**錄影，會一支一支依序掃描。
   - **打開掃描過的錄影**（同一個檔案、沒被改過）會直接顯示上次的結果和你的微調：起訖時間、勾選、手動新增的片段。如果那次掃描之後改過精華挑選設定或關鍵字規則，記錄區會提醒；按「掃描精華」就會用新設定重掃。
2. **掃描精華**：最多列 3 段。每段有縮圖、時間、觸發原因（例如「擊倒 ×2、淘汰」）、每個提示的時間點，以及片頭往前抓了幾秒。沒把握就不列。
   - 掃描時，讀到的提示會即時以黃色刻度出現在時間軸上，點刻度就跳到那裡先看，不用等掃完。
3. 點一段：播放器會跳到起點，可以調整：
   - 直接在時間軸上拖曳選取片段兩端的白色把手
   - 起點／終點直接打時間，或用 ±0.2／±1 秒、「設為目前」（快捷鍵 I／O）
   - 「▶ 播放這段」「▶ 聽結尾」
   - 取消勾選＝跳過，「刪除」＝從清單拿掉
4. **＋ 在目前位置新增**：掃描沒抓到的，可以手動加。
5. **匯出勾選的片段**：每段一個 MP4，預設存到 `影片\Apex精華`。匯出後自動檢查：完整解碼、畫面比例、影音起點和長度。
   - 格式：橫式 16:9／直式 9:16（裁中間）／直式 9:16（完整畫面）。直式是 1080×1920，給 YouTube Shorts 用；「裁中間」會切掉擊殺列表和左右兩邊，「完整畫面」把整個畫面放中間、上下補模糊背景。
6. 輸出資料夾、格式、視窗大小和位置會記住，下次開啟沿用。

## OBS 關台自動掃描

設定 →「OBS」：勾選「OBS 停止錄影後，自動匯入並掃描這次的錄影」，填位址（預設 `localhost`）、連接埠（預設 4455）和密碼，按「測試連線」確認。

- OBS 那邊要先開：工具 → WebSocket 伺服器設定 → 勾選「啟用 WebSocket 伺服器」，密碼在「顯示連線資訊」。需要 OBS 28 以上。
- 錄影一停止，程式就自動匯入那個檔案並開始掃描；如果正在忙，會排進佇列，連續停好幾次錄影也不會漏掉。
- 本程式只聽 OBS 的通知，不會改 OBS 的任何設定。OBS 沒開時會每 10 秒重試；密碼錯誤就停止重試。
- 密碼用 Windows DPAPI 加密後存在 `settings.json`，只有你的 Windows 帳戶能解開，不會以明碼存檔。
- OBS WebSocket 連線沒有加密，位址請維持 `localhost`（OBS 在本機）。填其他電腦時，設定視窗會顯示提醒。
- OBS 傳來的路徑必須是本機磁碟上存在的影片檔（例如 `D:\Videos\...mkv`）才會開啟；網址、網路共用資料夾（`\\server\share`）和其他類型的檔案會被忽略，並記在 log 裡。

## 介面語言

右上角「⚙ 設定」→「介面語言」：English／繁體中文／日本語。選了就立即切換，不用重開；選擇存在 `%LOCALAPPDATA%\ApexHighlighter\settings.json`，下次開啟沿用。第一次開啟時跟隨 Windows 顯示語言（中文→繁體中文、日文→日本語、其他→English）。

- 會跟著切換的：按鈕、說明、狀態列、候選片段的觸發原因與細節、略過原因、匯出檢查結果、掃描報告。切換前已寫進下方記錄區的訊息不會改寫。
- 遊戲語言不用設定：每次掃描都從錄影裡的提示和 HUD 文字自動判斷，結果顯示在右上角（例如「遊戲語言：English（自動偵測）」）。提示太少判斷不出來時會寫「大概是…」。CLI 仍可用 `--lang` 強制指定。
- 所有文字集中在 `src/ApexHighlighter.Core/Loc.cs`，一個鍵對應英、繁中、日三種。要加語言就是在每列多加一欄，再在 `Languages` 加一筆。
- CLI 預設輸出繁中，可加 `--ui-lang en` 或 `--ui-lang ja`。

## 偵測方式（不寫死語言和 HUD 位置）

- 每秒取 2 張畫面、縮到 1280 寬，用 Windows OCR 讀**整個畫面**。不假設提示在哪裡，位置是掃描後從錄影量出來的。
- `keywords.json` 有英、繁中、簡中、日、德的提示字表。掃描開頭每種語言都試，提示加上一直在畫面上的 HUD 字（例如 SQUADS LEFT）命中最多的語言勝出，之後只用它。
- 英文規則是用實際錄影確認的：
  - 會算：`KNOCKED DOWN`（擊倒）、`ELIMINATED`／`COMEBACK KILL`（淘汰）、`SQUAD WIPE`（滅隊）、`YOU ARE THE NEW KILL LEADER`（成為擊殺王）
  - 必須全大寫，而且關鍵字前面只能有圖示。所以 `Team Eliminated +5 RP`、字幕、`Knockdown Shield` 不會被算進去。
  - `SQUAD ELIMINATED` 是自己被滅隊，不算。
  - `ASSIST …` 是助攻，權重 0.5，只有助攻的交戰不給。
- 同一個提示會連續出現好幾張，會合併成一次。相隔 25 秒內的事件算同一波交戰。
- 片頭：從第一個擊倒往回找音量持續偏高（槍聲）的起點，限 8–25 秒。片尾：最後一個事件後 6 秒（冠軍多 8 秒）。
- 分數：擊倒 1、淘汰 1.5、滅隊 2.5、擊殺王 1、冠軍 4、助攻 0.5。只出現一張的命中減半。分數低於 2 不給，最多取 3 段。
- 二次確認：建議片段之前，只被讀到不到 3 次的提示，會在它出現的附近用每秒 6 張重讀一次。重讀時有再看到才算分（算全分）。分數掉到 2 以下就拿掉這段（「二次確認沒過」），換下一段來檢查。差一點沒選上的交戰，如果弱提示被確認是真的，也會補上。重讀失敗就保留原本的判斷。可以用 `--no-verify` 關掉。
- 每次掃描都會存完整記錄到 `%LOCALAPPDATA%\ApexHighlighter\scans\`，裡面有每一行 OCR 命中的文字和位置，以及被略過的交戰和略過原因，方便檢查誤判。

## Minecraft 精華

在工具列把 **Game** 選成 **Minecraft**（CLI：`--game minecraft`）。Minecraft 用自己的規則檔 `keywords.minecraft.json`，Apex 的偵測完全不變。

- 會抓：死亡畫面（`你死了！`、極限模式的 `遊戲結束！`）、右上角的通知（取得進度／達成目標／完成挑戰）、突襲血條的 `突襲 - 勝利`，以及聊天欄裡**你**擊殺別人的死亡訊息（`Alex 被 <你> 殺死了`）。
- 聊天欄的擊殺要知道你的名字：請在 **Settings → Minecraft player name** 填入（CLI：`--player <名稱>`）。每種語言的死亡訊息都是被殺的人寫在最前面，所以開頭是你名字的那行是你自己死掉，不算擊殺。沒填名字時只抓死亡、通知和突襲勝利。
- 分數：死亡 2、擊殺玩家 1.5、進度 1、目標 2、挑戰 3、突襲勝利 3。完成挑戰或突襲勝利一定會列入；只有一個普通進度（1 分）不夠。
- 死亡畫面會一直停到按「重生」為止，只算前 3 秒，片段才不會塞滿死亡畫面。
- 語言：英文、繁中、簡中、日文。**還沒用實際錄影確認過**，用字是照遊戲語言檔寫的。Minecraft 的像素字比較小，抓不到的話可以把 GUI 縮放調大，或用 CLI 的 `--width 1920`。

## 匯出

- 從起點開始重新編碼，切點是逐格精準的；影音都從 0 開始。
- 原畫面不是 16:9 時，不縮放、不裁切，置中補黑邊成 16:9（例如 1920×1200 → 2134×1200）。
- 只輸出一條音軌（介面上選的那條），AAC 192k。
- 先寫成 `.part.mp4`，完成後才改名；取消或失敗會刪掉。輸出路徑不能跟原始檔相同。

## 專案結構

```
src/ApexHighlighter.Core   偵測、FFmpeg、匯出（介面和 CLI 共用）
src/ApexHighlighter.App    WPF 介面
src/ApexHighlighter.Cli    命令列：掃描、匯出、批次、OCR 測試、音畫同步檢查
tests/ApexHighlighter.Tests 單元測試（關鍵字規則、分群、二次確認、流水線、匯出參數、快取、佇列…）
harness/                   一鍵驗證腳本和 Claude 的自動修正流程（自行啟用：enable-gate.ps1）
scripts/package.ps1        打包發佈 zip、安裝程式、可選簽章、SHA256SUMS.txt
installer/                 安裝程式的 Inno Setup 腳本（開始功能表項目、解除安裝）
.github/workflows/         發佈流程（弱點檢查、測試、簽章、雜湊）
docs/                      SECURITY、SIGNING 說明、截圖
acceptance/                驗收紀錄與掃描報告
```

## 這版不做

Twitch 下載、YouTube 上傳、付費 API。

- YouTube 私人上傳需要你在 Google Cloud 建立 OAuth 用戶端並授權本程式，屬於你的帳號設定，要你自己操作後才能接上。
- Twitch 下載需要另外安裝下載工具（例如 yt-dlp）。
