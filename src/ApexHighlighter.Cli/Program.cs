using System.Globalization;
using ApexHighlighter.Core;
using Spectre.Console;

// =====================================================================================
// Program.cs — 命令列版（apexhl.exe）
//
// 跟 App 共用同一個 Core，只是外殼換成終端機。用 Spectre.Console 畫進度條、表格、顏色。
//   apexhl scan / export / batch / thumb / obswatch / ocr / synccheck
// 結構：上面是「依指令分派」，中間每個指令一個函式，最下面是共用小工具。
//
// Spectre.Console 的文字可以帶標記，例如 "[red]錯誤[/]" 會印成紅字。
// 所以檔名這類「外來文字」一定要先 Markup.Escape，不然檔名裡的 [ ] 會被當成標記而出錯。
// 結束代碼：0 成功、1 用法錯誤、2 輸入錯誤、3 匯出檢查沒過、130 使用者按 Ctrl+C。
// =====================================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;

const string Accent = "mediumpurple1";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    ShowHelp();
    return args.Length == 0 ? 1 : 0;
}

string Opt(string name, string def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

bool Flag(string name) => Array.IndexOf(args, name) >= 0;

// CLI output stays in Traditional Chinese unless --ui-lang en|zh-Hant|ja is given.
Loc.Set(Opt("--ui-lang", "zh-Hant"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return args[0] switch
    {
        "scan" => await Scan(),
        "export" => await Export(),
        "batch" => await Batch(),
        "thumb" => await Thumb(),
        "obswatch" => await ObsWatch(),
        "synccheck" => await SyncCheck(),
        "ocr" => await Ocr(),
        _ => Unknown(),
    };
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]已取消[/]");
    return 130;
}
catch (FileNotFoundException ex)
{
    AnsiConsole.MarkupLine($"[red]✘ 找不到檔案：[/]{Markup.Escape(ex.FileName ?? ex.Message)}");
    return 2;
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]✘ {Markup.Escape(ex.Message)}[/]");
    return 2;
}

// ---------------------------------------------------------------- commands

