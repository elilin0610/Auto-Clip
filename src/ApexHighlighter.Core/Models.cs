namespace ApexHighlighter.Core;

/// <summary>One OCR line that matched a rule in one sampled frame.</summary>
public sealed record PromptHit(double Time, EventKind Kind, string Text, string Rest, double X, double Y, double W, double H);

/// <summary>A deduplicated on-screen prompt (the same prompt stays up for ~2–3 s and spans several samples).</summary>
public sealed record GameEvent(double Time, double LastSeen, EventKind Kind, string Text, int Samples);

public enum RejectKind { None, LowScore, AssistOnly, NotTopN }

/// <summary>One fight. Text properties are computed in the current interface language.</summary>
public sealed class Candidate
{
    public double Start { get; set; }
    public double End { get; set; }
    public double FirstEvent { get; init; }
    public double LastEvent { get; init; }
    public double Score { get; init; }
    public List<GameEvent> Events { get; init; } = [];
    /// <summary>Seconds between clip start and the first prompt.</summary>
    public double LeadIn { get; init; }
    public bool LeadInFromAudio { get; init; }
    public RejectKind Reject { get; init; }
    public double Threshold { get; init; }
    public int TopN { get; init; }

    public bool Accepted => Reject == RejectKind.None;
    public string Reason => DescribeEvents(Events);
    public string StartNote => Loc.F(LeadInFromAudio ? "start.audio" : "start.fixed", LeadIn);

    public string? RejectReason => Reject switch
    {
        RejectKind.LowScore => Loc.F("reject.lowScore", Score, Threshold, Reason),
        RejectKind.AssistOnly => Loc.F("reject.assistOnly", Reason),
        RejectKind.NotTopN => Loc.F("reject.notTop", Score, TopN),
        _ => null,
    };

    /// <summary>"Knock ×2, Elimination" in the current language.</summary>
    public static string DescribeEvents(IEnumerable<GameEvent> events) =>
        string.Join(Loc.T("list.sep"), events.GroupBy(e => e.Kind).OrderBy(k => k.Key)
            .Select(k => k.Count() > 1 ? $"{k.Key.Name()} ×{k.Count()}" : k.Key.Name()));
}

public sealed class ScanOptions
{
    /// <summary>Samples per second fed to OCR. Prompts stay up ~2–3 s, so 2 fps sees each one several times.</summary>
    public double SampleFps { get; set; } = 2;
    /// <summary>Width the frame is scaled to before OCR.</summary>
    public int OcrWidth { get; set; } = 1280;
    /// <summary>"auto" or a language id from keywords.json (en, zh-Hant…).</summary>
    public string Language { get; set; } = "auto";
    public int Workers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public bool HardwareDecode { get; set; } = true;
    /// <summary>Audio track used to estimate when the fight started.</summary>
    public int AudioTrack { get; set; } = 0;

    /// <summary>Events closer than this are the same fight.</summary>
    public double MergeGap { get; set; } = 25;
    public double MinPreRoll { get; set; } = 8;
    public double MaxPreRoll { get; set; } = 25;
    public double PostRoll { get; set; } = 6;
    /// <summary>Minimum cluster score (Knock 1, Elimination 1.5, Squad wipe 2.5…). Below this the fight is not suggested.</summary>
    public double MinScore { get; set; } = 2.0;
    public int MaxCandidates { get; set; } = 3;
}

public sealed class ScanProgress
{
    public double Fraction { get; init; }
    public string Message { get; init; } = "";
    /// <summary>Prompts read so far (raw samples, once the game language is locked), so the UI can show them during the scan.</summary>
    public IReadOnlyList<PromptHit>? Hits { get; init; }
}

public sealed class ScanResult
{
    public string File { get; init; } = "";
    public double Duration { get; init; }
    public string Resolution { get; init; } = "";
    public double ScanSeconds { get; set; }
    public int FramesScanned { get; set; }
    public string? Language { get; set; }
    public string? OcrLanguage { get; set; }
    /// <summary>False when the match had too few prompts/HUD text to be sure which game language it is.</summary>
    public bool LanguageConfident { get; set; }
    public bool LanguageVerified { get; set; }
    /// <summary>Median center of knock/elimination prompts, as a fraction of the frame (null if none were seen).</summary>
    public double? PromptX { get; set; }
    public double? PromptY { get; set; }
    public string? PromptPosition => PromptX is { } x && PromptY is { } y ? Loc.F("position", x, y) : null;
    public int SpectatingFramesSkipped { get; set; }
    public List<PromptHit> Hits { get; set; } = [];
    public List<GameEvent> Events { get; set; } = [];
    /// <summary>All fights found, including those below threshold (kept for reviewing misses).</summary>
    public List<Candidate> Clusters { get; set; } = [];
    /// <summary>At most MaxCandidates accepted fights, in time order.</summary>
    public List<Candidate> Candidates { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}
