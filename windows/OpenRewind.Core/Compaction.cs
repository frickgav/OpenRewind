namespace OpenRewind;

/// Encodes and decodes the videos for Compress Older Recordings. The app's version uses Media Foundation.
public interface IVideoCodec
{
    /// Pixel size of a frame's picture, or null when it cannot be read.
    (int Width, int Height)? Measure(FrameSource frame);
    /// Write these frames, in order, as a new video at `path`: frame i at i / Compaction.Fps seconds,
    /// width x height pixels (pictures that are a pixel bigger are cut to fit). Returns the codec used
    /// ("hevc" or "h264"). Throws when it can't.
    string Write(IReadOnlyList<FrameSource> frames, int width, int height, string path);
    /// True when the video at `path` opens and its first and last frames look like those pictures.
    bool Verify(string path, IReadOnlyList<FrameSource> frames);
    /// Close anything that still has this video open, so it can be deleted.
    void Release(string path);
}

/// A few minutes of one screen's stills that become one video.
public record SegmentPlan(DateTime WindowStart, string Display, List<CompactCandidate> Frames);

/// Stands in for the app being killed in tests: VideoCompactor does not clean up after it.
public sealed class CompactionInterrupted(string step) : Exception($"interrupted after {step}");

/// Grouping rules for Compress Older Recordings, the same on the Mac (VideoCompactor.swift).
public static class Compaction
{
    /// Frame i of a video is shown at i / Fps seconds.
    public const double Fps = 1;
    /// Frames younger than this stay stills (the recent timeline stays as fast as before).
    public static readonly TimeSpan MinAge = TimeSpan.FromHours(1);
    /// One video per screen per this much time. Windows start on the clock (:00, :05, ...), so one never spans midnight.
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    /// Frames looked at per database read.
    public const int BatchSize = 2000;
    /// Most frames in one video (a short capture interval could make more in one window).
    public const int MaxFrames = 600;
    /// Keyframe every this many frames: scrubbing decodes at most this many to show one.
    public const int KeyframeInterval = 20;

    public static DateTime WindowStart(DateTime t, TimeSpan window) =>
        new(t.ToUniversalTime().Ticks - t.ToUniversalTime().Ticks % window.Ticks, DateTimeKind.Utc);

    /// Group stills (oldest first) by time window and screen. Windows that end after `cutoff` are left
    /// for later, because they may still get frames; so is the last window of a full batch, which may go
    /// on past the batch.
    public static List<SegmentPlan> Plan(IReadOnlyList<CompactCandidate> batch, DateTime cutoff, TimeSpan window, bool batchFull)
    {
        var plans = new List<SegmentPlan>();
        var byKey = new Dictionary<(DateTime, string), SegmentPlan>();
        foreach (var c in batch)
        {
            var start = WindowStart(c.Time, window);
            if (start + window > cutoff) break;
            if (!byKey.TryGetValue((start, c.Display), out var p))
            {
                p = new SegmentPlan(start, c.Display, new());
                byKey[(start, c.Display)] = p;
                plans.Add(p);
            }
            p.Frames.Add(c);
        }
        if (batchFull && plans.Count > 0)
        {
            var last = plans[^1].WindowStart;
            var complete = plans.Where(p => p.WindowStart != last).ToList();
            // One window holding a whole batch: take it as it is, the rest becomes another video.
            if (complete.Count > 0) return complete;
        }
        return plans;
    }

    /// Runs of frames with the same pixel size (a video has one size), at most `max` frames each.
    /// Frames whose size is unknown are left out.
    public static List<(int Width, int Height, List<T> Frames)> SplitBySize<T>(IEnumerable<(T Item, (int Width, int Height)? Size)> items, int max = MaxFrames)
    {
        var runs = new List<(int, int, List<T>)>();
        foreach (var (item, size) in items)
        {
            if (size is not (int w, int h) || w < 2 || h < 2) continue;
            if (runs.Count == 0 || runs[^1].Item1 != w || runs[^1].Item2 != h || runs[^1].Item3.Count >= max) runs.Add((w, h, new()));
            runs[^1].Item3.Add(item);
        }
        return runs;
    }

    /// Encoders want even sizes: an odd last row or column is cut off.
    public static int Even(int n) => Math.Max(2, n & ~1);

    /// data/video/2026-10-19/2026-10-19T14-05-00Z-<screen>.mp4 (with -2, -3 ... when taken).
    public static string SegmentPath(string videoDir, DateTime windowStart, string display, Func<string, bool> taken)
    {
        var iso = Time.Iso(windowStart);
        var dir = Path.Combine(videoDir, iso[..10]);
        var name = iso[..19].Replace(':', '-') + "Z" + (SafeName(display) is { Length: > 0 } d ? "-" + d : "");
        for (int n = 1; ; n++)
        {
            var p = Path.Combine(dir, name + (n > 1 ? $"-{n}" : "") + ".mp4");
            if (!taken(p)) return p;
        }
    }

