# Apex 精華挑片（第一版：本機、橫式）

關台後把 Apex 錄影丟進來，程式會找出最多 3 段值得發 YouTube 的交戰。你可以播放、微調起訖、跳過不要的，再匯出成 16:9 MP4。原始錄影只讀取，不會修改。

## 需要

- Windows 10/11、.NET 11 SDK
- FFmpeg：`winget install Gyan.FFmpeg`。程式會自動找 PATH 和 winget 的安裝位置，也可以把 `ffmpeg.exe`、`ffprobe.exe` 放到程式資料夾的 `ffmpeg\` 子資料夾。
- Windows 內建 OCR：遊戲是什麼語言，就要裝那個語言包（設定 > 時間與語言 > 語言）。不需要付費 API。
- 有 NVIDIA 顯卡時用 NVENC 匯出，沒有就自動改用 x264。

## 執行

```powershell
dotnet run --project src\ApexHighlighter.App -c Release
```

如果 Windows 跳出「Application Control policy has blocked this file」，那是 Smart App Control 在擋未簽章的新程式。可以：

- 到「Windows 安全性 > 應用程式與瀏覽器控制 > Smart App Control」關掉，或
- 先用 CLI。

CLI（和介面用同一套核心）：

```powershell
dotnet run --project src\ApexHighlighter.Cli -c Release -- scan "錄影.mkv"
dotnet run --project src\ApexHighlighter.Cli -c Release -- export "錄影.mkv" 12:30 13:15 "輸出.mp4" --track 0
dotnet run --project src\ApexHighlighter.Cli -c Release -- synccheck "錄影.mkv" 12:30 "輸出.mp4"
dotnet run --project src\ApexHighlighter.Cli -c Release -- ocr "錄影.mkv" 754.5
dotnet run --project src\ApexHighlighter.Cli -c Release -- export "錄影.mkv" 12:30 13:15 "直式.mp4" --format vfit
dotnet run --project src\ApexHighlighter.Cli -c Release -- thumb "錄影.mkv" 754.5 "縮圖.jpg"
dotnet run --project src\ApexHighlighter.Cli -c Release -- obswatch --port 4455
```

組建後的執行檔叫 `apexhl.exe`（在 `src\ApexHighlighter.Cli\bin\<Debug或Release>\net11.0-windows10.0.19041.0\`），也可以直接執行它，例如 `apexhl scan "錄影.mkv"`。不加參數執行會顯示所有指令。`錄影.mkv` 只是範例檔名，請換成你自己的錄影路徑。

`obswatch` 的 OBS 密碼請放在環境變數 `APEXHL_OBS_PASSWORD`，不要打在指令裡（會留在命令歷史）。

## 介面

1. **匯入錄影…**，或直接把 MKV／MP4 拖進視窗。程式會量每條音軌的音量，預設選有聲音的那條。
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
- 錄影一停止，程式就自動匯入那個檔案並開始掃描；如果正在掃描或匯出，會等目前的工作做完。
- 本程式只聽 OBS 的通知，不會改 OBS 的任何設定。OBS 沒開時會每 10 秒重試；密碼錯誤就停止重試。
- 密碼用 Windows DPAPI 加密後存在 `settings.json`，只有你的 Windows 帳戶能解開，不會以明碼存檔。

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
- 每次掃描都會存完整記錄到 `%LOCALAPPDATA%\ApexHighlighter\scans\`，裡面有每一行 OCR 命中的文字和位置，以及被略過的交戰和略過原因，方便檢查誤判。

## 匯出

- 從起點開始重新編碼，切點是逐格精準的；影音都從 0 開始。
- 原畫面不是 16:9 時，不縮放、不裁切，置中補黑邊成 16:9（例如 1920×1200 → 2134×1200）。
- 只輸出一條音軌（介面上選的那條），AAC 192k。
- 先寫成 `.part.mp4`，完成後才改名；取消或失敗會刪掉。輸出路徑不能跟原始檔相同。

## 專案結構

```
src/ApexHighlighter.Core   偵測、FFmpeg、匯出（介面和 CLI 共用）
src/ApexHighlighter.App    WPF 介面
src/ApexHighlighter.Cli    命令列：掃描、匯出、OCR 測試、音畫同步檢查
acceptance/                驗收紀錄與掃描報告
```

## 這版不做

Twitch 下載、YouTube 上傳、付費 API。

- YouTube 私人上傳需要你在 Google Cloud 建立 OAuth 用戶端並授權本程式，屬於你的帳號設定，要你自己操作後才能接上。
- Twitch 下載需要另外安裝下載工具（例如 yt-dlp）。
