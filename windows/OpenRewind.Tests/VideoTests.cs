using System.Text.Json;
using OpenRewind;
using Xunit;

/// Compress Older Recordings: grouping, the crash-safe order, and deletes of compressed frames.
/// A fake codec stands in for Media Foundation: a "video" is a JSON list of the pictures it holds.
public class VideoTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-video-test-" + Guid.NewGuid());
    readonly Config config;
    readonly Database db;
    readonly FakeCodec codec = new();
    readonly DateTime now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public VideoTests()
    {
        config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    VideoCompactor Compactor() => new(db, codec, config.DataDir) { OrphanAge = TimeSpan.Zero };

    /// A still whose "picture" is `pixels` (the fake codec reads it back).
    long AddStill(DateTime t, string pixels, string display = "screen1", int w = 64, int h = 40)
    {
        var dir = Path.Combine(config.DataDir, Time.Iso(t)[..10]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Time.Iso(t).Replace(':', '-') + ".jpg");
        File.WriteAllText(file, $"{w}x{h}:{pixels}:" + new string('.', 2000));
        return db.Insert(new Frame(0, Time.Iso(t), "App", "Window", true, file, "text " + pixels, "ocr", 0, Display: display));
    }

    string? Picture(long id) => db.FrameById(id) is Frame f ? codec.Load(f.Source) : null;

    List<string> AllVideoPictures() =>
        Directory.Exists(Path.Combine(config.DataDir, "video"))
            ? Directory.GetFiles(Path.Combine(config.DataDir, "video"), "*.mp4", SearchOption.AllDirectories)
                       .SelectMany(p => FakeCodec.Read(p).Pictures).ToList()
            : new();

    [Fact]
    public void PlanGroupsByWindowAndScreenAndWaitsForRecentWindows()
    {
        var t0 = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc);
        var c = new List<CompactCandidate>
        {
            new(1, t0.AddSeconds(10), "A", "1"), new(2, t0.AddSeconds(20), "B", "2"), new(3, t0.AddSeconds(30), "A", "3"),
            new(4, t0.AddMinutes(5).AddSeconds(1), "A", "4"),   // next window
            new(5, t0.AddMinutes(58), "A", "5"),                // window ends after the cutoff
        };
        var plans = Compaction.Plan(c, t0.AddMinutes(59), Compaction.Window, batchFull: false);
        Assert.Equal(3, plans.Count);
        Assert.Equal(new long[] { 1, 3 }, plans[0].Frames.Select(f => f.Id));
        Assert.Equal("B", plans[1].Display);
        Assert.Equal(t0.AddMinutes(5), plans[2].WindowStart);
        // A full batch may stop in the middle of its last window: that one waits for the next read.
        Assert.Equal(2, Compaction.Plan(c.Take(4).ToList(), t0.AddMinutes(60), Compaction.Window, batchFull: true).Count);
        // Windows start on the clock, so a video never spans midnight.
        Assert.Equal(new DateTime(2026, 10, 19, 23, 55, 0, DateTimeKind.Utc),
                     Compaction.WindowStart(new DateTime(2026, 10, 19, 23, 59, 59, DateTimeKind.Utc), Compaction.Window));
    }

    [Fact]
    public void RunsSplitWhereThePictureSizeChanges()
    {
        var runs = Compaction.SplitBySize(new (int, (int, int)?)[] { (1, (10, 8)), (2, (10, 8)), (3, null), (4, (12, 8)), (5, (10, 8)), (6, (10, 8)) }, max: 2);
        Assert.Equal(new[] { "10x8:1,2", "12x8:4", "10x8:5,6" }, runs.Select(r => $"{r.Width}x{r.Height}:{string.Join(",", r.Frames)}"));
        Assert.Equal(1366, Compaction.Even(1367));
        Assert.Equal(540, Compaction.Even(540));
    }

    [Fact]
    public void SegmentNamesAreUniqueAndTempFilesAreNotVideos()
    {
        var dir = Path.Combine(home, "video");
        var start = new DateTime(2026, 10, 19, 14, 5, 0, DateTimeKind.Utc);
        var p = Compaction.SegmentPath(dir, start, @"\\.\DISPLAY1", _ => false);
        Assert.Equal(Path.Combine(dir, "2026-10-19", "2026-10-19T14-05-00Z-DISPLAY1.mp4"), p);
        Assert.EndsWith("-2.mp4", Compaction.SegmentPath(dir, start, @"\\.\DISPLAY1", x => x == p));
        Assert.EndsWith("DISPLAY1.tmp.mp4", Compaction.TempPath(p));
    }

    [Fact]
    public void OldStillsBecomeVideoAndStillLoad()
    {
        var ids = new List<long>();
        for (int i = 0; i < 6; i++) ids.Add(AddStill(now.AddHours(-3).AddSeconds(i * 70), $"p{i}"));   // two windows
        ids.Add(AddStill(now.AddHours(-3).AddSeconds(30), "other", display: "screen2"));
        var recent = AddStill(now.AddMinutes(-10), "recent");

        var r = Compactor().RunOnce(compress: true, now);
        Assert.Equal(7, r.Frames);
        Assert.Equal(3, r.Videos);
        Assert.Equal(0, r.Failed);
        foreach (var (id, i) in ids.Take(6).Select((id, i) => (id, i)))
        {
            var f = db.FrameById(id)!;
            Assert.Equal("", f.SnapshotPath);
            Assert.NotEqual("", f.VideoPath);
            Assert.Equal(f.VideoFrame / Compaction.Fps, f.VideoTime);
            Assert.Equal($"p{i}", Picture(id));
        }
        Assert.Equal("other", Picture(ids[6]));
        // Recent frames and their files are left alone; the old stills are gone.
        Assert.NotEqual("", db.FrameById(recent)!.SnapshotPath);
        Assert.Equal("recent", Picture(recent));
        Assert.Single(Directory.GetFiles(config.DataDir, "*.jpg", SearchOption.AllDirectories));
        var stats = db.VideoStats();
        Assert.Equal(3, stats.Segments);
        Assert.True(stats.Saved > 0);
        // The timeline and search see the video frames too.
        Assert.Equal(8, db.TimelineFrames(now.AddDays(-1)).Count(t => codec.Load(t.Source) != null));
        Assert.Equal("p2", codec.Load(db.Search(new SearchQuery { Q = "p2" }).Rows[0].Source));
    }

    [Fact]
    public void CompressionOffLeavesStills()
    {
        var id = AddStill(now.AddHours(-3), "a");
        Assert.Equal(0, Compactor().RunOnce(compress: false, now).Frames);
        Assert.NotEqual("", db.FrameById(id)!.SnapshotPath);
        Assert.Empty(AllVideoPictures());
    }

    [Fact]
    public void StoppingAfterTheVideoIsWrittenLosesNothing()
    {
        var ids = Enumerable.Range(0, 3).Select(i => AddStill(now.AddHours(-2).AddSeconds(i * 2), $"p{i}")).ToList();
        var c = Compactor();
        c.AfterStep = step => { if (step == "written") throw new CompactionInterrupted(step); };
        Assert.Throws<CompactionInterrupted>(() => c.RunOnce(compress: true, now));
        // The video is on disk but nothing points at it; every frame is still its still.
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
        {
            Assert.Equal("", db.FrameById(id)!.VideoPath);
            Assert.Equal($"p{i}", Picture(id));
        }
        Assert.NotEmpty(AllVideoPictures());
        // The next pass deletes the stray video, then compresses again.
        Compactor().RunOnce(compress: false, now);
        Assert.Empty(AllVideoPictures());
        Assert.Equal(3, Compactor().RunOnce(compress: true, now).Frames);
        Assert.Equal(new[] { "p0", "p1", "p2" }, ids.Select(Picture));
    }

    [Fact]
    public void StoppingAfterTheCommitFinishesDeletingStillsNextTime()
    {
        var ids = Enumerable.Range(0, 3).Select(i => AddStill(now.AddHours(-2).AddSeconds(i * 2), $"p{i}")).ToList();
        var stills = ids.Select(id => db.FrameById(id)!.SnapshotPath).ToList();
        var c = Compactor();
        c.AfterStep = step => { if (step == "committed") throw new CompactionInterrupted(step); };
        Assert.Throws<CompactionInterrupted>(() => c.RunOnce(compress: true, now));
        Assert.All(stills, p => Assert.True(File.Exists(p)));
        Assert.Equal(new[] { "p0", "p1", "p2" }, ids.Select(Picture));   // from the video now

        Compactor().RunOnce(compress: false, now);
        Assert.All(stills, p => Assert.False(File.Exists(p)));
        Assert.All(ids, id => Assert.Equal("", db.FrameById(id)!.SnapshotPath));
        Assert.Equal(new[] { "p0", "p1", "p2" }, ids.Select(Picture));
    }

    [Fact]
    public void ABadVideoKeepsTheStills()
    {
        var id = AddStill(now.AddHours(-2), "a");
        codec.FailWrite = true;
        Assert.Equal(1, Compactor().RunOnce(compress: true, now).Failed);
        codec.FailWrite = false;
        codec.FailVerify = true;
        Assert.Equal(1, Compactor().RunOnce(compress: true, now).Failed);
        Assert.Equal("a", Picture(id));
        Assert.Equal("", db.FrameById(id)!.VideoPath);
        Assert.Empty(Directory.Exists(Path.Combine(config.DataDir, "video"))
            ? Directory.GetFiles(Path.Combine(config.DataDir, "video"), "*", SearchOption.AllDirectories) : Array.Empty<string>());
    }

    [Fact]
    public void AFrameDeletedWhileTheVideoIsWrittenIsNotKeptInIt()
    {
        var ids = Enumerable.Range(0, 3).Select(i => AddStill(now.AddHours(-2).AddSeconds(i * 2), $"p{i}")).ToList();
        codec.AfterVerify = () => db.DeleteFrames(now.AddHours(-2), now.AddHours(-2).AddSeconds(1));
        var r = Compactor().RunOnce(compress: true, now);
        Assert.Equal(0, r.Frames);
        Assert.Null(db.FrameById(ids[0]));
        Assert.Empty(AllVideoPictures());
        Assert.Equal(new[] { "p1", "p2" }, ids.Skip(1).Select(Picture));
    }

    [Fact]
    public void DeletingAllFramesOfAVideoDeletesTheFile()
    {
        var ids = Enumerable.Range(0, 3).Select(i => AddStill(now.AddHours(-2).AddSeconds(i * 2), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        var video = db.FrameById(ids[0])!.VideoPath;
        var released = new List<string>();
        db.ReleasingVideos += released.AddRange;
        Assert.Equal(3, db.DeleteFrames(null, now));
        Assert.False(File.Exists(video));
        Assert.Equal(new[] { video }, released);
        Assert.Equal(0, db.VideoStats().Segments);
    }

    [Fact]
    public void DeletingPartOfAVideoWritesItAgainWithoutThoseFrames()
    {
        var t = now.AddHours(-2);
        var ids = Enumerable.Range(0, 6).Select(i => AddStill(t.AddSeconds(i * 10), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        var old = db.FrameById(ids[0])!.VideoPath;
        Assert.All(ids, id => Assert.Equal(old, db.FrameById(id)!.VideoPath));

        bool asked = false;
        db.VideosNeedRewrite += () => asked = true;
        // "Delete the last hour" style: a range that cuts through the video.
        Assert.Equal(3, db.DeleteFrames(t.AddSeconds(15), t.AddSeconds(45)));
        Assert.True(asked);
        Assert.True(db.Segments(onlyNeedingRewrite: true).Single().NeedsRewrite);

        var r = Compactor().RunOnce(compress: false, now);
        Assert.Equal(1, r.Rewritten);
        Assert.False(File.Exists(old));
        var kept = new[] { ids[0], ids[1], ids[5] };
        Assert.Equal(new[] { "p0", "p1", "p5" }, kept.Select(Picture));
        Assert.Equal(new[] { 0, 1, 2 }, kept.Select(id => db.FrameById(id)!.VideoFrame));
        // The deleted pictures are in no video any more.
        Assert.Equal(new[] { "p0", "p1", "p5" }, AllVideoPictures().OrderBy(p => p));
        Assert.False(db.Segments().Single().NeedsRewrite);
        Assert.Equal(3, db.Segments().Single().FrameCount);
    }

    [Fact]
    public void RetentionKeepsVideosItOnlyPartlyCovers()
    {
        var t = now.AddDays(-2);
        var ids = Enumerable.Range(0, 4).Select(i => AddStill(t.AddSeconds(i * 30), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        // Keep-for limit falls inside the video: older frames go, the rest is written again.
        db.DeleteFrames(null, t.AddSeconds(45));
        Compactor().RunOnce(compress: false, now);
        Assert.Equal(new[] { "p2", "p3" }, ids.Skip(2).Select(Picture));
        Assert.Equal(new[] { "p2", "p3" }, AllVideoPictures().OrderBy(p => p));
    }

    [Fact]
    public void OlderDatabasesGetTheVideoColumns()
    {
        var oldPath = Path.Combine(home, "old.sqlite");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE frames (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL, app_name TEXT NOT NULL DEFAULT '',
                    window_name TEXT NOT NULL DEFAULT '', focused INTEGER NOT NULL DEFAULT 1, snapshot_path TEXT NOT NULL DEFAULT '',
                    text TEXT NOT NULL DEFAULT '', text_source TEXT NOT NULL DEFAULT 'ocr', hash INTEGER NOT NULL DEFAULT 0,
                    browser_url TEXT NOT NULL DEFAULT '');
                INSERT INTO frames (timestamp, app_name, snapshot_path) VALUES ('2026-10-18T19:45:05.123Z', 'Old', 'x.jpg');
                """;
            cmd.ExecuteNonQuery();
        }
        using var reopened = new Database(oldPath);
        var f = reopened.FrameById(1)!;
        Assert.Equal(new FrameSource("x.jpg"), f.Source);
        Assert.Equal("", f.Display);
        Assert.Equal((0, 0L, 0L), reopened.VideoStats());
    }

    [Fact]
    public void MovedDataFolderRewritesVideoPaths()
    {
        var sep = Path.DirectorySeparatorChar;
        var old = Path.Combine(Path.GetTempPath(), "old-home-v") + sep;
        var video = old + "data" + sep + "video" + sep + "a.mp4";
        var id = db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "t", "ocr", 0));
        db.Exec($"UPDATE frames SET video_path = '{video}' WHERE id = {id}");
        db.Exec($"INSERT INTO video_segments (path, start_time, end_time, width, height, frame_count) VALUES ('{video}', '', '', 2, 2, 1)");
        File.WriteAllText(Path.Combine(home, Config.MovedFromFile), old);
        using var reopened = new Database(config.DbPath);
        var fresh = Path.GetFullPath(home).TrimEnd(sep) + sep + "data" + sep + "video" + sep + "a.mp4";
        Assert.Equal(fresh, reopened.FrameById(id)!.VideoPath);
        Assert.Equal(fresh, reopened.Segments().Single().Path);
    }

    [Fact]
    public void ApiServesVideoFramesThroughTheLoader()
    {
        var id = AddStill(now.AddHours(-2), "a");
        Compactor().RunOnce(compress: true, now);
        var api = new Api(db, config, frameJpeg: f => f.VideoPath.Length > 0 ? new byte[] { 1, 2, 3 } : null);
        var r = api.Handle(new HttpRequest("GET", $"/frames/{id}", new()));
        Assert.Equal(200, r.Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, r.Body);
        var s = api.Handle(new HttpRequest("GET", "/search", new() { ["q"] = "text" }));
        using var doc = JsonDocument.Parse(s.Body);
        Assert.EndsWith(".mp4", doc.RootElement.GetProperty("data")[0].GetProperty("content").GetProperty("file_path").GetString());
    }

    [Fact]
    public void LruDropsTheLeastRecentlyUsed()
    {
        var dropped = new List<int>();
        var lru = new Lru<string, int>(2, dropped.Add);
        lru.Set("a", 1);
        lru.Set("b", 2);
        Assert.True(lru.TryGet("a", out _));
        lru.Set("c", 3);
        Assert.False(lru.TryGet("b", out _));
        Assert.Equal(new[] { 2 }, dropped);
        lru.RemoveWhere(k => k == "a");
        Assert.Equal(new[] { 2, 1 }, dropped);
        Assert.Equal(1, lru.Count);
    }

    [Fact]
    public void SimilarPicturesAllowForLossyVideo()
    {
        var a = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        Assert.True(Compaction.Similar(a, a.Select(x => (byte)(x + 5)).ToArray()));
        Assert.False(Compaction.Similar(a, a.Select(x => (byte)(255 - x)).ToArray()));
        Assert.False(Compaction.Similar(a, new byte[5]));
    }
}

/// A "video" is JSON: its size, codec and the pictures in order. A still is "WxH:picture:padding".
sealed class FakeCodec : IVideoCodec
{
    public bool FailWrite, FailVerify;
    /// Runs once the new video checked out, before the compactor commits it.
    public Action? AfterVerify;

    public record Video(int Width, int Height, string Codec, List<string> Pictures);

    public static Video Read(string path) => JsonSerializer.Deserialize<Video>(File.ReadAllText(path))!;

    public string? Load(FrameSource f)
    {
        try
        {
            if (f.IsVideo)
            {
                var v = Read(f.VideoPath);
                return f.VideoFrame < v.Pictures.Count ? v.Pictures[f.VideoFrame] : null;
            }
            return File.ReadAllText(f.StillPath).Split(':')[1];
        }
        catch (IOException) { return null; }
    }

    public (int Width, int Height)? Measure(FrameSource f)
    {
        try
        {
            if (f.IsVideo) { var v = Read(f.VideoPath); return (v.Width, v.Height); }
            var wh = File.ReadAllText(f.StillPath).Split(':')[0].Split('x');
            return (int.Parse(wh[0]), int.Parse(wh[1]));
        }
        catch (IOException) { return null; }
    }

    public string Write(IReadOnlyList<FrameSource> frames, int width, int height, string path)
    {
        if (FailWrite) throw new IOException("encoder broke");
        var pics = frames.Select(f => Load(f) ?? throw new IOException("unreadable frame")).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(new Video(width, height, "h264", pics)));
        return "h264";
    }

    public bool Verify(string path, IReadOnlyList<FrameSource> frames)
    {
        if (FailVerify) return false;
        var v = Read(path);
        var ok = v.Pictures.Count == frames.Count && v.Pictures[0] == Load(frames[0]) && v.Pictures[^1] == Load(frames[^1]);
        AfterVerify?.Invoke();
        return ok;
    }

    public void Release(string path) { }
}
