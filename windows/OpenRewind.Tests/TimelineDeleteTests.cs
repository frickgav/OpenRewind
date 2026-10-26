using System.Text;
using OpenRewind;
using Xunit;

/// Delete on the timeline (TimelineDelete.cs): one screenshot, every screenshot with some words, and that the
/// deleted text is gone from the database file too, not only from search.
public class TimelineDeleteTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-test-" + Guid.NewGuid());
    readonly Config config;
    readonly Database db;

    public TimelineDeleteTests()
    {
        config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    long AddFrame(DateTime t, string app, string text)
    {
        var dir = Path.Combine(config.DataDir, Time.Iso(t)[..10]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Time.Iso(t).Replace(':', '-') + ".jpg");
        File.WriteAllBytes(file, new byte[] { 0xFF, 0xD8, 0xFF });
        return db.Insert(new Frame(0, Time.Iso(t), app, "Window", true, file, text, "ocr", 0));
    }

    /// True when `s` is anywhere in the database file or its write-ahead log.
    bool OnDisk(string s)
    {
        var needle = Encoding.UTF8.GetBytes(s);
        foreach (var f in new[] { config.DbPath, config.DbPath + "-wal" })
        {
            if (!File.Exists(f)) continue;
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            if (ms.ToArray().AsSpan().IndexOf(needle) >= 0) return true;
        }
        return false;
    }

    [Fact]
    public void DeletesOnlyThatScreenshotAndTheStarOnIt()
    {
        var t = DateTime.UtcNow.AddMinutes(-10);
        var before = AddFrame(t, "Terminal", "build output");
        var shown = AddFrame(t.AddSeconds(2), "Terminal", "signing secret whsec_abc");
        var after = AddFrame(t.AddSeconds(4), "Terminal", "build done");
        db.AddStar(t.AddSeconds(3));          // points at the deleted screenshot
        db.AddStar(t.AddSeconds(5));          // points at the next one
        Assert.Equal(1, db.DeleteFrame(shown));
        Assert.Null(db.FrameById(shown));
        Assert.NotNull(db.FrameById(before));
        Assert.NotNull(db.FrameById(after));
        Assert.Equal(0, db.Search(new SearchQuery { Q = "whsec" }).Total);
        Assert.Equal(2, db.Search(new SearchQuery { Q = "build" }).Total);
        var star = Assert.Single(db.Stars());
        Assert.Equal(Time.Iso(t.AddSeconds(5)), Time.Iso(star.Time));
        Assert.Equal(2, Directory.GetFiles(config.DataDir, "*.jpg", SearchOption.AllDirectories).Length);
        Assert.Equal(0, db.DeleteFrame(shown));  // already gone
    }

    [Fact]
    public void FindsEveryScreenshotWithTheWordsInAnyApp()
    {
        var t = DateTime.UtcNow.AddHours(-30);
        var a = AddFrame(t, "Terminal", "export STRIPE_KEY=sk_live_51Hxyz");
        var b = AddFrame(t.AddHours(20), "Safari", "dashboard sk_live_51Hxyz copied");
        AddFrame(t.AddHours(21), "Safari", "nothing here");
        Assert.Equal(new[] { a, b }, db.FrameIdsMatching("sk_live_51Hxyz"));
        foreach (var id in db.FrameIdsMatching("sk_live_51Hxyz")) db.DeleteFrame(id);
        Assert.Empty(db.FrameIdsMatching("sk_live_51Hxyz"));
        Assert.Equal(1, db.Search(new SearchQuery { Q = "nothing" }).Total);
        Assert.Empty(db.FrameIdsMatching("   "));
    }

    [Fact]
    public void DeletedTextIsNotLeftInTheDatabaseFile()
    {
        var t = DateTime.UtcNow.AddMinutes(-5);
        // Enough other screenshots that the deleted one shares its pages with ones that stay.
        for (int i = 0; i < 200; i++) AddFrame(t.AddSeconds(-2 * (i + 1)), "Editor", $"ordinary line number {i} of some code");
        var secret = AddFrame(t, "Editor", "zqxsecretvalue987 is the signing secret");
        db.FlushDeleted();
        Assert.True(OnDisk("zqxsecretvalue987"));  // the check can see it while it is there
        Assert.Equal(1, db.DeleteFrame(secret));
        db.FlushDeleted();
        Assert.False(OnDisk("zqxsecretvalue987"));
        Assert.Equal(200, db.Search(new SearchQuery { Q = "ordinary" }).Total);
    }

    [Fact]
    public void RangeDeleteAlsoLeavesNoText()
    {
        var t = DateTime.UtcNow.AddMinutes(-30);
        for (int i = 0; i < 50; i++) AddFrame(t.AddSeconds(2 * i), "Editor", $"kept words {i}");
        AddFrame(t.AddSeconds(101), "Browser", "qqwvtoken555 on the page");
        AddFrame(t.AddSeconds(103), "Browser", "qqwvtoken555 still there");
        for (int i = 0; i < 50; i++) AddFrame(t.AddSeconds(110 + 2 * i), "Editor", $"kept words after {i}");
        Assert.Equal(2, db.DeleteFrames(t.AddSeconds(100), t.AddSeconds(105)));
        db.FlushDeleted();
        Assert.False(OnDisk("qqwvtoken555"));
        Assert.Equal(100, db.Search(new SearchQuery { Q = "kept" }).Total);
    }
    [Fact]
    public void SelectedStretchTakesOnlyWhatTheTimelineShows()
    {
        var m = DateTime.UtcNow.AddMinutes(-30);
        var slack = new List<long>();
        var other = new List<long>();
        for (int i = 0; i < 6; i++)
        {
            slack.Add(AddFrame(m.AddSeconds(4 * i), "Slack", $"slack {i}"));
            other.Add(AddFrame(m.AddSeconds(4 * i + 2), i % 2 == 0 ? "Safari" : "Game", $"other {i}"));
        }
        var to = m.AddSeconds(30);
        Assert.Equal(slack, AppFilter.ShownIds(db, m, to, "Slack", null));
        Assert.Equal(new[] { other[0], slack[1] }, AppFilter.ShownIds(db, m.AddSeconds(2), m.AddSeconds(6), null, Array.Empty<string>()));   // end not included
        Assert.Equal(6, AppFilter.ShownIds(db, m, to, "Slack", null).Sum(id => db.DeleteFrame(id)));
        foreach (var id in slack) Assert.Null(db.FrameById(id));
        foreach (var id in other) Assert.NotNull(db.FrameById(id));
        Assert.Equal(new[] { other[0], other[2], other[4] }, AppFilter.ShownIds(db, m, to, null, new[] { "Game" }));
    }
}