async Task<int> Scan()
{
    if (!Need(2, "apexhl scan <錄影檔>")) return 2;
    var file = InputFile(args[1]);
    var info = await (await MediaInfo.ProbeAsync(file, cts.Token)).WithTrackVolumesAsync(cts.Token);
    var opt = new ScanOptions
    {
        Language = Opt("--lang", "auto"),
        SampleFps = double.Parse(Opt("--fps", "2"), CultureInfo.InvariantCulture),
        OcrWidth = int.Parse(Opt("--width", "1280")),
        AudioTrack = int.Parse(Opt("--track", info.DefaultAudioTrack().ToString())),
        MaxCandidates = int.Parse(Opt("--max", "3")),
        MinScore = double.Parse(Opt("--min-score", "2"), CultureInfo.InvariantCulture),
        MaxClipLength = double.Parse(Opt("--max-len", "120"), CultureInfo.InvariantCulture),
        RegionOcr = !Flag("--no-region"),
        VerifyCandidates = !Flag("--no-verify"),
    };
    if (Opt("--prefer-lang", "") is { Length: > 0 } pref) opt.PreferredLanguage = pref;
    if (Opt("--workers", "") is { Length: > 0 } wk) opt.Workers = int.Parse(wk);

    ShowMediaInfo(info);

    ScanResult result = null!;
    await AnsiConsole.Progress()
        .AutoClear(false)
        .Columns(new TaskDescriptionColumn { Alignment = Justify.Left }, new ProgressBarColumn { CompletedStyle = new Style(Color.MediumPurple1) },
                 new PercentageColumn(), new ElapsedTimeColumn(), new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("掃描中", maxValue: 1);
            var progress = new Progress<ScanProgress>(p =>
            {
                task.Value = p.Fraction;
                task.Description = p.Hits is { Count: > 0 } h ? $"掃描中 · 已讀到 [yellow]{h.Count}[/] 個提示" : "掃描中";
            });
            result = await new Scanner(KeywordConfig.Load()).ScanAsync(info, opt, progress, cts.Token);
            task.Value = 1;
            task.Description = "掃描完成";
        });

    var (json, txt) = ScanReport.Save(result, Opt("--report-dir", ScanReport.DefaultFolder));
    ScanCache.Save(result);   // 介面之後打開同一支錄影，就直接讀回這次的結果

    var lang = result.Language ?? "?";
    AnsiConsole.MarkupLine($"遊戲語言 [{Accent}]{Markup.Escape(lang)}[/]{(result.LanguageConfident ? "" : " [grey](提示太少，不太確定)[/]")}" +
                           $"  ·  讀到 {result.Events.Count} 個提示  ·  耗時 {result.ScanSeconds:0.0} 秒");
    AnsiConsole.WriteLine();

    if (result.Candidates.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]⚠ 這場沒有找到夠精彩的交戰[/]（不會硬湊片段）");
    }
    else
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
            .Title($"[bold]找到 {result.Candidates.Count} 段精華[/]")
            .AddColumn(new TableColumn("#").Centered())
            .AddColumn("時間")
            .AddColumn(new TableColumn("長度").RightAligned())
            .AddColumn(new TableColumn("分數").RightAligned())
            .AddColumn("內容");
        int n = 1;
        foreach (var c in result.Candidates)
        {
            var color = c.Score >= 6 ? "green" : c.Score >= 3.5 ? "yellow" : "grey";
            table.AddRow(
                $"{n++}",
                $"{TimeFmt.Hms(c.Start)} – {TimeFmt.Hms(c.End)}",
                $"{c.End - c.Start:0}s",
                $"[{color}]{c.Score:0.0}[/]",
                Markup.Escape(c.Reason));
        }
        AnsiConsole.Write(table);
    }

    var others = result.Clusters.Where(c => !c.Accepted).ToList();
    if (others.Count > 0)
    {
        AnsiConsole.MarkupLine($"[grey]另外 {others.Count} 段沒選上：[/]");
        foreach (var c in others)
            AnsiConsole.MarkupLine($"[grey]  {TimeFmt.Hms(c.FirstEvent)}  {Markup.Escape(c.RejectReason ?? "")}[/]");
    }
    foreach (var note in result.Notes)
        AnsiConsole.MarkupLine($"[grey]· {Markup.Escape(note)}[/]");

    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine($"[grey]報告[/] {PathLink(txt)}");
    AnsiConsole.MarkupLine($"[grey]JSON[/] {PathLink(json)}");
    return 0;
}

async Task<int> Export()
{
    if (!Need(5, "apexhl export <錄影檔> <開始> <結束> <輸出.mp4> [--format landscape|vcrop|vfit] [--mix] [--loudnorm]")) return 2;
    var info = await MediaInfo.ProbeAsync(InputFile(args[1]), cts.Token);
    if (!TimeFmt.TryParse(args[2], out var s) || !TimeFmt.TryParse(args[3], out var e))
    {
        AnsiConsole.MarkupLine("[red]✘ 時間格式錯誤[/]，可以寫秒數（40）或時:分:秒（0:00:40）");
        return 2;
    }
    var opt = new ExportOptions
    {
        AudioTrack = int.Parse(Opt("--track", "0")),
        Format = Opt("--format", "landscape") switch { "vcrop" => ExportFormat.VerticalCrop, "vfit" => ExportFormat.VerticalFit, _ => ExportFormat.Landscape },
        MixAllTracks = Flag("--mix"),
        NormalizeLoudness = Flag("--loudnorm"),
    };
    var (ow, oh) = Exporter.OutputSize(info.Width, info.Height, opt.Format);
    AnsiConsole.MarkupLine($"[{Accent}]{Markup.Escape(Path.GetFileName(info.Path))}[/]  {TimeFmt.Hms(s)} – {TimeFmt.Hms(e)}  →  {ow}×{oh}");

    string outFile = "";
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await AnsiConsole.Progress()
        .AutoClear(true)
        .Columns(new TaskDescriptionColumn(), new ProgressBarColumn { CompletedStyle = new Style(Color.MediumPurple1) },
                 new PercentageColumn(), new RemainingTimeColumn(), new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            var task = ctx.AddTask("匯出中", maxValue: 1);
            outFile = await Exporter.ExportAsync(info, s, e, args[4], opt, new Progress<double>(v => task.Value = v), cts.Token);
            task.Value = 1;
        });

    ExportCheck check = null!;
    await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("檢查輸出檔…",
        async _ => check = await Exporter.VerifyAsync(outFile, e - s, cts.Token, opt.Format));

    var mark = check.Ok ? "[green]✔[/]" : "[red]✘[/]";
    AnsiConsole.MarkupLine($"{mark} {PathLink(outFile)}  [grey]({sw.Elapsed.TotalSeconds:0.0} 秒)[/]");
    AnsiConsole.MarkupLine($"  {(check.Ok ? "[grey]" : "[red]")}{Markup.Escape(check.Summary)}[/]");
    return check.Ok ? 0 : 3;
}

