namespace ApexHighlighter.Core;

// =====================================================================================
// Models.cs — 各處共用的資料型別（只裝資料，幾乎沒有邏輯）
//
//   PromptHit    OCR 在某一張畫面讀到的一行提示（還沒去重複）
//   GameEvent    去重複後的一個提示事件（例如 00:19 擊倒）
//   Candidate    一場交戰 = 一個候選片段（起訖時間、分數、包含哪些事件、沒選上的原因）
//   ScanOptions  掃描參數（fps、幾個 worker、分群間隔、最多幾段…）
//   ScanProgress 掃描進度（回報給介面用）
//   ScanResult   掃描的完整結果
// =====================================================================================

/// <summary>One OCR line that matched a rule in one sampled frame.</summary>
public sealed record PromptHit(double Time, EventKind Kind, string Text, string Rest, double X, double Y, double W, double H);

/// <summary>A deduplicated on-screen prompt (the same prompt stays up for ~2–3 s and spans several samples).</summary>
public sealed record GameEvent(double Time, double LastSeen, EventKind Kind, string Text, int Samples);

public enum RejectKind { None, LowScore, AssistOnly, NotTopN, Unconfirmed }

/// <summary>
/// Result of the second look at one fight (<see cref="CandidateVerifier"/>): prompts seen in too few samples are read again
/// at a higher frame rate, and only the ones seen again count.
/// </summary>
/// <param name="Rechecked">Events that were weakly seen and read again.</param>
/// <param name="Confirmed">Events that count after the check (strong ones plus rechecked ones seen again).</param>
/// <param name="Total">Events in the fight.</param>
/// <param name="ScoreAfter">Score counting only confirmed events, each at full weight.</param>
/// <param name="Frames">Frames OCR'd during the recheck.</param>
/// <param name="Reread">False when the frames could not be read again, so the first decision was kept.</param>
public sealed record CandidateCheck(int Rechecked, int Confirmed, int Total, double ScoreAfter, int Frames, bool Reread);

/// <summary>Keeps a clip's start before its end (at least <see cref="MinLength"/> apart) and inside the recording.</summary>
public static class ClipRange
{
    public const double MinLength = 1.0;

    public static double ClampStart(double t, double end, double duration) =>
        Math.Clamp(t, 0, Math.Max(0, Math.Min(end, duration) - MinLength));

    public static double ClampEnd(double t, double start, double duration) =>
        Math.Clamp(t, Math.Min(Math.Max(0, start) + MinLength, duration), Math.Max(0, duration));
}

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
    public RejectKind Reject { get; set; }
    /// <summary>Second look at this fight, or null if it was not checked.</summary>
    public CandidateCheck? Check { get; set; }
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
        RejectKind.Unconfirmed => Loc.F("reject.unconfirmed", Check?.Confirmed ?? 0, Check?.Total ?? Events.Count, Check?.ScoreAfter ?? 0, Threshold),
        _ => null,
    };

    /// <summary>Events whose prompt appeared inside [start, end], e.g. after the user trimmed the clip.</summary>
    public List<GameEvent> EventsWithin(double start, double end) =>
        Events.Where(e => e.Time >= start - 0.05 && e.Time <= end + 0.05).ToList();

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
    /// <summary>
    /// With Language = "auto": the game language found last time (keywords.json id), or null. It only needs a little
    /// evidence to win, so a match with few prompts is not OCR'd with every installed language for its whole length.
    /// </summary>
    public string? PreferredLanguage { get; set; }
    /// <summary>
    /// If the language is still undecided after this many frames, the best one so far is taken (the preferred one on a tie),
    /// so a match with almost no text costs at most this many multi-language frames. 0 = never force.
    /// </summary>
    public int LanguageProbeFrames { get; set; } = 240;
    /// <summary>
    /// Once the language is known and prompts have been seen, OCR only the part of the frame where prompts appeared
    /// (plus a margin), with a full frame every <see cref="FullFrameEvery"/> samples so prompts in new places are still found.
    /// </summary>
    public bool RegionOcr { get; set; } = true;
    public int FullFrameEvery { get; set; } = 4;
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
    /// <summary>
    /// Longest suggested clip in seconds. A longer fight is split at its biggest quiet gap; one that can't be split keeps
    /// only the minimum lead-in. 0 = no limit.
    /// </summary>
    public double MaxClipLength { get; set; } = 120;

    /// <summary>
    /// Second look (produce → verify → redo): before a fight is suggested, prompts seen in fewer than
    /// <see cref="VerifyTrustSamples"/> samples are read again at <see cref="VerifyFps"/>. A fight whose score drops below
    /// <see cref="MinScore"/> is dropped and the next one is checked instead; a near-miss whose weak prompts are confirmed is promoted.
    /// </summary>
    public bool VerifyCandidates { get; set; } = true;
    public double VerifyFps { get; set; } = 6;
    /// <summary>Events seen in at least this many samples are trusted without a recheck.</summary>
    public int VerifyTrustSamples { get; set; } = 3;
    /// <summary>A rechecked event counts when its prompt is read again in at least this many frames.</summary>
    public int VerifyMinFrames { get; set; } = 3;
    /// <summary>Extra fights that may be checked beyond <see cref="MaxCandidates"/> when some fail the check.</summary>
    public int VerifySpare { get; set; } = 2;
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
    /// <summary>Frames where only the prompt area was OCR'd (the rest were read whole).</summary>
    public int RegionFrames { get; set; }
    public List<PromptHit> Hits { get; set; } = [];
    public List<GameEvent> Events { get; set; } = [];
    /// <summary>All fights found, including those below threshold (kept for reviewing misses).</summary>
    public List<Candidate> Clusters { get; set; } = [];
    /// <summary>At most MaxCandidates accepted fights, in time order.</summary>
    public List<Candidate> Candidates { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}
