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

    /// The frames where `app` was in front (any app when null) and no app in `hidden` (see HiddenApps) was,
    /// each with End set to when the next screenshot (of any app) was taken, so it does not stretch over time
    /// another app was in front. No app and nothing hidden: all frames, unchanged.
    public static List<TimelineFrame> Apply(List<TimelineFrame> all, string? app, IReadOnlyCollection<string>? hidden = null)
    {
        var hide = HiddenApps.Keys(hidden);
        if (app == null && hide.Count == 0) return all;
        var out_ = new List<TimelineFrame>();
        for (int i = 0; i < all.Count; i++)
            if ((app == null || all[i].AppName == app) && !hide.Contains(all[i].AppName))
                out_.Add(all[i] with { End = i + 1 < all.Count ? all[i + 1].Time : null });
        return out_;
    }

    /// The ids of the screenshots the timeline shows from `from` up to (not including) `to` with this app filter
    /// and these hidden apps, oldest first: what a delete of a selected stretch takes.
    public static List<long> ShownIds(Database db, DateTime from, DateTime to, string? app, IReadOnlyCollection<string>? hidden)
    {
        // Compared as the database stores times (to the millisecond); TimelineFrames also adds the first one after `to`.
        var end = Time.Iso(to);
        return Apply(db.TimelineFrames(from, to), app, hidden)
            .Where(f => string.CompareOrdinal(Time.Iso(f.Time), end) < 0).Select(f => f.Id).ToList();
    }

    /// True when, at `t`, an app in `hidden` was in front: `t` falls inside a screenshot of it in `all`
    /// (every app, time-sorted), which lasts until the next screenshot, at most 60 s.
    public static bool HiddenAt(List<TimelineFrame> all, DateTime t, IReadOnlyCollection<string>? hidden)
    {
        var hide = HiddenApps.Keys(hidden);
        if (hide.Count == 0) return false;
        int lo = 0, hi = all.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (all[mid].Time <= t) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (best < 0 || !hide.Contains(all[best].AppName)) return false;
        var cap = all[best].Time.AddSeconds(60);
        var next = best + 1 < all.Count && all[best + 1].Time < cap ? all[best + 1].Time : cap;
        return t < next;
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

/// Apps hidden from the timeline (H, or the apps menu), by the name the timeline shows. Saved in settings.json
/// (TimelineHiddenApps) so they stay hidden on every open. They are still recorded; Blocked Apps & Sites stops
/// that. The Mac app keeps the same list in UserDefaults.
public static class HiddenApps
{
    /// The names as a set that ignores case.
    public static HashSet<string> Keys(IEnumerable<string>? names) =>
        new((names ?? Array.Empty<string>()).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

    /// Adds `app` to `list` (sorted, no repeats). False when it was already there or is empty.
    public static bool Hide(List<string> list, string app)
    {
        app = app.Trim();
        if (app.Length == 0 || list.Contains(app, StringComparer.OrdinalIgnoreCase)) return false;
        list.Add(app);
        list.Sort(StringComparer.CurrentCultureIgnoreCase);
        return true;
    }

    /// Takes `app` off `list`. False when it was not there.
    public static bool Show(List<string> list, string app) =>
        list.RemoveAll(a => a.Equals(app, StringComparison.OrdinalIgnoreCase)) > 0;
}