async Task<int> Batch()
{
    // 多支錄影：掃描（CPU）和剪片（GPU）分兩站同時進行，一支掃完就去剪，下一支接著掃（Core 的 Pipeline）
    // Several recordings: scanning (CPU) and exporting (GPU) run as two stations at once (Core.Pipeline).
    string[] valued = ["--out", "--scan-workers", "--export-workers", "--format", "--track", "--lang", "--max", "--min-score", "--ui-lang"];
    var inputs = new List<string>();
    for (int i = 1; i < args.Length; i++)
    {
        if (valued.Contains(args[i])) { i++; continue; }
        if (args[i].StartsWith("--")) continue;
        var full = Path.GetFullPath(args[i]);
        if (Directory.Exists(full))
            inputs.AddRange(Directory.EnumerateFiles(full).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".mp4" or ".mkv" or ".mov" or ".flv")
                .Where(f => !f.EndsWith(".part.mp4", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f));
        else inputs.Add(InputFile(full));
    }
    if (inputs.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]✘ 沒有錄影檔[/]");
        AnsiConsole.MarkupLine($"  用法：[{Accent}]apexhl batch <錄影檔或資料夾…> --out <輸出資料夾> [--scan-workers 1] [--export-workers 2] [--no-export] [--no-verify][/]");
        return 2;
    }
    var outDir = Path.GetFullPath(Opt("--out", Path.Combine(Path.GetDirectoryName(inputs[0])!, "highlights")));
    Directory.CreateDirectory(outDir);
    int scanWorkers = int.Parse(Opt("--scan-workers", "1"));
    int exportWorkers = int.Parse(Opt("--export-workers", "2"));
    bool doExport = !Flag("--no-export");
    var format = Opt("--format", "landscape") switch { "vcrop" => ExportFormat.VerticalCrop, "vfit" => ExportFormat.VerticalFit, _ => ExportFormat.Landscape };
    var gate = new object();
    void Say(string markup) { lock (gate) AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] {markup}"); }

    AnsiConsole.MarkupLine($"[bold]{inputs.Count}[/] 支錄影 → {PathLink(outDir)}  [grey](掃描 {scanWorkers} 支、剪片 {exportWorkers} 支同時進行)[/]");
    var keywords = KeywordConfig.Load();

    var results = await Pipeline.RunAsync(inputs,
        async (file, ct) =>
        {
            var name = Markup.Escape(Path.GetFileName(file));
            Say($"[{Accent}]掃描[/] {name}");
            var info = await (await MediaInfo.ProbeAsync(file, ct)).WithTrackVolumesAsync(ct);
            var opt = new ScanOptions
            {
                Language = Opt("--lang", "auto"),
                AudioTrack = int.Parse(Opt("--track", info.DefaultAudioTrack().ToString())),
                MaxCandidates = int.Parse(Opt("--max", "3")),
                MinScore = double.Parse(Opt("--min-score", "2"), CultureInfo.InvariantCulture),
                VerifyCandidates = !Flag("--no-verify"),
            };
            if (scanWorkers > 1) opt.Workers = Math.Max(2, opt.Workers / scanWorkers);   // 同時掃好幾支時，分掉 OCR worker
            var result = await new Scanner(keywords).ScanAsync(info, opt, null, ct);
            ScanReport.Save(result);
            ScanCache.Save(result);
            Say($"[{Accent}]掃完[/] {name}：{result.Candidates.Count} 段  [grey]({result.ScanSeconds:0.0} 秒)[/]");
            return (Info: info, Result: result, Track: opt.AudioTrack);
        }, scanWorkers,
        async (file, scan, ct) =>
        {
            var row = new BatchRow(file, scan.Result.Candidates.Count, 0, 0, scan.Result.ScanSeconds, null);
            if (!doExport || scan.Result.Candidates.Count == 0) return row;
            var jobs = scan.Result.Candidates.Select((c, i) => new ExportJob(i + 1, c.Start, c.End)).ToList();
            var eo = new ExportOptions { AudioTrack = scan.Track, Format = format };
            var done = await ExportBatch.RunAsync(jobs, async (job, prog, token) =>
            {
                var target = Path.Combine(outDir, Exporter.SuggestName(scan.Info, job.Number, job.Start, job.End, format));
                var f = await Exporter.ExportAsync(scan.Info, job.Start, job.End, target, eo, prog, token);
                var check = await Exporter.VerifyAsync(f, job.End - job.Start, token, format);
                return (f, check);
            }, null, (i, r) => Say($"{(r.Ok ? "[green]✔[/]" : "[red]✘[/]")} {(r.File != null ? PathLink(r.File) : Markup.Escape(r.Error?.Message ?? ""))}"), null, ct);
            return row with { Exported = done.Count(d => d.Ok), Failed = done.Count(d => !d.Ok) };
        }, exportWorkers,
        (file, ex) => new BatchRow(file, 0, 0, 0, 0, ex.Message),
        cts.Token);

    var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
        .AddColumn("錄影").AddColumn(new TableColumn("精華").RightAligned()).AddColumn(new TableColumn("已匯出").RightAligned())
        .AddColumn(new TableColumn("掃描秒數").RightAligned()).AddColumn("問題");
    foreach (var r in results)
        table.AddRow(Markup.Escape(Path.GetFileName(r.File)), $"{r.Clips}", r.Failed > 0 ? $"[red]{r.Exported}/{r.Clips}[/]" : $"{r.Exported}",
            $"{r.ScanSeconds:0.0}", r.Error != null ? $"[red]{Markup.Escape(r.Error)}[/]" : r.Failed > 0 ? $"[red]{r.Failed} 段匯出檢查沒過[/]" : "");
    AnsiConsole.Write(table);
    if (results.Any(r => r.Error != null)) return 2;
    return results.Any(r => r.Failed > 0) ? 3 : 0;
}

