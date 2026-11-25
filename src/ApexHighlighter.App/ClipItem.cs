using System.ComponentModel;
using System.Runtime.CompilerServices;
using ApexHighlighter.Core;

namespace ApexHighlighter.App;

/// <summary>One clip in the list: an auto-detected candidate or a manual one.</summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    private double _start, _end;
    private bool _keep = true;
    private int _number;
    private string _status = "";

    public ClipItem(double start, double end, string reason, string detail, bool auto)
    {
        _start = start; _end = end; Reason = reason; Detail = detail; IsAuto = auto;
    }

    public bool IsAuto { get; }
    public string Reason { get; }
    public string Detail { get; }
    public string Source => IsAuto ? "自動偵測" : "手動";

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

    public string Status { get => _status; set { _status = value; Raise(); } }

    public string Title => $"#{Number}  {TimeFmt.Hms(Start)} – {TimeFmt.Hms(End)}";
    public string Length => $"{Math.Max(0, End - Start):0} 秒";
    public string StartText => TimeFmt.HmsMs(Start);
    public string EndText => TimeFmt.HmsMs(End);

    private void RaiseTimes()
    {
        Raise(nameof(Title)); Raise(nameof(Length)); Raise(nameof(StartText)); Raise(nameof(EndText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
