# 検証ハーネス（loop / graph / harness）

[繁體中文](README.zh-TW.md) | [English](README.md) | **日本語**

Claude や開発者が変更後に一連の検証を実行する仕組みです。ビルド、単体テスト、実録画のスキャン、試し書き出し、レポート生成を行います。エージェントの loop・graph・harness の考え方を参考にしています。

| 層 | ファイル | 役割 |
|---|---|---|
| harness | `verify.ps1` | 全検証を実行し、`out/report.md` と `out/report.json` を生成 |
| graph | `verify.ps1` 内 | 依存順に実行。独立した工程は並列（`-MaxParallel`、既定3） |
| loop | `stop-gate.ps1` + `../.claude/settings.json` | Claude の停止前に検証。失敗したときは最大5回まで修正を続ける |

```
build ──┬── unit-tests
        ├── scan:A-no-kills
        └── scan:B-squad-wipe ── export:B-squad-wipe
```

## 初回設定

1. `golden.example.json` を `golden.local.json` にコピーします。後者はコミットしません。
2. `file` に録画サンプルのパスを設定するか、環境変数 `APEXHL_SAMPLES` に保存フォルダーを指定します。
3. PowerShell で `harness\verify.ps1` を1回手動で実行してください。実行例は[英語版](README.md)にあります。

`golden.local.json` がなければスキャン・書き出しを、テストプロジェクトがなければ単体テストをスキップします。レポートにはスキップを明記し、失敗には数えません。

## 正解データの項目

| 項目 | 意味 |
|---|---|
| `minCandidates` / `maxCandidates` | 候補クリップ数の範囲。上限0は誤検出の防止に使います |
| `mustCover` | いずれかの候補に含めるべき秒数（見逃しの検査） |
| `mustNotCover` | すべての候補から除外すべき秒数（誤検出の検査） |
| `language` | 検出したゲーム言語 |
| `maxClipSeconds`、`maxScanSeconds` | クリップ時間とスキャン時間の上限 |
| `export` | 最初の候補を書き出し、`apexhl export` の検証（長さと音画開始位置）も実行 |

## 停止前の検証ループ

このリポジトリで Claude Code が処理を終えようとすると `stop-gate.ps1` が実行されます。

- `src/` と `tests/` に変更がないか、同じ変更が検証済みなら終了できます。
- それ以外では `verify.ps1` を実行します。成功なら終了、失敗ならレポートを Claude に返して修正を促します。
- 連続5回失敗したら停止し、人による確認を求めます。

`APEXHL_GATE=off` でゲートを無効にし、`APEXHL_GATE=quick` でビルドと単体テストだけを実行し、`APEXHL_GATE_ROUNDS=N` で上限を変更します。`/loop-fix <task>` でこのループを使うタスクを開始できます。

## 注意点

- CLI は `dotnet apexhl.dll` として実行できます。
- 作成時のクラウド環境に PowerShell がなく、スクリプトは Windows 実機未検証です。初回実行で修正が必要な場合があります。
