using System.Text;
using System.Text.Json;
using OpenRewind;
using Xunit;

public class CoreTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-test-" + Guid.NewGuid());
    readonly Config config;
    readonly Database db;

    public CoreTests()
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

    [Fact]
    public void TimestampsMatchTheMacFormat()
    {
        var d = new DateTime(2026, 10, 18, 19, 45, 5, 123, DateTimeKind.Utc);
        Assert.Equal("2026-10-18T19:45:05.123Z", Time.Iso(d));
        Assert.Equal(d, Time.ParseIso("2026-10-18T19:45:05.123Z"));
        Assert.Equal("2026-10-18T17:45:05.123Z", Time.Parse("2h ago", d));
        Assert.Equal("2026-10-18T19:15:05.123Z", Time.Parse("30m ago", d));
        Assert.Null(Time.Parse("soon"));
        Assert.Equal("2026-10-18T19:45:05.000Z", Time.Parse("2026-10-18T19:45:05Z"));
    }

    [Fact]
    public void FtsQueryQuotesEveryWord()
    {
        Assert.Equal("\"hello\"* \"wor\"*", Database.FtsQuery("hello  wor\"\""));
        Assert.Null(Database.FtsQuery("   "));
    }

    [Fact]
    public void SearchFindsScreenTextByPrefix()
    {
        var now = DateTime.UtcNow;
        AddFrame(now.AddMinutes(-2), "Chrome", "quarterly revenue report");
        AddFrame(now.AddMinutes(-1), "Code", "fn main() {}");
        var (rows, total) = db.Search(new SearchQuery { Q = "quart rev" });
        Assert.Equal(1, total);
        Assert.Equal("Chrome", rows[0].AppName);
        Assert.Equal(2, db.Search(new SearchQuery()).Total);
        Assert.Single(db.Search(new SearchQuery { AppName = "cod" }).Rows);
    }

    [Fact]
    public void TranscriptsAreSearchableAndMarkTheChunk()
    {
        var id = db.InsertChunk(Path.Combine(config.DataDir, "a.wav"), Time.Now(), "microphone", true);
        var chunk = db.NextPendingChunk()!;
        Assert.Equal(id, chunk.Id);
        db.SaveTranscripts(chunk, new[] { new Segment(0, 2, "let's ship the windows build"), new Segment(2, 4, "on friday") });
        Assert.Equal("transcribed", db.ChunkById(id)!.Status);
        Assert.Null(db.NextPendingChunk());
        var (rows, total) = db.SearchAudio(new SearchQuery { Q = "windows" }, 10);
        Assert.Equal(1, total);
        Assert.Equal(id, rows[0].ChunkId);
    }

    [Fact]
    public void FailedChunksGiveUpAfterThreeTries()
    {
        var id = db.InsertChunk("x.wav", Time.Now(), "system audio", false);
        for (int i = 0; i < 3; i++) db.MarkChunk(id, "pending", "boom");
        Assert.Equal("failed", db.ChunkById(id)!.Status);
    }

    [Fact]
    public void DeleteRemovesRowsFilesAndIndex()
    {
        var now = DateTime.UtcNow;
        AddFrame(now.AddHours(-3), "Old", "ancient words");
        AddFrame(now.AddMinutes(-1), "New", "fresh words");
        Assert.Equal(1, db.DeleteFrames(null, now.AddHours(-1)));
        Assert.Equal(0, db.Search(new SearchQuery { Q = "ancient" }).Total);
        Assert.Equal(1, db.Search(new SearchQuery { Q = "words" }).Total);
        Assert.Single(Directory.GetFiles(config.DataDir, "*.jpg", SearchOption.AllDirectories));
    }

    [Fact]
    public void WavRoundTripsAt16k()
    {
        var path = Path.Combine(home, "t.wav");
        var s = Enumerable.Range(0, 16000).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        Wav.Write(s, 16000, path);
        Assert.Equal(44 + 32000, new FileInfo(path).Length);
        var back = Wav.Load16k(path);
        Assert.InRange(back.Length, 15990, 16000);
        Assert.InRange(back[100] - s[100], -0.001f, 0.001f);
    }

    [Fact]
    public void ResamplerTurns48kStereoInto16kMono()
    {
        var r = new Resampler();
        int total = 0;
        for (int b = 0; b < 10; b++) total += r.Convert(new float[4800 * 2], 2, 48000).Length;  // 10 x 100 ms
        Assert.InRange(total, 15990, 16000);
    }

    [Fact]
    public void WhisperParseDropsMadeUpLines()
    {
        var json = """{"transcription":[{"offsets":{"from":0,"to":1500},"text":" Hello team."},{"offsets":{"from":1500,"to":3000},"text":" [BLANK_AUDIO]"},{"offsets":{"from":3000,"to":4000},"text":" Thanks for watching!"}]}""";
        var segs = Transcriber.Parse(json);
        Assert.Single(segs);
        Assert.Equal("Hello team.", segs[0].Text);
        Assert.Equal(1.5, segs[0].End);
        Assert.Empty(Transcriber.Parse("""{"transcription":[{"offsets":{"from":0,"to":900},"text":" Thank you."}]}"""));
    }

    [Fact]
    public void AxisShrinksBreaksAndRoundTrips()
    {
        var t0 = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
        var frames = new[] { 0, 60, 120, 7200, 7260 }.Select((s, i) => new TimelineFrame(i, t0.AddSeconds(s), "A", "", "")).ToList();
        var sessions = TimelineAxis.BuildSessions(frames, Array.Empty<TimelineAudio>(), t0.AddSeconds(7262));
        Assert.Equal(2, sessions.Count);
        var axis = new TimelineAxis(sessions, 30);
        Assert.Equal(122 + 30, axis.V(t0.AddSeconds(7200)), 3);
        foreach (var s in new[] { 10, 100, 3600, 7230 })
            Assert.Equal(t0.AddSeconds(s), axis.Date(axis.V(t0.AddSeconds(s))));
        Assert.Equal("1h 57m break", TimelineAxis.BreakLabel(sessions[1].Start - sessions[0].End));
    }

    [Fact]
    public void ApiSearchMergesScreenAndAudio()
    {
        AddFrame(DateTime.UtcNow.AddMinutes(-1), "Notepad", "budget plan");
        var cid = db.InsertChunk("x.wav", Time.Now(), "microphone", true);
        db.SaveTranscripts(db.ChunkById(cid)!, new[] { new Segment(0, 1, "the budget is fine") });
        var api = new Api(db, config);
        var r = api.Handle(new HttpRequest("GET", "/search", new() { ["q"] = "budget" }));
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal(2, doc.RootElement.GetProperty("pagination").GetProperty("total").GetInt32());
        Assert.Equal("Audio", doc.RootElement.GetProperty("data")[0].GetProperty("type").GetString());
        Assert.Equal(404, api.Handle(new HttpRequest("GET", "/frames/999", new())).Status);
        Assert.Equal(405, api.Handle(new HttpRequest("POST", "/search", new())).Status);
    }

    [Fact]
    public void SearchPageComesFromTheSwiftSource()
    {
        Assert.StartsWith("<!doctype html>", WebUI.Html);
        Assert.EndsWith("</html>", WebUI.Html);
    }

    [Fact]
    public async Task ServerAnswersOverHttp()
    {
        var api = new Api(db, config);
        using var server = new Server(0 + 39000 + Random.Shared.Next(1000), api.Handle);
        server.Start();
        using var http = new HttpClient();
        var body = await http.GetStringAsync($"http://127.0.0.1:{server.Port}/health");
        Assert.Contains("\"status\":\"healthy\"", body);
        var miss = await http.GetAsync($"http://127.0.0.1:{server.Port}/frames/../../etc/passwd");
        Assert.Equal(404, (int)miss.StatusCode);
    }

    [Fact]
    public void MovedDataFolderGetsItsPathsRewritten()
    {
        var sep = Path.DirectorySeparatorChar;
        var old = Path.Combine(Path.GetTempPath(), "old-home-ü") + sep;
        db.Insert(new Frame(0, Time.Now(), "A", "", true, old + "data" + sep + "x.jpg", "t", "ocr", 0));
        db.InsertChunk(old + "data" + sep + "audio" + sep + "y.wav", Time.Now(), "microphone", true);
        db.InsertChunk("/elsewhere/z.wav", Time.Now(), "microphone", true);
        File.WriteAllText(Path.Combine(home, Config.MovedFromFile), old);
        using (var reopened = new Database(config.DbPath))
        {
            var fresh = Path.GetFullPath(home).TrimEnd(sep) + sep;
            Assert.Equal(fresh + "data" + sep + "x.jpg", reopened.Search(new SearchQuery()).Rows[0].SnapshotPath);
            Assert.Equal(fresh + "data" + sep + "audio" + sep + "y.wav", reopened.ChunkById(1)!.FilePath);
            Assert.Equal("/elsewhere/z.wav", reopened.ChunkById(2)!.FilePath);
        }
        Assert.False(File.Exists(Path.Combine(home, Config.MovedFromFile)));
    }

    [Fact]
    public void SnippetCentersOnTheMatch()
    {
        var text = new string('a', 300) + " needle " + new string('b', 300);
        var s = SearchHits.Snippet(text, "needle");
        Assert.StartsWith("…", s);
        Assert.Contains("needle", s);
        Assert.EndsWith("…", s);
    }
}
