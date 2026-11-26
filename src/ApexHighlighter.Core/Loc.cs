using System.Globalization;

namespace ApexHighlighter.Core;

// =====================================================================================
// Loc.cs — 介面文字的翻譯表（英文 / 繁中 / 日文）
//
// 每個 key 對應三種語言，例如 "btn.scan" → ["Find highlights", "掃描精華", "ハイライトを探す"]。
//   Loc.T(key)        取目前語言的文字
//   Loc.F(key, 參數)  取文字並填入 {0}{1}…
//   Loc.Set(code)     切換語言，並觸發 Changed 事件讓畫面更新
// 只管「本程式的介面文字」；遊戲語言是掃描時自動偵測的，跟這裡無關。
// =====================================================================================

/// <summary>
/// Interface strings in English, Traditional Chinese and Japanese. One table shared by the WPF app, the CLI and
/// the texts the core produces (fight reasons, skip reasons, export checks, scan reports).
/// </summary>
public static class Loc
{
    public sealed record Language(string Code, string NativeName);

    public static IReadOnlyList<Language> Languages { get; } =
    [
        new("en", "English"),
        new("zh-Hant", "繁體中文"),
        new("ja", "日本語"),
    ];

    private static int _index = 1;

    public static string Current => Languages[_index].Code;

    /// <summary>Raised after the language changes, so open windows can refresh.</summary>
    public static event Action? Changed;

    public static void Set(string code)
    {
        int i = IndexOf(code);
        if (i < 0 || i == _index) return;
        _index = i;
        Changed?.Invoke();
    }

    public static bool IsSupported(string? code) => code != null && IndexOf(code) >= 0;

