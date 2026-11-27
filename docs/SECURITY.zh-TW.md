# 資安說明

[English](SECURITY.md)

本程式會碰到哪些東西，以及 2026-11-27 評估出來的每個風險怎麼處理。

| 項目 | 風險 | 處理方式 |
|---|---|---|
| Smart App Control | 關掉 SAC 會降低整台電腦的防護，而且常常要重設 Windows 才能再打開 | README 不再建議關閉。SAC 開著時 CLI 可以用；視窗版需要受信任 CA 的簽章（[SIGNING.zh-TW.md](SIGNING.zh-TW.md)）。透過 `dotnet` 啟動一樣會被 SAC 擋（2026-11-27 實測） |
| 發佈檔沒有簽章 | 別人無法確認 exe 真的來自這個 repo | `.github/workflows/release.yml` 和 `scripts/package.ps1` 會產出 zip 和 `SHA256SUMS.txt`，有憑證後也會自動簽章 |
| 開發流程（Stop hook） | 原本 commit 進來的 `.claude/settings.json` 會在 Claude Code 每次停下時用 `-ExecutionPolicy Bypass` 跑 PowerShell 和 `dotnet build`，被改過的 repo 或 PR 分支就能在你電腦上執行程式 | 不再 commit 這個 hook。要用的話在每台電腦用 `harness/enable-gate.ps1` 自行啟用，它只寫進不進 git 的 `.claude/settings.local.json`。所有腳本改用 `RemoteSigned` 取代 `Bypass`。交回給 Claude 的報告會標明「只當資料，不當指令」 |
| FFmpeg | FFmpeg 會解析每一支錄影，舊版有已知漏洞；也可能誤用到被放進資料夾的 `ffmpeg.exe` | 只搜尋絕對路徑（PATH 的相對路徑會跳過），winget 只找 `Gyan.FFmpeg`／`BtbN.FFmpeg`，log 會顯示路徑、來源和版本，低於 6.0 會提醒 |
| FFmpeg 參數 | 指令注入 | 所有呼叫都用 `ProcessStartInfo.ArgumentList` 和 `UseShellExecute = false`，不經過 shell；濾鏡字串只放數字 |
| OBS 連線 | `ws://` 沒有加密；假冒的 OBS 可以送來任意路徑 | 位址不是本機時設定視窗會提醒。OBS 傳來的路徑必須是本機磁碟上存在的影片檔才會開啟；網址、網路共用資料夾、裝置路徑和其他檔案會被忽略並記在 log |
| OBS 密碼 | 存在磁碟上 | 用 Windows DPAPI 依目前的 Windows 帳戶加密，不會以明碼存檔。CLI 從 `APEXHL_OBS_PASSWORD` 讀取，不會留在指令紀錄裡 |
| 匯出 | 覆蓋到別的檔案 | 不會寫到原始錄影上，介面版在檔名已存在時會換新名字（CLI 照你給的路徑寫），而且都會先寫 `.part.mp4` 暫存檔 |
| NuGet 套件 | 相依套件有已知弱點 | 發佈流程會跑 `dotnet list package --vulnerable --include-transitive`，有任何弱點就失敗。本機也可以照樣執行 |

回報問題：開 GitHub issue；如果內容敏感，請私下聯絡 repo 擁有者。