    /// Where a video is written before it is checked. Never referenced by the database.
    public static string TempPath(string path) => path[..^4] + TempSuffix;
    public const string TempSuffix = ".tmp.mp4";

    static string SafeName(string s) =>
        new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-') is var n && n.Length > 40 ? n[..40] : n;

    /// Two small gray thumbnails show the same picture: the average pixel is off by at most `tolerance`
    /// levels (video is lossy, so they are never exactly equal).
    public static bool Similar(byte[] a, byte[] b, double tolerance = 12)
    {
        if (a.Length != b.Length || a.Length == 0) return false;
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length <= tolerance;
    }
}

/// Compress Older Recordings: turns stills older than an hour into short videos, crash-safely.
/// Each video is written to a temp file, read back and checked, renamed, then the frames are pointed
/// at it in one transaction, and only then are the stills deleted. Stopping at any point loses nothing:
/// the next pass deletes temp and unreferenced videos and finishes deleting stills already in a video.
/// It also rewrites videos that lost some of their frames to a delete, even while compression is off.
public sealed class VideoCompactor
{
    readonly Database db;
    readonly IVideoCodec codec;
    public readonly string VideoDir;
    public TimeSpan MinAge = Compaction.MinAge;
    public TimeSpan Window = Compaction.Window;
    public int BatchSize = Compaction.BatchSize;
    /// Videos no row points at are deleted once they are this old (a pass that stopped before committing).
    public TimeSpan OrphanAge = TimeSpan.FromMinutes(10);
    /// Called after "written" (video renamed into place) and "committed" (rows point at it). For tests.
    public Action<string>? AfterStep;
    /// Frames up to this time were tried since the app started; ones that failed are tried again next launch.
    string cursor = "";

    public record Result(int Frames, int Videos, int Rewritten, int Failed);

    public VideoCompactor(Database db, IVideoCodec codec, string dataDir)
    {
        this.db = db;
        this.codec = codec;
        VideoDir = Path.Combine(dataDir, "video");
    }

    /// One pass. `compress`: also turn older stills into video (the setting). `keepGoing` is asked between
    /// videos (quitting stops the pass soon); `keepCompressing` only between new videos (the setting was
    /// turned off: rewrites after a delete still finish).
    public Result RunOnce(bool compress, DateTime now, Func<bool>? keepGoing = null, Func<bool>? keepCompressing = null)
    {
        var go = keepGoing ?? (() => true);
        var compressing = keepCompressing ?? (() => true);
        keepGoing = () => go() && compressing();
        CleanUp();
        int rewritten = 0, failed = 0, frames = 0, videos = 0;
        foreach (var seg in db.Segments(onlyNeedingRewrite: true))
        {
            if (!go()) break;
            if (Rewrite(seg)) rewritten++; else failed++;
        }
        if (!compress) return new(0, 0, rewritten, failed);

        var cutoff = now - MinAge;
        while (keepGoing())
        {
            var batch = db.CompactCandidates(cutoff, cursor, BatchSize);
            var plans = Compaction.Plan(batch, cutoff, Window, batch.Count == BatchSize);
            if (plans.Count == 0) break;
            foreach (var plan in plans)
            {
                if (!keepGoing()) return new(frames, videos, rewritten, failed);
                var (n, v, f) = Compact(plan);
                frames += n; videos += v; failed += f;
                var last = Time.Iso(plan.Frames[^1].Time);
                if (string.CompareOrdinal(last, cursor) > 0) cursor = last;
            }
        }
        return new(frames, videos, rewritten, failed);
    }

