using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// One thread for all Media Foundation work. Readers and writers are COM objects; made and used on one
/// MTA thread, they are never called from the UI's STA thread or marshalled between apartments
/// (mfreadwrite has no proxies for that). Other threads hand work over and wait for it. Encoding a video
/// goes one frame at a time, so the timeline's requests get their turn in between.
static class MfThread
{
    static readonly BlockingCollection<Action> queue = new();
    static readonly Thread thread;

    static MfThread()
    {
        thread = new Thread(() => { foreach (var work in queue.GetConsumingEnumerable()) work(); })
        {
            IsBackground = true,
            Name = "media foundation",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public static T Run<T>(Func<T> work)
    {
        if (Thread.CurrentThread == thread) return work();
        T result = default!;
        ExceptionDispatchInfo? error = null;
        using var done = new ManualResetEventSlim();
        queue.Add(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = ExceptionDispatchInfo.Capture(e); }
            finally { done.Set(); }
        });
        done.Wait();
        error?.Throw();
        return result;
    }

    public static void Run(Action work) => Run(() => { work(); return true; });
}

/// The one way to get a frame's picture, whether it is a still file or a frame inside a video
/// (Compress Older Recordings). The timeline, search highlights, the local API, Ask AI and the MCP
/// server all read through here. Video frames are decoded with a few cached readers and kept in a small
/// LRU (at most 48 frames and 256 MB), so scrubbing stays quick. Readers unused for 30 s are closed, so
/// a long-running process (the MCP server) doesn't keep a video open that the app wants to delete.
static class FrameImages
{
    const long CacheBytes = 256L << 20;
    static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(30);
    static readonly object gate = new();
    /// Decoded video frames by "path#frame".
    static readonly Lru<string, Bitmap> frames = new(48, b => b.Dispose(), b => (long)b.Width * b.Height * 4, CacheBytes);
    /// Open videos, so the next frame of the same one needs no seek.
    static readonly Lru<string, Mp4Reader> readers = new(3, r => r.Dispose());
    static readonly Dictionary<string, DateTime> lastUse = new(Database.PathComparer);
    static readonly System.Threading.Timer idle = new(_ => CloseIdle(), null, IdleClose, IdleClose);

    /// The picture as a new Bitmap the caller owns, or null when it is gone or can't be read.
    /// `cache: false` for one-off reads (the compactor reading a whole video) that would only push the
    /// timeline's frames out of the cache.
    public static Bitmap? Load(FrameSource f, bool cache = true) =>
        f.IsVideo ? MfThread.Run(() => LoadVideo(f, cache)) : LoadStill(f.StillPath);

    static Bitmap? LoadVideo(FrameSource f, bool cache)
    {
        var key = Key(f.VideoPath, f.VideoFrame);
        lock (gate)
        {
            if (frames.TryGet(key, out var hit)) return new Bitmap(hit);
            try
            {
                if (!readers.TryGet(f.VideoPath, out var reader))
                {
                    if (!File.Exists(f.VideoPath)) return null;
                    reader = Mp4Reader.Open(f.VideoPath);
                    readers.Set(f.VideoPath, reader);
                }
                lastUse[f.VideoPath] = DateTime.UtcNow;
                var path = f.VideoPath;
                var bmp = reader.Read(f.VideoTime, cache ? (n, b) => frames.Set(Key(path, n), b) : null);
                if (bmp == null || !cache) return bmp;
                var copy = new Bitmap(bmp);
                frames.Set(key, bmp);
                return copy;
            }
            catch (Exception e) when (e is IOException or ExternalException or InvalidOperationException or ArgumentException)
            {
                Log.Write($"cannot read frame {f.VideoFrame} of {Path.GetFileName(f.VideoPath)}: {e.Message}");
                Forget(f.VideoPath);
                return null;
            }
        }
    }

