# 程式碼簽章

[English](SIGNING.md)

要在 Smart App Control 開著的情況下使用視窗版（`ApexHighlighter.App`），不論是自己的電腦還是別人的電腦，簽章都是唯一的方法。2026-11-27 實測：SAC 開著時，`ApexHighlighter.App.exe` 和 `dotnet ApexHighlighter.App.dll` 都會被擋，因為 SAC 也會檢查 `dotnet.exe` 載入的 DLL。CLI（`dotnet apexhl.dll`）可以執行，所以在簽章之前，掃描和匯出可以先用 CLI。

申請憑證需要你本人的身分文件和付款，只能由你操作。之後的步驟這個 repo 都已經準備好了。

## 怎麼選

| 方式 | 費用 | 誰能申請 | 在這裡怎麼簽 |
|---|---|---|---|
| Azure Artifact Signing（舊名 Trusted Signing） | 約每月 US$9.99 | 組織：美國、加拿大、歐盟、英國；個人：只有美國、加拿大（2026-11 時）。**台灣目前還不能用。** | CI：`.github/workflows/release.yml` |
| 向憑證商買 OV 程式碼簽章憑證（例如 DigiCert、Sectigo、SSL.com、Certum） | 約每年 US$150–300 | 大多數國家的個人和公司，包含台灣 | 在本機插 USB token 簽：`scripts/package.ps1 -CertThumbprint ...`，或用憑證商的雲端簽章服務 |
| SignPath Foundation | 免費 | 符合條件的開源專案（公開 repo、OSI 授權） | 用他們代管的流程（見 signpath.org） |
| 自簽憑證 | 免費 | 任何人 | **沒有用**：SAC 和其他電腦都不信任 |

資料來源：微軟〈Code signing options for Windows app developers〉（learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options），2026-11-27 查詢。開放的國家會變動，付款前請再確認一次。

注意：
- Smart App Control 看的是「受信任 CA 簽發的有效簽章」。新的發行者剛開始被下載時，SmartScreen 可能還是會警告，持續用同一個身分簽章，累積信譽後就會消失。
- 從 2023 年起，OV 憑證的私鑰必須放在硬體 token 或雲端 HSM 裡，所以不能存成 GitHub secret。

## 方式 A：在 CI 用 Azure Artifact Signing

1. 在 Azure 入口網站建立 Artifact Signing 帳戶，完成身分驗證，建立憑證設定檔（Public Trust）。
2. 建立 app registration（或使用者指派的受控識別），加上這個 GitHub repo 的 federated credential，並在帳戶上給它「Artifact Signing Certificate Profile Signer」角色（舊名「Trusted Signing Certificate Profile Signer」）。這是 OpenID Connect，GitHub 裡不存任何密碼或 client secret。
3. 在 GitHub：Settings > Secrets and variables > Actions：
   - Secrets：`AZURE_CLIENT_ID`、`AZURE_TENANT_ID`、`AZURE_SUBSCRIPTION_ID`
   - Variables：`SIGNING_MODE` = `artifact-signing`、`ARTIFACT_SIGNING_ENDPOINT`（例如 `https://eus.codesigning.azure.net/`）、`ARTIFACT_SIGNING_ACCOUNT`、`ARTIFACT_SIGNING_PROFILE`
4. 推一個標籤，例如 `v0.3.0`。流程會跑測試、publish、只簽本專案自己的 `.exe`／`.dll`、逐一檢查簽章、壓縮、寫出 `SHA256SUMS.txt`，再建立 GitHub Release。

流程裡用的是 `azure/trusted-signing-action@v0`。如果微軟把它改名成 Artifact Signing，改那一行就好。

## 方式 B：OV 憑證 + USB token（台灣可用）

1. 購買 OV 程式碼簽章憑證，完成憑證商的驗證。憑證商會寄 USB token，或給你雲端簽章服務的帳號。
2. 安裝 Windows SDK（裡面有 `signtool.exe`）和 token 的驅動程式，插上 token，查憑證指紋：`Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert`。
3. 組建、簽章、壓縮、算雜湊：
   ```powershell
   powershell -NoProfile -ExecutionPolicy RemoteSigned -File scripts\package.ps1 -Version 0.3.0 -CertThumbprint <指紋>
   ```
4. 把 `out\release\*.zip` 和 `out\release\SHA256SUMS.txt` 上傳到 GitHub Release。

如果憑證商提供的是雲端簽章而不是 token，就照它的 signtool 或 GitHub Action 說明，在 `package.ps1 -PublishOnly` 和 `package.ps1 -PackOnly` 之間簽 `out\publish`。

## 核對下載檔（給使用者）

```powershell
Get-FileHash .\ApexHighlighter-0.3.0-app.zip -Algorithm SHA256
```

結果要和同一個 Release 的 `SHA256SUMS.txt` 裡那一行相同。有簽章的版本，在 `ApexHighlighter.App.exe` 按右鍵 > 內容 > 數位簽章，應該看得到發行者。