    /// Finish what a stopped pass left: temp files, videos no row points at, stills already in a video.
    void CleanUp()
    {
        if (Directory.Exists(VideoDir))
        {
            var known = db.SegmentPaths();
            foreach (var f in Directory.EnumerateFiles(VideoDir, "*.mp4", SearchOption.AllDirectories).ToList())
            {
                var temp = f.EndsWith(Compaction.TempSuffix, StringComparison.OrdinalIgnoreCase);
                if (!temp && known.Contains(f)) continue;
                if (!temp && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < OrphanAge) continue;
                Delete(f);
            }
            foreach (var d in Directory.EnumerateDirectories(VideoDir))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        var left = db.LeftoverStills();
        if (left.Count == 0) return;
        foreach (var (_, p) in left) Delete(p);
        db.ClearStills(left.Select(l => l.Id));
    }

    (int Frames, int Videos, int Failed) Compact(SegmentPlan plan)
    {
        var runs = Compaction.SplitBySize(plan.Frames.Select(c => (c, codec.Measure(new FrameSource(c.StillPath)))));
        int frames = 0, videos = 0, failed = 0;
        foreach (var (w, h, run) in runs)
        {
            var path = Compaction.SegmentPath(VideoDir, plan.WindowStart, plan.Display, p => File.Exists(p) || File.Exists(Compaction.TempPath(p)));
            var sources = run.Select(c => new FrameSource(c.StillPath)).ToList();
            var ids = run.Select(c => c.Id).ToList();
            long sourceBytes = run.Sum(c => Size(c.StillPath));
            var seg = Write(path, sources, Compaction.Even(w), Compaction.Even(h), (name, bytes) =>
                new VideoSegment(0, path, plan.Display, Time.Iso(run[0].Time), Time.Iso(run[^1].Time), Compaction.Even(w), Compaction.Even(h),
                                 run.Count, name, bytes, sourceBytes));
            if (seg == null) { failed++; continue; }
            if (!db.CommitSegment(seg, ids))
            {
                // A frame was deleted while this was written: drop the video, the rest goes next launch.
                Delete(path);
                continue;
            }
            AfterStep?.Invoke("committed");
            foreach (var c in run) Delete(c.StillPath);
            db.ClearStills(ids);
            frames += run.Count;
            videos++;
        }
        return (frames, videos, failed);
    }

    /// Write the frames of a video that lost some to a delete into a new video, then swap it in.
    bool Rewrite(VideoSegment old)
    {
        var rows = db.SegmentFrames(old.Path);
        if (rows.Count == 0)
        {
            db.DropSegment(old.Id);
            Delete(old.Path);
            return true;
        }
        var path = Compaction.SegmentPath(VideoDir, Time.ParseIso(old.StartTime) ?? DateTime.UtcNow,
                                          old.Display, p => File.Exists(p) || File.Exists(Compaction.TempPath(p)) || Database.PathComparer.Equals(p, old.Path));
        // The stills it replaced took about this much (scaled to the frames left).
        long sourceBytes = old.FrameCount > 0 ? old.SourceBytes * rows.Count / old.FrameCount : 0;
        var fresh = Write(path, rows.Select(r => r.Source).ToList(), old.Width, old.Height, (name, bytes) =>
            new VideoSegment(old.Id, path, old.Display, rows[0].Timestamp, rows[^1].Timestamp, old.Width, old.Height, rows.Count, name, bytes, sourceBytes));
        if (fresh == null) return false;
        if (!db.CommitRewrite(old, fresh, rows.Select(r => r.Id).ToList()))
        {
            Delete(path);
            return false;
        }
        AfterStep?.Invoke("committed");
        Delete(old.Path);
        return true;
    }

    /// Encode to a temp file, check it reads back, move it into place. Null when any of that failed
    /// (nothing is left behind and the stills are untouched).
    VideoSegment? Write(string path, List<FrameSource> sources, int w, int h, Func<string, long, VideoSegment> make)
    {
        var tmp = Compaction.TempPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var name = codec.Write(sources, w, h, tmp);
            if (!codec.Verify(tmp, sources)) throw new IOException("the new video did not read back like the pictures");
            codec.Release(tmp);
            File.Move(tmp, path);
            AfterStep?.Invoke("written");
            return make(name, Size(path));
        }
        catch (Exception e) when (e is not CompactionInterrupted)
        {
            Log.Write($"compress older recordings: could not write {Path.GetFileName(path)}: {e.Message}");
            Delete(tmp);
            Delete(path);
            return null;
        }
    }

    void Delete(string path)
    {
        if (path.Length == 0) return;
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) codec.Release(path);
        try { File.Delete(path); } catch (Exception e) { Log.Write($"cannot delete {path}: {e.Message}"); }
    }

    static long Size(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}

/// Least recently used cache: decoded frames and open video readers.
public sealed class Lru<TKey, TValue>(int capacity, Action<TValue>? evicted = null) where TKey : notnull
{
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = new();
    readonly LinkedList<(TKey Key, TValue Value)> order = new();

    public int Count => map.Count;

    public bool TryGet(TKey key, out TValue value)
    {
        if (map.TryGetValue(key, out var node))
        {
            order.Remove(node);
            order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (map.TryGetValue(key, out var old))
        {
            order.Remove(old);
            if (!ReferenceEquals(old.Value.Value, value)) evicted?.Invoke(old.Value.Value);
        }
        map[key] = order.AddFirst((key, value));
        while (map.Count > capacity)
        {
            var last = order.Last!;
            order.RemoveLast();
            map.Remove(last.Value.Key);
            evicted?.Invoke(last.Value.Value);
        }
    }

    /// Drop every entry whose key matches.
    public void RemoveWhere(Func<TKey, bool> match)
    {
        foreach (var node in map.Values.Where(n => match(n.Value.Key)).ToList())
        {
            order.Remove(node);
            map.Remove(node.Value.Key);
            evicted?.Invoke(node.Value.Value);
        }
    }
}
