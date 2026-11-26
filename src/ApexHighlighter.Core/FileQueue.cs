namespace ApexHighlighter.Core;

// =====================================================================================
// FileQueue.cs — 等著要掃描的錄影
//
// OBS 錄影停止、或一次拖進好幾支錄影時，正在忙的話就排進來，做完一支再拿下一支。
// 以前只記得「最後一支」，忙的時候連續停兩次錄影，前一支就會被蓋掉；現在每一支都會排隊。
// 同一個檔案排兩次只算一次。
// =====================================================================================

/// <summary>First-in first-out list of recordings waiting to be scanned. Thread-safe; the same path is only queued once.</summary>
public sealed class FileQueue
{
    private readonly LinkedList<string> _items = new();
    private readonly object _gate = new();

    public int Count { get { lock (_gate) return _items.Count; } }

    /// <summary>Adds the file unless it is already waiting. Returns false if it was a duplicate.</summary>
    public bool Enqueue(string path)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            if (_items.Any(p => string.Equals(Normalize(p), full, StringComparison.OrdinalIgnoreCase))) return false;
            _items.AddLast(path);
            return true;
        }
    }

    public bool TryDequeue(out string path)
    {
        lock (_gate)
        {
            if (_items.First is { } n) { _items.RemoveFirst(); path = n.Value; return true; }
        }
        path = "";
        return false;
    }

    public IReadOnlyList<string> Snapshot() { lock (_gate) return _items.ToList(); }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}
