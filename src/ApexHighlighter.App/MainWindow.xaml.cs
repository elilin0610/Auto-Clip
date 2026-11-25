using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ApexHighlighter.Core;
using Microsoft.Win32;

namespace ApexHighlighter.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ClipItem> _clips = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MediaInfo? _info;
    private CancellationTokenSource? _cts;
    private bool _playing, _dragging, _updatingSlider;
    private double? _stopAt;
    private KeywordConfig? _cfg;

    public MainWindow()
    {
        InitializeComponent();
        ClipList.ItemsSource = _clips;
        _clips.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ClipItem c in e.NewItems)
                    c.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(ClipItem.Keep)) { DrawMarkers(); UpdateButtons(); } };
            Renumber(); DrawMarkers(); UpdateButtons();
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        OutDirBox.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Apex精華");

        LanguageBox.Items.Add("自動");
        try
        {
            _cfg = KeywordConfig.Load();
            foreach (var l in _cfg.Languages) LanguageBox.Items.Add(l.Id);
        }
        catch (Exception ex) { Log("讀不到 keywords.json：" + ex.Message); }
        LanguageBox.SelectedIndex = 0;

        if (FFmpegLocator.TryFind(out var dir)) Log($"FFmpeg：{dir}");
        else Log("找不到 FFmpeg。請執行 winget install Gyan.FFmpeg，或把 ffmpeg.exe、ffprobe.exe 放到程式資料夾的 ffmpeg 子資料夾。");
        Log("OCR 語言（Windows 內建）：" + string.Join("、", SafeOcrLanguages()));
        SizeChanged += (_, _) => DrawMarkers();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += async (_, _) => await OpenFileAsync(args[1]);
    }

    private static IEnumerable<string> SafeOcrLanguages()
    {
        try { return OcrReader.InstalledLanguages(); } catch { return ["（無法取得）"]; }
    }

    // ───────────── Import ─────────────

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "選擇一場 Apex 錄影",
            Filter = "錄影檔 (*.mkv;*.mp4;*.mov;*.flv)|*.mkv;*.mp4;*.mov;*.flv|所有檔案|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog(this) == true) await OpenFileAsync(dlg.FileName);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) await OpenFileAsync(files[0]);
    }

    private async Task OpenFileAsync(string path)
    {
        if (_cts != null) { MessageBox.Show(this, "請先等目前的工作結束或按取消。", Title); return; }
        try
        {
            SetBusy(true, "讀取檔案資訊…");
            using var cts = new CancellationTokenSource();
            var info = await MediaInfo.ProbeAsync(path, cts.Token);
            info = await info.WithTrackVolumesAsync(cts.Token);
            _info = info;
            _clips.Clear();
            FileText.Text = $"{System.IO.Path.GetFileName(path)}　{info.Width}×{info.Height}　{info.Fps:0.##} fps　{TimeFmt.Hms(info.Duration)}";
            FileText.ToolTip = path;

            AudioBox.Items.Clear();
            foreach (var t in info.AudioTracks)
                AudioBox.Items.Add(t + (t.MeanVolumeDb is { } v ? $"  {v:0} dB" : ""));
            if (info.AudioTracks.Count == 0) AudioBox.Items.Add("（沒有音軌）");
            AudioBox.SelectedIndex = Math.Max(0, info.DefaultAudioTrack());

            var (cw, ch) = Exporter.Canvas(info.Width, info.Height);
            ExportInfo.Text = cw == info.Width && ch == info.Height
                ? $"輸出 {cw}×{ch}（原尺寸，16:9）"
                : $"輸出 {cw}×{ch}（原畫面 {info.Width}×{info.Height} 置中補黑邊成 16:9，不裁切不拉伸）";

            Player.Source = new Uri(path);
            Player.Play(); Player.Pause();
            _playing = false;
            PlayerHint.Visibility = Visibility.Collapsed;
            Log($"已匯入：{path}（{info.Width}×{info.Height}、{TimeFmt.Hms(info.Duration)}、音軌 {info.AudioTracks.Count} 條）。原始檔只讀取，不會被修改。");
            ListHint.Text = "按「掃描精華」自動找交戰，或用播放器找到位置後按「＋ 在目前位置新增」。";
            SetBusy(false, "就緒");
        }
        catch (Exception ex)
        {
            SetBusy(false, "匯入失敗");
            Log("匯入失敗：" + ex.Message);
            MessageBox.Show(this, ex.Message, "匯入失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────── Scan ─────────────

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null || _cfg == null) return;
        var opt = new ScanOptions
        {
            Language = LanguageBox.SelectedIndex <= 0 ? "auto" : (string)LanguageBox.SelectedItem,
            AudioTrack = Math.Max(0, AudioBox.SelectedIndex),
        };
        _cts = new CancellationTokenSource();
        SetBusy(true, "掃描中…");
        PausePlayer();
        var progress = new Progress<ScanProgress>(p => { Progress.Value = p.Fraction; StatusText.Text = p.Message; });
        try
        {
            Log($"開始掃描（每秒 {opt.SampleFps} 張、OCR 寬 {opt.OcrWidth}px、{opt.Workers} 執行緒、語言 {opt.Language}）…");
            var result = await Task.Run(() => new Scanner(_cfg).ScanAsync(_info, opt, progress, _cts.Token));
            string reportPath = "";
            try { reportPath = ScanReport.Save(result).Text; } catch (Exception ex) { Log("報告存檔失敗：" + ex.Message); }

            foreach (var c in _clips.Where(c => c.IsAuto).ToList()) _clips.Remove(c);
            int idx = 0;
            foreach (var c in result.Candidates)
            {
                var times = string.Join("、", c.Events.Select(ev => $"{TimeFmt.Hms(ev.Time)} {ev.Kind.Zh()}"));
                _clips.Insert(idx++, new ClipItem(c.Start, c.End, c.Reason, $"分數 {c.Score:0.0}｜{times}｜{c.StartNote}", true));
            }
            SortClips();

            Log($"掃描完成：耗時 {result.ScanSeconds:0.0} 秒（{(result.ScanSeconds > 0 ? result.Duration / result.ScanSeconds : 0):0.0} 倍速），語言 {result.Language}（OCR {result.OcrLanguage}）。");
            if (result.PromptPosition != null) Log(result.PromptPosition);
            foreach (var n in result.Notes) Log("備註：" + n);
            Log($"讀到 {result.Events.Count} 個提示，合併成 {result.Clusters.Count} 波交戰，建議 {result.Candidates.Count} 段。");
            foreach (var c in result.Clusters.Where(c => !c.Accepted))
                Log($"  略過 {TimeFmt.Hms(c.Start)}–{TimeFmt.Hms(c.End)}：{c.RejectReason}");
            if (reportPath.Length > 0) Log("完整記錄（含每個 OCR 命中，方便檢查誤判）：" + reportPath);

            ListHint.Text = result.Candidates.Count == 0
                ? "這場沒有找到夠確定的精華（沒有硬湊）。可以用播放器手動新增。"
                : $"找到 {result.Candidates.Count} 段。取消勾選＝跳過；選取後可微調起訖。";
            SetBusy(false, $"掃描完成，{result.Candidates.Count} 段候選");
            if (_clips.Count > 0) ClipList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { SetBusy(false, "已取消"); Log("掃描已取消。"); }
        catch (Exception ex)
        {
            SetBusy(false, "掃描失敗");
            Log("掃描失敗：" + ex.Message);
            MessageBox.Show(this, ex.Message, "掃描失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ───────────── Export ─────────────

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var items = _clips.Where(c => c.Keep).ToList();
        if (items.Count == 0) { MessageBox.Show(this, "沒有勾選任何片段。", Title); return; }
        var bad = items.FirstOrDefault(c => c.End - c.Start < 1);
        if (bad != null) { MessageBox.Show(this, $"片段 #{bad.Number} 太短或起訖顛倒。", Title); return; }

        var outDir = OutDirBox.Text.Trim();
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { MessageBox.Show(this, "無法建立輸出資料夾：" + ex.Message, Title); return; }

        var opt = new ExportOptions { AudioTrack = Math.Max(0, AudioBox.SelectedIndex) };
        _cts = new CancellationTokenSource();
        SetBusy(true, "匯出中…");
        PausePlayer();
        try
        {
            bool nvenc = await Exporter.NvencAvailableAsync(_cts.Token);
            Log($"開始匯出 {items.Count} 段（編碼器：{(nvenc ? "NVIDIA NVENC" : "x264 CPU")}，音軌 {opt.AudioTrack + 1}）…");
            int i = 0;
            foreach (var c in items)
            {
                i++;
                var name = Exporter.SuggestName(_info, c.Number, c.Start, c.End);
                var outPath = UniquePath(System.IO.Path.Combine(outDir, name));
                int idx = i;
                var prog = new Progress<double>(p =>
                {
                    Progress.Value = ((idx - 1) + p) / items.Count;
                    StatusText.Text = $"匯出 {idx}/{items.Count}：{p:P0}";
                });
                var sw = Stopwatch.StartNew();
                c.Status = "匯出中…";
                var file = await Task.Run(() => Exporter.ExportAsync(_info, c.Start, c.End, outPath, opt, prog, _cts.Token));
                StatusText.Text = $"檢查 {idx}/{items.Count}（完整解碼、音畫對齊）…";
                var check = await Task.Run(() => Exporter.VerifyAsync(file, c.End - c.Start, _cts.Token));
                c.Status = (check.Ok ? "✔ " : "⚠ ") + System.IO.Path.GetFileName(file);
                Log($"#{c.Number} → {file}（{sw.Elapsed.TotalSeconds:0.0} 秒）　{check.Summary}");
            }
            SetBusy(false, $"匯出完成：{items.Count} 段");
            Log($"匯出完成，檔案在 {outDir}");
        }
        catch (OperationCanceledException) { SetBusy(false, "已取消"); Log("匯出已取消，未完成的檔案已刪除。"); }
        catch (Exception ex)
        {
            SetBusy(false, "匯出失敗");
            Log("匯出失敗：" + ex.Message);
            MessageBox.Show(this, ex.Message, "匯出失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); }
    }

    private static string UniquePath(string p)
    {
        if (!File.Exists(p)) return p;
        var dir = System.IO.Path.GetDirectoryName(p)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(p);
        for (int n = 2; ; n++)
        {
            var q = System.IO.Path.Combine(dir, $"{stem} ({n}).mp4");
            if (!File.Exists(q)) return q;
        }
    }

    private void BrowseOut_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "選擇輸出資料夾", InitialDirectory = Directory.Exists(OutDirBox.Text) ? OutDirBox.Text : null };
        if (dlg.ShowDialog(this) == true) OutDirBox.Text = dlg.FolderName;
    }

    private void OpenOut_Click(object sender, RoutedEventArgs e)
    {
        var d = OutDirBox.Text.Trim();
        if (!Directory.Exists(d)) Directory.CreateDirectory(d);
        Process.Start(new ProcessStartInfo("explorer.exe", d) { UseShellExecute = true });
    }

    // ───────────── Clip list & editor ─────────────

    private ClipItem? Selected => ClipList.SelectedItem as ClipItem;

    private void AddManual_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var pos = Player.Position.TotalSeconds;
        var start = Math.Max(0, pos - 15);
        var end = Math.Min(_info.Duration, pos + 15);
        var c = new ClipItem(start, end, "手動新增", $"以 {TimeFmt.Hms(pos)} 為中心，前後各 15 秒，請再微調", false);
        _clips.Add(c);
        SortClips();
        ClipList.SelectedItem = c;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } c) _clips.Remove(c);
    }

    private void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Editor.DataContext = Selected;
        Editor.IsEnabled = Selected != null;
        DrawMarkers();
        if (Selected is { } c) SeekTo(c.Start);
    }

    private void ClipList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => PlayClip_Click(sender, e);

    private void StartBox_Commit(object sender, RoutedEventArgs e) => CommitTime(StartBox, true);
    private void EndBox_Commit(object sender, RoutedEventArgs e) => CommitTime(EndBox, false);

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTime((TextBox)sender, sender == StartBox); e.Handled = true; }
    }

    private void CommitTime(TextBox box, bool start)
    {
        if (Selected is not { } c || _info == null) return;
        if (TimeFmt.TryParse(box.Text, out var t))
        {
            t = Math.Clamp(t, 0, _info.Duration);
            if (start) c.Start = t; else c.End = t;
            SeekTo(start ? c.Start : Math.Max(c.Start, c.End - 3));
            DrawMarkers();
        }
        box.Text = start ? c.StartText : c.EndText;
    }

    private void Nudge_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c || _info == null) return;
        var parts = ((string)((Button)sender).Tag).Split(':');
        var delta = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        if (parts[0] == "s") { c.Start = Math.Clamp(c.Start + delta, 0, _info.Duration); SeekTo(c.Start); }
        else { c.End = Math.Clamp(c.End + delta, 0, _info.Duration); SeekTo(c.End); }
        DrawMarkers();
    }

    private void SetHere_Click(object sender, RoutedEventArgs e) => SetHere((string)((Button)sender).Tag == "s");

    private void SetHere(bool start)
    {
        if (Selected is not { } c) return;
        var pos = Player.Position.TotalSeconds;
        if (start) c.Start = pos; else c.End = pos;
        DrawMarkers();
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        PlayRange(c.Start, c.End);
    }

    private void PlayTail_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } c) return;
        PlayRange(Math.Max(c.Start, c.End - 3), c.End);
    }

    private void Renumber()
    {
        int n = 1;
        foreach (var c in _clips) c.Number = n++;
    }

    private void SortClips()
    {
        var sel = Selected;
        var sorted = _clips.OrderBy(c => c.Start).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = _clips.IndexOf(sorted[i]);
            if (cur != i) _clips.Move(cur, i);
        }
        Renumber();
        if (sel != null) ClipList.SelectedItem = sel;
    }

    private void DrawMarkers()
    {
        Markers.Children.Clear();
        if (_info == null || _info.Duration <= 0) return;
        double w = Markers.ActualWidth;
        foreach (var c in _clips)
        {
            var r = new Rectangle
            {
                Width = Math.Max(2, (c.End - c.Start) / _info.Duration * w),
                Height = 12,
                Fill = new SolidColorBrush(c == Selected ? Color.FromRgb(0xE5, 0x48, 0x4D) : c.Keep ? Color.FromRgb(0x8A, 0x3A, 0x3E) : Color.FromRgb(0x4A, 0x4F, 0x5A)),
                ToolTip = c.Title,
            };
            Canvas.SetLeft(r, c.Start / _info.Duration * w);
            Markers.Children.Add(r);
        }
    }

    // ───────────── Player ─────────────

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        var dur = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : _info?.Duration ?? 1;
        Timeline.Maximum = Math.Max(1, _info?.Duration ?? dur);
        Timeline.SmallChange = 1; Timeline.LargeChange = 10;
        DrawMarkers();
        UpdateButtons();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        PlayerHint.Text = "這個格式無法在預覽播放器播放（仍可掃描與匯出）";
        PlayerHint.Visibility = Visibility.Visible;
        Log("預覽播放失敗：" + e.ErrorException?.Message);
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => PausePlayer();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) PausePlayer();
        else { _stopAt = null; Player.Play(); _playing = true; PlayButton.Content = "❚❚ 暫停"; }
    }

    private void PlayRange(double from, double to)
    {
        SeekTo(from);
        _stopAt = to;
        Player.Play();
        _playing = true;
        PlayButton.Content = "❚❚ 暫停";
    }

    private void PausePlayer()
    {
        Player.Pause();
        _playing = false;
        _stopAt = null;
        PlayButton.Content = "▶ 播放";
    }

    private void SeekTo(double s)
    {
        if (_info == null) return;
        s = Math.Clamp(s, 0, _info.Duration);
        Player.Position = TimeSpan.FromSeconds(s);
        _updatingSlider = true;
        Timeline.Value = s;
        _updatingSlider = false;
        UpdateTimeText();
    }

    private void Seek_Click(object sender, RoutedEventArgs e)
    {
        var d = double.Parse((string)((Button)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        SeekTo(Player.Position.TotalSeconds + d);
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_info == null || _dragging) return;
        var pos = Player.Position.TotalSeconds;
        if (_playing && _stopAt is { } stop && pos >= stop) { PausePlayer(); SeekTo(stop); return; }
        _updatingSlider = true;
        Timeline.Value = pos;
        _updatingSlider = false;
        UpdateTimeText();
    }

    private void UpdateTimeText() =>
        TimeText.Text = $"{TimeFmt.HmsMs(Player.Position.TotalSeconds)} / {TimeFmt.Hms(_info?.Duration ?? 0)}";

    private void Timeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || _info == null) return;
        Player.Position = TimeSpan.FromSeconds(e.NewValue);
        UpdateTimeText();
    }

    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e) => _dragging = true;
    private void Timeline_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        if (_info != null) Player.Position = TimeSpan.FromSeconds(Timeline.Value);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || _info == null) return;
        switch (e.Key)
        {
            case Key.Space: PlayPause_Click(this, e); e.Handled = true; break;
            case Key.Left: SeekTo(Player.Position.TotalSeconds - 2); e.Handled = true; break;
            case Key.Right: SeekTo(Player.Position.TotalSeconds + 2); e.Handled = true; break;
            case Key.I: SetHere(true); e.Handled = true; break;
            case Key.O: SetHere(false); e.Handled = true; break;
        }
    }

    // ───────────── Helpers ─────────────

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        if (!busy) Progress.Value = 0;
        ImportButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        UpdateButtons(busy);
    }

    private void UpdateButtons(bool? busy = null)
    {
        bool b = busy ?? _cts != null;
        bool has = _info != null;
        ScanButton.IsEnabled = has && !b;
        ExportButton.IsEnabled = has && !b && _clips.Any(c => c.Keep);
        AddButton.IsEnabled = has && !b;
        PlayButton.IsEnabled = has;
    }

    private void Log(string s)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
