using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using ApexHighlighter.Core;
using Microsoft.Win32;

namespace ApexHighlighter.App;

// =====================================================================================
// MainWindow.xaml.cs — 主視窗的所有行為（畫面長相在 MainWindow.xaml）
//
// 區塊（搜尋「// ─────────────」可以一段一段跳）：
//   建構與設定        讀設定、還原視窗位置、填下拉選單、啟動 OBS 監聽
//   匯入              按鈕 / 拖放檔案 → MediaInfo 讀資訊 → 載入播放器
//   掃描              RunScanAsync：呼叫 Scanner，掃描中即時畫黃色刻度，掃完載入縮圖
//   片段清單          勾選、選取、微調起訖（±1 秒、設為目前位置）、手動新增
//   播放器與時間軸    播放 / 暫停 / 跳轉、DrawMarkers 畫片段色塊和刻度、拖曳把手改起訖
//   匯出              依勾選的片段逐一呼叫 Exporter，再 VerifyAsync 檢查
//   OBS               錄影停止 → 自動匯入並掃描；正在忙就排隊等
//
// WPF 小提醒：只有 UI 執行緒能改畫面，背景工作要用 Dispatcher 切回來。
// =====================================================================================

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

    // Prompt positions shown as ticks on the timeline: live while scanning, then the deduplicated events.
    private List<(double Time, EventKind Kind)> _ticks = [];
    // Which clip edge is being dragged on the timeline ("s" or "e"), if any.
    private string? _dragEdge;
    private bool _fillingFormats;

    private ObsWatcher? _obs;
    private string? _pendingObsPath;

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

        var st = App.Settings;
        OutDirBox.Text = !string.IsNullOrWhiteSpace(st.OutputDir) ? st.OutputDir
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Apex精華");
        RestorePlacement();
        Closing += (_, _) => SaveSettings();
        Closed += async (_, _) => await StopObsAsync();

        try { _cfg = KeywordConfig.Load(); }
        catch (Exception ex) { Log(Loc.F("log.keywordsFail", ex.Message)); }

        if (FFmpegLocator.TryFind(out var dir)) Log(Loc.F("log.ffmpeg", dir));
        else Log(Loc.T("err.ffmpegMissing"));
        Log(Loc.F("log.ocrLangs", string.Join(Loc.T("list.sep"), SafeOcrLanguages())));
        SizeChanged += (_, _) => DrawMarkers();

        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        ApplyLanguage();
        FormatBox.SelectedIndex = (int)st.ExportFormat;
        RestartObs();

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += async (_, _) => await OpenFileAsync(args[1]);
    }

    private static IEnumerable<string> SafeOcrLanguages()
    {
        try { return OcrReader.InstalledLanguages(); } catch { return [Loc.T("log.ocrUnknown")]; }
    }

    // ───────────── 語言：OCR 可用語言、開設定視窗 ─────────────

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        var before = (s.ObsEnabled, s.ObsHost, s.ObsPort, s.ObsPasswordProtected);
        new SettingsWindow { Owner = this }.ShowDialog();
        if (before != (s.ObsEnabled, s.ObsHost, s.ObsPort, s.ObsPasswordProtected)) RestartObs();
    }

    // ───────────── 記住的設定：視窗位置、輸出資料夾 ─────────────

    private void RestorePlacement()
    {
        var s = App.Settings;
        if (s.WindowWidth is not { } w || s.WindowHeight is not { } h || s.WindowLeft is not { } l || s.WindowTop is not { } t) return;
        // Only restore if the window would still be on a screen (a monitor may have been unplugged).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!screen.IntersectsWith(new Rect(l + 40, t + 10, Math.Max(1, w - 80), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l; Top = t; Width = Math.Max(MinWidth, w); Height = Math.Max(MinHeight, h);
        if (s.WindowMaximized) Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void SaveSettings()
    {
        var s = App.Settings;
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!r.IsEmpty) { s.WindowLeft = r.Left; s.WindowTop = r.Top; s.WindowWidth = r.Width; s.WindowHeight = r.Height; }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.OutputDir = OutDirBox.Text.Trim();
        s.ExportFormat = SelectedFormat;
        s.Save();
    }

    private void OutDirBox_LostFocus(object sender, RoutedEventArgs e)
    {
        App.Settings.OutputDir = OutDirBox.Text.Trim();
        App.Settings.Save();
    }

    // ───────────── 匯出格式（橫式 / 直式裁中間 / 直式完整畫面） ─────────────

    private ExportFormat SelectedFormat => FormatBox.SelectedIndex >= 0 ? (ExportFormat)FormatBox.SelectedIndex : ExportFormat.Landscape;

    private void FillFormats()
    {
        int sel = FormatBox.SelectedIndex;
        _fillingFormats = true;
        FormatBox.Items.Clear();
        foreach (var f in Enum.GetValues<ExportFormat>()) FormatBox.Items.Add(Loc.T("fmt." + f));
        FormatBox.SelectedIndex = sel;
        _fillingFormats = false;
    }

    private void FormatBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingFormats || FormatBox.SelectedIndex < 0) return;
        App.Settings.ExportFormat = SelectedFormat;
        App.Settings.Save();
        if (_info != null) ExportInfo.Text = DescribeOutput(_info);
    }

    // ───────────── OBS：連線、狀態顯示、錄影停止後自動匯入並掃描 ─────────────

    /// <summary>(Re)connects to OBS with the current settings, or stops listening if it's turned off.</summary>
    private void RestartObs()
    {
        _ = StopObsAsync();
        var s = App.Settings;
        if (!s.ObsEnabled) { ObsText.Visibility = Visibility.Collapsed; return; }

        var w = new ObsWatcher(s.ObsHost, s.ObsPort, s.GetObsPassword());
        _obs = w;
        w.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            if (_obs != w) return;
            UpdateObsText();
            if (state is ObsState.Connected or ObsState.AuthFailed) Log(Loc.T("obs.state." + state));
        });
        w.RecordingStopped += path => Dispatcher.BeginInvoke(async () => { if (_obs == w) await OnObsRecordingStoppedAsync(path); });
        ObsText.Visibility = Visibility.Visible;
        UpdateObsText();
        w.Start();
    }

    private async Task StopObsAsync()
    {
        var old = _obs;
        _obs = null;
        if (old != null) await old.DisposeAsync();
    }

    private void UpdateObsText()
    {
        if (_obs == null) return;
        ObsText.Text = Loc.T("obs.state." + _obs.State);
        ObsText.ToolTip = _obs.LastError;
        ObsText.Foreground = new SolidColorBrush(_obs.State switch
        {
            ObsState.Connected => Color.FromRgb(0x7F, 0xD1, 0x8B),
            ObsState.AuthFailed => Color.FromRgb(0xE5, 0x48, 0x4D),
            _ => Color.FromRgb(0x9A, 0xA1, 0xAE),
        });
    }

    /// <summary>OBS finished a recording: import it and scan it, or queue it if something is already running.</summary>
    private async Task OnObsRecordingStoppedAsync(string path)
    {
        if (_cts != null) { _pendingObsPath = path; Log(Loc.F("log.obsBusy", path)); return; }
        await Task.Delay(1500);   // Give OBS a moment to finish writing the file.
        if (!File.Exists(path)) { Log(Loc.F("log.obsMissing", path)); return; }
        Log(Loc.F("log.obsRecording", path));
        await OpenFileAsync(path);
        if (_info != null && string.Equals(_info.Path, path, StringComparison.OrdinalIgnoreCase)) await RunScanAsync();
    }

    private void RunPendingObs()
    {
        if (_pendingObsPath is not { } p) return;
        _pendingObsPath = null;
        Dispatcher.BeginInvoke(async () => await OnObsRecordingStoppedAsync(p));
    }

    // ───────────── 介面語言切換與顯示文字 ─────────────

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

        FillFormats();
        UpdateObsText();
        GameLangText.Text = _detectedGameLang == null ? Loc.T("game.auto")
            : Loc.F(_gameLangSure ? "game.detected" : "game.uncertain", GameLanguageNames.GetValueOrDefault(_detectedGameLang, _detectedGameLang));

        if (_info == null) FileText.Text = Loc.T("file.none");
        else
        {
            int track = AudioBox.SelectedIndex;
            FillAudioTracks(_info);
            AudioBox.SelectedIndex = track;
            ExportInfo.Text = DescribeOutput(_info);
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

    private string DescribeOutput(MediaInfo info)
    {
        var f = SelectedFormat;
        if (f != ExportFormat.Landscape)
            return Loc.F(f == ExportFormat.VerticalCrop ? "exp.vcropInfo" : "exp.vfitInfo", Exporter.VerticalW, Exporter.VerticalH);
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

    // ───────────── 匯入錄影（按鈕或拖放） ─────────────

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
            ExportInfo.Text = DescribeOutput(info);
            _ticks = [];

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

    // ───────────── 掃描：呼叫 Scanner，掃描中即時畫黃色刻度 ─────────────

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private async Task RunScanAsync()
    {
        if (_info == null || _cfg == null || _cts != null) return;
        var opt = new ScanOptions
        {
            Language = "auto",
            AudioTrack = Math.Max(0, AudioBox.SelectedIndex),
        };
        _cts = new CancellationTokenSource();
        SetBusy(true, Loc.T("st.scanning"));
        PausePlayer();
        _ticks = [];
        DrawMarkers();
        var progress = new Progress<ScanProgress>(p =>
        {
            Progress.Value = p.Fraction; StatusText.Text = p.Message;
            if (p.Hits != null)
            {
                _ticks = TicksFromHits(p.Hits);
                SetHint("hint.scanning", _ticks.Count);
                DrawMarkers();
            }
        });
        try
        {
            Log(Loc.F("log.scanStart", opt.SampleFps, opt.OcrWidth, opt.Workers, opt.Language));
            var result = await Task.Run(() => new Scanner(_cfg).ScanAsync(_info, opt, progress, _cts.Token));
            string reportPath = "";
            try { reportPath = ScanReport.Save(result).Text; } catch (Exception ex) { Log(Loc.F("log.reportFail", ex.Message)); }

            foreach (var c in _clips.Where(c => c.IsAuto).ToList()) _clips.Remove(c);
            foreach (var c in result.Candidates) _clips.Add(ClipItem.FromCandidate(c));
            SortClips();
            _ticks = result.Events.Select(ev => (ev.Time, ev.Kind)).ToList();
            DrawMarkers();
            _ = LoadThumbnailsAsync();

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
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); RunPendingObs(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ───────────── 匯出：把勾選的片段逐一剪出並檢查 ─────────────

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

        var opt = new ExportOptions { AudioTrack = Math.Max(0, AudioBox.SelectedIndex), Format = SelectedFormat };
        App.Settings.OutputDir = outDir;
        App.Settings.Save();
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
                var name = Exporter.SuggestName(_info, c.Number, c.Start, c.End, opt.Format);
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
                var check = await Task.Run(() => Exporter.VerifyAsync(file, c.End - c.Start, _cts.Token, opt.Format));
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
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); RunPendingObs(); }
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
        if (dlg.ShowDialog(this) != true) return;
        OutDirBox.Text = dlg.FolderName;
        App.Settings.OutputDir = dlg.FolderName;
        App.Settings.Save();
    }

    private void OpenOut_Click(object sender, RoutedEventArgs e)
    {
        var d = OutDirBox.Text.Trim();
        if (!Directory.Exists(d)) Directory.CreateDirectory(d);
        Process.Start(new ProcessStartInfo("explorer.exe", d) { UseShellExecute = true });
    }

    // ───────────── 片段清單：新增、刪除、選取、微調起訖時間 ─────────────

    private ClipItem? Selected => ClipList.SelectedItem as ClipItem;

    private void AddManual_Click(object sender, RoutedEventArgs e)
    {
        if (_info == null) return;
        var pos = Player.Position.TotalSeconds;
        var c = ClipItem.Manual(Math.Max(0, pos - 15), Math.Min(_info.Duration, pos + 15), pos);
        _clips.Add(c);
        SortClips();
        ClipList.SelectedItem = c;
        _ = LoadThumbnailsAsync();
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

    private static readonly SolidColorBrush TickBrush = Frozen(Color.FromRgb(0xFF, 0xD1, 0x66));
    private static readonly SolidColorBrush HandleBrush = Frozen(Color.FromRgb(0xF2, 0xF3, 0xF5));
    // ───────────── 時間軸：畫片段色塊 / 黃色刻度 / 拖曳把手 ─────────────

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Clip ranges, prompt ticks, and drag handles for the selected clip's start and end.</summary>
    private void DrawMarkers()
    {
        Markers.Children.Clear();
        if (_info == null || _info.Duration <= 0) return;
        double w = Markers.ActualWidth, h = Math.Max(1, Markers.ActualHeight), dur = _info.Duration;
        foreach (var c in _clips)
        {
            var r = new Rectangle
            {
                Width = Math.Max(2, (c.End - c.Start) / dur * w),
                Height = h,
                Fill = new SolidColorBrush(c == Selected ? Color.FromRgb(0xE5, 0x48, 0x4D) : c.Keep ? Color.FromRgb(0x8A, 0x3A, 0x3E) : Color.FromRgb(0x4A, 0x4F, 0x5A)),
                ToolTip = c.Title,
                Tag = c,
                Cursor = Cursors.Hand,
            };
            Canvas.SetLeft(r, c.Start / dur * w);
            Markers.Children.Add(r);
        }
        foreach (var (t, kind) in _ticks)
        {
            var r = new Rectangle { Width = 2, Height = h, Fill = TickBrush, Tag = t, Cursor = Cursors.Hand, ToolTip = $"{TimeFmt.Hms(t)}  {kind.Name()}" };
            Canvas.SetLeft(r, t / dur * w - 1);
            Markers.Children.Add(r);
        }
        if (Selected is { } sel)
        {
            foreach (var (edge, t) in new[] { ("s", sel.Start), ("e", sel.End) })
            {
                var r = new Rectangle
                {
                    Width = 6, Height = h, Fill = HandleBrush, RadiusX = 2, RadiusY = 2, Tag = edge, Cursor = Cursors.SizeWE,
                    ToolTip = Loc.T(edge == "s" ? "tl.dragStart" : "tl.dragEnd"),
                };
                Canvas.SetLeft(r, t / dur * w - 3);
                Markers.Children.Add(r);
            }
        }
    }

    /// <summary>Collapses raw OCR samples (one prompt shows up in several frames) into one tick per prompt.</summary>
    private static List<(double, EventKind)> TicksFromHits(IReadOnlyList<PromptHit> hits)
    {
        var ticks = new List<(double, EventKind)>();
        var last = new Dictionary<EventKind, double>();
        foreach (var hit in hits)
        {
            if (!last.TryGetValue(hit.Kind, out var prev) || hit.Time - prev > 4) ticks.Add((hit.Time, hit.Kind));
            last[hit.Kind] = hit.Time;
        }
        return ticks;
    }

    // Timeline strip: drag the selected clip's handles, click a tick to look at that prompt, click a clip to select it.
    private void Markers_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_info == null) return;
        switch ((e.OriginalSource as FrameworkElement)?.Tag)
        {
            case string edge when Selected != null:
                _dragEdge = edge;
                PausePlayer();
                Markers.CaptureMouse();
                break;
            case double tickTime:
                SeekTo(tickTime - 3);
                break;
            case ClipItem c:
                ClipList.SelectedItem = c;
                break;
            default:
                SeekTo(e.GetPosition(Markers).X / Math.Max(1, Markers.ActualWidth) * _info.Duration);
                break;
        }
        e.Handled = true;
    }

    private void Markers_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEdge == null || Selected is not { } c || _info == null) return;
        var t = Math.Clamp(e.GetPosition(Markers).X / Math.Max(1, Markers.ActualWidth) * _info.Duration, 0, _info.Duration);
        if (_dragEdge == "s") { c.Start = Math.Min(t, c.End - 1); SeekTo(c.Start); }
        else { c.End = Math.Max(t, c.Start + 1); SeekTo(c.End); }
        DrawMarkers();
    }

    private void Markers_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragEdge == null) return;
        _dragEdge = null;
        Markers.ReleaseMouseCapture();
        SortClips();
        DrawMarkers();
    }

    // ───────────── 縮圖 ─────────────

    /// <summary>Fills in preview pictures for clips that don't have one yet.</summary>
    private async Task LoadThumbnailsAsync()
    {
        if (_info == null) return;
        var path = _info.Path;
        foreach (var c in _clips.Where(c => c.Thumbnail == null).ToList())
        {
            byte[]? jpg = null;
            try { jpg = await Thumbnail.GrabJpegAsync(path, c.ThumbTime, 224); } catch { }
            if (jpg == null || _info?.Path != path) continue;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(jpg);
            bmp.EndInit();
            bmp.Freeze();
            c.Thumbnail = bmp;
        }
    }

    // ───────────── 播放器：播放、暫停、跳轉、進度條、快捷鍵 ─────────────

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

    // ───────────── 小工具：忙碌狀態、按鈕啟用、記錄 ─────────────

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
