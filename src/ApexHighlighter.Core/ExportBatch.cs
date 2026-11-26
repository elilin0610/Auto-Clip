namespace ApexHighlighter.Core;

// =====================================================================================
// ExportBatch.cs — 一次匯出好幾段
//
// 一段一段依序匯出。某一段失敗（例如 ffmpeg 出錯、磁碟滿了）只記下錯誤，繼續匯出下一段；
// 只有使用者按「取消」才會整批停下。介面和 CLI 都可以用。
// =====================================================================================

public sealed record ExportJob(int Number, double Start, double End);

public sealed record ExportJobResult(ExportJob Job, string? File, ExportCheck? Check, Exception? Error)
{
    /// <summary>Exported and passed every check.</summary>
    public bool Ok => Error == null && Check is { Ok: true };
}

public static class ExportBatch
{
    /// <summary>
    /// Runs <paramref name="runOne"/> for every job in order. A failing job is recorded and the next one still runs;
    /// only cancellation stops the batch (and is rethrown).
    /// </summary>
    public static async Task<List<ExportJobResult>> RunAsync(
        IReadOnlyList<ExportJob> jobs,
        Func<ExportJob, IProgress<double>, CancellationToken, Task<(string File, ExportCheck Check)>> runOne,
        Action<int, ExportJob>? onStart,
        Action<int, ExportJobResult>? onDone,
        IProgress<double>? overall,
        CancellationToken ct)
    {
        var results = new List<ExportJobResult>(jobs.Count);
        for (int i = 0; i < jobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var job = jobs[i];
            int idx = i;
            onStart?.Invoke(i, job);
            var prog = new Progress<double>(p => overall?.Report((idx + Math.Clamp(p, 0, 1)) / jobs.Count));
            ExportJobResult r;
            try
            {
                var (file, check) = await runOne(job, prog, ct);
                r = new ExportJobResult(job, file, check, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { r = new ExportJobResult(job, null, null, ex); }
            results.Add(r);
            onDone?.Invoke(i, r);
        }
        overall?.Report(1);
        return results;
    }
}
