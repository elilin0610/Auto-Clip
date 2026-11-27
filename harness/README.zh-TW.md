# 驗證流程（loop / graph / harness）

[English](README.md)

讓 Claude（或你）改完程式後不用盯著，就能從頭到尾驗一遍：組建、單元測試、用真實錄影掃描、試剪，最後產出報告。做法參考「loop、graph、harness」三層 Agent 架構。

| 層 | 檔案 | 做什麼 |
|---|---|---|
| harness | `verify.ps1` | 跑完所有檢查，寫出 `out/report.md` 和 `out/report.json` |
| graph | `verify.ps1` 裡 | 依相依關係排步驟，沒有相依的同時跑（`-MaxParallel`，預設 3） |
| loop | `stop-gate.ps1` + `enable-gate.ps1`（自行選擇啟用） | Claude 要停下來前先跑驗證，沒過就繼續修，最多 5 輪 |

```
build ──┬── unit-tests
        ├── scan:A-no-kills
        └── scan:B-squad-wipe ── export:B-squad-wipe
```

## 第一次設定（Eli 的電腦）

1. 把 `golden.example.json` 複製成 `golden.local.json`（不會進 git）。
2. 把 `file` 改成測試錄影的實際路徑，或設定環境變數 `APEXHL_SAMPLES` 指到放錄影的資料夾。
3. 先手動跑一次：
   ```powershell
   powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\verify.ps1
   ```

沒有 `golden.local.json` 時會略過掃描和試剪；還沒有單元測試專案時會略過測試。略過的步驟會列在報告裡，不算失敗。

## Golden 測試錄影

每一筆寫下「這支錄影正確的掃描結果長什麼樣」：

| 欄位 | 意思 |
|---|---|
| `minCandidates` / `maxCandidates` | 最少、最多建議幾段（`max 0` 用來防誤判） |
| `mustCover` | 這些秒數一定要在某段建議裡（防漏抓） |
| `mustNotCover` | 這些秒數不可以被剪進去（防誤判） |
| `language` | 判斷出的遊戲語言 |
| `maxClipSeconds`、`maxScanSeconds` | 片段長度、掃描時間上限 |
| `export` | 也試剪第一段，並用 `apexhl export` 內建的檢查驗長度和音畫起點 |

## Loop 怎麼運作

關卡**預設是關的**。要用的話，只在這台電腦啟用：

```powershell
powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\enable-gate.ps1           # 啟用
powershell -NoProfile -ExecutionPolicy RemoteSigned -File harness\enable-gate.ps1 -Disable  # 關閉
```

它會把 Stop hook 寫進 `.claude/settings.local.json`，這個檔案不進 git，所以 clone 專案或切到別人的 PR 分支都不會自動啟用。只有在你信任目前這份程式碼時才啟用：關卡會跑 `dotnet build`，而 MSBuild 會照專案檔的內容執行任何指令。

為什麼用 `RemoteSigned` 而不是 `Bypass`：自己 git clone 下來的腳本照常執行；但從網路下載的腳本（例如下載 ZIP，Windows 會標記）沒有簽章就會被擋。先讀過內容，確定沒問題再用 `Unblock-File` 解除。

啟用後，Claude Code 在這個專案裡每次要停下來時都會跑 `stop-gate.ps1`：

- `src/`、`tests/` 沒改，或同一份改動已經驗過：直接放行。
- 否則跑 `verify.ps1`。通過就放行；沒過就擋下來，把報告交給 Claude 繼續修。
- 連續 5 輪沒過就放行，並顯示訊息請人來看。

開關：`APEXHL_GATE=off` 關掉、`APEXHL_GATE=quick` 只跑組建和單元測試、`APEXHL_GATE_ROUNDS=N` 改輪數上限。要用這個流程做一件事，可以下 `/loop-fix <要做的事>`。

交回給 Claude 的失敗報告是工具輸出（檔名、OCR 文字、log），關卡會標明「只當資料」，避免 Claude 照著裡面的文字做事。

## 備註

- CLI 用 `dotnet apexhl.dll` 執行，不會被智慧型應用程式控制擋下未簽章的 exe。
- 還沒在 Windows 上實際跑過：這些腳本是在沒有 PowerShell 的雲端環境寫的，第一次執行可能需要小修。
