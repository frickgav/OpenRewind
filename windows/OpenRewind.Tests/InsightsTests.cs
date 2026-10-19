using OpenRewind;
using Xunit;

public class InsightsTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-insights-" + Guid.NewGuid());
    readonly Database db;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    /// Monday 2026-10-19, 09:00 UTC.
    static readonly DateTime T0 = new(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
    static readonly Func<string, string, string, bool> NothingHidden = (_, _, _) => false;

    public InsightsTests()
    {
        var config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    void Add(DateTime t, string app, string window = "W", string url = "") =>
        db.Insert(new Frame(0, Time.Iso(t), app, window, true, "", "", "ocr", 0, url));

    /// A frame every `every` seconds from `start` for `minutes`.
    void Use(DateTime start, double minutes, string app, string window = "W", string url = "", double every = 2)
    {
        for (double s = 0; s < minutes * 60; s += every) Add(start.AddSeconds(s), app, window, url);
    }

    InsightsReport Day(DateTime now, Func<string, string, string, bool>? hides = null) =>
        Insights.Load(db, InsightsRange.Day, T0.Date, now, hides ?? NothingHidden, Utc, DayOfWeek.Monday);

    [Fact]
    public void FramesBecomeSpansWithBreaksAtIdle()
    {
        Use(T0, 10, "Code");
        Use(T0.AddMinutes(10), 5, "Chrome", "Docs", "https://www.github.com/x");
        // 20 minutes with nothing saved (locked), then Code again.
        Use(T0.AddMinutes(35), 5, "Code");
        var spans = db.ActivitySpans(T0.AddHours(-1), T0.AddHours(2));
        Assert.Equal(3, spans.Count);
        Assert.Equal(("Code", T0, T0.AddMinutes(10)), (spans[0].App, spans[0].Start, spans[0].End));
        Assert.Equal("https://www.github.com/x", spans[1].Url);
        // A break longer than the idle cutoff does not count: the last frame before it gets 2 s, like the newest one.
        Assert.Equal(T0.AddMinutes(15), spans[1].End);
        Assert.Equal(T0.AddMinutes(35), spans[2].Start);
        // The newest frame counts a couple of seconds.
        Assert.Equal(T0.AddMinutes(40), spans[2].End);
    }

    [Fact]
    public void AStillScreenCountsOnlyAWhile()
    {
        Use(T0, 2, "Code");
        // Nothing changes for 30 minutes: one frame a minute.
        Use(T0.AddMinutes(2), 30, "Code", every: 60);
        Use(T0.AddMinutes(32), 2, "Code");
        var r = Day(T0.AddDays(1));
        // 2 + 5 (still, then away) + 2 minutes.
        Assert.Equal(9 * 60, r.TotalSeconds, 0);
        Assert.Equal(9 * 60, r.Apps.Single().Seconds, 0);
    }

    [Fact]
    public void TimePerAppAndSiteWithComparison()
    {
        Use(T0.AddDays(-1), 30, "Code");
        Use(T0.AddDays(-1).AddMinutes(30), 10, "Chrome", "Mail", "https://mail.example.com/inbox");
        Use(T0, 20, "Code");
        Use(T0.AddMinutes(20), 40, "Chrome", "GitHub", "https://www.github.com/a");
        var r = Day(T0.AddDays(1));
        Assert.False(r.SoFar);
        Assert.Equal(new[] { "Chrome", "Code" }, r.Apps.Select(a => a.Name));
        Assert.Equal(40 * 60, r.Apps[0].Seconds, 0);
        Assert.Equal(10 * 60, r.Apps[0].PreviousSeconds, 0);
        Assert.Equal("+30m", Insights.Change(r.Apps[0].Seconds, r.Apps[0].PreviousSeconds));
        Assert.Equal("−10m", Insights.Change(r.Apps[1].Seconds, r.Apps[1].PreviousSeconds));
        Assert.Equal(T0.AddMinutes(20), r.Apps[0].First);
        Assert.Equal("github.com", r.Sites.Single().Name);
        Assert.Equal(60 * 60, r.TotalSeconds, 0);
        Assert.Equal(40 * 60, r.PreviousTotalSeconds, 0);
        Assert.Equal(60 * 60, r.Hours[9], 0);
        Assert.Equal(0, r.Hours[10], 0);
    }

    [Fact]
    public void ATodayIsComparedWithYesterdayUpToTheSameTime()
    {
        Use(T0.AddDays(-1), 30, "Code");
        Use(T0.AddDays(-1).AddHours(5), 30, "Code");
        Use(T0, 10, "Code");
        var r = Day(T0.AddHours(1));
        Assert.True(r.SoFar);
        Assert.Equal(T0.AddDays(-1).AddHours(1), r.PreviousEnd);
        Assert.Equal(30 * 60, r.PreviousTotalSeconds, 0);
    }

    [Fact]
    public void BlockedFramesAreLeftOutEntirely()
    {
        Use(T0, 10, "Code");
        Use(T0.AddMinutes(10), 10, "Chrome", "My Bank", "https://bank.example.com");
        Use(T0.AddMinutes(20), 10, "1Password");
        Use(T0.AddMinutes(30), 10, "Code");
        var list = new Blocklist(new[] { new BlockedApp("1password", "1Password") }, Array.Empty<string>(), false, sites: new[] { "bank.example.com" });
        var r = Day(T0.AddDays(1), (a, w, u) => list.BlocksFrame(a, w, u));
        Assert.Equal(new[] { "Code" }, r.Apps.Select(a => a.Name));
        Assert.Empty(r.Sites);
        Assert.Equal(20 * 60, r.TotalSeconds, 0);
        // Code → (blocked) → Code is not a switch.
        Assert.Equal(0, r.Switches);
    }

    [Fact]
    public void SwitchesCountChangesWhileInUse()
    {
        var spans = new List<ActivitySpan>
        {
            new(T0, T0.AddMinutes(1), "Code", "a", ""),
            new(T0.AddMinutes(1), T0.AddMinutes(2), "Code", "b", ""),  // another window, same app
            new(T0.AddMinutes(2), T0.AddMinutes(3), "Chrome", "", ""),
            new(T0.AddMinutes(3), T0.AddMinutes(4), "Code", "a", ""),
            new(T0.AddMinutes(30), T0.AddMinutes(31), "Slack", "", ""),  // after a break
        };
        Assert.Equal(2, Insights.Switches(spans));
    }

    [Fact]
    public void FocusSessionsAllowShortDetours()
    {
        var spans = new List<ActivitySpan>
        {
            // 25 minutes of Code with a 40 s look at Slack in the middle.
            new(T0, T0.AddMinutes(12), "Code", "", ""),
            new(T0.AddMinutes(12), T0.AddMinutes(12).AddSeconds(40), "Slack", "", ""),
            new(T0.AddMinutes(12).AddSeconds(40), T0.AddMinutes(25).AddSeconds(40), "Code", "", ""),
            // A 5 minute detour ends it; 11 more minutes is a second session.
            new(T0.AddMinutes(30).AddSeconds(40), T0.AddMinutes(41).AddSeconds(40), "Code", "", ""),
            // Too short.
            new(T0.AddMinutes(41).AddSeconds(40), T0.AddMinutes(45), "Chrome", "", ""),
        };
        var f = Insights.FocusSessions(spans);
        Assert.Equal(2, f.Count);
        Assert.Equal(("Code", T0, T0.AddMinutes(25).AddSeconds(40), 25 * 60.0), (f[0].App, f[0].Start, f[0].End, f[0].Seconds));
        Assert.Equal(11 * 60, f[1].Seconds, 0);
    }

    [Fact]
    public void JumpingBetweenTwoAppsIsNotFocus()
    {
        var spans = new List<ActivitySpan>();
        for (int i = 0; i < 60; i++)
            spans.Add(new(T0.AddSeconds(i * 30), T0.AddSeconds(i * 30 + 30), i % 2 == 0 ? "Code" : "Chrome", "", ""));
        Assert.Empty(Insights.FocusSessions(spans));
    }

    [Fact]
    public void TopTenThenOther()
    {
        var now = new List<ActivitySpan>();
        var t = T0;
        for (int i = 0; i < 13; i++)
        {
            now.Add(new(t, t.AddMinutes(20 - i), $"App{i}", "", ""));
            t = t.AddMinutes(20 - i);
        }
        var before = new List<ActivitySpan> { new(T0.AddDays(-1), T0.AddDays(-1).AddMinutes(5), "App12", "", "") };
        var top = Insights.Top(now, before, s => s.App);
        Assert.Equal(11, top.Count);
        Assert.Equal("App0", top[0].Name);
        Assert.True(top[10].IsOther);
        Assert.Equal(3, top[10].Others);
        Assert.Equal((10 + 9 + 8) * 60, top[10].Seconds, 0);
        Assert.Equal(5 * 60, top[10].PreviousSeconds, 0);
    }

    [Fact]
    public void AWeekHasADayByHourGrid()
    {
        Use(T0.AddDays(2).AddHours(5), 30, "Code");  // Wednesday 14:00
        Use(T0.AddDays(-7), 10, "Code");
        var r = Insights.Load(db, InsightsRange.Week, T0.AddDays(3), T0.AddDays(30), NothingHidden, Utc, DayOfWeek.Monday);
        Assert.Equal(T0.Date, r.Start);
        Assert.Equal(T0.Date.AddDays(7), r.End);
        Assert.Equal(7, r.DayHours.Length);
        Assert.Equal(30 * 60, r.DayHours[2][14], 0);
        Assert.Equal(30 * 60, r.Hours[14], 0);
        Assert.Equal(10 * 60, r.PreviousTotalSeconds, 0);
    }

    [Fact]
    public void PeriodsAndHours()
    {
        var sunday = new DateTime(2026, 10, 25);
        Assert.Equal((new DateTime(2026, 10, 19), new DateTime(2026, 10, 26)), Insights.LocalPeriod(InsightsRange.Week, sunday, DayOfWeek.Monday));
        Assert.Equal((sunday, sunday.AddDays(7)), Insights.LocalPeriod(InsightsRange.Week, sunday, DayOfWeek.Sunday));
        Assert.Equal((sunday, sunday.AddDays(1)), Insights.LocalPeriod(InsightsRange.Day, sunday.AddHours(15), DayOfWeek.Monday));
        var parts = Insights.ByLocalHour(T0.AddMinutes(50), T0.AddMinutes(130), Utc).ToList();
        Assert.Equal(new[] { 600.0, 3600, 600 }, parts.Select(p => p.Seconds));
        Assert.Equal(new[] { 9, 10, 11 }, parts.Select(p => p.Local.Hour));
    }

    [Fact]
    public void Text()
    {
        Assert.Equal("2h 05m", Insights.Duration(125 * 60));
        Assert.Equal("45m", Insights.Duration(45 * 60 + 10));
        Assert.Equal("under 1m", Insights.Duration(20));
        Assert.Equal("same", Insights.Change(600.0, 590.0));
        Assert.Equal("+3", Insights.Change(10, 7));
        Assert.Equal("−2", Insights.Change(5, 7));
    }
}
