# Code signing

[繁體中文](SIGNING.zh-TW.md)

Signing is the only way to run the window app (`ApexHighlighter.App`) with Smart App Control on, on your own PC as well as on other people's. Tested 2026-11-27: with SAC on, both `ApexHighlighter.App.exe` and `dotnet ApexHighlighter.App.dll` are blocked, because SAC also checks DLLs loaded by `dotnet.exe`. The CLI (`dotnet apexhl.dll`) does run, so it covers scanning and exporting until the app is signed.

Getting a certificate needs your own identity documents and payment, so only you can do it. Everything after that is ready in this repo.

## Which option fits

| Option | Cost | Who can get it | How it signs here |
|---|---|---|---|
| Azure Artifact Signing (formerly Trusted Signing) | about US$9.99/month | Organizations in the USA, Canada, EU, UK. Individuals in the USA and Canada only (as of 2026-11). **Not yet available in Taiwan.** | CI: `.github/workflows/release.yml` |
| OV code signing certificate from a CA (for example DigiCert, Sectigo, SSL.com, Certum) | about US$150–300/year | Individuals and companies in most countries, including Taiwan | Locally with the USB token: `scripts/package.ps1 -CertThumbprint ...`, or the CA's cloud signing service |
| SignPath Foundation | free | Open-source projects that meet their rules (public repo, OSI license) | Their managed pipeline (see signpath.org) |
| Self-signed certificate | free | anyone | **Does not help**: SAC and other PCs do not trust it |

Source: Microsoft, "Code signing options for Windows app developers" (learn.microsoft.com/windows/apps/package-and-deploy/code-signing-options), checked 2026-11-27. Availability changes, so check it again before paying.

Notes:
- A valid signature from a trusted CA is what Smart App Control looks for. SmartScreen may still warn on the first downloads of a new publisher until reputation builds; that is expected and goes away with consistent signing.
- Since 2023 the private key of an OV certificate must live on a hardware token or cloud HSM, so it cannot be stored as a GitHub secret.

## Option A: Azure Artifact Signing in CI

1. In the Azure portal, create an Artifact Signing account, finish identity validation, and create a certificate profile (Public Trust).
2. Create an app registration (or user-assigned managed identity) with a federated credential for this GitHub repo, and give it the "Artifact Signing Certificate Profile Signer" role (older name: "Trusted Signing Certificate Profile Signer") on the account. This uses OpenID Connect, so no password or client secret is stored in GitHub.
3. In GitHub: Settings > Secrets and variables > Actions:
   - Secrets: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
   - Variables: `SIGNING_MODE` = `artifact-signing`, `ARTIFACT_SIGNING_ENDPOINT` (for example `https://eus.codesigning.azure.net/`), `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE`
4. Push a tag such as `v0.3.0`. The workflow tests, publishes, signs only this project's own `.exe`/`.dll` files, checks every signature, zips, writes `SHA256SUMS.txt` and creates the GitHub Release.

The workflow uses `azure/trusted-signing-action@v0`. If Microsoft has renamed it to match "Artifact Signing", update that one line.

## Option B: OV certificate on a USB token (works in Taiwan)

1. Buy an OV code signing certificate and complete the CA's validation. The CA ships a USB token, or gives you access to its cloud signing service.
2. Install the Windows SDK (for `signtool.exe`) and the token's driver, plug in the token, and find the certificate thumbprint: `Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert`.
3. Build, sign, zip and hash:
   ```powershell
   powershell -NoProfile -ExecutionPolicy RemoteSigned -File scripts\package.ps1 -Version 0.3.0 -CertThumbprint <thumbprint>
   ```
4. Upload `out\release\*.zip` and `out\release\SHA256SUMS.txt` to a GitHub Release.

If the CA offers cloud signing instead of a token, follow its signtool or GitHub Action instructions and sign `out\publish` between `package.ps1 -PublishOnly` and `package.ps1 -PackOnly`.

## Checking a download (for users)

```powershell
Get-FileHash .\ApexHighlighter-0.3.0-app.zip -Algorithm SHA256
```

The hash must match the line in `SHA256SUMS.txt` from the same release. For a signed build, right-click `ApexHighlighter.App.exe` > Properties > Digital Signatures should show the publisher.