    /// Read the file into memory first, so it is not locked (retention or compression may delete it).
    static Bitmap? LoadStill(string path)
    {
        if (path.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(path));
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or ExternalException) { return null; }
    }

    /// Pixel size of a still without decoding all of it.
    public static (int Width, int Height)? StillSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var img = Image.FromStream(fs, false, false);
            return (img.Width, img.Height);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or ExternalException) { return null; }
    }

    /// The picture as JPEG, at most `maxSide` pixels on its long side (0 = full size).
    public static byte[]? Jpeg(FrameSource f, int maxSide = 0, long quality = 80)
    {
        using var image = Load(f);
        if (image == null) return null;
        var scale = maxSide > 0 ? Math.Min(1, maxSide / (double)Math.Max(image.Width, image.Height)) : 1;
        using var small = ImageTools.Scaled(image, scale);
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        small.Save(ms, ImageTools.JpegCodec, ps);
        return ms.ToArray();
    }

    /// Close a video and forget its frames (before it is deleted or replaced).
    public static void Release(string videoPath) => MfThread.Run(() => { lock (gate) Forget(videoPath); });

    static void Forget(string videoPath)
    {
        readers.RemoveWhere(p => Database.PathComparer.Equals(p, videoPath));
        lastUse.Remove(videoPath);
        var prefix = videoPath + "#";
        frames.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    static void CloseIdle()
    {
        try
        {
            MfThread.Run(() =>
            {
                lock (gate)
                {
                    var cutoff = DateTime.UtcNow - IdleClose;
                    var old = lastUse.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                    foreach (var p in old)
                    {
                        readers.RemoveWhere(k => Database.PathComparer.Equals(k, p));
                        lastUse.Remove(p);
                    }
                }
            });
        }
        catch (Exception e) { Log.Write($"closing idle videos failed: {e.Message}"); }
    }

    static string Key(string path, int frame) => path + "#" + frame;

    /// A small gray copy for checking a video frame against its screenshot. The screenshot is first cut to
    /// the even size a video has, and both are shrunk smoothly, so thin lines of text land on the same
    /// thumbnail pixels (a video scaled down from a very large screenshot compares the same way).
    public static byte[] Comparable(Bitmap image)
    {
        int w = Compaction.Even(image.Width), h = Compaction.Even(image.Height);
        if (w == image.Width && h == image.Height) return ImageTools.SmoothThumbnail(image);
        using var cut = image.Clone(new Rectangle(0, 0, w, h), image.PixelFormat);
        return ImageTools.SmoothThumbnail(cut);
    }
}

/// IVideoCodec for the compactor: Media Foundation H.264, which every Windows 10/11 desktop can play back.
/// HEVC is not used here: on Windows it needs the optional HEVC Video Extensions to decode, and a data
/// folder restored on a PC without them would show nothing for its older recordings.
sealed class MfCodec : IVideoCodec
{
    /// "h264", or null when this PC can't make videos (checked once, see Probe).
    public string? Usable => probe.Value.Codec;
    public string? ProbeError => probe.Value.Error;
    /// The check has run (it takes a moment, so the menu does not wait for it).
    public bool Checked => probe.IsValueCreated;
    readonly Lazy<(string? Codec, string? Error)> probe = new(() => MfThread.Run(Probe));

    public (int Width, int Height)? Measure(FrameSource f) =>
        f.IsVideo ? (FrameImages.Load(f, cache: false) is Bitmap b ? Dispose(b, (b.Width, b.Height)) : null) : FrameImages.StillSize(f.StillPath);

    static T Dispose<T>(Bitmap b, T value) { b.Dispose(); return value; }

    public string Write(IReadOnlyList<FrameSource> frames, int width, int height, string path)
    {
        var codec = Usable ?? throw new InvalidOperationException($"no video encoder on this PC ({ProbeError})");
        var writer = MfThread.Run(() => Mp4Writer.Create(path, width, height));
        try
        {
            // One frame per turn on the Media Foundation thread: the timeline can read in between.
            foreach (var f in frames)
                MfThread.Run(() =>
                {
                    using var picture = FrameImages.Load(f, cache: false) ?? throw new IOException("a picture could not be read");
                    writer.Add(picture);
                });
            MfThread.Run(writer.Finish);
        }
        finally { MfThread.Run(writer.Dispose); }
        return codec;
    }

