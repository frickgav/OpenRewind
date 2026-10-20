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
            got.AddRange(r.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("content").GetProperty("frame_id").GetInt64()));
        }
        Assert.Equal(expected, got);
        Assert.Equal(150, total);
    }

    [Fact]
    public void NothingVisibleGivesAnEmptyPageAndZeroTotal()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        var rows = Enumerable.Range(0, 30).Select(i => new Frame(i, Time.Iso(t.AddSeconds(i)), "BBEdit", "", true, "", "x", "ocr", 0)).ToList();
        for (int i = 0; i < 30; i++) db.Insert(rows[i]);
        var (shown, total) = Api.VisibleFrames(db, new SearchQuery(), 10, List.HidesFrame);
        Assert.Empty(shown);
        Assert.Equal(0, total);
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
