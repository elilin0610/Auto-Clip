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
//   OBS / 佇列        錄影停止或一次拖進好幾支 → 排進 FileQueue，一支一支匯入並掃描（忙的時候不會漏掉任何一支）
//   掃描結果快取      ScanCache：重開同一支錄影直接讀回上次的結果和上次的微調（起訖、勾選、手動片段）；
//                     掃描後改過設定會提醒要重掃
//   匯出的「一段失敗繼續下一段」在 Core 的 ExportBatch；起訖時間的防呆在 Core 的 ClipRange
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
    // Recordings waiting to be imported and scanned (OBS stops while busy, or several files dropped at once).
    private readonly FileQueue _queue = new();

    // The scan the clip list belongs to (new or from the cache), and saving the user's edits back to the cache.
    private ScanResult? _result;
    private bool _clipsDirty, _restoringClips;
    private readonly DispatcherTimer _saveClipsTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

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
        DarkTitleBar.Attach(this);
        ClipList.ItemsSource = _clips;
        _clips.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (ClipItem c in e.NewItems)
                    c.PropertyChanged += (_, a) =>
                    {
                        if (a.PropertyName == nameof(ClipItem.Keep)) { DrawMarkers(); UpdateButtons(); }
                        if (a.PropertyName is nameof(ClipItem.Keep) or nameof(ClipItem.Start) or nameof(ClipItem.End)) MarkClipsDirty();
                    };
            if (e.OldItems != null)
                foreach (ClipItem c in e.OldItems) c.Detach();
            Renumber(); DrawMarkers(); UpdateButtons();
            MarkClipsDirty();
        };
        _saveClipsTimer.Tick += (_, _) => { _saveClipsTimer.Stop(); SaveClipsNow(); };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        var st = App.Settings;
        OutDirBox.Text = !string.IsNullOrWhiteSpace(st.OutputDir) ? st.OutputDir
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Loc.T("dir.default"));
        RestorePlacement();
        Closing += (_, _) => { SaveClipsNow(); SaveSettings(); };
        Closed += async (_, _) => await StopObsAsync();

        try { _cfg = KeywordConfig.Load(); }
        catch (Exception ex) { Log(Loc.F("log.keywordsFail", ex.Message)); }

        // 記下 FFmpeg 從哪裡找到、什麼版本；太舊會提醒更新
        _ = LogFFmpegAsync();
        Log(Loc.F("log.ocrLangs", string.Join(Loc.T("list.sep"), SafeOcrLanguages())));
        SizeChanged += (_, _) => DrawMarkers();

        Loc.Changed += OnLanguageChanged;
        Closed += (_, _) => Loc.Changed -= OnLanguageChanged;
        ApplyLanguage();
        FormatBox.SelectedIndex = (int)st.ExportFormat;
        MixBox.IsChecked = st.MixAllTracks;
        LoudnormBox.IsChecked = st.NormalizeLoudness;
        AudioBox.SelectionChanged += (_, _) => UpdatePreviewTrackHint();
        RestartObs();

        var args = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
        if (args.Count > 0) Loaded += async (_, _) => await OpenManyAsync(args);
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
        s.MixAllTracks = MixBox.IsChecked == true;
        s.NormalizeLoudness = LoudnormBox.IsChecked == true;
        s.Save();
    }

    private void AudioOption_Changed(object sender, RoutedEventArgs e)
    {
        App.Settings.MixAllTracks = MixBox.IsChecked == true;
        App.Settings.NormalizeLoudness = LoudnormBox.IsChecked == true;
        App.Settings.Save();
        UpdatePreviewTrackHint();
    }

    /// <summary>The preview player can only play the file's default track, so say so when the export will sound different.</summary>
    private void UpdatePreviewTrackHint()
    {
        bool differs = _info is { AudioTracks.Count: > 1 } && (AudioBox.SelectedIndex > 0 || MixBox.IsChecked == true);
        PreviewTrackHint.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
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
        w.RecordingRejected += path => Dispatcher.BeginInvoke(() => { if (_obs == w) Log(Loc.F("log.obsRejected", path)); });
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

    /// <summary>OBS finished a recording: queue it, and start on it right away if nothing else is running.</summary>
    private async Task LogFFmpegAsync()
    {
        try { Log(await FFmpegLocator.DescribeAsync()); }
        catch (Exception ex) { Log(Loc.F("log.ffmpeg", ex.Message)); }
    }

    private async Task OnObsRecordingStoppedAsync(string path)
    {
        await Task.Delay(1500);   // Give OBS a moment to finish writing the file.
        if (_cts != null || _queue.Count > 0)
        {
            if (_queue.Enqueue(path)) Log(Loc.F("log.obsBusy", path));
            return;
        }
        Log(Loc.F("log.obsRecording", path));
        await ProcessAsync(path);
    }

    /// <summary>Several recordings at once (drag &amp; drop, file dialog, command line): open one now, scan the rest one by one.</summary>
    private async Task OpenManyAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1 && _cts == null) { await OpenFileAsync(paths[0]); return; }
        foreach (var p in paths) if (_queue.Enqueue(p)) Log(Loc.F("log.queued", p, _queue.Count));
        Log(Loc.F("log.batch", paths.Count));
        if (_cts == null) RunPending();
    }

    /// <summary>Imports a queued recording and scans it, unless its previous scan was loaded from the cache.</summary>
    private async Task ProcessAsync(string path)
    {
        bool scanning = false;
        try
        {
            if (!File.Exists(path)) { Log(Loc.F("log.obsMissing", path)); return; }
            var cached = await OpenFileAsync(path);
            if (!cached && _info != null && string.Equals(_info.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                scanning = true;
                await RunScanAsync();   // its finally block picks up the next queued file
            }
        }
        finally
        {
            if (!scanning) RunPending();
        }
    }

    /// <summary>Starts on the next queued recording, if any and if nothing is running.</summary>
    private void RunPending()
    {
        if (_cts != null || !_queue.TryDequeue(out var p)) return;
        Dispatcher.BeginInvoke(async () => await ProcessAsync(p));
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
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) await OpenManyAsync(dlg.FileNames);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            await OpenManyAsync(files.Where(File.Exists).ToList());
    }

    /// <summary>Loads a recording into the player. Returns true when its previous scan was found and shown.</summary>
    private async Task<bool> OpenFileAsync(string path)
    {
        if (_cts != null)
        {
            if (_queue.Enqueue(path)) Log(Loc.F("log.queued", path, _queue.Count));
            return false;
        }
        try
        {
            SetBusy(true, Loc.T("st.reading"));
            using var cts = new CancellationTokenSource();
            var info = await MediaInfo.ProbeAsync(path, cts.Token);
            info = await info.WithTrackVolumesAsync(cts.Token);
            SaveClipsNow();   // keep the edits made to the previous recording
            _info = info;
            _result = null;
            _restoringClips = true;
            _clips.Clear();
            _restoringClips = false;
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
            UpdatePreviewTrackHint();
            SetBusy(false, Loc.T("st.ready"));

            // Seen this exact file before? Show the previous scan instead of making the user wait for a new one.
            if (ScanCache.TryLoad(path, _cfg == null ? null : ScanCache.Fingerprint(CurrentScanOptions(), _cfg)) is { } prev)
            {
                ShowScanResult(prev.Result, prev.Clips);
                Log(Loc.F("log.cacheLoaded", prev.SavedAt.ToString("yyyy-MM-dd HH:mm")));
                if (prev.Clips != null) Log(Loc.T("log.cacheClips"));
                if (prev.SettingsChanged) Log(Loc.T("log.cacheSettingsChanged"));
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("st.importFailed"));
            Log(Loc.F("log.importFailed", ex.Message));
            // While working through a queue nobody may be watching; don't stop it with a dialog.
            if (_queue.Count == 0) MessageBox.Show(this, ex.Message, Loc.T("st.importFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ───────────── 掃描：呼叫 Scanner，掃描中即時畫黃色刻度 ─────────────

    private async void Scan_Click(object sender, RoutedEventArgs e) => await RunScanAsync();

    private async Task RunScanAsync()
    {
        if (_info == null || _cfg == null || _cts != null) return;
        var st = App.Settings;
        var opt = CurrentScanOptions();
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
            ScanCache.Save(result, ScanCache.Fingerprint(opt, _cfg));
            if (result.LanguageConfident && result.Language != null && st.LastGameLanguage != result.Language)
            {
                st.LastGameLanguage = result.Language;
                st.Save();
            }

            ShowScanResult(result, null);
            if (_clips.Any(c => !c.IsAuto)) MarkClipsDirty();   // clips added by hand survive a rescan; remember them with it
            Log(Loc.F("log.scanDone", result.ScanSeconds, result.ScanSeconds > 0 ? result.Duration / result.ScanSeconds : 0, result.Language, result.OcrLanguage));
            if (result.PromptPosition != null) Log(result.PromptPosition);
            foreach (var n in result.Notes) Log(Loc.F("rep.note", n));
            Log(Loc.F("log.summary", result.Events.Count, result.Clusters.Count, result.Candidates.Count));
            foreach (var c in result.Clusters.Where(c => !c.Accepted))
                Log(Loc.F("log.skipped", TimeFmt.Hms(c.Start), TimeFmt.Hms(c.End), c.RejectReason));
            if (reportPath.Length > 0) Log(Loc.F("log.report", reportPath));
            SetBusy(false, Loc.F("st.scanDone", result.Candidates.Count));
        }
        catch (OperationCanceledException) { SetBusy(false, Loc.T("st.cancelled")); Log(Loc.T("log.scanCancelled")); }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("st.scanFailed"));
            Log(Loc.F("log.scanFailed", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.T("st.scanFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); RunPending(); }
    }

    /// <summary>The scan settings from the settings window (also what the cache fingerprint is made of).</summary>
    private ScanOptions CurrentScanOptions()
    {
        var st = App.Settings;
        return new ScanOptions
        {
            Language = "auto",
            PreferredLanguage = st.LastGameLanguage,
            AudioTrack = Math.Max(0, AudioBox.SelectedIndex),
            MaxCandidates = Math.Max(1, st.MaxCandidates),
            MinScore = st.MinScore,
            MaxClipLength = st.MaxClipLength,
        };
    }

    /// <summary>
    /// Puts a scan result (new or from the cache) on screen: clips, timeline ticks, detected game language.
    /// <paramref name="saved"/> is the clip list as the user left it last time; null = the scan's suggestions
    /// (clips added by hand stay).
    /// </summary>
    private void ShowScanResult(ScanResult result, IReadOnlyList<SavedClip>? saved)
    {
        _restoringClips = true;
        try
        {
            if (saved != null)
            {
                _clips.Clear();
                foreach (var s in saved)
                {
                    ClipItem item;
                    if (s.Candidate is int i && i >= 0 && i < result.Candidates.Count) item = ClipItem.FromCandidate(result.Candidates[i]);
                    else if (s.Candidate == null) item = ClipItem.Manual(s.Start, s.End, s.ManualCenter);
                    else continue;   // points at a suggestion that no longer exists
                    item.Start = s.Start; item.End = s.End; item.Keep = s.Keep;
                    _clips.Add(item);
                }
            }
            else
            {
                foreach (var c in _clips.Where(c => c.IsAuto).ToList()) _clips.Remove(c);
                foreach (var c in result.Candidates) _clips.Add(ClipItem.FromCandidate(c));
            }
            _result = result;
            SortClips();
        }
        finally { _restoringClips = false; }
        _ticks = result.Events.Select(ev => (ev.Time, ev.Kind)).ToList();
        DrawMarkers();
        _ = LoadThumbnailsAsync();

        _detectedGameLang = result.Language;
        _gameLangSure = result.LanguageConfident;
        ApplyLanguage();
        if (result.Candidates.Count == 0) SetHint("hint.none");
        else SetHint("hint.found", result.Candidates.Count);
        if (_clips.Count > 0) ClipList.SelectedIndex = 0;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ───────────── 記住微調：起訖、勾選、手動片段存回掃描快取 ─────────────

    /// <summary>The clip list changed by the user: save it shortly (several quick edits make one write).</summary>
    private void MarkClipsDirty()
    {
        if (_restoringClips || _result == null || _info == null) return;
        _clipsDirty = true;
        _saveClipsTimer.Stop();
        _saveClipsTimer.Start();
    }

    private void SaveClipsNow()
    {
        if (!_clipsDirty || _result is not { } result || _info == null) return;
        _clipsDirty = false;
        _saveClipsTimer.Stop();
        var list = new List<SavedClip>();
        foreach (var c in _clips)
        {
            int i = c.Candidate == null ? -1 : result.Candidates.IndexOf(c.Candidate);
            // An auto clip whose suggestion isn't in this result is kept as a hand-made one, so it isn't lost.
            list.Add(i >= 0
                ? new SavedClip(c.Start, c.End, c.Keep, i, 0)
                : new SavedClip(c.Start, c.End, c.Keep, null, c.IsAuto ? c.ThumbTime : c.ManualCenter));
        }
        ScanCache.SaveClips(_info.Path, list);
    }

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

        var opt = new ExportOptions
        {
            AudioTrack = Math.Max(0, AudioBox.SelectedIndex), Format = SelectedFormat,
            MixAllTracks = MixBox.IsChecked == true, NormalizeLoudness = LoudnormBox.IsChecked == true,
        };
        var info = _info;
        App.Settings.OutputDir = outDir;
        App.Settings.Save();
        _cts = new CancellationTokenSource();
        SetBusy(true, Loc.T("st.exporting"));
        PausePlayer();
        try
        {
            var ct = _cts.Token;
            bool nvenc = await Exporter.NvencAvailableAsync(ct);
            Log(Loc.F("log.exportStart", items.Count, Loc.T(nvenc ? "exp.encNvenc" : "exp.encX264"), opt.AudioTrack + 1));
            var jobs = items.Select(c => new ExportJob(c.Number, c.Start, c.End)).ToList();
            var sw = new Stopwatch();
            int current = 0;
            var overall = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = Loc.F("st.exportProgress", current + 1, items.Count, Math.Clamp(p * items.Count - current, 0, 1));
            });
            // 一段失敗只記下來，繼續匯出下一段（ExportBatch）；按取消才整批停
            var results = await ExportBatch.RunAsync(jobs,
                async (job, prog, token) =>
                {
                    var outPath = UniquePath(System.IO.Path.Combine(outDir, Exporter.SuggestName(info, job.Number, job.Start, job.End, opt.Format)));
                    var file = await Task.Run(() => Exporter.ExportAsync(info, job.Start, job.End, outPath, opt, prog, token));
                    await Dispatcher.InvokeAsync(() => StatusText.Text = Loc.F("st.checking", current + 1, items.Count));
                    var check = await Task.Run(() => Exporter.VerifyAsync(file, job.End - job.Start, token, opt.Format));
                    return (file, check);
                },
                (i, _) => { current = i; sw.Restart(); items[i].SetStatus(ClipStatus.Exporting); },
                (i, r) =>
                {
                    var c = items[i];
                    if (r.Error != null)
                    {
                        c.SetStatus(ClipStatus.Problem, r.Error.Message.Split('\n')[0]);
                        Log(Loc.F("log.exportOneFailed", c.Number, r.Error.Message));
                    }
                    else
                    {
                        c.SetStatus(r.Check!.Ok ? ClipStatus.Done : ClipStatus.Problem, System.IO.Path.GetFileName(r.File!));
                        Log(Loc.F("log.exportOne", c.Number, r.File, sw.Elapsed.TotalSeconds, r.Check.Summary));
                    }
                },
                overall, ct);
            int failed = results.Count(r => r.Error != null);
            SetBusy(false, failed == 0 ? Loc.F("st.exportDone", items.Count) : Loc.F("st.exportPartial", items.Count - failed, items.Count, failed));
            Log(Loc.F("log.exportDone", outDir));
        }
        catch (OperationCanceledException)
        {
            foreach (var c in items.Where(c => c.IsExporting)) c.SetStatus(ClipStatus.None);
            SetBusy(false, Loc.T("st.cancelled"));
            Log(Loc.T("log.exportCancelled"));
        }
        catch (Exception ex)
        {
            SetBusy(false, Loc.T("st.exportFailed"));
            Log(Loc.F("log.exportFailed", ex.Message));
            MessageBox.Show(this, ex.Message, Loc.T("st.exportFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _cts?.Dispose(); _cts = null; UpdateButtons(); RunPending(); }
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
            if (start) c.MoveStart(t, _info.Duration); else c.MoveEnd(t, _info.Duration);
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
        if (parts[0] == "s") { c.MoveStart(c.Start + delta, _info.Duration); SeekTo(c.Start); }
        else { c.MoveEnd(c.End + delta, _info.Duration); SeekTo(c.End); }
        DrawMarkers();
    }

    private void SetHere_Click(object sender, RoutedEventArgs e) => SetHere((string)((Button)sender).Tag == "s");

    private void SetHere(bool start)
    {
        if (Selected is not { } c || _info == null) return;
        var pos = Player.Position.TotalSeconds;
        if (start) c.MoveStart(pos, _info.Duration); else c.MoveEnd(pos, _info.Duration);
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
        if (_dragEdge == "s") { c.MoveStart(t, _info.Duration); SeekTo(c.Start); }
        else { c.MoveEnd(t, _info.Duration); SeekTo(c.End); }
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
