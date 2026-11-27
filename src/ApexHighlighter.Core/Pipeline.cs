using System.Threading.Channels;

namespace ApexHighlighter.Core;

// =====================================================================================
// Pipeline.cs — 多支錄影的兩段式流水線（graph：掃描 → 剪片）
//
// 一次處理好幾支錄影時，把工作拆成兩站：
//   第 1 站「掃描」（OCR，吃 CPU）    同時最多 workers1 支
//   第 2 站「剪片」（NVENC，吃 GPU）  同時最多 workers2 支
// 一支掃完就交給第 2 站，第 1 站馬上接著掃下一支。所以第 2 支在掃描時，第 1 支已經在剪了，
// CPU 和 GPU 都不會閒著。
// 某一支失敗只影響那一支（交給 onError 產生結果），其他照跑；只有取消會整批停下。
// 結果依輸入的順序回傳。CLI 的 batch 指令用它，邏輯本身不碰 FFmpeg，可以直接做單元測試。
// =====================================================================================

public static class Pipeline
{
    public static async Task<IReadOnlyList<TOut>> RunAsync<TIn, TMid, TOut>(
        IReadOnlyList<TIn> inputs,
        Func<TIn, CancellationToken, Task<TMid>> stage1, int workers1,
        Func<TIn, TMid, CancellationToken, Task<TOut>> stage2, int workers2,
        Func<TIn, Exception, TOut> onError,
        CancellationToken ct)
    {
        var results = new TOut[inputs.Count];
        var todo = Channel.CreateUnbounded<int>();
        foreach (var i in Enumerable.Range(0, inputs.Count)) todo.Writer.TryWrite(i);
        todo.Writer.Complete();
        // Bounded so stage 1 can't run far ahead of stage 2 and pile up finished scans.
        var handoff = Channel.CreateBounded<(int Index, TMid Mid)>(Math.Max(1, workers2));

        async Task Stage1Worker()
        {
            await foreach (var i in todo.Reader.ReadAllAsync(ct))
            {
                TMid mid;
                try { mid = await stage1(inputs[i], ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { results[i] = onError(inputs[i], ex); continue; }
                await handoff.Writer.WriteAsync((i, mid), ct);
            }
        }

        async Task Stage2Worker()
        {
            await foreach (var (i, mid) in handoff.Reader.ReadAllAsync(ct))
            {
                try { results[i] = await stage2(inputs[i], mid, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { results[i] = onError(inputs[i], ex); }
            }
        }

        var s2 = Enumerable.Range(0, Math.Max(1, workers2)).Select(_ => Task.Run(Stage2Worker, ct)).ToArray();
        var s1 = Enumerable.Range(0, Math.Max(1, workers1)).Select(_ => Task.Run(Stage1Worker, ct)).ToArray();
        try { await Task.WhenAll(s1); }
        finally { handoff.Writer.TryComplete(); }
        await Task.WhenAll(s2);
        return results;
    }
}
