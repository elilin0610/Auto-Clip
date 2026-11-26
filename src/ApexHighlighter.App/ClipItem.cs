using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;


// =====================================================================================
// ClipItem.cs — 片段清單裡的「一張卡片」
//
// 包一個 Candidate（自動找到的）或手動新增的片段，加上介面需要的東西：
// 勾選、編號、縮圖、匯出狀態、顯示用的文字。
// 實作 INotifyPropertyChanged：屬性一改，畫面上綁定它的地方就自動更新（WPF 資料繫結）。
// =====================================================================================

public enum ClipStatus { None, Exporting, Done, Problem }

/// <summary>One clip in the list: an auto-detected candidate or a manual one. All text follows the current interface language.</summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    private readonly Candidate? _candidate;
    private readonly double _manualCenter;
    private double _start, _end;
    private bool _keep = true;
    private int _number;
    private ClipStatus _status;
    private string _statusFile = "";

    private ClipItem(double start, double end, Candidate? candidate, double manualCenter)
    {
        _start = start; _end = end; _candidate = candidate; _manualCenter = manualCenter;
        Loc.Changed += Refresh;
    }

    public static ClipItem FromCandidate(Candidate c) => new(c.Start, c.End, c, 0);
    public static ClipItem Manual(double start, double end, double center) => new(start, end, null, center);

    public bool IsAuto => _candidate != null;

    /// <summary>Where the preview picture is taken: just after the first prompt appears, or the playhead for a manual clip.</summary>
    public double ThumbTime => _candidate != null ? _candidate.FirstEvent + 0.4 : _manualCenter;

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; Raise(); } }

    public string Reason => _candidate != null ? _candidate.Reason : Loc.T("clip.manual");

    public string Detail => _candidate != null
        ? Loc.F("clip.detail", _candidate.Score,
            string.Join(Loc.T("list.sep"), _candidate.Events.Select(e => $"{TimeFmt.Hms(e.Time)} {e.Kind.Name()}")),
            _candidate.StartNote)
        : Loc.F("clip.manualDetail", TimeFmt.Hms(_manualCenter));

    public string Source => Loc.T(IsAuto ? "clip.auto" : "clip.manualSrc");

    public int Number { get => _number; set { _number = value; Raise(); Raise(nameof(Title)); } }

    public double Start
    {
        get => _start;
        set { _start = Math.Max(0, value); Raise(); RaiseTimes(); }
    }

    public double End
    {
        get => _end;
        set { _end = Math.Max(0, value); Raise(); RaiseTimes(); }
    }

    public bool Keep { get => _keep; set { _keep = value; Raise(); } }

    /// <summary>Moves the start, never past the end (stays at least 1 s long) or outside the recording.</summary>
    public void MoveStart(double t, double duration) => Start = ClipRange.ClampStart(t, End, duration);

    /// <summary>Moves the end, never before the start (stays at least 1 s long) or past the end of the recording.</summary>
    public void MoveEnd(double t, double duration) => End = ClipRange.ClampEnd(t, Start, duration);

    public void SetStatus(ClipStatus status, string file = "")
    {
        _status = status; _statusFile = file;
        Raise(nameof(Status));
    }

    public bool IsExporting => _status == ClipStatus.Exporting;

    public string Status => _status switch
    {
        ClipStatus.Exporting => Loc.T("clip.exporting"),
        ClipStatus.Done => "✔ " + _statusFile,
        ClipStatus.Problem => "⚠ " + _statusFile,
        _ => "",
    };

    public string Title => $"#{Number}  {TimeFmt.Hms(Start)} – {TimeFmt.Hms(End)}";
    public string Length => Loc.F("clip.length", Math.Max(0, End - Start));
    public string StartText => TimeFmt.HmsMs(Start);
    public string EndText => TimeFmt.HmsMs(End);

    private void RaiseTimes()
    {
        Raise(nameof(Title)); Raise(nameof(Length)); Raise(nameof(StartText)); Raise(nameof(EndText));
    }

    private void Refresh()
    {
        foreach (var n in new[] { nameof(Reason), nameof(Detail), nameof(Source), nameof(Status), nameof(Length) }) Raise(n);
    }

    /// <summary>Unhooks the language-change handler when the item leaves the list.</summary>
    public void Detach() => Loc.Changed -= Refresh;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