    public bool Verify(string path, IReadOnlyList<FrameSource> frames, int width, int height) => MfThread.Run(() =>
    {
        try
        {
            using var reader = Mp4Reader.Open(path);
            if (reader.Duration < (frames.Count - 0.5) / Compaction.Fps) return false;
            foreach (var i in Compaction.CheckedFrames(frames.Count))
            {
                using var decoded = reader.Read(i / Compaction.Fps);
                using var source = FrameImages.Load(frames[i], cache: false);
                if (decoded == null || source == null) return false;
                if (decoded.Width != width || decoded.Height != height) return false;
                if (!Compaction.Similar(FrameImages.Comparable(decoded), FrameImages.Comparable(source))) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or ExternalException or InvalidOperationException)
        {
            Log.Write($"cannot read back {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    });

    public void WriteStill(FrameSource frame, string path)
    {
        using var picture = FrameImages.Load(frame, cache: false) ?? throw new IOException("the frame could not be read");
        ImageTools.WriteJpeg(picture, path, 95);  // the only copy left, and already lossy once: keep it close
    }

    public void Release(string path) => FrameImages.Release(path);

    /// Whether the videos already made still play on this PC (a codec removed, a folder restored on
    /// another PC): opens up to `max` of them and reads their first frame. For doctor.
    public static (int Checked, int Bad) CheckVideos(Database db, int max = 100)
    {
        var all = db.Segments();
        var pick = all.Count <= max ? all : Enumerable.Range(0, max).Select(i => all[i * all.Count / max]).ToList();
        int bad = 0;
        foreach (var seg in pick)
        {
            using var b = FrameImages.Load(new FrameSource("", seg.Path, 0, 0), cache: false);
            if (b == null) bad++;
        }
        return (pick.Count, bad);
    }

    /// Encode and decode a short test clip with odd proportions and thin lines, like a screen.
    static (string? Codec, string? Error) Probe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openrewind-probe-{Environment.ProcessId}-h264.mp4");
        try
        {
            const int w = 362, h = 206;
            var pictures = Enumerable.Range(0, 3).Select(i => TestPicture(i, w, h)).ToList();
            using (var writer = Mp4Writer.Create(path, w, h))
            {
                foreach (var p in pictures) writer.Add(p);
                writer.Finish();
            }
            using (var r = Mp4Reader.Open(path))
            {
                foreach (var i in new[] { 0, 2, 1 })
                {
                    using var back = r.Read(i / Compaction.Fps);
                    if (back == null || back.Width != w || back.Height != h ||
                        !Compaction.Similar(FrameImages.Comparable(back), FrameImages.Comparable(pictures[i])))
                        throw new IOException($"frame {i} did not read back the same");
                }
            }
            foreach (var p in pictures) p.Dispose();
            return ("h264", null);
        }
        catch (Exception e)
        {
            Log.Write($"video check: H.264: {e.Message}");
            return (null, $"H.264: {e.Message}");
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// A picture with text and shapes that differs per frame, like a screen.
    static Bitmap TestPicture(int i, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.SteelBlue, 0, 0, w, 24);
        g.FillRectangle(Brushes.Orange, 20 + i * 90, 60, 80, 80);
        using var font = new Font(FontFamily.GenericSansSerif, 9);
        for (int line = 0; line < 4; line++) g.DrawString($"OpenRewind frame {i} line {line}", font, Brushes.Black, 10, 150 + line * 13);
        return b;
    }
}

/// Runs the compactor in the background: at launch, every 15 minutes, and right after a delete that left
/// a video with frames to drop (also when that delete comes during a pass: one more pass follows).
/// One pass at a time, below normal priority.
sealed class VideoJob : IDisposable
{
    readonly VideoCompactor compactor;
    readonly MfCodec codec;
    readonly System.Threading.Timer timer;
    volatile bool stopping;
    /// A kick came while a pass was running: run again when it ends.
    volatile bool pending;
    int running;
    /// Compress Older Recordings. Off: no new videos, but rewrites after deletes still happen and videos still play.
    public volatile bool Enabled;

    public VideoJob(Database db, Config config, bool enabled)
    {
        codec = new MfCodec();
        compactor = new VideoCompactor(db, codec, config.DataDir);
        Enabled = enabled;
        db.VideosNeedRewrite += Kick;
        timer = new System.Threading.Timer(_ => Kick(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(15));
    }

    /// Why this PC can't make videos (shown in the menu), or null when it can or that is not known yet.
    public string? Unavailable => codec.Checked && codec.Usable == null ? codec.ProbeError ?? "no video encoder" : null;

    public string? Codec => codec.Usable;

    /// Start a pass now; when one is running, run another right after it.
    public void Kick()
    {
        if (stopping) return;
        if (Interlocked.Exchange(ref running, 1) == 1) { pending = true; return; }
        var t = new Thread(() =>
        {
            try
            {
                do
                {
                    pending = false;
                    try
                    {
                        bool compress = Enabled && codec.Usable != null;
                        var r = compactor.RunOnce(compress, DateTime.UtcNow, () => !stopping, () => Enabled);
                        if (r.Frames + r.Rewritten + r.Unpacked + r.Failed > 0)
                            Log.Write($"compress older recordings: {r.Frames} frames into {r.Videos} videos ({codec.Usable}), {r.Rewritten} rewritten, {r.Unpacked} turned back into pictures, {r.Failed} failed");
                    }
                    catch (Exception e) { Log.Write($"compress older recordings failed: {e.Message}"); }
                } while (pending && !stopping);
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
                if (pending && !stopping) Kick();
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "video compactor" };
        t.Start();
    }

    public void Dispose()
    {
        stopping = true;
        timer.Dispose();
    }
}