    private static int IndexOf(string code)
    {
        for (int i = 0; i < Languages.Count; i++)
            if (string.Equals(Languages[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Best match for the Windows display language: any Chinese → 繁體中文, Japanese → 日本語, otherwise English.</summary>
    public static string FromSystem()
    {
        var c = CultureInfo.CurrentUICulture.Name;
        if (c.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
        if (c.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
        return "en";
    }

    public static string T(string key)
    {
        if (!Table.TryGetValue(key, out var row)) return key;
        var s = row[_index];
        return string.IsNullOrEmpty(s) ? row[0] : s;
    }

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>All keys (used by the completeness check).</summary>
    public static IEnumerable<KeyValuePair<string, string[]>> All => Table;

    // [English, 繁體中文, 日本語]
    private static readonly Dictionary<string, string[]> Table = new()
    {
        // ── Event kinds ──
        ["kind.Knock"] = ["Knock", "擊倒", "ダウン"],
        ["kind.Elimination"] = ["Elimination", "淘汰", "キル"],
        ["kind.SquadWipe"] = ["Squad wipe", "全隊淘汰", "部隊全滅"],
        ["kind.KillLeader"] = ["Kill leader", "成為擊殺王", "キルリーダー"],
        ["kind.Champion"] = ["Champion", "冠軍", "チャンピオン"],
        ["kind.Assist"] = ["Assist", "助攻", "アシスト"],
        ["list.sep"] = [", ", "、", "、"],

        // ── Core errors / notes ──
        ["err.ffmpegMissing"] = [
            "ffmpeg.exe / ffprobe.exe not found. Install FFmpeg (for example: winget install Gyan.FFmpeg), or put both files in an \"ffmpeg\" folder next to the app.",
            "找不到 ffmpeg.exe / ffprobe.exe。請安裝 FFmpeg（例如 winget install Gyan.FFmpeg），或把兩個 exe 放到程式資料夾下的 ffmpeg 子資料夾。",
            "ffmpeg.exe / ffprobe.exe が見つかりません。FFmpeg をインストールする（例：winget install Gyan.FFmpeg）か、アプリと同じ場所の ffmpeg フォルダーに 2 つの exe を置いてください。"],
        ["err.toolFailed"] = ["{0} failed (exit {1}):\n{2}", "{0} 失敗 (exit {1}):\n{2}", "{0} が失敗しました (exit {1}):\n{2}"],
        ["err.noVideo"] = ["No video stream found in the file.", "檔案裡找不到影片串流。", "ファイルに映像ストリームがありません。"],
        ["err.keywordsBad"] = ["keywords.json is malformed.", "keywords.json 格式錯誤。", "keywords.json の形式が正しくありません。"],
        ["err.noOcr"] = [
            "No usable OCR language. Install an English or Chinese language pack (with OCR) in Windows Settings > Time & language > Language.",
            "找不到可用的 OCR 語言。請在 Windows 設定 > 時間與語言 > 語言 安裝英文或中文語言包（含 OCR）。",
            "使用できる OCR 言語がありません。Windows の設定 > 時刻と言語 > 言語 で英語または中国語の言語パック（OCR 付き）をインストールしてください。"],
        ["err.ocrCreate"] = ["Cannot create OCR engine: {0}", "無法建立 OCR 引擎：{0}", "OCR エンジンを作成できません：{0}"],
        ["err.langMissing"] = ["Language {0} is not in keywords.json.", "keywords.json 裡沒有語言 {0}。", "keywords.json に言語 {0} がありません。"],
        ["err.noFrames"] = ["ffmpeg produced no frames: {0}", "ffmpeg 沒有輸出任何畫面：{0}", "ffmpeg からフレームが出力されませんでした：{0}"],
        ["err.endBeforeStart"] = ["The end time must be after the start time.", "結束時間必須晚於開始時間。", "終了時刻は開始時刻より後にしてください。"],
        ["err.overwriteSource"] = ["The output file cannot overwrite the original recording.", "輸出檔不能覆蓋原始錄影。", "出力ファイルで元の録画を上書きすることはできません。"],
        ["err.exportFailed"] = ["Export failed ({0}): {1}", "匯出失敗（{0}）：{1}", "書き出しに失敗しました（{0}）：{1}"],

        ["track.fmt"] = ["Track {0} ({1}ch{2})", "音軌 {0}（{1}ch{2}）", "トラック {0}（{1}ch{2}）"],
        ["track.sep"] = [", ", "，", "、"],

        ["note.ocrFallback"] = [
            "No {0} OCR language is installed; reading with the {1} engine instead.",
            "系統沒有安裝 {0} 的 OCR 語言，改用 {1} 引擎讀取。",
            "{0} の OCR 言語がインストールされていないため、{1} エンジンで読み取ります。"],
        ["note.ocrAvailable"] = [
            "Available OCR languages: {0}. The game language is detected automatically.",
            "可用的 OCR 語言：{0}；自動判斷遊戲語言。",
            "利用できる OCR 言語：{0}。ゲームの言語は自動で判定します。"],
        ["note.langUncertain"] = [
            "Not enough prompts or HUD text to tell the game language; using {0} for now ({1} hits).",
            "畫面上讀不到足夠的提示或 HUD 文字，無法確定遊戲語言；暫用 {0}（命中 {1} 次）。",
            "表示や HUD の文字が少なく、ゲームの言語を判定できません。暫定で {0} を使います（{1} 件）。"],
        ["note.langUnverified"] = [
            "The keywords for \"{0}\" have not been checked against real recordings yet. Review the results and edit keywords.json if needed.",
            "「{0}」的關鍵字還沒用實際錄影確認過，結果請多看一眼，必要時修改 keywords.json。",
            "「{0}」のキーワードはまだ実際の録画で確認していません。結果をよく確認し、必要なら keywords.json を修正してください。"],
        ["note.audioFailed"] = [
            "Audio analysis failed; using a fixed lead-in instead: {0}",
            "音量分析失敗，片頭改用固定前置秒數：{0}",
            "音量の解析に失敗したため、固定の前置き秒数を使います：{0}"],

        ["progress.scanning"] = ["Scanning {0} / {1}", "掃描中 {0} / {1}", "スキャン中 {0} / {1}"],
        ["progress.lang"] = [" (language: {0})", "（語言：{0}）", "（言語：{0}）"],
        ["progress.detecting"] = [" (detecting game language)", "（判斷遊戲語言中）", "（ゲームの言語を判定中）"],
        ["progress.merging"] = ["Analyzing audio and grouping fights…", "分析音量、合併交戰…", "音量を解析し、交戦をまとめています…"],

        ["start.audio"] = ["lead-in {0:0} s, from audio", "依音量往前抓 {0:0} 秒", "音量から {0:0} 秒前に開始"],
        ["start.fixed"] = ["fixed lead-in {0:0} s", "固定往前 {0:0} 秒", "固定で {0:0} 秒前に開始"],
        ["reject.lowScore"] = [
            "Score {0:0.0} is below the threshold {1:0.0} (only {2}; not sure it's a highlight)",
            "分數 {0:0.0} 低於門檻 {1:0.0}（只有{2}，不夠確定是精華）",
            "スコア {0:0.0} がしきい値 {1:0.0} 未満（{2} のみで、ハイライトとは言い切れません）"],
        ["reject.assistOnly"] = ["Assists only ({0}); a teammate got the kills", "只有助攻（{0}），主攻是隊友", "アシストのみ（{0}）。キルは味方によるもの"],
        ["reject.notTop"] = ["Score {0:0.0}, outside the top {1}", "分數 {0:0.0}，排在前 {1} 名之外", "スコア {0:0.0}、上位 {1} 件に入りません"],
        ["position"] = [
            "Knock / elimination prompts were mostly at {0:P0} across and {1:P0} down the screen (measured from this recording, not a preset)",
            "擊倒／淘汰提示大多在畫面水平 {0:P0}、垂直 {1:P0} 的位置（從錄影自動量到，不是預設值）",
            "ダウン／キルの表示は主に画面の横 {0:P0}、縦 {1:P0} の位置（この録画から自動計測。既定値ではありません）"],

        ["verify.decodeFail"] = ["full decode failed", "完整解碼失敗", "全体のデコードに失敗"],
        ["verify.wrongAspect"] = ["not {2} ({0}x{1})", "畫面不是 {2}（{0}x{1}）", "{2} ではありません（{0}x{1}）"],
        ["verify.noAudio"] = ["no audio", "沒有音訊", "音声がありません"],
        ["verify.startDiff"] = ["audio and video start {0:0} ms apart", "音訊與畫面起點差 {0:0} ms", "音声と映像の開始が {0:0} ms ずれています"],
        ["verify.lenDiff"] = ["audio and video lengths differ by {0:0} ms", "音訊與畫面長度差 {0:0} ms", "音声と映像の長さが {0:0} ms 違います"],
        ["verify.durMismatch"] = ["length {0:0.00} s does not match the expected {1:0.00} s", "長度 {0:0.00}s 與預期 {1:0.00}s 不符", "長さ {0:0.00} 秒が想定の {1:0.00} 秒と一致しません"],
        ["verify.ok"] = [
            "OK: {0}x{1}, {2:0.00} s, A/V start diff {3:0} ms, length diff {4:0} ms, decodes cleanly",
            "OK：{0}x{1}，長度 {2:0.00}s，影/音起點差 {3:0} ms，長度差 {4:0} ms，完整解碼無錯誤",
            "OK：{0}x{1}、長さ {2:0.00} 秒、音声/映像の開始差 {3:0} ms、長さの差 {4:0} ms、デコードエラーなし"],
        ["verify.problems"] = ["Problems: {0}", "問題：{0}", "問題：{0}"],
        ["verify.sep"] = ["; ", "；", "；"],
        ["file.highlight"] = ["highlight", "精華", "ハイライト"],

        // ── Scan report ──
        ["rep.file"] = ["File: {0}", "檔案：{0}", "ファイル：{0}"],
        ["rep.length"] = ["Length: {0} ({1})", "長度：{0}（{1}）", "長さ：{0}（{1}）"],
        ["rep.time"] = [
            "Scan time: {0:0.0} s ({1} samples, about {2:0.0}x real time)",
            "掃描耗時：{0:0.0} 秒（{1} 張取樣，約 {2:0.0} 倍速）",
            "スキャン時間：{0:0.0} 秒（{1} 枚をサンプリング、約 {2:0.0} 倍速）"],
        ["rep.lang"] = ["Game language: {0} (OCR {1}{2})", "遊戲語言：{0}（OCR {1}{2}）", "ゲームの言語：{0}（OCR {1}{2}）"],
        ["rep.unverified"] = [", keywords not yet verified", "，關鍵字未經實測", "、キーワード未検証"],
        ["rep.spect"] = ["Spectating frames skipped: {0}", "觀戰畫面略過：{0} 張", "観戦画面をスキップ：{0} 枚"],
        ["rep.note"] = ["Note: {0}", "備註：{0}", "備考：{0}"],
        ["rep.events"] = ["Prompts detected ({0} after de-duplication):", "偵測到的提示（去重後 {0} 個）：", "検出した表示（重複を除いて {0} 件）："],
        ["rep.event"] = ["  {0}  {1}  seen {2}x  \"{3}\"", "  {0}  {1}  出現 {2} 次  「{3}」", "  {0}  {1}  {2} 回表示  「{3}」"],
        ["rep.clusters"] = ["Fights ({0}, {1} suggested):", "交戰（{0} 波，建議 {1} 段）：", "交戦（{0} 回、提案 {1} 件）："],
        ["rep.suggest"] = ["Suggestion {0}", "建議 {0}", "提案 {0}"],
        ["rep.skip"] = ["Skipped", "略過", "スキップ"],
        ["rep.cluster"] = ["  [{0}] {1}–{2}  score {3:0.0}  {4}; {5}", "  [{0}] {1}–{2}  分數 {3:0.0}  {4}；{5}", "  [{0}] {1}–{2}  スコア {3:0.0}  {4}；{5}"],
        ["rep.why"] = ["         Reason: {0}", "         原因：{0}", "         理由：{0}"],

        // ── App: main window ──
        ["app.title"] = ["Apex Highlight Picker", "Apex 精華挑片", "Apex ハイライト選び"],
        ["app.font"] = ["Segoe UI, Microsoft JhengHei UI, Yu Gothic UI", "Microsoft JhengHei UI, Segoe UI", "Yu Gothic UI, Meiryo UI, Segoe UI"],
        ["btn.import"] = ["Import recording…", "匯入錄影…", "録画を読み込む…"],
        ["btn.scan"] = ["Find highlights", "掃描精華", "ハイライトを探す"],
        ["btn.cancel"] = ["Cancel", "取消", "キャンセル"],
        ["btn.settings"] = ["⚙ Settings", "⚙ 設定", "⚙ 設定"],
        ["game.auto"] = ["Game language: detected when you scan", "遊戲語言：掃描時自動偵測", "ゲームの言語：スキャン時に自動判定"],
        ["game.detected"] = ["Game language: {0} (detected)", "遊戲語言：{0}（自動偵測）", "ゲームの言語：{0}（自動判定）"],
        ["game.uncertain"] = ["Game language: probably {0} (few prompts)", "遊戲語言：大概是{0}（提示太少）", "ゲームの言語：たぶん{0}（表示が少ない）"],
        ["game.tip"] = [
            "Read from the prompts and HUD text in the recording. Nothing to set.",
            "從錄影裡的提示和 HUD 文字判斷，不用設定。",
            "録画内の表示と HUD の文字から判定します。設定は不要です。"],
        ["file.none"] = [
            "Nothing imported yet. You can drag an OBS MKV/MP4 into the window.",
            "尚未匯入。可直接把 OBS 的 MKV／MP4 拖進視窗。",
            "まだ読み込んでいません。OBS の MKV／MP4 をウィンドウにドラッグできます。"],
        ["list.title"] = ["Candidates", "候選片段", "候補クリップ"],
        ["btn.addHere"] = ["+ Add at playhead", "＋ 在目前位置新增", "＋ 現在位置に追加"],
        ["hint.initial"] = [
            "After importing, click \"Find highlights\", or find a spot in the player and add a clip yourself.",
            "匯入後按「掃描精華」，或用播放器找到位置後手動新增片段。",
            "読み込んだら「ハイライトを探す」を押すか、プレーヤーで位置を決めて手動でクリップを追加してください。"],
        ["hint.imported"] = [
            "Click \"Find highlights\" to detect fights, or find a spot in the player and click \"+ Add at playhead\".",
            "按「掃描精華」自動找交戰，或用播放器找到位置後按「＋ 在目前位置新增」。",
            "「ハイライトを探す」で交戦を自動検出するか、プレーヤーで位置を決めて「＋ 現在位置に追加」を押してください。"],
        ["hint.none"] = [
            "No confident highlights in this match (nothing padded in). You can still add clips in the player.",
            "這場沒有找到夠確定的精華（沒有硬湊）。可以用播放器手動新增。",
            "この試合では確実なハイライトが見つかりませんでした（無理に埋めていません）。プレーヤーから手動で追加できます。"],
        ["hint.found"] = [
            "Found {0}. Untick to skip; select one to fine-tune its start and end.",
            "找到 {0} 段。取消勾選＝跳過；選取後可微調起訖。",
            "{0} 件見つかりました。チェックを外すとスキップ、選択すると開始・終了を微調整できます。"],

        ["ed.start"] = ["Start", "起點", "開始"],
        ["ed.end"] = ["End", "終點", "終了"],
        ["ed.setHere"] = ["Set here", "設為目前", "現在位置"],
        ["ed.setStartTip"] = ["Set the start to the current playhead (shortcut I)", "把起點設成播放器目前位置（快捷鍵 I）", "開始を現在の再生位置にする（ショートカット I）"],
        ["ed.setEndTip"] = ["Set the end to the current playhead (shortcut O)", "把終點設成播放器目前位置（快捷鍵 O）", "終了を現在の再生位置にする（ショートカット O）"],
        ["ed.play"] = ["▶ Play clip", "▶ 播放這段", "▶ クリップを再生"],
        ["ed.tail"] = ["▶ Check ending", "▶ 聽結尾", "▶ 終わりを確認"],
        ["ed.tailTip"] = ["Play the last 3 seconds to check how it ends", "從終點前 3 秒播到終點，檢查收尾", "終了の 3 秒前から再生して締めを確認"],
        ["ed.remove"] = ["Remove", "刪除", "削除"],
        ["item.keepTip"] = ["Ticked = export, unticked = skip", "勾選＝匯出；取消勾選＝跳過", "チェック＝書き出す、外す＝スキップ"],

        ["player.play"] = ["▶ Play", "▶ 播放", "▶ 再生"],
        ["player.pause"] = ["❚❚ Pause", "❚❚ 暫停", "❚❚ 一時停止"],
        ["player.keys"] = [
            "Space play/pause   ←→ 2 s   I/O set start/end",
            "空白鍵播放／暫停　←→ 2 秒　I／O 設起訖",
            "Space 再生／一時停止　←→ 2 秒　I／O 開始・終了を設定"],
        ["player.preview"] = ["Preview", "預覽", "プレビュー"],
        ["player.unsupported"] = [
            "This format can't be previewed (scanning and export still work)",
            "這個格式無法在預覽播放器播放（仍可掃描與匯出）",
            "この形式はプレビューできません（スキャンと書き出しは使えます）"],

        ["exp.track"] = ["Audio track", "音軌", "音声トラック"],
        ["exp.outDir"] = ["Output folder", "輸出資料夾", "出力フォルダー"],
        ["exp.browse"] = ["Browse…", "選擇…", "参照…"],
        ["exp.open"] = ["Open folder", "開啟資料夾", "フォルダーを開く"],
        ["exp.button"] = ["Export ticked clips", "匯出勾選的片段", "チェックしたクリップを書き出す"],
        ["exp.native"] = ["Output {0}×{1} (original size, 16:9)", "輸出 {0}×{1}（原尺寸，16:9）", "出力 {0}×{1}（元のサイズ、16:9）"],
        ["exp.padded"] = [
            "Output {0}×{1} (the original {2}×{3} centered with black bars to make 16:9; no crop or stretch)",
            "輸出 {0}×{1}（原畫面 {2}×{3} 置中補黑邊成 16:9，不裁切不拉伸）",
            "出力 {0}×{1}（元の {2}×{3} を中央に置き、黒帯で 16:9 に。切り抜き・引き伸ばしなし）"],
        ["exp.noTracks"] = ["(no audio tracks)", "（沒有音軌）", "（音声トラックなし）"],
        ["exp.encNvenc"] = ["NVIDIA NVENC", "NVIDIA NVENC", "NVIDIA NVENC"],
        ["exp.encX264"] = ["x264 (CPU)", "x264（CPU）", "x264（CPU）"],

        ["clip.manual"] = ["Added manually", "手動新增", "手動で追加"],
        ["clip.manualDetail"] = [
            "Centered on {0}, 15 s either side — fine-tune as needed",
            "以 {0} 為中心，前後各 15 秒，請再微調",
            "{0} を中心に前後 15 秒。必要に応じて微調整してください"],
        ["clip.detail"] = ["Score {0:0.0} | {1} | {2}", "分數 {0:0.0}｜{1}｜{2}", "スコア {0:0.0}｜{1}｜{2}"],
        ["clip.auto"] = ["Auto-detected", "自動偵測", "自動検出"],
        ["clip.manualSrc"] = ["Manual", "手動", "手動"],
        ["clip.length"] = ["{0:0} s", "{0:0} 秒", "{0:0} 秒"],
        ["clip.exporting"] = ["Exporting…", "匯出中…", "書き出し中…"],

        ["st.ready"] = ["Ready", "就緒", "準備完了"],
        ["st.reading"] = ["Reading file info…", "讀取檔案資訊…", "ファイル情報を読み込み中…"],
        ["st.importFailed"] = ["Import failed", "匯入失敗", "読み込みに失敗"],
        ["st.scanning"] = ["Scanning…", "掃描中…", "スキャン中…"],
        ["st.scanDone"] = ["Scan finished: {0} candidate(s)", "掃描完成，{0} 段候選", "スキャン完了、候補 {0} 件"],
        ["st.cancelled"] = ["Cancelled", "已取消", "キャンセルしました"],
        ["st.scanFailed"] = ["Scan failed", "掃描失敗", "スキャンに失敗"],
        ["st.exporting"] = ["Exporting…", "匯出中…", "書き出し中…"],
        ["st.exportProgress"] = ["Exporting {0}/{1}: {2:P0}", "匯出 {0}/{1}：{2:P0}", "書き出し中 {0}/{1}：{2:P0}"],
        ["st.checking"] = ["Checking {0}/{1} (full decode, A/V alignment)…", "檢查 {0}/{1}（完整解碼、音畫對齊）…", "確認中 {0}/{1}（全体デコード、音声と映像のずれ）…"],
        ["st.exportDone"] = ["Export finished: {0} clips", "匯出完成：{0} 段", "書き出し完了：{0} 件"],
        ["st.exportFailed"] = ["Export failed", "匯出失敗", "書き出しに失敗"],

        ["msg.busy"] = ["Please wait for the current task to finish, or click Cancel.", "請先等目前的工作結束或按取消。", "今の処理が終わるまで待つか、キャンセルを押してください。"],
        ["msg.noneTicked"] = ["No clips are ticked.", "沒有勾選任何片段。", "チェックされたクリップがありません。"],
        ["msg.tooShort"] = ["Clip #{0} is too short, or its start is after its end.", "片段 #{0} 太短或起訖顛倒。", "クリップ #{0} が短すぎるか、開始と終了が逆です。"],
        ["msg.cantCreateDir"] = ["Cannot create the output folder: {0}", "無法建立輸出資料夾：{0}", "出力フォルダーを作成できません：{0}"],
        ["dlg.openTitle"] = ["Choose an Apex recording", "選擇一場 Apex 錄影", "Apex の録画を選択"],
        ["dlg.filter"] = [
            "Recordings (*.mkv;*.mp4;*.mov;*.flv)|*.mkv;*.mp4;*.mov;*.flv|All files|*.*",
            "錄影檔 (*.mkv;*.mp4;*.mov;*.flv)|*.mkv;*.mp4;*.mov;*.flv|所有檔案|*.*",
            "録画ファイル (*.mkv;*.mp4;*.mov;*.flv)|*.mkv;*.mp4;*.mov;*.flv|すべてのファイル|*.*"],
        ["dlg.folderTitle"] = ["Choose the output folder", "選擇輸出資料夾", "出力フォルダーを選択"],

        ["log.ffmpeg"] = ["FFmpeg: {0}", "FFmpeg：{0}", "FFmpeg：{0}"],
        ["log.ocrLangs"] = ["OCR languages (built into Windows): {0}", "OCR 語言（Windows 內建）：{0}", "OCR 言語（Windows 標準）：{0}"],
        ["log.ocrUnknown"] = ["(unavailable)", "（無法取得）", "（取得できません）"],
        ["log.keywordsFail"] = ["Cannot read keywords.json: {0}", "讀不到 keywords.json：{0}", "keywords.json を読み込めません：{0}"],
        ["log.imported"] = [
            "Imported: {0} ({1}×{2}, {3}, {4} audio tracks). The original file is only read, never modified.",
            "已匯入：{0}（{1}×{2}、{3}、音軌 {4} 條）。原始檔只讀取，不會被修改。",
            "読み込み完了：{0}（{1}×{2}、{3}、音声トラック {4} 本）。元のファイルは読み取るだけで、変更しません。"],
        ["log.importFailed"] = ["Import failed: {0}", "匯入失敗：{0}", "読み込みに失敗しました：{0}"],
        ["log.scanStart"] = [
            "Scanning ({0} frames/s, OCR width {1}px, {2} threads, language {3})…",
            "開始掃描（每秒 {0} 張、OCR 寬 {1}px、{2} 執行緒、語言 {3}）…",
            "スキャン開始（毎秒 {0} 枚、OCR 幅 {1}px、{2} スレッド、言語 {3}）…"],
        ["log.reportFail"] = ["Could not save the report: {0}", "報告存檔失敗：{0}", "レポートを保存できませんでした：{0}"],
        ["log.scanDone"] = [
            "Scan finished in {0:0.0} s ({1:0.0}x real time), language {2} (OCR {3}).",
            "掃描完成：耗時 {0:0.0} 秒（{1:0.0} 倍速），語言 {2}（OCR {3}）。",
            "スキャン完了：{0:0.0} 秒（{1:0.0} 倍速）、言語 {2}（OCR {3}）。"],
        ["log.summary"] = [
            "Read {0} prompts, grouped into {1} fights, {2} suggested.",
            "讀到 {0} 個提示，合併成 {1} 波交戰，建議 {2} 段。",
            "表示を {0} 件読み取り、{1} 回の交戦にまとめ、{2} 件を提案しました。"],
        ["log.skipped"] = ["  Skipped {0}–{1}: {2}", "  略過 {0}–{1}：{2}", "  スキップ {0}–{1}：{2}"],
        ["log.report"] = [
            "Full log (every OCR hit, for checking misdetections): {0}",
            "完整記錄（含每個 OCR 命中，方便檢查誤判）：{0}",
            "詳細ログ（誤検出の確認用に、すべての OCR ヒットを記録）：{0}"],
        ["log.scanCancelled"] = ["Scan cancelled.", "掃描已取消。", "スキャンをキャンセルしました。"],
        ["log.scanFailed"] = ["Scan failed: {0}", "掃描失敗：{0}", "スキャンに失敗しました：{0}"],
        ["log.exportStart"] = [
            "Exporting {0} clips (encoder: {1}, audio track {2})…",
            "開始匯出 {0} 段（編碼器：{1}，音軌 {2}）…",
            "{0} 件の書き出しを開始（エンコーダー：{1}、音声トラック {2}）…"],
        ["log.exportOne"] = ["#{0} → {1} ({2:0.0} s)   {3}", "#{0} → {1}（{2:0.0} 秒）　{3}", "#{0} → {1}（{2:0.0} 秒）　{3}"],
        ["log.exportDone"] = ["Export finished. Files are in {0}", "匯出完成，檔案在 {0}", "書き出し完了。ファイルは {0} にあります"],
        ["log.exportCancelled"] = ["Export cancelled; unfinished files were deleted.", "匯出已取消，未完成的檔案已刪除。", "書き出しをキャンセルしました。未完成のファイルは削除しました。"],
        ["log.exportFailed"] = ["Export failed: {0}", "匯出失敗：{0}", "書き出しに失敗しました：{0}"],
        ["log.previewFailed"] = ["Preview failed: {0}", "預覽播放失敗：{0}", "プレビューに失敗しました：{0}"],
        ["log.uiLang"] = ["Interface language: {0}", "介面語言：{0}", "表示言語：{0}"],

        // ── Settings window ──
        ["set.title"] = ["Settings", "設定", "設定"],
        ["set.uiLang"] = ["Interface language", "介面語言", "表示言語"],
        ["set.uiLangNote"] = [
            "Applies right away and is remembered next time. Only the app's own text changes; the game's language is detected from the recording.",
            "立即套用，下次開啟時沿用。只影響本程式的文字；遊戲語言會從錄影自動偵測。",
            "すぐに反映され、次回も使われます。変わるのはこのアプリの表示だけで、ゲームの言語は録画から自動で判定します。"],
        ["set.close"] = ["Close", "關閉", "閉じる"],
        ["set.obs"] = ["OBS", "OBS", "OBS"],
        ["set.obsEnable"] = [
            "When OBS stops recording, import and scan the recording automatically",
            "OBS 停止錄影後，自動匯入並掃描這次的錄影",
            "OBS の録画が止まったら、その録画を自動で読み込んでスキャンする"],
        ["set.obsHost"] = ["Address", "位址", "アドレス"],
        ["set.obsPort"] = ["Port", "連接埠", "ポート"],
        ["set.obsPassword"] = ["Password", "密碼", "パスワード"],
        ["set.obsTest"] = ["Test connection", "測試連線", "接続テスト"],
        ["set.obsNote"] = [
            "In OBS: Tools → WebSocket Server Settings → tick \"Enable WebSocket server\". The password is under \"Show Connect Info\". It is stored encrypted, and only your Windows account can read it. This app only listens to OBS and never changes its settings.",
            "在 OBS：工具 → WebSocket 伺服器設定 → 勾選「啟用 WebSocket 伺服器」，密碼在「顯示連線資訊」裡。密碼會加密存在本機，只有你的 Windows 帳戶能解開。本程式只聽 OBS 的通知，不會改 OBS 的設定。",
            "OBS で：ツール → WebSocket サーバー設定 →「WebSocket サーバーを有効にする」にチェック。パスワードは「接続情報を表示」にあります。パスワードは暗号化して保存され、あなたの Windows アカウントだけが読めます。このアプリは OBS の通知を受け取るだけで、OBS の設定は変更しません。"],
        ["obs.testing"] = ["Connecting…", "連線中…", "接続中…"],
        ["obs.ok"] = ["Connected to OBS {0}", "已連上 OBS {0}", "OBS {0} に接続しました"],
        ["obs.fail"] = ["Could not connect: {0}", "連不上：{0}", "接続できません：{0}"],
        ["obs.authFail"] = ["Wrong password", "密碼錯誤", "パスワードが違います"],
        ["obs.state.Connecting"] = ["OBS: connecting…", "OBS：連線中…", "OBS：接続中…"],
        ["obs.state.Connected"] = ["OBS: connected", "OBS：已連線", "OBS：接続済み"],
        ["obs.state.Disconnected"] = ["OBS: not connected (retrying)", "OBS：未連線（自動重試）", "OBS：未接続（再試行中）"],
        ["obs.state.AuthFailed"] = ["OBS: wrong password", "OBS：密碼錯誤", "OBS：パスワードが違います"],
        ["obs.state.Off"] = ["OBS: off", "OBS：關閉", "OBS：オフ"],
        ["log.obsRecording"] = [
            "OBS finished recording: {0}. Importing and scanning…",
            "OBS 錄影結束：{0}，自動匯入並掃描…",
            "OBS の録画が終わりました：{0}。読み込んでスキャンします…"],
        ["log.obsBusy"] = [
            "OBS finished recording {0}; it will be scanned when the current task is done.",
            "OBS 錄影結束：{0}；目前正在忙，完成後再掃描。",
            "OBS の録画が終わりました：{0}。今の作業が終わったらスキャンします。"],
        ["log.obsMissing"] = [
            "OBS reported a recording that can't be found: {0}",
            "OBS 回報的錄影檔找不到：{0}",
            "OBS が知らせた録画ファイルが見つかりません：{0}"],

        // ── Export formats, timeline, thumbnails ──
        ["exp.format"] = ["Format", "格式", "形式"],
        ["fmt.Landscape"] = ["Landscape 16:9", "橫式 16:9", "横 16:9"],
        ["fmt.VerticalCrop"] = ["Vertical 9:16 (crop middle)", "直式 9:16（裁中間）", "縦 9:16（中央を切り抜き）"],
        ["fmt.VerticalFit"] = ["Vertical 9:16 (whole frame)", "直式 9:16（完整畫面）", "縦 9:16（画面全体）"],
        ["exp.vcropInfo"] = [
            "Output {0}×{1} for Shorts: the middle of the frame, so the kill feed and screen edges are cut off",
            "輸出 {0}×{1}（Shorts 用）：只留畫面中間，擊殺列表和左右兩邊會被裁掉",
            "出力 {0}×{1}（ショート用）：画面の中央だけ。キルログと左右の端は切れます"],
        ["exp.vfitInfo"] = [
            "Output {0}×{1} for Shorts: the whole frame across the middle, with a blurred copy above and below",
            "輸出 {0}×{1}（Shorts 用）：完整畫面放在中間，上下用模糊的畫面補滿",
            "出力 {0}×{1}（ショート用）：画面全体を中央に置き、上下はぼかした画面で埋めます"],
        ["hint.scanning"] = [
            "Scanning… {0} prompts so far. The yellow ticks on the timeline show where; click one to look while the scan goes on.",
            "掃描中…目前讀到 {0} 個提示。時間軸上的黃色刻度就是位置，掃描時也可以點一下先看。",
            "スキャン中…これまでに {0} 件の表示を読み取りました。タイムラインの黄色い目盛りが位置です。スキャン中でもクリックして確認できます。"],
        ["tl.dragStart"] = ["Drag to move the start", "拖曳調整起點", "ドラッグで開始位置を調整"],
        ["tl.dragEnd"] = ["Drag to move the end", "拖曳調整終點", "ドラッグで終了位置を調整"],
    };
}