async Task<int> Thumb()
{
    if (!Need(4, "apexhl thumb <錄影檔> <秒> <輸出.jpg> [--width 192]")) return 2;
    var jpg = await Thumbnail.GrabJpegAsync(InputFile(args[1]), double.Parse(args[2], CultureInfo.InvariantCulture), int.Parse(Opt("--width", "192")), cts.Token);
    if (jpg == null) { AnsiConsole.MarkupLine("[red]✘ 讀不到畫面[/]"); return 2; }
    await File.WriteAllBytesAsync(args[3], jpg);
    AnsiConsole.MarkupLine($"[green]✔[/] {PathLink(Path.GetFullPath(args[3]))}  [grey]{jpg.Length / 1024.0:0.0} KB[/]");
    return 0;
}

async Task<int> ObsWatch()
{
    // The password comes from an environment variable so it never ends up in shell history.
    var host = Opt("--host", "localhost"); var port = int.Parse(Opt("--port", "4455"));
    var pw = Environment.GetEnvironmentVariable("APEXHL_OBS_PASSWORD");
    string version = "";
    try
    {
        await AnsiConsole.Status().StartAsync($"連線到 OBS（{Markup.Escape(host)}:{port}）…",
            async _ => version = await ObsWatcher.TestAsync(host, port, pw, cts.Token));
    }
    catch (ObsAuthException)
    {
        AnsiConsole.MarkupLine("[red]✘ OBS 密碼錯誤[/]，請把密碼放進環境變數 APEXHL_OBS_PASSWORD");
        return 2;
    }
    catch (Exception ex) when (!cts.IsCancellationRequested)
    {
        AnsiConsole.MarkupLine($"[red]✘[/] {Markup.Escape(ObsWatcher.FriendlyError(ex, host, port))}");
        return 2;
    }
    AnsiConsole.MarkupLine($"[green]✔[/] 已連線 OBS {Markup.Escape(version)}，等待錄影停止…  [grey](Ctrl+C 結束)[/]");

    await using var w = new ObsWatcher(host, port, pw);
    w.StateChanged += st =>
    {
        var color = st switch { ObsState.Connected => "green", ObsState.Connecting => "yellow", _ => "red" };
        var err = string.IsNullOrEmpty(w.LastError) ? "" : $"  [grey]{Markup.Escape(w.LastError)}[/]";
        AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] [{color}]● {st}[/]{err}");
    };
    w.RecordingStopped += path =>
        AnsiConsole.MarkupLine($"[grey]{DateTime.Now:HH:mm:ss}[/] [{Accent}]■ 錄影結束[/] {PathLink(path)}");
    w.Start();
    try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
    return 0;
}

