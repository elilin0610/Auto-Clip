# ApexHighlighter（初版：ローカル処理・横長動画）

[繁體中文](README.zh-TW.md) | [English](README.md) | **日本語**

配信後に Apex の録画を読み込むと、YouTube 向けの交戦シーンを最大3件提案します。プレビュー、開始・終了時刻の調整、不要な候補の除外、16:9 MP4 の書き出しができます。元の録画は読み取り専用で変更しません。

## 動作要件

- Windows 10/11、.NET 11 SDK。
- FFmpeg：`winget install Gyan.FFmpeg`。PATH と winget のインストール先を自動で検索します。`ffmpeg.exe` と `ffprobe.exe` をアプリフォルダーの `ffmpeg\` サブフォルダーに配置する方法もあります。
- Windows OCR：ゲームと同じ言語パックを設定の［時刻と言語］→［言語］からインストールします。有料 API は不要です。
- NVIDIA GPU があれば NVENC、なければ x264 で書き出します。

## 起動

```powershell
dotnet run --project src\ApexHighlighter.App -c Release
```

Windows に「Application Control policy has blocked this file」と出た場合は、Smart App Control が署名のないプログラムを止めています。保護を維持する場合は CLI を推奨します。Smart App Control の管理場所は［Windows セキュリティ］→［アプリとブラウザー コントロール］→［Smart App Control］です。

CLI（GUI と同じ処理を使用）：

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

`batch` は複数の録画を、スキャン（CPU、OCR）と書き出し（GPU、NVENC）の2段階で並行処理します。スキャンが終わった録画から書き出しに回し、同時に次の録画をスキャンします。1本が失敗しても残りは処理を続けます。オプション：`--scan-workers 1`、`--export-workers 2`、`--format`、`--no-export`。

単体テスト（FFmpeg や録画は不要）：

```powershell
dotnet test tests\ApexHighlighter.Tests
```

ビルドから実録画のスキャン、試し書き出しまで検証するには、[検証ハーネス](harness/README.ja.md)を参照してください。

ビルド後の実行ファイルは `apexhl.exe` です（`src\ApexHighlighter.Cli\bin\<Debug or Release>\net11.0-windows10.0.19041.0\`）。`apexhl scan "recording.mkv"` のように直接実行できます。引数を付けないと全コマンドを表示します。`recording.mkv` は例なので、自分の録画のパスを指定してください。

`obswatch` 用の OBS パスワードは環境変数 `APEXHL_OBS_PASSWORD` に設定し、コマンド履歴に残る引数には直接書かないでください。

## 画面操作

1. 録画をインポートするか、MKV／MP4 をウィンドウにドロップします。各トラックの音量を調べ、既定では音のあるトラックを選びます。
2. スキャンは最大3件を提案します。サムネイル、時刻、検出理由（例：ノック2回とキル）、各通知の時刻、開始前に追加した秒数を表示し、確信の低い候補は出しません。
   - スキャン中も検出した通知が黄色い目印で表示され、クリックすると完了を待たずにその時刻へ移動できます。
3. クリップを選ぶと開始位置に移動します。
   - タイムラインの選択範囲の両端にある白いハンドルをドラッグできます。
   - 開始・終了時刻を入力するか、±0.2／±1 秒または現在位置への設定（I／O）を使えます。
   - 選択区間の再生や、末尾音声の確認ができます。
   - チェックを外すとスキップし、削除すると一覧から外れます。
4. 検出されなかった区間は現在位置から手動で追加できます。
5. チェックした各クリップを個別の MP4 として、既定では `影片\Apex精華` に出力します。完全デコード、画面比率、映像・音声の開始位置と長さを自動確認します。
   - 横16:9、縦9:16（中央切り抜き）、縦9:16（全画面）に対応します。縦は YouTube Shorts 用の1080×1920です。中央切り抜きはキルフィードを含む両側を切り、全画面は全体を中央に配置して上下をぼかし背景で補います。
6. 出力先、形式、ウィンドウの大きさと位置は次回も引き継がれます。

## OBS 録画停止時の自動スキャン

設定 → OBS で、録画停止後に自動でインポート・スキャンする項目をオンにします。アドレス（既定 `localhost`）、ポート（4455）、パスワードを入力し、接続テストします。

- OBS 28 以降の［ツール］→［WebSocket サーバー設定］でサーバーを有効にし、接続情報を表示してパスワードを確認します。
- 停止時にその録画を自動で取り込み、処理を開始します。処理中なら録画をキューに追加するため、複数の録画が続けて停止しても取りこぼしません。
- OBS の通知のみ受信し、OBS の設定は変更しません。OBS が起動していないときは10秒間隔で再試行し、パスワードが誤っている場合は再試行を停止します。
- パスワードは Windows DPAPI で暗号化し、`settings.json` に保存します。復号できるのは自分の Windows アカウントだけで、平文では保存しません。

## 表示言語

右上の歯車から設定 → 表示言語で English／繁體中文／日本語を選択できます。再起動なしで反映し、`%LOCALAPPDATA%\ApexHighlighter\settings.json` に保存します。初回は Windows の表示言語に合わせます（中国語→繁体字、日本語→日本語、それ以外→英語）。

- ボタン、説明、状態、候補の理由・詳細、除外理由、書き出し結果、スキャンレポートが切り替わります。ログ欄にすでにあるメッセージは変わりません。
- ゲーム内言語は録画の通知と HUD から毎回推定し、右上に表示します。情報が乏しい場合は暫定結果になります。CLI では `--lang` で指定できます。
- 文言は `src/ApexHighlighter.Core/Loc.cs` にあり、各キーに英語、繁体字中国語、日本語を対応させています。言語追加は各行への列追加と `Languages` への項目追加で行います。
- CLI は既定で繁体字中国語です。`--ui-lang en` または `--ui-lang ja` で変更できます。

## 検出方法（言語・HUD の位置を固定しない）

- 毎秒2フレームを幅1280に縮小し、Windows OCR で画面全体を読みます。通知位置は録画から測定します。
- `keywords.json` には英語、繁体字中国語、簡体字中国語、日本語、ドイツ語の通知語があります。冒頭で各言語を試し、通知と常時 HUD（例：SQUADS LEFT）の一致数が最も多い言語に絞ります。
- 英語のルールは実際の録画で確認済みです。
  - `KNOCKED DOWN`（ノック）、`ELIMINATED`／`COMEBACK KILL`（キル）、`SQUAD WIPE`（敵部隊全滅）、`YOU ARE THE NEW KILL LEADER`（キルリーダー）を数えます。
  - 全大文字で、語の前にはアイコンのみ許容します。`Team Eliminated +5 RP`、字幕、`Knockdown Shield` は数えません。
  - `SQUAD ELIMINATED` は自分の部隊の全滅なので対象外です。
  - `ASSIST …` の重みは0.5です。アシストだけの交戦は提案しません。
- 複数フレームに続く同じ通知は1件に統合し、25秒以内のイベントを同じ交戦とみなします。
- 最初のノックから8〜25秒さかのぼり、大きい音（発砲音）が続いた開始点を探します。終了は最後のイベントから6秒後で、チャンピオン時はさらに8秒延長します。
- 点数：ノック1、キル1.5、部隊全滅2.5、キルリーダー1、チャンピオン4、アシスト0.5。1フレームだけの検出は半分、2未満は候補外で、最大3件です。
- 再確認：提案前に、3フレーム未満で検出された通知の付近を毎秒6フレームで再読します。再検出した通知だけを満点で数え、2点未満になった候補は除外して次を確認します。しきい値に僅かに届かなかった候補も、弱い通知が確認できれば採用します。フレームの再取得に失敗した場合は最初の判断を維持します。`--no-verify` で無効にできます。
- 完全なスキャン記録は `%LOCALAPPDATA%\ApexHighlighter\scans\` に保存し、OCR の文字と座標、候補外の交戦と理由を確認できます。

## 書き出し

- 開始フレームから再エンコードし、映像と音声を両方とも0から開始します。
- 元が16:9でなければ、横長出力時は拡大縮小も切り抜きもせず、中央に配置して黒帯で補います（例：1920×1200 → 2134×1200）。
- 選択した音声トラック1本を AAC 192k で出力します。
- まず `.part.mp4` を作成し、成功時に名前を変更します。中断または失敗時は一時ファイルを削除します。元ファイルと同じ出力先は使えません。

## 構成

```
src/ApexHighlighter.Core   検出、FFmpeg、書き出し（GUI／CLI 共通）
src/ApexHighlighter.App    WPF GUI
src/ApexHighlighter.Cli    CLI：スキャン、書き出し、一括処理、OCR、音画同期の確認
tests/ApexHighlighter.Tests 単体テスト（キーワード、グループ化、再確認、パイプライン、出力引数、キャッシュ、キューなど）
harness/                   一括検証スクリプトと Claude の修正ループ
acceptance/                受け入れ記録とスキャンレポート
```

## 今回の対象外

Twitch ダウンロード、YouTube アップロード、有料 API。

- YouTube への非公開アップロードを組み込むには、ご自身の Google Cloud で OAuth クライアントを作成し、アプリを認可する必要があります。
- Twitch のダウンロードには yt-dlp など別のツールが必要です。

[受け入れ記録](acceptance/%E9%A9%97%E6%94%B6%E7%B4%80%E9%8C%84.ja.md) | [検証ハーネス](harness/README.ja.md)
