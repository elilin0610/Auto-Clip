using System.Globalization;
using ApexHighlighter.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        用法：
          apexhl scan <錄影檔> [--lang auto|en|zh-Hant…] [--fps 2] [--width 1280] [--workers N] [--track N]
          apexhl export <錄影檔> <開始秒或hh:mm:ss> <結束> <輸出.mp4> [--track N] [--format landscape|vcrop|vfit]
          apexhl ocr <錄影檔> <秒> [--width 1280]      列出該時間點畫面上 OCR 讀到的文字
          apexhl thumb <錄影檔> <秒> <輸出.jpg> [--width 192]
          apexhl obswatch [--host localhost] [--port 4455]   等 OBS 停止錄影（密碼放環境變數 APEXHL_OBS_PASSWORD）
        """);
    return 1;
}

string Opt(string name, string def)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

// CLI output stays in Traditional Chinese unless --ui-lang en|zh-Hant|ja is given.
Loc.Set(Opt("--ui-lang", "zh-Hant"));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (args[0])
{
    case "scan":
    {
        var info = await (await MediaInfo.ProbeAsync(args[1], cts.Token)).WithTrackVolumesAsync(cts.Token);
        var opt = new ScanOptions
        {
            Language = Opt("--lang", "auto"),
            SampleFps = double.Parse(Opt("--fps", "2"), CultureInfo.InvariantCulture),
            OcrWidth = int.Parse(Opt("--width", "1280")),
            AudioTrack = int.Parse(Opt("--track", info.DefaultAudioTrack().ToString())),
        };
        if (Opt("--workers", "") is { Length: > 0 } wk) opt.Workers = int.Parse(wk);
        Console.WriteLine($"{info.Width}x{info.Height} {info.Fps:0.##}fps {TimeFmt.Hms(info.Duration)}，音軌：{string.Join(" / ", info.AudioTracks.Select(t => $"{t}{(t.MeanVolumeDb is { } v ? $" {v:0}dB" : "")}"))}");
        Console.WriteLine($"workers={opt.Workers} fps={opt.SampleFps} width={opt.OcrWidth}");
        var last = DateTime.MinValue;
        var progress = new Progress<ScanProgress>(p =>
        {
            if ((DateTime.Now - last).TotalSeconds < 5) return;
            last = DateTime.Now;
            Console.WriteLine($"  {p.Fraction:P0} {p.Message}");
        });
        var result = await new Scanner(KeywordConfig.Load()).ScanAsync(info, opt, progress, cts.Token);
        var (json, txt) = ScanReport.Save(result, Opt("--report-dir", ScanReport.DefaultFolder));
        Console.WriteLine();
        Console.WriteLine(ScanReport.ToText(result));
        Console.WriteLine($"報告：{txt}");
        Console.WriteLine($"JSON：{json}");
        return 0;
    }
    case "export":
    {
        var info = await MediaInfo.ProbeAsync(args[1], cts.Token);
        if (!TimeFmt.TryParse(args[2], out var s) || !TimeFmt.TryParse(args[3], out var e)) { Console.Error.WriteLine("時間格式錯誤"); return 2; }
        var opt = new ExportOptions
        {
            AudioTrack = int.Parse(Opt("--track", "0")),
            Format = Opt("--format", "landscape") switch { "vcrop" => ExportFormat.VerticalCrop, "vfit" => ExportFormat.VerticalFit, _ => ExportFormat.Landscape },
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var outFile = await Exporter.ExportAsync(info, s, e, args[4], opt, null, cts.Token);
        Console.WriteLine($"匯出 {outFile}，耗時 {sw.Elapsed.TotalSeconds:0.0}s");
        var check = await Exporter.VerifyAsync(outFile, e - s, cts.Token, opt.Format);
        Console.WriteLine(check.Summary);
        return check.Ok ? 0 : 3;
    }
    case "synccheck":
    {
        // Independent A/V sync check: find where the output's audio and video each line up inside the source.
        // If both land at the same source time, audio and video are in sync.
        var src = args[1]; var outFile = args[3];
        if (!TimeFmt.TryParse(args[2], out var s0)) return 2;
        int track = int.Parse(Opt("--track", "0"));
        var outInfo = await MediaInfo.ProbeAsync(outFile);
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
            Console.WriteLine($"輸出 {at:0.0}s：音訊對到原檔偏移 {audioOffsetMs:+0;-0;0} ms（相關 {best:0.00}），畫面偏移 {videoOffsetMs:+0;-0;0} ms（平均差 {bestD:0.0}），音畫相對差 {audioOffsetMs - videoOffsetMs:+0;-0;0} ms");
        }
        return 0;
    }
    case "thumb":
    {
        var jpg = await Thumbnail.GrabJpegAsync(args[1], double.Parse(args[2], CultureInfo.InvariantCulture), int.Parse(Opt("--width", "192")), cts.Token);
        if (jpg == null) { Console.Error.WriteLine("讀不到畫面"); return 2; }
        await File.WriteAllBytesAsync(args[3], jpg);
        Console.WriteLine($"{args[3]}  {jpg.Length} bytes");
        return 0;
    }
    case "obswatch":
    {
        // The password comes from an environment variable so it never ends up in shell history.
        var host = Opt("--host", "localhost"); var port = int.Parse(Opt("--port", "4455"));
        var pw = Environment.GetEnvironmentVariable("APEXHL_OBS_PASSWORD");
        try { Console.WriteLine($"OBS {await ObsWatcher.TestAsync(host, port, pw, cts.Token)}"); }
        catch (Exception ex) { Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}"); return 2; }
        await using var w = new ObsWatcher(host, port, pw);
        w.StateChanged += st => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {st} {w.LastError}");
        w.RecordingStopped += path => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 錄影結束：{path}");
        w.Start();
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch (OperationCanceledException) { }
        return 0;
    }
    case "ocr":
    {
        var info = await MediaInfo.ProbeAsync(args[1], cts.Token);
        var t = double.Parse(args[2], CultureInfo.InvariantCulture);
        int width = int.Parse(Opt("--width", "1280"));
        int w = Math.Min(width, info.Width); w -= w % 2;
        int h = (int)Math.Round(info.Height * (double)w / info.Width); h -= h % 2;
        var raw = await RunRaw(info.Path, t, w, h);
        foreach (var tag in OcrReader.InstalledLanguages())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lines = await OcrReader.Create(tag).ReadAsync(raw, w, h);
            Console.WriteLine($"--- {tag} ({sw.ElapsedMilliseconds} ms, {lines.Count} lines)");
            foreach (var l in lines) Console.WriteLine($"  [{l.X:0.00},{l.Y:0.00}] {l.Text}");
        }
        return 0;
    }
}
return 1;

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
