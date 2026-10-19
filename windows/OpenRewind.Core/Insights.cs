using Microsoft.Data.Sqlite;
using System.Globalization;

namespace OpenRewind;

public enum InsightsRange { Day, Week }

/// A stretch of time with one app, window and address in front and no break in it. Start and End are UTC.
/// Blocked: saved while a blocked window or site was in front (left out of Insights).
public sealed record ActivitySpan(DateTime Start, DateTime End, string App, string Window, string Url, bool Blocked = false)
{
    public double Seconds => (End - Start).TotalSeconds;
}

/// One bar: an app or a website. `First` is when it was first in front in the period (UTC).
/// `Others` counts the apps or sites folded into the "Other" bar (0 for a real one).
public sealed record InsightsItem(string Name, double Seconds, double PreviousSeconds, DateTime First, int Others = 0)
{
    public bool IsOther => Others > 0;
}

/// A long stretch mostly in one app. Seconds is the time in that app; Start and End are UTC.
public sealed record FocusSession(string App, DateTime Start, DateTime End, double Seconds);

/// Where the time went in one day or week. Everything blocked is left out.
public sealed class InsightsReport
{
    public InsightsRange Range;
    /// The period, UTC. `Start` is local midnight (the first day of the week for a week).
    public DateTime Start, End;
    /// The previous day or week, cut to the same length when this one is still going.
    public DateTime PreviousStart, PreviousEnd;
    public double TotalSeconds, PreviousTotalSeconds;
    public int Switches, PreviousSwitches;
    /// Top apps (most time first) and an "Other" bar for the rest.
    public List<InsightsItem> Apps = new();
    public List<InsightsItem> Sites = new();
    /// Active seconds per local hour, summed over the period.
    public double[] Hours = new double[24];
    /// Active seconds per day (one row for a day, seven for a week) and local hour.
    public double[][] DayHours = Array.Empty<double[]>();
    /// Longest first.
    public List<FocusSession> Focus = new();
    /// True when the period is still going, so the comparison stops at the same time.
    public bool SoFar;

    public bool IsEmpty => TotalSeconds < 1;
}

/// Insights: time per app and site, when you were active, focus stretches and app switches,
/// worked out on this computer from the saved frames (no AI). The Mac app has the same rules
/// in Insights.swift.
public static class Insights
{
    /// A frame counts until the next one, unless the break is longer than this: then the computer
    /// was locked, asleep or paused (a screen that is on gets a frame at least once a minute), and
    /// the frame counts like the newest one.
    public static readonly TimeSpan IdleCutoff = TimeSpan.FromMinutes(2);
    /// A screen that does not change gets one frame a minute. A gap this long means nothing changed.
    public static readonly TimeSpan StillGap = TimeSpan.FromSeconds(50);
    /// After this long without any change on screen, the rest counts as away, unless audio was
    /// recorded then (a call, a video): that counts as present.
    public static readonly TimeSpan StillLimit = TimeSpan.FromMinutes(5);
    /// Length of a recorded audio clip (the default; `--chunk` changes it).
    public static readonly TimeSpan AudioChunk = TimeSpan.FromSeconds(30);
    /// The newest frame of all counts this long.
    public static readonly TimeSpan LastFrame = TimeSpan.FromSeconds(2);
    /// A focus session survives a look at another app (or a pause) this long.
    public static readonly TimeSpan FocusDetour = TimeSpan.FromMinutes(1);
    /// Shortest focus session listed.
    public static readonly TimeSpan MinFocus = TimeSpan.FromMinutes(10);
    /// Share of a focus session's time spent in its app.
    public const double FocusShare = 0.8;
    public const int TopCount = 10;
    public const int FocusCount = 5;

    /// The day or week around `localDay`, in local time. Weeks start on `firstDay`.
    public static (DateTime Start, DateTime End) LocalPeriod(InsightsRange range, DateTime localDay, DayOfWeek firstDay)
    {
        var day = localDay.Date;
        if (range == InsightsRange.Day) return (day, day.AddDays(1));
        var back = ((int)day.DayOfWeek - (int)firstDay + 7) % 7;
        var start = day.AddDays(-back);
        return (start, start.AddDays(7));
    }

