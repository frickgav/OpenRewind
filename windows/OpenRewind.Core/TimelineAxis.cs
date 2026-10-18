namespace OpenRewind;

/// Maps real time to a position on the timeline bar where long breaks are shrunk.
/// Recorded stretches ("sessions") keep their real length; each break between them
/// takes a small fixed width, so a 2-hour gap does not eat the whole bar.
/// Positions ("v") are in "bar seconds".
public sealed class TimelineAxis
{
    public record struct Session(DateTime Start, DateTime End);

    /// A break longer than this splits sessions.
    public static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(5);

    public readonly List<Session> Sessions;
    /// Width of one break, in bar seconds.
    public readonly double GapV;
    /// Bar position where each session starts.
    readonly List<double> vStart = new();

    public TimelineAxis(List<Session> sessions, double gapV)
    {
        Sessions = sessions;
        GapV = gapV;
        double acc = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0) acc += gapV;
            vStart.Add(acc);
            acc += (sessions[i].End - sessions[i].Start).TotalSeconds;
        }
    }

    /// Group frames and audio into sessions. A short session is added at "now" when
    /// recording stopped a while ago, so the break up to now is shrunk too.
    public static List<Session> BuildSessions(IEnumerable<TimelineFrame> frames, IEnumerable<TimelineAudio> audio, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var spans = frames.Select(f => (f.Time, f.Time.AddSeconds(2))).Concat(audio.Select(a => (a.Start, a.End)))
                          .OrderBy(s => s.Item1).ToList();
        var out_ = new List<Session>();
        foreach (var (a, b) in spans)
        {
            if (out_.Count > 0 && a - out_[^1].End <= GapThreshold)
                out_[^1] = out_[^1] with { End = b > out_[^1].End ? b : out_[^1].End };
            else
                out_.Add(new Session(a, b));
        }
        if (out_.Count > 0 && now - out_[^1].End > GapThreshold) out_.Add(new Session(now, now));
        else if (out_.Count > 0 && now > out_[^1].End) out_[^1] = out_[^1] with { End = now };
        return out_;
    }

    /// Index of the last session that starts at or before `d` (or 0).
    int SessionIndex(DateTime d)
    {
        int lo = 0, hi = Sessions.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Sessions[mid].Start <= d) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    public double V(DateTime d)
    {
        if (Sessions.Count == 0) return 0;
        int i = SessionIndex(d);
        var s = Sessions[i];
        if (d < s.Start) return (d - s.Start).TotalSeconds;  // before the first session
        if (d <= s.End || i == Sessions.Count - 1) return vStart[i] + (d - s.Start).TotalSeconds;
        // Inside the break after session i: spread it over the fixed break width.
        var next = Sessions[i + 1];
        var frac = (d - s.End).TotalSeconds / Math.Max(1, (next.Start - s.End).TotalSeconds);
        return vStart[i] + (s.End - s.Start).TotalSeconds + frac * GapV;
    }

    public DateTime Date(double v)
    {
        if (Sessions.Count == 0) return DateTime.UtcNow;
        int lo = 0, hi = vStart.Count - 1, i = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (vStart[mid] <= v) { i = mid; lo = mid + 1; } else hi = mid - 1;
        }
        var s = Sessions[i];
        var dur = (s.End - s.Start).TotalSeconds;
        var into = v - vStart[i];
        if (into <= dur || i == Sessions.Count - 1) return s.Start.AddSeconds(into);
        var next = Sessions[i + 1];
        var frac = Math.Min(1, (into - dur) / GapV);
        return s.End.AddSeconds(frac * (next.Start - s.End).TotalSeconds);
    }

    /// Breaks between sessions, as (end of one session, start of the next).
    public IEnumerable<(DateTime From, DateTime To)> Gaps
    {
        get { for (int i = 1; i < Sessions.Count; i++) yield return (Sessions[i - 1].End, Sessions[i].Start); }
    }

    public static string BreakLabel(TimeSpan span)
    {
        int m = (int)span.TotalMinutes;
        if (m < 60) return $"{m}m break";
        if (m < 24 * 60) return $"{m / 60}h {m % 60}m break";
        return $"{m / 1440}d break";
    }
}
