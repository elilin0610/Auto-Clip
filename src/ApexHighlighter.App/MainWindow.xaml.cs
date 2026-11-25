using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
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
    private bool _playing, _dragging, _updatingSlider, _previewUnsupported;
    private double? _stopAt;
    private KeywordConfig? _cfg;
    // The list hint depends on state, so remember which one is showing and re-render it when the language changes.
    private string _hintKey = "hint.initial";
    private object?[] _hintArgs = [];

    // Game language found by the last scan (null = not scanned yet). Always detected automatically.
    private string? _detectedGameLang;
    private bool _gameLangSure;

    /// <summary>Native names for the game-language list (keywords.json ids).</summary>
    private static readonly Dictionary<string, string> GameLanguageNames = new()
    {
        ["en"] = "English", ["zh-Hant"] = "繁體中文", ["zh-Hans"] = "简体中文", ["ja"] = "日本語", ["de"] = "Deutsch",
    };

    public MainWindow()
    {
        InitializeComponent();
        ClipList.ItemsSource = _clips;
        _clips.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ClipItem c in e.NewItems)
                    c.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(ClipItem.Keep)) { DrawMarkers(); UpdateButtons(); } };
            if (e.OldItems != null)
                foreach (ClipItem c in e.OldItems) c.Detach();
            Renumber(); DrawMarkers(); UpdateButtons();
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        OutDirBox.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Apex精華");

        try { _cfg = KeywordConfig.Load(); }
        catch (Exception ex) { Log(Loc.F("log.keywordsFail", ex.Message)); }

        if (FFmpegLocator.TryFind(out var dir)) Log(Loc.F("log.ffmpeg", dir));
        else Log(Loc.T("err.ffmpegMissing"));
        Log(Loc.F("log.ocrLangs", string.Join(Loc.T("list.sep"), SafeOcrLanguages())));
        SizeChanged += (_, _) => DrawMarkers();

        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        ApplyLanguage();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += async (_, _) => await OpenFileAsync(args[1]);
    }

    private static IEnumerable<string> SafeOcrLanguages()
    {
        try { return OcrReader.InstalledLanguages(); } catch { return [Loc.T("log.ocrUnknown")]; }
    }

    // ───────────── Language ─────────────

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();

    private void OnLanguageChanged()
    {
        ApplyLanguage();
        Log(Loc.F("log.uiLang", Loc.Languages.First(l => l.Code == Loc.Current).NativeName));
    }

    /// <summary>Re-renders the texts that are built in code (XAML texts update through bindings).</summary>
    private void ApplyLanguage()
    {
        // Tells WPF which glyph variants to use (CJK characters differ between Chinese and Japanese fonts).
        Language = XmlLanguage.GetLanguage(Loc.Current switch { "zh-Hant" => "zh-TW", "ja" => "ja-JP", _ => "en-US" });

        GameLangText.Text = _detectedGameLang == null ? Loc.T("game.auto")
            : Loc.F(_gameLangSure ? "game.detected" : "game.uncertain", GameLanguageNames.GetValueOrDefault(_detectedGameLang, _detectedGameLang));

        if (_info == null) FileText.Text = Loc.T("file.none");
        else
        {
            int track = AudioBox.SelectedIndex;
            FillAudioTracks(_info);
            AudioBox.SelectedIndex = track;
            ExportInfo.Text = DescribeCanvas(_info);
        }
        ListHint.Text = Loc.F(_hintKey, _hintArgs);
        PlayButton.Content = Loc.T(_playing ? "player.pause" : "player.play");
        if (_previewUnsupported) PlayerHint.Text = Loc.T("player.unsupported");
        DrawMarkers();
    }

    private void FillAudioTracks(MediaInfo info)
    {
        AudioBox.Items.Clear();
        foreach (var t in info.AudioTracks)
            AudioBox.Items.Add(t + (t.MeanVolumeDb is { } v ? $"  {v:0} dB" : ""));
        if (info.AudioTracks.Count == 0) AudioBox.Items.Add(Loc.T("exp.noTracks"));
    }

    private static string DescribeCanvas(MediaInfo info)
    {
        var (cw, ch) = Exporter.Canvas(info.Width, info.Height);
        return cw == info.Width && ch == info.Height
            ? Loc.F("exp.native", cw, ch)
            : Loc.F("exp.padded", cw, ch, info.Width, info.Height);
    }

    private void SetHint(string key, params object?[] args)
    {
        _hintKey = key; _hintArgs = args;
        ListHint.Text = Loc.F(key, args);
    }

    // ───────────── Import ─────────────

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("dlg.openTitle"),
            Filter = Loc.T("dlg.filter"),
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
        if (_cts != null) { MessageBox.Show(this, Loc.T("msg.busy"), Title); return; }
        try
        {
            SetBusy(true, Loc.T("st.reading"));
            using var cts = new CancellationTokenSource();
            var info = await MediaInfo.ProbeAsync(path, cts.Token);
            info = await info.WithTrackVolumesAsync(cts.Token);
            _info = info;
            _clips.Clear();
            _detectedGameLang = null;
            GameLangText.Text = Loc.T("game.auto");
            FileText.Text = $"{System.IO.Path.GetFileName(path)}　{info.Width}×{info.Height}　{info.Fps:0.##} fps　{TimeFmt.Hms(info.Duration)}";
            FileText.ToolTip = path;

            FillAudioTracks(info);
            AudioBox.SelectedIndex = Math.Max(0, info.DefaultAudioTrack());
            ExportInfo.Text = DescribeCanvas(info);

            _previewUnsupported = false;
            Player.Source = new Uri(path);
            Player.Play(); Player.Pause();
            _playing = false;
            PlayButton.Content = Loc.T("player.play");
            PlayerHint.Visibility = Visibility.Collapsed;
            Log(Loc.F("log.imported", path, info.Width, info.Height, TimeFmt.Hms(info.Duration), info.AudioTracks.Count));
            SetHint("hint.imported");
            SetBusy(false, Loc.T("st.ready"));
        }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("st.importFailed"));
            Log(Loc.F("log.importFailed", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.T("st.importFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ───────────── Scan ─────────────

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null || _cfg == null) return;
        var opt = new ScanOptions
        {
            Language = "auto",
            AudioTrack = Math.Max(0, AudioBox.SelectedIndex),
        };
        _cts = new CancellationTokenSource();
        SetBusy(true, Loc.T("st.scanning"));
        PausePlayer();
        var progress = new Progress<ScanProgress>(p => { Progress.Value = p.Fraction; StatusText.Text = p.Message; });
        try
        {
            Log(Loc.F("log.scanStart", opt.SampleFps, opt.OcrWidth, opt.Workers, opt.Language));
            var result = await Task.Run(() => new Scanner(_cfg).ScanAsync(_info, opt, progress, _cts.Token));
            string reportPath = "";
            try { reportPath = ScanReport.Save(result).Text; } catch (Exception ex) { Log(Loc.F("log.reportFail", ex.Message)); }

            foreach (var c in _clips.Where(c => c.IsAuto).ToList()) _clips.Remove(c);
            foreach (var c in result.Candidates) _clips.Add(ClipItem.FromCandidate(c));
            SortClips();

            _detectedGameLang = result.Language;
            _gameLangSure = result.LanguageConfident;
            ApplyLanguage();
            Log(Loc.F("log.scanDone", result.ScanSeconds, result.ScanSeconds > 0 ? result.Duration / result.ScanSeconds : 0, result.Language, result.OcrLanguage));
            if (result.PromptPosition != null) Log(result.PromptPosition);
            foreach (var n in result.Notes) Log(Loc.F("rep.note", n));
            Log(Loc.F("log.summary", result.Events.Count, result.Clusters.Count, result.Candidates.Count));
            foreach (var c in result.Clusters.Where(c => !c.Accepted))
                Log(Loc.F("log.skipped", TimeFmt.Hms(c.Start), TimeFmt.Hms(c.End), c.RejectReason));
            if (reportPath.Length > 0) Log(Loc.F("log.report", reportPath));

            if (result.Candidates.Count == 0) SetHint("hint.none");
            else SetHint("hint.found", result.Candidates.Count);
            SetBusy(false, Loc.F("st.scanDone", result.Candidates.Count));
            if (_clips.Count > 0) ClipList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { SetBusy(false, Loc.T("st.cancelled")); Log(Loc.T("log.scanCancelled")); }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("st.scanFailed"));
            Log(Loc.F("log.scanFailed", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.T("st.scanFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ───────────── Export ─────────────

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var items = _clips.Where(c => c.Keep).ToList();
        if (items.Count == 0) { MessageBox.Show(this, Loc.T("msg.noneTicked"), Title); return; }
        var bad = items.FirstOrDefault(c => c.End - c.Start < 1);
        if (bad != null) { MessageBox.Show(this, Loc.F("msg.tooShort", bad.Number), Title); return; }

        var outDir = OutDirBox.Text.Trim();
        try { Directory.CreateDirectory(outDir); }
        catch (Exception ex) { MessageBox.Show(this, Loc.F("msg.cantCreateDir", ex.Message), Title); return; }

        var opt = new ExportOptions { AudioTrack = Math.Max(0, AudioBox.SelectedIndex) };
        _cts = new CancellationTokenSource();
        SetBusy(true, Loc.T("st.exporting"));
        PausePlayer();
        try
        {
            bool nvenc = await Exporter.NvencAvailableAsync(_cts.Token);
            Log(Loc.F("log.exportStart", items.Count, Loc.T(nvenc ? "exp.encNvenc" : "exp.encX264"), opt.AudioTrack + 1));
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
                    StatusText.Text = Loc.F("st.exportProgress", idx, items.Count, p);
                });
                var sw = Stopwatch.StartNew();
                c.SetStatus(ClipStatus.Exporting);
                var file = await Task.Run(() => Exporter.ExportAsync(_info, c.Start, c.End, outPath, opt, prog, _cts.Token));
                StatusText.Text = Loc.F("st.checking", idx, items.Count);
                var check = await Task.Run(() => Exporter.VerifyAsync(file, c.End - c.Start, _cts.Token));
                c.SetStatus(check.Ok ? ClipStatus.Done : ClipStatus.Problem, System.IO.Path.GetFileName(file));
                Log(Loc.F("log.exportOne", c.Number, file, sw.Elapsed.TotalSeconds, check.Summary));
            }
            SetBusy(false, Loc.F("st.exportDone", items.Count));
            Log(Loc.F("log.exportDone", outDir));
        }
        catch (OperationCanceledException)
        {
            foreach (var c in items.Where(c => c.Status == Loc.T("clip.exporting"))) c.SetStatus(ClipStatus.None);
            SetBusy(false, Loc.T("st.cancelled"));
            Log(Loc.T("log.exportCancelled"));
        }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("st.exportFailed"));
            Log(Loc.F("log.exportFailed", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.T("st.exportFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        var dlg = new OpenFolderDialog { Title = Loc.T("dlg.folderTitle"), InitialDirectory = Directory.Exists(OutDirBox.Text) ? OutDirBox.Text : null };
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
        var c = ClipItem.Manual(Math.Max(0, pos - 15), Math.Min(_info.Duration, pos + 15), pos);
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
        _previewUnsupported = true;
        PlayerHint.Text = Loc.T("player.unsupported");
        PlayerHint.Visibility = Visibility.Visible;
        Log(Loc.F("log.previewFailed", e.ErrorException?.Message));
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e) => PausePlayer();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) PausePlayer();
        else { _stopAt = null; Player.Play(); _playing = true; PlayButton.Content = Loc.T("player.pause"); }
    }

    private void PlayRange(double from, double to)
    {
        SeekTo(from);
        _stopAt = to;
        Player.Play();
        _playing = true;
        PlayButton.Content = Loc.T("player.pause");
    }

    private void PausePlayer()
    {
        Player.Pause();
        _playing = false;
        _stopAt = null;
        PlayButton.Content = Loc.T("player.play");
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