async Task<int> SyncCheck()
{
    // Independent A/V sync check: find where the output's audio and video each line up inside the source.
    // If both land at the same source time, audio and video are in sync.
    if (!Need(4, "apexhl synccheck <原始錄影> <開始秒> <輸出.mp4> [--track N]")) return 2;
    var src = InputFile(args[1]); var outFile = InputFile(args[3]);
    if (!TimeFmt.TryParse(args[2], out var s0)) return 2;
    int track = int.Parse(Opt("--track", "0"));
    var outInfo = await MediaInfo.ProbeAsync(outFile);
    var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey35)
        .AddColumn("輸出位置").AddColumn(new TableColumn("音訊偏移").RightAligned())
        .AddColumn(new TableColumn("畫面偏移").RightAligned()).AddColumn(new TableColumn("音畫差").RightAligned());
    foreach (var at in new[] { 2.0, outInfo.Duration / 2, Math.Max(2, outInfo.Duration - 4) })
    {
        // Audio: 1 s of output audio vs. source audio, search ±0.5 s.
        var a = await Pcm(outFile, at, 1.0, 0);
        var b = await Pcm(src, s0 + at - 0.5, 2.0, track);
        int bestLag = 0; double best = double.MinValue;
        for (int lag = 0; lag + a.Length <= b.Length; lag++)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++) { dot += a[i] * b[lag + i]; na += a[i] * a[i]; nb += b[lag + i] * b[lag + i]; }
            var c = dot / Math.Sqrt(na * nb + 1e-9);
            if (c > best) { best = c; bestLag = lag; }
        }
        double audioOffsetMs = (bestLag / 8000.0 - 0.5) * 1000;

        // Video: output frame vs. source frames within ±10 frames, smallest pixel difference wins.
        var fps = outInfo.Fps > 0 ? outInfo.Fps : 60;
        var vo = await Gray(outFile, at, 1);
        var vs = await Gray(src, s0 + at - 10 / fps, 21);
        int frameBytes = vo.Length;
        int bestF = 0; double bestD = double.MaxValue;
        for (int f = 0; f + 1 <= vs.Length / frameBytes; f++)
        {
            double d = 0;
            for (int i = 0; i < frameBytes; i++) d += Math.Abs(vo[i] - vs[f * frameBytes + i]);
            d /= frameBytes;
            if (d < bestD) { bestD = d; bestF = f; }
        }
        double videoOffsetMs = (bestF - 10) / fps * 1000;
        var diff = audioOffsetMs - videoOffsetMs;
        var color = Math.Abs(diff) <= 45 ? "green" : "red";
        table.AddRow($"{at:0.0}s", $"{audioOffsetMs:+0;-0;0} ms [grey](相關 {best:0.00})[/]",
            $"{videoOffsetMs:+0;-0;0} ms [grey](差 {bestD:0.0})[/]", $"[{color}]{diff:+0;-0;0} ms[/]");
    }
    AnsiConsole.Write(table);
    return 0;
}

