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
        Assert.Equal(Path.Combine(dir, "2026-10-19"), Path.GetDirectoryName(p));
        Assert.Matches(@"^2026-10-19T14-05-00Z-DISPLAY1-[0-9a-f]{8}\.mp4$", Path.GetFileName(p));
        // Never the same name twice, even for the same window and screen.
        Assert.NotEqual(p, Compaction.SegmentPath(dir, start, @"\\.\DISPLAY1", _ => false));
        // A name that is taken (a file, a temp file, or a path the database still points at) is skipped.
        int asked = 0;
        Compaction.SegmentPath(dir, start, "x", _ => asked++ == 0);
        Assert.Equal(2, asked);
        Assert.EndsWith(".tmp.mp4", Compaction.TempPath(p));
        Assert.NotEqual(p, Compaction.TempPath(p));
    }

    [Fact]
    public void CheckingANewVideoLooksAtFramesInTheMiddleToo()
    {
        Assert.Equal(new[] { 0, 3, 6, 9 }, Compaction.CheckedFrames(10));
        Assert.Equal(new[] { 0, 1 }, Compaction.CheckedFrames(2));
        Assert.Equal(new[] { 0 }, Compaction.CheckedFrames(1));
        Assert.Empty(Compaction.CheckedFrames(0));
    }

    [Fact]
    public void VeryWideCapturesAreScaledDownToWhatEncodersTake()
    {
        Assert.Equal((964, 542), Compaction.Fit(965, 543));
        Assert.Equal((4096, 2304), Compaction.Fit(4096, 2304));
        Assert.Equal((4096, 1152), Compaction.Fit(5120, 1440));
        Assert.Equal((4096, 2304), Compaction.Fit(7680, 4320));
        Assert.Equal((2048, 2304), Compaction.Fit(4000, 4500));

        var id = AddStill(now.AddHours(-2), "wide", w: 5120, h: 1440);
        Assert.Equal(1, Compactor().RunOnce(compress: true, now).Frames);
        var v = FakeCodec.Read(db.FrameById(id)!.VideoPath);
        Assert.Equal((4096, 1152), (v.Width, v.Height));
        Assert.Equal((4096, 1152), (db.Segments().Single().Width, db.Segments().Single().Height));
        Assert.Equal("wide", Picture(id));
    }

    [Fact]
    public void AStillThatKeepsFailingIsNotTriedForever()
    {
        var id = AddStill(now.AddHours(-2), "a");
        codec.FailWrite = true;
        for (int i = 0; i < Compaction.MaxTries; i++) Assert.Equal(1, Compactor().RunOnce(compress: true, now).Failed);
        Assert.Equal(0, Compactor().RunOnce(compress: true, now).Failed);
        codec.FailWrite = false;
        Assert.Equal(0, Compactor().RunOnce(compress: true, now).Frames);
        Assert.Equal("a", Picture(id));
        // Other stills still go into videos.
        var other = AddStill(now.AddHours(-2).AddSeconds(5), "b");
        Assert.Equal(1, Compactor().RunOnce(compress: true, now).Frames);
        Assert.NotEqual("", db.FrameById(other)!.VideoPath);
    }

    [Fact]
    public void ManyBatchesAreReadInOnePass()
    {
        // Each still in its own window; two per read, and the last window of a full read waits for the next.
        var ids = Enumerable.Range(0, 5).Select(i => AddStill(now.AddHours(-3).AddSeconds(i * 301), $"p{i}")).ToList();
        var c = Compactor();
        c.BatchSize = 2;
        var r = c.RunOnce(compress: true, now);
        Assert.Equal(5, r.Frames);
        Assert.All(ids, id => Assert.NotEqual("", db.FrameById(id)!.VideoPath));
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
        // So does Export Clip, which reads only its range.
        var clip = db.TimelineFrames(now.AddHours(-4), now.AddHours(-2));
        Assert.Equal(7, clip.Count(t => t.Source.IsVideo));
        Assert.Equal("p3", codec.Load(clip.First(t => t.Id == ids[3]).Source));
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
    public void CleanUpKeepsAVideoAFramePointsAt()
    {
        var ids = Enumerable.Range(0, 3).Select(i => AddStill(now.AddHours(-2).AddSeconds(i * 2), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        var video = db.FrameById(ids[0])!.VideoPath;
        // Its row is gone (say, a delete cut short), but frames still point at it: not an orphan.
        db.Exec("DELETE FROM video_segments");
        Compactor().RunOnce(compress: false, now);
        Assert.True(File.Exists(video));
        Assert.Equal(new[] { "p0", "p1", "p2" }, ids.Select(Picture));
    }

    [Fact]
    public void CleanUpDeletesNothingWhenTheDatabaseCantBeRead()
    {
        var id = AddStill(now.AddHours(-2), "a");
        Compactor().RunOnce(compress: true, now);
        var video = db.FrameById(id)!.VideoPath;
        db.Exec("ALTER TABLE video_segments RENAME TO video_segments_gone");
        Assert.ThrowsAny<Exception>(() => Compactor().RunOnce(compress: false, now));
        Assert.True(File.Exists(video));
        db.Exec("ALTER TABLE video_segments_gone RENAME TO video_segments");
        Assert.Equal("a", Picture(id));
    }

    [Fact]
    public void AVideoThatLostFramesWithoutTheMarkIsWrittenAgain()
    {
        var ids = Enumerable.Range(0, 4).Select(i => AddStill(now.AddHours(-2).AddSeconds(i * 2), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        // Another process (or an older copy of the app) deletes a row and nothing else.
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={config.DbPath};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"DELETE FROM frames WHERE id = {ids[1]}";
            cmd.ExecuteNonQuery();
        }
        var r = Compactor().RunOnce(compress: false, now);
        Assert.Equal(1, r.Rewritten);
        Assert.Equal(new[] { "p0", "p2", "p3" }, AllVideoPictures().OrderBy(p => p));
        Assert.Equal(new[] { "p0", "p2", "p3" }, new[] { ids[0], ids[2], ids[3] }.Select(Picture));
    }

    [Fact]
    public void ADeleteFromAnotherProcessDuringARewriteIsNotLost()
    {
        var t = now.AddHours(-2);
        var ids = Enumerable.Range(0, 6).Select(i => AddStill(t.AddSeconds(i * 10), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        db.DeleteFrames(t.AddSeconds(15), t.AddSeconds(25));   // p2
        using var other = new Database(config.DbPath);
        bool once = false;
        codec.AfterVerify = () => { if (!once) { once = true; other.DeleteFrames(t.AddSeconds(35), t.AddSeconds(45)); } };   // p4
        var r = Compactor().RunOnce(compress: false, now);
        // The rewrite would have kept p4: it is dropped, and the video stays marked.
        Assert.Equal(0, r.Rewritten);
        Assert.Equal(0, r.Unpacked);
        Assert.True(db.Segments().Single().NeedsRewrite);
        Assert.Equal(1, Compactor().RunOnce(compress: false, now).Rewritten);
        Assert.Equal(new[] { "p0", "p1", "p3", "p5" }, AllVideoPictures().OrderBy(p => p));
        Assert.Equal(0, db.StuckRewrites());
    }

    [Fact]
    public void AVideoThatCantBeWrittenAgainBecomesPictures()
    {
        var t = now.AddHours(-2);
        var ids = Enumerable.Range(0, 4).Select(i => AddStill(t.AddSeconds(i * 10), $"p{i}")).ToList();
        Compactor().RunOnce(compress: true, now);
        var video = db.FrameById(ids[0])!.VideoPath;
        db.DeleteFrames(t.AddSeconds(5), t.AddSeconds(15));   // p1
        Assert.Equal(1, db.StuckRewrites());
        codec.FailWrite = true;   // say, the encoder is gone
        var r = Compactor().RunOnce(compress: false, now);
        Assert.Equal((0, 1, 0), (r.Rewritten, r.Unpacked, r.Failed));
        // The deleted picture is in no file; the others are stills again.
        Assert.False(File.Exists(video));
        Assert.Empty(AllVideoPictures());
        Assert.Empty(db.Segments());
        Assert.Equal(0, db.StuckRewrites());
        foreach (var (id, p) in new[] { (ids[0], "p0"), (ids[2], "p2"), (ids[3], "p3") })
        {
            var f = db.FrameById(id)!;
            Assert.Equal("", f.VideoPath);
            Assert.True(File.Exists(f.SnapshotPath));
            Assert.Equal(p, Picture(id));
        }
    }

    [Fact]
    public void ReferencedPathsAreTaken()
    {
        var id = db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "t", "ocr", 0));
        db.Exec($"UPDATE frames SET video_path = 'a.mp4' WHERE id = {id}");
        db.Exec("INSERT INTO video_segments (path, start_time, end_time, width, height, frame_count) VALUES ('b.mp4', '', '', 2, 2, 1)");
        Assert.True(db.IsVideoReferenced("a.mp4"));
        Assert.True(db.IsVideoReferenced("b.mp4"));
        Assert.False(db.IsVideoReferenced("c.mp4"));
    }

    [Fact]
    public void ReadOnlyOpenOfADatabaseWithoutVideoColumnsReadsStills()
    {
        // A database the recording app has not migrated yet (the MCP server may start first).
        var path = Path.Combine(home, "unmigrated", "db.sqlite");
        long id;
        using (var fresh = new Database(path))
            id = fresh.Insert(new Frame(0, Time.Iso(now), "Code", "main.cs", true, "x.jpg", "hello unmigrated", "ocr", 0));
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                DROP INDEX idx_frames_video; DROP TABLE video_segments; DROP TABLE video_skip;
                ALTER TABLE frames DROP COLUMN display; ALTER TABLE frames DROP COLUMN video_path;
                ALTER TABLE frames DROP COLUMN video_frame; ALTER TABLE frames DROP COLUMN video_time;
                """;
            cmd.ExecuteNonQuery();
        }
        using var ro = Database.OpenReadOnly(path);
        Assert.Equal(new FrameSource("x.jpg"), ro.FrameById(id)!.Source);
        Assert.Equal((0, 0L, 0L), ro.VideoStats());
        Assert.Contains("main.cs", new AiTools(ro).Run(new AiToolCall("1", "search_screen", new System.Text.Json.Nodes.JsonObject { ["query"] = "unmigrated" })));
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
    public void LruKeepsItsTotalWeightUnderTheCap()
    {
        var dropped = new List<int>();
        var lru = new Lru<string, int>(10, dropped.Add, v => v, maxWeight: 10);
        lru.Set("a", 4);
        lru.Set("b", 5);
        lru.Set("c", 3);   // 12: "a" goes
        Assert.Equal(new[] { 4 }, dropped);
        Assert.Equal(8, lru.Weight);
        lru.Set("d", 11);  // bigger than the whole cache: not kept
        Assert.Equal(new[] { 4, 11 }, dropped);
        Assert.Equal(2, lru.Count);
        Assert.Equal(8, lru.Weight);
        lru.RemoveWhere(_ => true);
        Assert.Equal(0, lru.Weight);
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
/// Files are read and written through RecordingFile, like the real codec, so it works with encryption on.
sealed class FakeCodec : IVideoCodec
{
    public bool FailWrite, FailVerify;
    /// Runs once the new video checked out, before the compactor commits it.
    public Action? AfterVerify;

    public record Video(int Width, int Height, string Codec, List<string> Pictures);

    public static Video Read(string path) => JsonSerializer.Deserialize<Video>(RecordingFile.Read(path))!;

    static string Text(string path) => System.Text.Encoding.UTF8.GetString(RecordingFile.Read(path));

    public string? Load(FrameSource f)
    {
        try
        {
            if (f.IsVideo)
            {
                var v = Read(f.VideoPath);
                return f.VideoFrame < v.Pictures.Count ? v.Pictures[f.VideoFrame] : null;
            }
            return Text(f.StillPath).Split(':')[1];
        }
        catch (IOException) { return null; }
    }

    public (int Width, int Height)? Measure(FrameSource f)
    {
        try
        {
            if (f.IsVideo) { var v = Read(f.VideoPath); return (v.Width, v.Height); }
            var wh = Text(f.StillPath).Split(':')[0].Split('x');
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

    public bool Verify(string path, IReadOnlyList<FrameSource> frames, int width, int height)
    {
        if (FailVerify) return false;
        var v = Read(path);
        var ok = v.Width == width && v.Height == height && v.Pictures.Count == frames.Count &&
                 Compaction.CheckedFrames(frames.Count).All(i => v.Pictures[i] == Load(frames[i]));
        AfterVerify?.Invoke();
        return ok;
    }

    public void WriteStill(FrameSource frame, string path)
    {
        var size = Measure(frame) ?? throw new IOException("unreadable frame");
        var pic = Load(frame) ?? throw new IOException("unreadable frame");
        RecordingFile.Write(path, System.Text.Encoding.UTF8.GetBytes($"{size.Width}x{size.Height}:{pic}:" + new string('.', 2000)));
    }

    public void Release(string path) { }
}

[CollectionDefinition(EncryptedRecordingsTests.Collection, DisableParallelization = true)]
public class EncryptedRecordingsCollection { }

/// Encrypt Recordings turned on for the whole process (RecordingFile.Key and EncryptNew), as the app does:
/// these run on their own, never alongside other tests. Clips for both speech engines and compressed
/// videos are encrypted on disk and still read back.
[Collection(Collection)]
public class EncryptedRecordingsTests : IDisposable
{
    public const string Collection = "Encrypt Recordings on";
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-encrypted-test-" + Guid.NewGuid());
    readonly Config config;
    readonly byte[] key = RecordingFile.NewKey();
    readonly byte[]? keyBefore = RecordingFile.Key;
    readonly bool encryptBefore = RecordingFile.EncryptNew;
    readonly Func<byte[]?>? loaderBefore = RecordingFile.KeyLoader;
    readonly DateTime now = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    public EncryptedRecordingsTests()
    {
        config = Config.Load(new[] { "--home", home });
        RecordingFile.Key = key;
        RecordingFile.EncryptNew = true;
        RecordingFile.KeyLoader = null;
    }

    public void Dispose()
    {
        RecordingFile.Key = keyBefore;
        RecordingFile.EncryptNew = encryptBefore;
        RecordingFile.KeyLoader = loaderBefore;
        try { Directory.Delete(home, true); } catch { }
    }

    [Fact]
    public void BothSpeechEnginesReadAnEncryptedClip()
    {
        var dir = Path.Combine(config.DataDir, "audio", "2026-10-19");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "12-00-00.wav");
        var samples = Enumerable.Range(0, 1600).Select(i => (float)Math.Sin(i / 10.0) * 0.5f).ToArray();
        Wav.Write(samples, 16000, path);
        Assert.True(RecordingFile.IsEncrypted(path));

        // The same samples as the plain clip gives.
        Assert.Equal(Wav.Load16k(Wav.Bytes(samples, 16000)), Transcriber.LoadClipSamples(path));

        // No key: the transcriber is told so (it waits instead of using up the clip's tries).
        RecordingFile.Key = null;
        var e = Assert.ThrowsAny<Exception>(() => Transcriber.LoadClipSamples(path));
        Assert.True(RecordingFile.IsNoKey(e));
    }

    [Fact]
    public void CompressedVideosAreSavedEncryptedAndStillRead()
    {
        using var db = new Database(config.DbPath, key, createEncrypted: true);
        var codec = new FakeCodec();
        var ids = new List<long>();
        for (int i = 0; i < 4; i++)
        {
            var t = now.AddHours(-3).AddSeconds(i * 2);
            var dir = Path.Combine(config.DataDir, Time.Iso(t)[..10]);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Time.Iso(t).Replace(':', '-') + ".jpg");
            RecordingFile.Write(file, System.Text.Encoding.UTF8.GetBytes($"64x40:p{i}:" + new string('.', 2000)));
            ids.Add(db.Insert(new Frame(0, Time.Iso(t), "App", "Window", true, file, $"text p{i}", "ocr", 0, Display: "screen1")));
        }
        var compactor = new VideoCompactor(db, codec, config.DataDir) { OrphanAge = TimeSpan.Zero };
        var r = compactor.RunOnce(compress: true, now);
        Assert.Equal(4, r.Frames);
        Assert.Equal(0, r.Failed);

        // The video is encrypted, and no plain video (or its temp file) was left under data/.
        var files = Directory.GetFiles(config.DataDir, "*", SearchOption.AllDirectories);
        var video = Assert.Single(files);
        Assert.EndsWith(".mp4", video);
        Assert.True(RecordingFile.IsEncrypted(video));
        foreach (var (id, i) in ids.Select((id, i) => (id, i))) Assert.Equal($"p{i}", codec.Load(db.FrameById(id)!.Source));

        // A delete rewrites the video, encrypted again.
        db.DeleteFrames(now.AddHours(-3).AddSeconds(1), now.AddHours(-3).AddSeconds(3));
        Assert.Equal(1, compactor.RunOnce(compress: false, now).Rewritten);
        var left = Directory.GetFiles(config.DataDir, "*", SearchOption.AllDirectories);
        Assert.All(left, f => Assert.True(RecordingFile.IsEncrypted(f)));
        Assert.Equal(new[] { "p0", "p2", "p3" }, ids.Select(id => db.FrameById(id)).OfType<Frame>().Select(f => codec.Load(f.Source)));

        // Deleting the rest deletes the encrypted video too.
        db.DeleteFrames(null, now);
        compactor.RunOnce(compress: false, now);
        Assert.Empty(Directory.GetFiles(config.DataDir, "*.mp4", SearchOption.AllDirectories));
    }
}
