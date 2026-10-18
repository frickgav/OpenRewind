namespace OpenRewind;

/// When a new screenshot is close enough to the last stored one to skip it.
public static class FrameDedup
{
    /// While only a few words change (typing, a ticking clock), store at most one frame this often.
    public const double MinorChangeSeconds = 10;

    /// Words added plus words removed between two texts, ignoring order.
    public static int WordsChanged(string a, string b)
    {
        var counts = new Dictionary<string, int>();
        foreach (var w in Words(a)) counts[w] = counts.GetValueOrDefault(w) + 1;
        foreach (var w in Words(b)) counts[w] = counts.GetValueOrDefault(w) - 1;
        return counts.Values.Sum(Math.Abs);
    }

    /// True when the texts differ by at most 4 words, or 2% of the longer one.
    public static bool IsMinorChange(string a, string b)
    {
        int longer = Math.Max(Words(a).Length, Words(b).Length);
        return WordsChanged(a, b) <= Math.Max(4, longer / 50);
    }

    static string[] Words(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
