using System.Text.Json.Nodes;
using OpenRewind;

public class StarsTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-stars-test-" + Guid.NewGuid());
    readonly Database db;
    static readonly DateTime T0 = new(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);
    static readonly Blocklist None = new(Array.Empty<BlockedApp>(), Array.Empty<string>(), false);

    public StarsTests()
    {
        db = new Database(Config.Load(new[] { "--home", home }).DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    void AddFrame(DateTime utc, string app, string window, string url = "", string text = "", bool blocked = false) =>
        db.Insert(new Frame(0, Time.Iso(utc), app, window, true, "", text, "ocr", 0, url, Blocked: blocked));

    [Fact]
    public void AddsListsAndRemovesStarsNewestFirst()
    {
        var a = db.AddStar(T0);
        var b = db.AddStar(T0.AddMinutes(5), "pricing");
        Assert.Equal(new[] { b, a }, db.Stars().Select(s => s.Id));
        Assert.Equal("pricing", db.Stars()[0].Note);
        Assert.Equal(T0, db.Stars()[1].Time);
        Assert.Single(db.Stars(from: T0.AddMinutes(1)));
        db.RemoveStar(a);
        Assert.Equal(new[] { b }, db.Stars().Select(s => s.Id));
    }

    [Fact]
    public void AStarShowsTheScreenshotAtOrJustBeforeIt()
    {
        AddFrame(T0, "Code", "old.cs");
        AddFrame(T0.AddSeconds(20), "Chrome", "Pricing page");
        AddFrame(T0.AddSeconds(40), "Mail", "Inbox");
        Assert.Equal("Chrome", db.FrameForStar(T0.AddSeconds(30))!.AppName);
        // Pressed just before recording started: the first screenshot after it.
        Assert.Equal("Code", db.FrameForStar(T0.AddSeconds(-10))!.AppName);
        Assert.Null(db.FrameForStar(T0.AddHours(1)));
    }

    [Fact]
    public void DeletingRecordingsDeletesTheirStarsEvenWithNoFramesLeft()
    {
        AddFrame(T0, "Code", "a");
        db.AddStar(T0);
        db.AddStar(T0.AddHours(2));          // no frame near it
        var kept = db.AddStar(T0.AddHours(5));
        db.DeleteFrames(null, T0.AddHours(3));
        Assert.Equal(new[] { kept }, db.Stars().Select(s => s.Id));
    }

    [Fact]
    public void AskAiListsStarsButLeavesOutBlockedOnes()
    {
        AddFrame(T0, "Chrome", "Pricing — Acme", "https://acme.example.com/pricing", "Pro plan $49 per month");
        AddFrame(T0.AddMinutes(10), "Signal", "Chat with Sam", text: "secret");
        AddFrame(T0.AddMinutes(20), "Chrome", "", blocked: true);
        db.AddStar(T0.AddSeconds(5), "check this");
        db.AddStar(T0.AddMinutes(10).AddSeconds(5));
        db.AddStar(T0.AddMinutes(20).AddSeconds(5));
        var tools = new AiTools(db) { Blocked = new Blocklist(new[] { new BlockedApp("signal", "Signal") }, Array.Empty<string>(), false) };
        var text = tools.Run(new AiToolCall("1", "list_starred", new JsonObject()));
        Assert.StartsWith("1 starred moments", text);
        Assert.Contains("Pricing — Acme", text);
        Assert.Contains("Note: check this", text);
        Assert.Contains("Pro plan $49", text);
        Assert.DoesNotContain("Signal", text);
        Assert.DoesNotContain("secret", text);
        Assert.StartsWith("No starred", new AiTools(db) { Blocked = None }.Run(new AiToolCall("2", "list_starred", new JsonObject { ["start"] = "2026-03-11 00:00" })));
    }

    [Fact]
    public void TheStarredFilterMatchesWordsAndHidesBlockedDetails()
    {
        AddFrame(T0, "Chrome", "Pricing — Acme", "https://acme.example.com/pricing", "Pro plan $49 per month");
        AddFrame(T0.AddMinutes(10), "Signal", "Chat with Sam", text: "secret plan");
        db.AddStar(T0.AddSeconds(5), "check this");
        db.AddStar(T0.AddMinutes(10).AddSeconds(5));
        var list = new Blocklist(new[] { new BlockedApp("signal", "Signal") }, Array.Empty<string>(), false);
        var all = SearchHits.Starred(db, "", null, list);
        Assert.Equal(new[] { "Signal", "Chrome" }, all.Select(h => h.Title));
        Assert.All(all, h => Assert.True(h.IsStar));
        Assert.Equal("", all[0].Snippet);  // blocked now: the app name only
        Assert.Contains("check this", all[1].Snippet);
        // Words match the note, title, address or screen text, but never a blocked moment's text.
        Assert.Equal(new[] { "Chrome" }, SearchHits.Starred(db, "plan", null, list).Select(h => h.Title));
        Assert.Contains("Pro plan", SearchHits.Starred(db, "plan", null, list)[0].Snippet);
        Assert.Empty(SearchHits.Starred(db, "", "Mail", list));
    }

    [Fact]
    public void AReadOnlyOpenOfAnOldDatabaseHasNoStars()
    {
        var path = Path.Combine(home, "old.sqlite");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE frames (id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, app_name TEXT NOT NULL DEFAULT '', window_name TEXT NOT NULL DEFAULT '', focused INTEGER NOT NULL DEFAULT 1, snapshot_path TEXT NOT NULL DEFAULT '', text TEXT NOT NULL DEFAULT '', text_source TEXT NOT NULL DEFAULT 'ocr', hash INTEGER NOT NULL DEFAULT 0, browser_url TEXT NOT NULL DEFAULT '')";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var ro = Database.OpenReadOnly(path);
        Assert.Empty(ro.Stars());
    }
}