    /// The same period one step earlier or later.
    public static DateTime Step(InsightsRange range, DateTime localDay, int n) =>
        localDay.Date.AddDays(n * (range == InsightsRange.Day ? 1 : 7));

    /// A local wall-clock time as UTC; a time skipped by a clock change moves forward an hour.
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }

    /// Reads the frames and works out the report. `hides` leaves out blocked apps, windows and sites.
    public static InsightsReport Load(Database db, InsightsRange range, DateTime localDay, DateTime nowUtc,
                                      Func<string, string, string, bool> hides, TimeZoneInfo? tz = null, DayOfWeek? firstDay = null)
    {
        tz ??= TimeZoneInfo.Local;
        var first = firstDay ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var (_, le) = LocalPeriod(range, localDay, first);
        var (ps, _) = LocalPeriod(range, Step(range, localDay, -1), first);
        var spans = db.ActivitySpans(ToUtc(ps, tz), ToUtc(le, tz));
        return Build(spans, range, localDay, nowUtc, hides, tz, first);
    }

    /// The report for the period around `localDay`, from spans covering it and the period before.
    public static InsightsReport Build(IEnumerable<ActivitySpan> spans, InsightsRange range, DateTime localDay, DateTime nowUtc,
                                       Func<string, string, string, bool> hides, TimeZoneInfo tz, DayOfWeek firstDay)
    {
        var (ls, le) = LocalPeriod(range, localDay, firstDay);
        var (pls, ple) = LocalPeriod(range, Step(range, localDay, -1), firstDay);
        var r = new InsightsReport { Range = range, Start = ToUtc(ls, tz), End = ToUtc(le, tz), PreviousStart = ToUtc(pls, tz), PreviousEnd = ToUtc(ple, tz) };
        // A period that is still going is compared with the same stretch of the one before.
        if (nowUtc < r.End && nowUtc > r.Start)
        {
            r.SoFar = true;
            // By the clock: the same time of day, that many days into the previous period.
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
            r.PreviousEnd = ToUtc(pls + (nowLocal - ls), tz);
        }

        // Blocked frames are left out entirely: marked when saved, or blocked now (ask once per app,
        // window and address).
        var verdicts = new Dictionary<(string, string, string), bool>();
        var kept = spans.Where(s =>
        {
            if (s.Blocked) return false;
            var key = (s.App, s.Window, s.Url);
            if (!verdicts.TryGetValue(key, out var hidden)) verdicts[key] = hidden = hides(s.App, s.Window, s.Url);
            return !hidden;
        }).OrderBy(s => s.Start).ToList();

        var now = Clip(kept, r.Start, Min(r.End, nowUtc));
        var before = Clip(kept, r.PreviousStart, r.PreviousEnd);

        r.TotalSeconds = now.Sum(s => s.Seconds);
        r.PreviousTotalSeconds = before.Sum(s => s.Seconds);
        r.Switches = Switches(now);
        r.PreviousSwitches = Switches(before);
        r.Apps = Top(now, before, s => s.App);
        r.Sites = Top(now.Where(s => Site(s.Url).Length > 0).ToList(), before, s => Site(s.Url));
        r.Focus = FocusSessions(now).Take(FocusCount).ToList();

        var days = (int)Math.Round((le - ls).TotalDays);
        r.DayHours = Enumerable.Range(0, days).Select(_ => new double[24]).ToArray();
        foreach (var s in now)
            foreach (var (local, secs) in ByLocalHour(s.Start, s.End, tz))
            {
                r.Hours[local.Hour] += secs;
                var d = (int)(local.Date - ls).TotalDays;
                if (d >= 0 && d < days) r.DayHours[d][local.Hour] += secs;
            }
        return r;
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// Frames (oldest first) to spans. Each frame counts until the next one, except: after a break
    /// longer than IdleCutoff it counts LastFrame, and once nothing has changed on screen for
    /// StillLimit (only the once-a-minute frames), it stops counting until something changes or
    /// audio is recorded (`audio`: start times of recorded clips, oldest first).
    /// Frames in a row with the same app, window, address and blocked mark and no break between them are joined.
    public static List<ActivitySpan> Spans(IEnumerable<ActivityRow> rows, IReadOnlyList<DateTime>? audio = null)
    {
        var out_ = new List<ActivitySpan>();
        ActivityRow? prev = null;
        double still = 0;
        int a = 0;
        foreach (var row in rows)
        {
            if (prev != null) Add(prev, (row.Time - prev.Time).TotalSeconds);
            prev = row;
        }
        if (prev != null) Add(prev, LastFrame.TotalSeconds);
        return out_;

        // True when a clip overlaps [from, to). Frames come in time order, so `a` only moves forward.
        bool Heard(DateTime from, DateTime to)
        {
            if (audio == null) return false;
            while (a < audio.Count && audio[a] + AudioChunk <= from) a++;
            return a < audio.Count && audio[a] < to;
        }

        void Add(ActivityRow f, double gap)
        {
            double active;
            if (gap > IdleCutoff.TotalSeconds) { active = LastFrame.TotalSeconds; still = 0; }
            else if (gap >= StillGap.TotalSeconds && !Heard(f.Time, f.Time.AddSeconds(gap)))
            {
                active = Math.Max(0, Math.Min(gap, StillLimit.TotalSeconds - still));
                still += gap;
            }
            else { active = gap; still = 0; }
            if (active <= 0) return;
            var end = f.Time.AddSeconds(active);
            if (out_.Count > 0 && out_[^1] is var last && last.App == f.App && last.Window == f.Window && last.Url == f.Url
                && last.Blocked == f.Blocked && last.End >= f.Time.AddMilliseconds(-1))
                out_[^1] = last with { End = end };
            else out_.Add(new ActivitySpan(f.Time, end, f.App, f.Window, f.Url, f.Blocked));
        }
    }

    /// The parts of the spans inside [from, to).
    public static List<ActivitySpan> Clip(IEnumerable<ActivitySpan> spans, DateTime from, DateTime to)
    {
        var out_ = new List<ActivitySpan>();
        foreach (var s in spans)
        {
            var a = s.Start < from ? from : s.Start;
            var b = s.End > to ? to : s.End;
            if (b > a) out_.Add(s with { Start = a, End = b });
        }
        return out_;
    }

    /// "github.com" for a web address (without "www."), or "" when it is not one.
    public static string Site(string url)
    {
        var host = WebAddress.Host(url).ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    /// Time per app (or site), most first: the top ones, then one "Other" bar for the rest.
    public static List<InsightsItem> Top(List<ActivitySpan> now, List<ActivitySpan> before, Func<ActivitySpan, string> key)
    {
        var time = new Dictionary<string, double>();
        var first = new Dictionary<string, DateTime>();
        foreach (var s in now)
        {
            var k = key(s);
            time[k] = time.GetValueOrDefault(k) + s.Seconds;
            if (!first.ContainsKey(k)) first[k] = s.Start;
        }
        var prev = new Dictionary<string, double>();
        foreach (var s in before) { var k = key(s); prev[k] = prev.GetValueOrDefault(k) + s.Seconds; }
        var sorted = time.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var items = sorted.Take(TopCount).Select(p => new InsightsItem(p.Key, p.Value, prev.GetValueOrDefault(p.Key), first[p.Key])).ToList();
        var rest = sorted.Skip(TopCount).ToList();
        if (rest.Count > 0)
            items.Add(new InsightsItem("Other", rest.Sum(p => p.Value), rest.Sum(p => prev.GetValueOrDefault(p.Key)),
                                       rest.Min(p => first[p.Key]), rest.Count));
        return items;
    }

    /// Times the app in front changed while in use. A change after a break (idle, locked, or a
    /// blocked app) does not count.
    public static int Switches(List<ActivitySpan> spans)
    {
        int n = 0;
        for (int i = 1; i < spans.Count; i++)
            if (spans[i].App != spans[i - 1].App && spans[i].Start - spans[i - 1].End <= LastFrame) n++;
        return n;
    }

    /// Stretches mostly in one app: its spans, joined while the time away between them (other
    /// apps, breaks) stays under FocusDetour, at least MinFocus in that app and FocusShare of the
    /// stretch. Longest first.
    public static List<FocusSession> FocusSessions(List<ActivitySpan> spans)
    {
        var out_ = new List<FocusSession>();
        foreach (var group in spans.GroupBy(s => s.App))
        {
            FocusSession? cur = null;
            foreach (var s in group.OrderBy(s => s.Start))
            {
                if (cur != null && s.Start - cur.End <= FocusDetour)
                    cur = cur with { End = s.End > cur.End ? s.End : cur.End, Seconds = cur.Seconds + s.Seconds };
                else
                {
                    if (cur != null) Keep(cur);
                    cur = new FocusSession(s.App, s.Start, s.End, s.Seconds);
                }
            }
            if (cur != null) Keep(cur);
        }
        return out_.OrderByDescending(f => f.Seconds).ThenBy(f => f.Start).ToList();

        void Keep(FocusSession f)
        {
            if (f.Seconds >= MinFocus.TotalSeconds && f.Seconds >= FocusShare * (f.End - f.Start).TotalSeconds) out_.Add(f);
        }
    }

    /// Splits [start, end) at local hour boundaries: each piece's local start and length in seconds.
    /// Steps by wall-clock minutes to the next hour from the current offset, so the hour repeated
    /// when clocks go back gets its own piece in the right hour.
    public static IEnumerable<(DateTime Local, double Seconds)> ByLocalHour(DateTime start, DateTime end, TimeZoneInfo tz)
    {
        var t = start;
        while (t < end)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(t, tz);
            var next = t + (local.Date.AddHours(local.Hour + 1) - local);
            if (next <= t) next = t.AddHours(1);
            if (next > end) next = end;
            yield return (local, (next - t).TotalSeconds);
            t = next;
        }
    }

    // MARK: text

    /// "2h 05m", "45m", "under 1m".
    public static string Duration(double secs)
    {
        var m = (int)Math.Round(secs / 60);
        if (m < 1) return secs >= 1 ? "under 1m" : "0m";
        return m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m}m";
    }

    /// "+12m", "−1h 05m", or "same" for a change under a minute.
    public static string Change(double now, double before)
    {
        var d = now - before;
        if (Math.Abs(d) < 60) return "same";
        return (d > 0 ? "+" : "−") + Duration(Math.Abs(d));
    }

    /// "+4", "−12", or "same".
    public static string Change(int now, int before) =>
        now == before ? "same" : now > before ? $"+{now - before}" : $"−{before - now}";
}

