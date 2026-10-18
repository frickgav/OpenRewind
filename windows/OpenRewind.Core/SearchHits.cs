namespace OpenRewind;

/// One match from screen text or a transcript, placed on the timeline.
public record SearchHit(string Id, bool IsAudio, DateTime Time, string Title, string Snippet, long? ChunkId);

public static class SearchHits
{
    /// Screen and speech matches for the timeline's search box, newest first.
    public static List<SearchHit> Find(Database db, string q)
    {
        var s = new SearchQuery { Q = q, Limit = 200 };
        var screen = db.Search(s).Rows.Select(f => Time.ParseIso(f.Timestamp) is DateTime t
            ? new SearchHit($"f{f.Id}", false, t, f.AppName.Length == 0 ? "Screen" : f.AppName, Snippet(f.Text, q), null) : null);
        var spoken = db.SearchAudio(s, 200).Rows.Select(a => Time.ParseIso(a.Timestamp) is DateTime t
            ? new SearchHit($"a{a.Id}", true, t, a.IsInput ? "Microphone" : "System audio", a.Text, a.ChunkId) : null);
        return Collapse(screen.Concat(spoken).OfType<SearchHit>().OrderByDescending(h => h.Time).ToList());
    }

    /// The same text stays on screen for many frames. Keep the first frame of each run
    /// so results are distinct moments, not 40 copies of one window.
    public static List<SearchHit> Collapse(List<SearchHit> hits)
    {
        var out_ = new List<SearchHit>();
        foreach (var h in hits)
        {
            if (out_.Count > 0 && out_[^1] is var last && !last.IsAudio && !h.IsAudio &&
                last.Title == h.Title && last.Snippet == h.Snippet && (last.Time - h.Time).TotalSeconds < 300)
            {
                out_[^1] = h;  // newest-first list: keep the earlier frame of the run
                continue;
            }
            out_.Add(h);
        }
        return out_;
    }

    public static string Snippet(string text, string q)
    {
        var flat = text.Replace('\n', ' ');
        var lower = flat.ToLowerInvariant();
        var at = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Select(w => lower.IndexOf(w, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        if (at < 0) return flat.Length > 140 ? flat[..140] : flat;
        int start = Math.Max(0, at - 50);
        int end = Math.Min(flat.Length, start + 160);
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }

    /// The words of a search, lowercased, for marking them on a screenshot.
    public static List<string> Terms(string q) =>
        q.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
         .Select(w => w.Replace("\"", "").ToLowerInvariant()).Where(w => w.Length > 0).Distinct().ToList();

    /// True when a word read off the screen contains one of the search terms ("Invoice:" matches "invoice").
    public static bool Matches(string word, IReadOnlyList<string> terms) =>
        terms.Any(t => word.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// Stable color per app (hue 0–359), so the timeline bar shows app switches at a glance. Same hash as the Mac app.
    public static int AppHue(string app)
    {
        uint h = 2166136261;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(app)) h = unchecked((h ^ b) * 16777619);
        return (int)(h % 360);
    }
}
