namespace OpenRewind;

/// Encodes and decodes the videos for Compress Older Recordings. The app's version uses Media Foundation.
public interface IVideoCodec
{
    /// Pixel size of a frame's picture, or null when it cannot be read.
    (int Width, int Height)? Measure(FrameSource frame);
    /// Write these frames, in order, as a new video at `path`: frame i at i / Compaction.Fps seconds,
    /// width x height pixels (pictures a pixel bigger are cut to fit; bigger ones are scaled down, see
    /// Compaction.Fit). Returns the codec used ("hevc" or "h264"). Throws when it can't.
    string Write(IReadOnlyList<FrameSource> frames, int width, int height, string path);
    /// True when the video at `path` opens, is width x height and long enough, and a few of its frames
    /// (Compaction.CheckedFrames: first, last and two in between) look like those pictures.
    bool Verify(string path, IReadOnlyList<FrameSource> frames, int width, int height);
    /// Save one frame's picture as a JPEG still (a video that can't be written again is turned back
    /// into stills, see VideoCompactor.Unpack). Throws when it can't.
    void WriteStill(FrameSource frame, string path);
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
    /// Largest video every H.264 encoder takes (level 5.1). Bigger screenshots are scaled down to fit.
    public const int MaxWidth = 4096, MaxHeight = 2304;
    /// A still that failed to go into a video this many times stays a still (a picture or size the
    /// encoder can't take would otherwise be tried again on every launch).
    public const int MaxTries = 3;

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

    /// The video size for pictures of w x h: the same cut to even, or scaled down to fit MaxWidth x MaxHeight.
    public static (int Width, int Height) Fit(int w, int h)
    {
        var scale = Math.Min(1, Math.Min(MaxWidth / (double)w, MaxHeight / (double)h));
        return scale >= 1 ? (Even(w), Even(h)) : (Even((int)(w * scale)), Even((int)(h * scale)));
    }

