namespace OpenRewind;

/// The timeline's app filter: only the moments when one app was in front.
public static class AppFilter
{
    /// A break longer than this splits the filtered timeline, so time spent in other apps shrinks to a notch.
    public static readonly TimeSpan GapThreshold = TimeSpan.FromMinutes(1);

    /// Apps in these (time-sorted) frames, the one in front longest first.
    /// A frame counts until the next screenshot, at most 60 s.
    public static List<string> Apps(List<TimelineFrame> frames)
    {
        var time = new Dictionary<string, double>();
        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if (f.AppName.Length == 0) continue;
            var secs = i + 1 < frames.Count ? Math.Min(60, (frames[i + 1].Time - f.Time).TotalSeconds) : 2;
            time[f.AppName] = time.GetValueOrDefault(f.AppName) + secs;
        }
        return time.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key).ToList();
    }

    /// The frames where `app` was in front, each with End set to when the next screenshot (of any app)
    /// was taken, so it does not stretch over time another app was in front. No app: all frames, unchanged.
    public static List<TimelineFrame> Apply(List<TimelineFrame> all, string? app)
    {
        if (app == null) return all;
        var out_ = new List<TimelineFrame>();
        for (int i = 0; i < all.Count; i++)
            if (all[i].AppName == app) out_.Add(all[i] with { End = i + 1 < all.Count ? all[i + 1].Time : null });
        return out_;
    }

    /// How long a filtered frame stays on screen: until the next screenshot, at most 60 s (like the bar's blocks).
    public static DateTime Until(TimelineFrame f)
    {
        var cap = f.Time.AddSeconds(60);
        if (f.End is not DateTime e) return cap;
        return e < f.Time.AddSeconds(2) ? f.Time.AddSeconds(2) : e < cap ? e : cap;
    }

    /// True when `t` falls inside one of these (filtered, time-sorted) frames.
    public static bool Covers(List<TimelineFrame> frames, DateTime t)
    {
        int lo = 0, hi = frames.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (frames[mid].Time <= t) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best >= 0 && t < Until(frames[best]);
    }
}