async Task<int> Ocr()
{
    if (!Need(3, "apexhl ocr <錄影檔> <秒> [--width 1280]")) return 2;
    var info = await MediaInfo.ProbeAsync(InputFile(args[1]), cts.Token);
    var t = double.Parse(args[2], CultureInfo.InvariantCulture);
    int width = int.Parse(Opt("--width", "1280"));
    int w = Math.Min(width, info.Width); w -= w % 2;
    int h = (int)Math.Round(info.Height * (double)w / info.Width); h -= h % 2;
    var raw = await RunRaw(info.Path, t, w, h);
    foreach (var tag in OcrReader.InstalledLanguages())
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lines = await OcrReader.Create(tag).ReadAsync(raw, w, h);
        AnsiConsole.Write(new Rule($"[{Accent}]{Markup.Escape(tag)}[/] [grey]{sw.ElapsedMilliseconds} ms · {lines.Count} 行[/]").LeftJustified());
        foreach (var l in lines) AnsiConsole.MarkupLine($"  [grey]{l.X:0.00},{l.Y:0.00}[/]  {Markup.Escape(l.Text)}");
    }
    return 0;
}

int Unknown()
{
    AnsiConsole.MarkupLine($"[red]✘ 沒有「{Markup.Escape(args[0])}」這個指令[/]");
    ShowHelp();
    return 1;
}

// ---------------------------------------------------------------- helpers

bool Need(int count, string usage)
{
    if (args.Length >= count) return true;
    AnsiConsole.MarkupLine("[red]✘ 參數不夠[/]");
    AnsiConsole.MarkupLine($"  用法：[{Accent}]{Markup.Escape(usage)}[/]");
    return false;
}

static string InputFile(string path)
{
    var full = Path.GetFullPath(path);
    if (!File.Exists(full)) throw new FileNotFoundException(null, full);
    return full;
}

static string PathLink(string path) => $"[link=file:///{Markup.Escape(path.Replace('\\', '/'))}]{Markup.Escape(path)}[/]";

static void ShowMediaInfo(MediaInfo info)
{
    var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
    grid.AddRow("[grey]檔案[/]", $"[bold]{Markup.Escape(Path.GetFileName(info.Path))}[/]");
    grid.AddRow("[grey]畫面[/]", $"{info.Width}×{info.Height} · {info.Fps:0.##} fps · {TimeFmt.Hms(info.Duration)}");
    grid.AddRow("[grey]音軌[/]", Markup.Escape(string.Join(" / ",
        info.AudioTracks.Select(t => $"{t}{(t.MeanVolumeDb is { } v ? $" {v:0} dB" : "")}"))));
    AnsiConsole.Write(new Panel(grid).Border(BoxBorder.Rounded).BorderColor(Color.MediumPurple1).Header("[bold] Apex 精華挑片 [/]"));
}

