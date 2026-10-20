using System.Text.Json;
using OpenRewind;
using Xunit;

/// The local search server (port 3031) leaves out everything the block list covers, the same as Ask AI
/// and AI apps: blocked apps, sites, private windows, blocked words in the title or address, and frames
/// saved while a blocked window was in front.
public class ServerBlocklistTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-test-" + Guid.NewGuid());
    readonly Config config;
    readonly Database db;

    public ServerBlocklistTests()
    {
        config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    long AddFrame(DateTime t, string app, string window, string text, string url = "", bool blocked = false)
    {
        var dir = Path.Combine(config.DataDir, Time.Iso(t)[..10]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Time.Iso(t).Replace(':', '-') + ".jpg");
        File.WriteAllBytes(file, new byte[] { 0xFF, 0xD8, 0xFF });
        return db.Insert(new Frame(0, Time.Iso(t), app, window, true, file, text, "ocr", 0, url, blocked));
    }

    static readonly Blocklist List = new(new[] { new BlockedApp("bbedit", "BBEdit") }, new[] { "Payroll" }, true,
                                         sites: new[] { "chase.com" });

    Api ApiWith(Blocklist list) => new(db, config, blocked: () => list);

    static JsonElement Json(HttpResponse r) => JsonDocument.Parse(r.Body).RootElement.Clone();

    static HttpRequest Get(string path, Dictionary<string, string>? q = null) => new("GET", path, q ?? new());

    /// One visible frame and one of each kind the block list hides, all with the word "secret" on screen.
    (long Visible, long[] Hidden) AddMix()
    {
        var t = DateTime.UtcNow.AddMinutes(-30);
        var visible = AddFrame(t, "Notepad", "notes.txt", "secret plan");
        var hidden = new[]
        {
            AddFrame(t.AddMinutes(1), "BBEdit", "draft.txt", "secret draft"),                                       // blocked app
            AddFrame(t.AddMinutes(2), "Chrome", "Statements", "secret balance", "https://secure.chase.com/acct"),  // blocked site
            AddFrame(t.AddMinutes(3), "Chrome", "Payroll – Acme", "secret salaries"),                             // blocked word in the title
            AddFrame(t.AddMinutes(4), "Chrome", "Docs", "secret sheet", "https://docs.example.com/payroll/2026"),   // blocked word in the address
            AddFrame(t.AddMinutes(5), "Firefox", "Search — Private Browsing", "secret search"),                    // private window
            AddFrame(t.AddMinutes(6), "Notepad", "", "secret", blocked: true),                                     // saved while blocked
        };
        return (visible, hidden);
    }

    [Fact]
    public void BlockedColumnIsReadBack()
    {
        var id = AddFrame(DateTime.UtcNow, "Notepad", "", "", blocked: true);
        Assert.True(db.FrameById(id)!.Blocked);
        Assert.False(db.FrameById(AddFrame(DateTime.UtcNow.AddSeconds(1), "Notepad", "a", "b"))!.Blocked);
    }

    [Fact]
    public void HidesFrameUsesTheAskAiRule()
    {
        var f = new Frame(1, Time.Now(), "Notepad", "notes", true, "", "", "ocr", 0);
        Assert.False(List.HidesFrame(f));
        Assert.True(List.HidesFrame(f with { AppName = "bbedit" }));
        Assert.True(List.HidesFrame(f with { WindowName = "Payroll report" }));
        Assert.False(List.HidesFrame(f with { WindowName = "Payrolls" }));
        Assert.True(List.HidesFrame(f with { BrowserUrl = "https://www.chase.com/" }));
        Assert.True(List.HidesFrame(f with { BrowserUrl = "https://hr.example.com/payroll" }));
        Assert.True(List.HidesFrame(f with { WindowName = "New InPrivate tab" }));
        Assert.True(List.HidesFrame(f with { Blocked = true }));
    }

    [Fact]
    public void SearchLeavesOutBlockedFrames()
    {
        var (visible, _) = AddMix();
        var api = ApiWith(List);
        foreach (var q in new Dictionary<string, string>[]
        {
            new() { ["q"] = "secret" },
            new() { ["q"] = "secret", ["content_type"] = "ocr" },
            new(),
            new() { ["start_time"] = "1d ago", ["content_type"] = "ocr" },
        })
        {
            var r = Json(api.Handle(Get("/search", q)));
            var data = r.GetProperty("data").EnumerateArray().ToList();
            Assert.Single(data);
            Assert.Equal(visible, data[0].GetProperty("content").GetProperty("frame_id").GetInt64());
            Assert.Equal(1, r.GetProperty("pagination").GetProperty("total").GetInt32());
        }
        // Asking for a blocked app or window by name finds nothing.
        foreach (var q in new Dictionary<string, string>[] { new() { ["app_name"] = "BBEdit" }, new() { ["window_name"] = "Payroll" } })
        {
            var r = Json(api.Handle(Get("/search", q)));
            Assert.Empty(r.GetProperty("data").EnumerateArray());
            Assert.Equal(0, r.GetProperty("pagination").GetProperty("total").GetInt32());
        }
        // Without a block list they all come back.
        var open = Json(ApiWith(new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false)).Handle(Get("/search", new() { ["q"] = "secret" })));
        Assert.Equal(6, open.GetProperty("pagination").GetProperty("total").GetInt32());   // all but the frame saved while blocked
    }

    [Fact]
    public void BlockedFramePictureIsNotFound()
    {
        var (visible, hidden) = AddMix();
        var api = ApiWith(List);
        Assert.Equal(200, api.Handle(Get($"/frames/{visible}")).Status);
        foreach (var id in hidden)
        {
            var r = api.Handle(Get($"/frames/{id}"));
            Assert.Equal(404, r.Status);
            Assert.Equal("frame not found", Json(r).GetProperty("error").GetString());
        }
    }

    [Fact]
    public void AppsLeaveOutBlockedApps()
    {
        AddMix();
        var apps = Json(ApiWith(List).Handle(Get("/apps"))).GetProperty("data").EnumerateArray()
            .Select(a => a.GetProperty("app_name").GetString()).ToList();
        Assert.DoesNotContain("BBEdit", apps);
        Assert.Contains("Notepad", apps);
        Assert.Contains("Chrome", apps);
    }

    [Fact]
    public void PagesSkipBlockedFramesWithoutGapsOrRepeats()
    {
        var t = DateTime.UtcNow.AddHours(-2);
        var expected = new List<long>();
        for (int i = 0; i < 450; i++)
        {
            // Two blocked frames for every visible one, so whole pages of the database are hidden.
            var visible = i % 3 == 0;
            var id = AddFrame(t.AddSeconds(i), visible ? "Notepad" : "BBEdit", "w", "budget " + i);
            if (visible) expected.Add(id);
        }
        expected.Reverse();   // newest first
        var api = ApiWith(List);
        var got = new List<long>();
        int total = -1;
        for (int offset = 0; offset < 200; offset += 40)
        {
            var r = Json(api.Handle(Get("/search", new() { ["q"] = "budget", ["limit"] = "40", ["offset"] = offset.ToString() })));
            total = r.GetProperty("pagination").GetProperty("total").GetInt32();
            Assert.Equal(150, total);   // on every page, not only once all of them were read
            got.AddRange(r.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("content").GetProperty("frame_id").GetInt64()));
        }
        Assert.Equal(expected, got);
        Assert.Equal(150, total);
    }

    [Fact]
    public void NothingVisibleGivesAnEmptyPageAndZeroTotal()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 30; i++) db.Insert(new Frame(0, Time.Iso(t.AddSeconds(i)), "BBEdit", "", true, "", "x", "ocr", 0));
        Assert.Equal(0, Api.VisibleCount(db, new SearchQuery(), List));
        Assert.False(Api.EachVisibleFrame(db, new SearchQuery(), List, 10, _ => throw new Exception("nothing is visible")));
        var r = Json(ApiWith(List).Handle(Get("/search")));
        Assert.Empty(r.GetProperty("data").EnumerateArray());
        Assert.Equal(0, r.GetProperty("pagination").GetProperty("total").GetInt32());
    }

    /// The total must not count hidden frames on any page, the first one included: comparing it with what comes
    /// back would tell whether blocked screenshots contain a word.
    [Fact]
    public void TotalCountsOnlyVisibleFramesFromTheFirstPage()
    {
        var t = DateTime.UtcNow.AddHours(-3);
        for (int i = 0; i < 600; i++)
        {
            // 150 visible; the rest hidden in every way, most of them by rules only code can check.
            var (app, window, url, blocked) = (i % 4) switch
            {
                0 => ("Notepad", "notes " + i, "", false),
                1 => (i % 8 == 1 ? "BBEdit" : "Chrome", "Payroll " + i, "", false),
                2 => ("Chrome", "Statement " + i, $"https://secure.chase.com/s/{i}", false),
                _ => (i % 8 == 3 ? "Notepad" : "Firefox", i % 8 == 3 ? "" : $"Tab {i} — Private Browsing", "", i % 8 == 3),
            };
            AddFrame(t.AddSeconds(i), app, window, "budget overdraft " + i, url, blocked);
        }
        var api = ApiWith(List);
        foreach (var limit in new[] { "1", "40", "100" })
        {
            var r = Json(api.Handle(Get("/search", new() { ["q"] = "overdraft", ["limit"] = limit })));
            Assert.Equal(150, r.GetProperty("pagination").GetProperty("total").GetInt32());
            Assert.False(r.GetProperty("pagination").TryGetProperty("truncated", out _));
            Assert.All(r.GetProperty("data").EnumerateArray(), d => Assert.Equal("Notepad", d.GetProperty("content").GetProperty("app_name").GetString()));
        }
        // Without a word too, and with audio in the mix (none here).
        Assert.Equal(150, Json(api.Handle(Get("/search", new() { ["limit"] = "5" }))).GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Equal(150, Api.VisibleCount(db, new SearchQuery { Q = "budget" }, List));
    }

    [Fact]
    public void AScanNeverReturnsAFrameTwiceWhileRecordingGoesOn()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < 100; i++) AddFrame(t.AddSeconds(i), i % 2 == 0 ? "Notepad" : "BBEdit", "w", "x");
        var seen = new List<Frame>();
        Api.EachVisibleFrame(db, new SearchQuery(), List, 7, f =>
        {
            seen.Add(f);
            // New frames land at the top while the scan pages down; OFFSET paging would shift and repeat one.
            AddFrame(DateTime.UtcNow.AddSeconds(seen.Count), "Notepad", "w", "x");
            return true;
        });
        Assert.Equal(50, seen.Count);
        Assert.Equal(seen.Count, seen.Select(f => f.Id).Distinct().Count());
        Assert.True(seen.Zip(seen.Skip(1)).All(p => string.CompareOrdinal(p.First.Timestamp, p.Second.Timestamp) >= 0));
        // The scan reads no text; the page is read in full afterwards.
        Assert.All(seen, f => Assert.Equal("", f.Text));
    }

    [Fact]
    public void AScanStopsAfterTooManyHiddenFrames()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Notepad", "old", "x");
        for (int i = 1; i <= 50; i++) AddFrame(t.AddSeconds(i), "Chrome", "Payroll " + i, "x");
        AddFrame(t.AddSeconds(60), "Notepad", "new", "x");
        var seen = new List<string>();
        Assert.True(Api.EachVisibleFrame(db, new SearchQuery(), List, 10, f => { seen.Add(f.WindowName); return true; }, maxHidden: 20));
        Assert.Equal(new[] { "new" }, seen);
        seen.Clear();
        Assert.False(Api.EachVisibleFrame(db, new SearchQuery(), List, 10, f => { seen.Add(f.WindowName); return true; }));
        Assert.Equal(new[] { "new", "old" }, seen);
    }

    [Fact]
    public void ReadOnlyOpenWithoutTheBlockedColumnStillReadsVideoFrames()
    {
        // Each optional column is checked on its own: a database with video columns but no blocked mark
        // keeps its video paths.
        var path = Path.Combine(home, "noblocked", "db.sqlite");
        long id;
        using (var fresh = new Database(path))
        {
            id = fresh.Insert(new Frame(0, Time.Iso(DateTime.UtcNow), "Notepad", "notes", true, "", "hello", "ocr", 0));
            fresh.Exec($"UPDATE frames SET video_path = 'v.mp4', video_frame = 3 WHERE id = {id}");
        }
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP INDEX IF EXISTS idx_frames_insights; ALTER TABLE frames DROP COLUMN blocked;";
            cmd.ExecuteNonQuery();
        }
        using var ro = Database.OpenReadOnly(path);
        var f = ro.FrameById(id)!;
        Assert.Equal(("v.mp4", 3, false), (f.VideoPath, f.VideoFrame, f.Blocked));
        Assert.Equal(1, Api.VisibleCount(ro, new SearchQuery { Q = "hello" }, List));
        Assert.Single(ro.SearchKeys(new SearchQuery { Q = "hello", SkipBlocked = true }));
    }

    [Fact]
    public async Task BlockedFramesNeverComeBackOverHttp()
    {
        var (visible, hidden) = AddMix();
        var api = ApiWith(List);
        using var server = new Server(39000 + Random.Shared.Next(1000), api.Handle);
        server.Start();
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/search?q=secret");
        Assert.Contains($"\"frame_id\":{visible}", body);
        foreach (var word in new[] { "BBEdit", "chase", "Payroll", "Private Browsing", "salaries", "balance", "draft" })
            Assert.DoesNotContain(word, body, StringComparison.OrdinalIgnoreCase);
        foreach (var id in hidden)
            Assert.Equal(404, (int)(await http.GetAsync($"http://127.0.0.1:{server.Port}/frames/{id}")).StatusCode);
    }
}