    /// data/video/2026-10-19/2026-10-19T14-05-00Z-<screen>-<random>.mp4. The random part means a name is
    /// never used twice, so a timeline still holding an old frame's place can't show another video's picture.
    public static string SegmentPath(string videoDir, DateTime windowStart, string display, Func<string, bool> taken)
    {
        var iso = Time.Iso(windowStart);
        var dir = Path.Combine(videoDir, iso[..10]);
        var name = iso[..19].Replace(':', '-') + "Z" + (SafeName(display) is { Length: > 0 } d ? "-" + d : "");
        while (true)
        {
            var p = Path.Combine(dir, name + "-" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
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

    /// Which frames of a new video are checked against their pictures: the first, the last, and two in between.
    public static int[] CheckedFrames(int count) =>
        new[] { 0, count / 3, 2 * count / 3, count - 1 }.Where(i => i >= 0 && i < count).Distinct().ToArray();
}

/// Compress Older Recordings: turns stills older than an hour into short videos, crash-safely.
/// Each video is written to a temp file, read back and checked, renamed, then the frames are pointed
/// at it in one transaction, and only then are the stills deleted. Stopping at any point loses nothing:
/// the next pass deletes temp and unreferenced videos and finishes deleting stills already in a video.
/// It also rewrites videos that lost some of their frames to a delete, even while compression is off;
/// a video that can't be written again is turned back into stills, so deleted pictures never stay in one.
public sealed class VideoCompactor
{
    readonly Database db;
    readonly IVideoCodec codec;
    readonly string dataDir;
    public readonly string VideoDir;
    public TimeSpan MinAge = Compaction.MinAge;
    public TimeSpan Window = Compaction.Window;
    public int BatchSize = Compaction.BatchSize;
    /// Videos no row points at are deleted once they are this old (a pass that stopped before committing).
    public TimeSpan OrphanAge = TimeSpan.FromMinutes(10);
    /// Called after "written" (video renamed into place) and "committed" (rows point at it). For tests.
    public Action<string>? AfterStep;
    /// Frames up to this time (the stored timestamp, as it is) were tried since the app started.
    string cursor = "";

    public record Result(int Frames, int Videos, int Rewritten, int Failed, int Unpacked = 0);

    public VideoCompactor(Database db, IVideoCodec codec, string dataDir)
    {
        this.db = db;
        this.codec = codec;
        this.dataDir = dataDir;
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
        int rewritten = 0, unpacked = 0, failed = 0, frames = 0, videos = 0;
        foreach (var seg in db.Segments(onlyNeedingRewrite: true))
        {
            if (!go()) break;
            switch (Rewrite(seg))
            {
                case true: rewritten++; break;
                // Its frames changed while it was written (another delete): it stays marked, the next pass does it.
                case null: break;
                // It could not be written again: keep its frames as stills rather than deleted ones in a video.
                case false: if (Unpack(seg)) unpacked++; else failed++; break;
            }
        }
        if (!compress) return new(0, 0, rewritten, failed, unpacked);

        var cutoff = now - MinAge;
        while (keepGoing())
        {
            var batch = db.CompactCandidates(cutoff, cursor, BatchSize);
            var plans = Compaction.Plan(batch, cutoff, Window, batch.Count == BatchSize);
            if (plans.Count == 0) break;
            var before = cursor;
            foreach (var plan in plans)
            {
                if (!keepGoing()) return new(frames, videos, rewritten, failed, unpacked);
                var (n, v, f) = Compact(plan);
                frames += n; videos += v; failed += f;
                var last = plan.Frames[^1].Timestamp;
                if (string.CompareOrdinal(last, cursor) > 0) cursor = last;
            }
            if (cursor == before) break;  // never read the same batch twice
        }
        return new(frames, videos, rewritten, failed, unpacked);
    }

    /// Finish what a stopped pass left: temp files, videos no row points at, stills already in a video,
    /// and videos that lost frames without being marked (a delete cut short, or one by another process).
    void CleanUp()
    {
        if (Directory.Exists(VideoDir))
        {
            // A failed read throws here, so nothing is deleted on a guess.
            var known = db.SegmentPaths();
            foreach (var f in Directory.EnumerateFiles(VideoDir, "*.mp4", SearchOption.AllDirectories).ToList())
            {
                var temp = f.EndsWith(Compaction.TempSuffix, StringComparison.OrdinalIgnoreCase);
                if (!temp && known.Contains(f)) continue;
                if (!temp && DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < OrphanAge) continue;
                // Asked again for this one file: delete only what no video row and no frame points at.
                if (!temp && db.IsVideoReferenced(f)) continue;
                Delete(f);
            }
            foreach (var d in Directory.EnumerateDirectories(VideoDir))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
        var left = db.LeftoverStills();
        if (left.Count > 0)
        {
            foreach (var (_, p) in left) Delete(p);
            db.ClearStills(left.Select(l => l.Id));
        }
        var marked = db.MarkShrunkSegments();
        if (marked > 0) Log.Write($"compress older recordings: {marked} videos lost frames without being marked; writing them again");
    }

    (int Frames, int Videos, int Failed) Compact(SegmentPlan plan)
    {
        var runs = Compaction.SplitBySize(plan.Frames.Select(c => (c, codec.Measure(new FrameSource(c.StillPath)))));
        int frames = 0, videos = 0, failed = 0;
        foreach (var (w, h, run) in runs)
        {
            var path = NewPath(plan.WindowStart, plan.Display);
            var sources = run.Select(c => new FrameSource(c.StillPath)).ToList();
            var ids = run.Select(c => c.Id).ToList();
            long sourceBytes = run.Sum(c => Size(c.StillPath));
            var (vw, vh) = Compaction.Fit(w, h);
            var seg = Write(path, sources, vw, vh, (name, bytes) =>
                new VideoSegment(0, path, plan.Display, run[0].Timestamp, run[^1].Timestamp, vw, vh, run.Count, name, bytes, sourceBytes));
            if (seg == null)
            {
                db.NoteCompactFailure(ids);
                failed++;
                continue;
            }
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
    /// Decoding and encoding again loses a little quality; that happens about once per video.
    /// True when done, false when the video could not be written, null when its frames changed meanwhile.
    bool? Rewrite(VideoSegment old)
    {
        var rows = db.SegmentFrames(old.Path);
        if (rows.Count == 0)
        {
            db.DropSegment(old.Id);
            Delete(old.Path);
            return true;
        }
        var path = NewPath(Time.ParseIso(old.StartTime) ?? DateTime.UtcNow, old.Display);
        // The stills it replaced took about this much (scaled to the frames left).
        long sourceBytes = old.FrameCount > 0 ? old.SourceBytes * rows.Count / old.FrameCount : 0;
        var fresh = Write(path, rows.Select(r => r.Source).ToList(), old.Width, old.Height, (name, bytes) =>
            new VideoSegment(old.Id, path, old.Display, rows[0].Timestamp, rows[^1].Timestamp, old.Width, old.Height, rows.Count, name, bytes, sourceBytes));
        if (fresh == null) return false;
        if (!db.CommitRewrite(old, fresh, rows.Select(r => r.Id).ToList()))
        {
            Delete(path);
            return null;
        }
        AfterStep?.Invoke("committed");
        Delete(old.Path);
        return true;
    }

    /// A video that must lose frames but can't be written again (no encoder any more, a size it won't
    /// take): save the frames it keeps as stills, point their rows at them, and delete the video.
    bool Unpack(VideoSegment old)
    {
        var rows = db.SegmentFrames(old.Path);
        var written = new List<string>();
        try
        {
            foreach (var r in rows)
            {
                var ts = r.Timestamp;
                var dir = Path.Combine(dataDir, ts.Length >= 10 ? ts[..10] : "unpacked");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, ts.Replace(':', '-') + "-" + r.Id + ".jpg");
                codec.WriteStill(r.Source, file);
                written.Add(file);
            }
            if (!db.CommitUnpack(old, rows.Select(r => r.Id).ToList(), written))
                throw new IOException("its frames changed meanwhile");
        }
        catch (Exception e) when (e is not CompactionInterrupted)
        {
            Log.Write($"compress older recordings: cannot turn {Path.GetFileName(old.Path)} back into pictures: {e.Message}");
            foreach (var f in written) Delete(f);
            return false;
        }
        Delete(old.Path);
        Log.Write($"compress older recordings: {Path.GetFileName(old.Path)} could not be written again, so its {rows.Count} frames are pictures again");
        return true;
    }

    string NewPath(DateTime windowStart, string display) =>
        Compaction.SegmentPath(VideoDir, windowStart, display, p => File.Exists(p) || File.Exists(Compaction.TempPath(p)) || db.IsVideoReferenced(p));

    /// Encode to a temp file, check it reads back, move it into place. Null when any of that failed
    /// (nothing is left behind and the stills are untouched). With Encrypt Recordings on, the video is made
    /// in the user's temp folder (never as a plain file under data/) and saved encrypted. Whether to encrypt
    /// is decided again when the video is put in place, under RecordingFile.CommitGate: Encrypt Recordings
    /// may have been switched while it was being made, and the migration may have finished meanwhile.
    VideoSegment? Write(string path, List<FrameSource> sources, int w, int h, Func<string, long, VideoSegment> make)
    {
        var madeOutside = RecordingFile.EncryptNew;
        var tmp = madeOutside ? Path.Combine(Path.GetTempPath(), $"{RecordingFile.TempCopyPrefix}{Guid.NewGuid():N}.mp4") : Compaction.TempPath(path);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var name = codec.Write(sources, w, h, tmp);
            if (!codec.Verify(tmp, sources, w, h)) throw new IOException("the new video did not read back like the pictures");
            codec.Release(tmp);
            lock (RecordingFile.CommitGate)
            {
                if (RecordingFile.EncryptNew || madeOutside)
                {
                    // Encrypted or not by the setting now, written atomically under data/.
                    RecordingFile.Write(path, File.ReadAllBytes(tmp));
                    Delete(tmp);
                }
                else File.Move(tmp, path);
            }
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
        // Under RecordingFile's lock, so an encryption switch converting this file never writes it back.
        RecordingFile.Delete(path);
        if (File.Exists(path)) Log.Write($"cannot delete {path}");
    }

    static long Size(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}

/// Least recently used cache: decoded frames and open video readers. At most `capacity` entries and,
/// when `weight` is given, at most `maxWeight` in total (decoded screenshots are big: cap them by bytes).
public sealed class Lru<TKey, TValue>(int capacity, Action<TValue>? evicted = null, Func<TValue, long>? weight = null, long maxWeight = long.MaxValue)
    where TKey : notnull
{
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> map = new();
    readonly LinkedList<(TKey Key, TValue Value)> order = new();

    public int Count => map.Count;
    /// Total weight of what is cached.
    public long Weight { get; private set; }

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
            map.Remove(key);
            Weight -= Of(old.Value.Value);
            if (!ReferenceEquals(old.Value.Value, value)) evicted?.Invoke(old.Value.Value);
        }
        var w = Of(value);
        if (w > maxWeight) { evicted?.Invoke(value); return; }  // bigger than the whole cache: don't keep it
        map[key] = order.AddFirst((key, value));
        Weight += w;
        while (map.Count > capacity || Weight > maxWeight)
        {
            var last = order.Last!;
            order.RemoveLast();
            map.Remove(last.Value.Key);
            Weight -= Of(last.Value.Value);
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
            Weight -= Of(node.Value.Value);
            evicted?.Invoke(node.Value.Value);
        }
    }

    long Of(TValue v) => weight?.Invoke(v) ?? 0;
}