public sealed partial class Database
{
    /// Unbroken stretches with one app, window and address in front, oldest first, from frames in
    /// [from, to) (UTC). Reads only time, app, window, address and the blocked mark, through
    /// idx_frames_insights, so the screen text is never touched; the spans are worked out while
    /// reading (Insights.Spans). Working out the gaps with SQLite window functions instead was
    /// several times slower.
    public List<ActivitySpan> ActivitySpans(DateTime from, DateTime to)
    {
        // Frames a little before and after, so the edges count the same as the middle.
        var start = Time.Iso(from - Insights.StillLimit - Insights.IdleCutoff);
        var stop = Time.Iso(to + Insights.IdleCutoff);
        lock (gate)
        {
            var audio = new List<DateTime>();
            using (var a = Cmd("SELECT julianday(timestamp) FROM audio_chunks WHERE timestamp >= ? AND timestamp < ? AND status != 'silent' ORDER BY timestamp",
                               Time.Iso(from - Insights.StillLimit - Insights.IdleCutoff - Insights.AudioChunk), stop))
            using (var ar = a.ExecuteReader())
                while (ar.Read()) if (!ar.IsDBNull(0)) audio.Add(FromJulian(ar.GetDouble(0)));
            using var cmd = Cmd("SELECT julianday(timestamp), app_name, window_name, browser_url, blocked FROM frames WHERE timestamp >= ? AND timestamp < ? ORDER BY timestamp",
                                start, stop);
            using var r = cmd.ExecuteReader();
            return Insights.Spans(Rows(r), audio);
        }

        static IEnumerable<ActivityRow> Rows(SqliteDataReader r)
        {
            while (r.Read())
                if (!r.IsDBNull(0))
                    yield return new ActivityRow(FromJulian(r.GetDouble(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0);
        }
    }

    static DateTime FromJulian(double jd) =>
        DateTime.SpecifyKind(DateTime.UnixEpoch.AddMilliseconds(Math.Round((jd - 2440587.5) * 86_400_000)), DateTimeKind.Utc);
}