static void ShowHelp()
{
    AnsiConsole.Write(new Rule($"[bold {Accent}]apexhl[/] [grey]Apex 精華挑片 · 命令列版[/]").LeftJustified());
    var t = new Table().Border(TableBorder.None).HideHeaders()
        .AddColumn(new TableColumn("").NoWrap().PadRight(3)).AddColumn("");
    t.AddRow($"[{Accent}]scan[/] <錄影檔>", "找出精華（預設最多 3 段）\n[grey]--lang auto|en|zh-Hant…  --prefer-lang en  --fps 2  --width 1280  --workers N  --track N\n--max 3  --min-score 2  --max-len 120（0 = 不限）  --no-region（每張都讀整個畫面）  --no-verify（不做二次確認）[/]");
    t.AddRow($"[{Accent}]export[/] <錄影檔> <開始> <結束> <輸出.mp4>", "剪出一段\n[grey]--format landscape|vcrop|vfit  --track N  --mix（混合所有音軌）  --loudnorm（音量標準化）[/]");
    t.AddRow($"[{Accent}]batch[/] <錄影檔或資料夾…>", "一次處理好幾支：掃描和剪片同時進行，一支掃完就剪\n[grey]--out <資料夾>  --scan-workers 1  --export-workers 2  --format landscape|vcrop|vfit  --no-export  --no-verify[/]");
    t.AddRow($"[{Accent}]thumb[/] <錄影檔> <秒> <輸出.jpg>", "存一張縮圖  [grey]--width 192[/]");
    t.AddRow($"[{Accent}]obswatch[/]", "等 OBS 停止錄影  [grey]--host localhost --port 4455，密碼放 APEXHL_OBS_PASSWORD[/]");
    t.AddRow($"[{Accent}]ocr[/] <錄影檔> <秒>", "列出該時間畫面上 OCR 讀到的字");
    t.AddRow($"[{Accent}]synccheck[/] <原始錄影> <開始秒> <輸出.mp4>", "檢查匯出檔影音是否同步");
    AnsiConsole.Write(t);
    AnsiConsole.MarkupLine("[grey]時間可寫秒數（40）或時:分:秒（0:00:40）。原始錄影只會被讀取，不會被修改。[/]");
}

static async Task<float[]> Pcm(string path, double start, double dur, int track)
{
    var bytes = await Capture(["-v", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-i", path, "-t", dur.ToString("0.###", CultureInfo.InvariantCulture),
        "-map", $"0:a:{track}", "-ac", "1", "-ar", "8000", "-f", "s16le", "pipe:1"]);
    var r = new float[bytes.Length / 2];
    for (int i = 0; i < r.Length; i++) r[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
    return r;
}

static Task<byte[]> Gray(string path, double start, int frames) =>
    Capture(["-v", "error", "-ss", start.ToString("0.####", CultureInfo.InvariantCulture), "-i", path, "-frames:v", frames.ToString(),
        "-vf", "scale=160:90:flags=area", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1"]);

static async Task<byte[]> Capture(string[] a)
{
    var psi = new System.Diagnostics.ProcessStartInfo(FFmpegLocator.FFmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var x in a) psi.ArgumentList.Add(x);
    using var p = System.Diagnostics.Process.Start(psi)!;
    _ = p.StandardError.ReadToEndAsync();
    using var ms = new MemoryStream();
    await p.StandardOutput.BaseStream.CopyToAsync(ms);
    await p.WaitForExitAsync();
    return ms.ToArray();
}

static async Task<byte[]> RunRaw(string path, double t, int w, int h)
{
    var psi = new System.Diagnostics.ProcessStartInfo(FFmpegLocator.FFmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var a in new[] { "-v", "error", "-ss", t.ToString(CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-vf", $"scale={w}:{h}:flags=area", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" })
        psi.ArgumentList.Add(a);
    using var p = System.Diagnostics.Process.Start(psi)!;
    using var ms = new MemoryStream();
    await p.StandardOutput.BaseStream.CopyToAsync(ms);
    await p.WaitForExitAsync();
    return ms.ToArray();
}

sealed record BatchRow(string File, int Clips, int Exported, int Failed, double ScanSeconds, string? Error);
