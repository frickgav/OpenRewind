using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// The one way to get a frame's picture, whether it is a still file or a frame inside a video
/// (Compress Older Recordings). The timeline, search highlights, the local API and Ask AI all read through here.
/// Video frames are decoded with a few cached readers and kept in a small LRU, so scrubbing stays quick.
static class FrameImages
{
    static readonly object gate = new();
    /// Decoded video frames by "path#frame".
    static readonly Lru<string, Bitmap> frames = new(48, b => b.Dispose());
    /// Open videos, so the next frame of the same one needs no seek.
    static readonly Lru<string, Mp4Reader> readers = new(3, r => r.Dispose());

    /// The picture as a new Bitmap the caller owns, or null when it is gone or can't be read.
    public static Bitmap? Load(FrameSource f)
    {
        if (!f.IsVideo) return LoadStill(f.StillPath);
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
                var path = f.VideoPath;
                var bmp = reader.Read(f.VideoTime, (n, b) => frames.Set(Key(path, n), b));
                if (bmp == null) return null;
                frames.Set(key, bmp);
                return new Bitmap(bmp);
            }
            catch (Exception e)
            {
                Log.Write($"cannot read frame {f.VideoFrame} of {Path.GetFileName(f.VideoPath)}: {e.Message}");
                Release(f.VideoPath);
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
    public static void Release(string videoPath)
    {
        lock (gate)
        {
            readers.RemoveWhere(p => Database.PathComparer.Equals(p, videoPath));
            var prefix = videoPath + "#";
            frames.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    static string Key(string path, int frame) => path + "#" + frame;

    /// A small gray copy for checking a video frame against its screenshot. The screenshot is first cut to
    /// the even size the video has, so both are shrunk the same way (thin lines of text would otherwise
    /// land on different thumbnail pixels).
    public static byte[] Comparable(Bitmap image)
    {
        int w = Compaction.Even(image.Width), h = Compaction.Even(image.Height);
        if (w == image.Width && h == image.Height) return ImageTools.Thumbnail(image);
        using var cut = image.Clone(new Rectangle(0, 0, w, h), image.PixelFormat);
        return ImageTools.Thumbnail(cut);
    }
}

/// IVideoCodec for the compactor: Media Foundation, HEVC when the PC can encode and decode it, else H.264.
sealed class MfCodec : IVideoCodec
{
    /// "hevc", "h264", or null when this PC can't make videos (checked once, see Probe).
    public string? Usable => probe.Value.Codec;
    public string? ProbeError => probe.Value.Error;
    /// The check has run (it takes a moment, so the menu does not wait for it).
    public bool Checked => probe.IsValueCreated;
    readonly Lazy<(string? Codec, string? Error)> probe = new(Probe);

    public (int Width, int Height)? Measure(FrameSource f) =>
        f.IsVideo ? (FrameImages.Load(f) is Bitmap b ? Dispose(b, (b.Width, b.Height)) : null) : FrameImages.StillSize(f.StillPath);

    static T Dispose<T>(Bitmap b, T value) { b.Dispose(); return value; }

    public string Write(IReadOnlyList<FrameSource> frames, int width, int height, string path)
    {
        var codec = Usable ?? throw new InvalidOperationException($"no video encoder on this PC ({ProbeError})");
        using var writer = Mp4Writer.Create(path, width, height, hevc: codec == "hevc");
        foreach (var f in frames)
        {
            using var picture = FrameImages.Load(f) ?? throw new IOException("a picture could not be read");
            writer.Add(picture);
        }
        writer.Finish();
        return codec;
    }

    public bool Verify(string path, IReadOnlyList<FrameSource> frames)
    {
        try
        {
            using var reader = Mp4Reader.Open(path);
            if (reader.Duration < (frames.Count - 0.5) / Compaction.Fps) return false;
            foreach (var i in new[] { 0, frames.Count - 1 }.Distinct())
            {
                using var decoded = reader.Read(i / Compaction.Fps);
                using var source = FrameImages.Load(frames[i]);
                if (decoded == null || source == null) return false;
                if (decoded.Width != Compaction.Even(source.Width) || decoded.Height != Compaction.Even(source.Height)) return false;
                if (!Compaction.Similar(FrameImages.Comparable(decoded), FrameImages.Comparable(source))) return false;
            }
            return true;
        }
        catch (Exception e) when (e is IOException or ExternalException or InvalidOperationException)
        {
            Log.Write($"cannot read back {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    public void Release(string path) => FrameImages.Release(path);

    /// Encode and decode a short test clip: HEVC first, then H.264. Returns the codec that worked.
    static (string? Codec, string? Error) Probe()
    {
        string? error = null;
        foreach (var hevc in new[] { true, false })
        {
            var path = Path.Combine(Path.GetTempPath(), $"openrewind-probe-{Environment.ProcessId}-{(hevc ? "hevc" : "h264")}.mp4");
            try
            {
                var pictures = Enumerable.Range(0, 3).Select(TestPicture).ToList();
                using (var w = Mp4Writer.Create(path, 320, 180, hevc))
                {
                    foreach (var p in pictures) w.Add(p);
                    w.Finish();
                }
                using (var r = Mp4Reader.Open(path))
                {
                    foreach (var i in new[] { 0, 2, 1 })
                    {
                        using var back = r.Read(i / Compaction.Fps);
                        if (back == null || back.Width != 320 || back.Height != 180 ||
                            !Compaction.Similar(ImageTools.Thumbnail(back), ImageTools.Thumbnail(pictures[i])))
                            throw new IOException($"frame {i} did not read back the same");
                    }
                }
                foreach (var p in pictures) p.Dispose();
                return (hevc ? "hevc" : "h264", null);
            }
            catch (Exception e)
            {
                error = $"{(hevc ? "HEVC" : "H.264")}: {e.Message}";
                Log.Write($"video check: {error}");
            }
            finally { try { File.Delete(path); } catch { } }
        }
        return (null, error);
    }

    /// A picture with text and shapes that differs per frame, like a screen.
    static Bitmap TestPicture(int i)
    {
        var b = new Bitmap(320, 180, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.SteelBlue, 0, 0, 320, 24);
        g.FillRectangle(Brushes.Orange, 20 + i * 90, 60, 80, 80);
        using var font = new Font(FontFamily.GenericSansSerif, 12);
        g.DrawString($"OpenRewind frame {i}", font, Brushes.Black, 10, 150);
        return b;
    }
}

/// Runs the compactor in the background: at launch, every 15 minutes, and soon after a delete that left
/// a video with frames to drop. One pass at a time, below normal priority.
sealed class VideoJob : IDisposable
{
    readonly VideoCompactor compactor;
    readonly MfCodec codec;
    readonly System.Threading.Timer timer;
    volatile bool stopping;
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

    /// Start a pass now unless one is running.
    public void Kick()
    {
        if (stopping || Interlocked.Exchange(ref running, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                bool compress = Enabled && codec.Usable != null;
                var r = compactor.RunOnce(compress, DateTime.UtcNow, () => !stopping, () => Enabled);
                if (r.Frames + r.Rewritten + r.Failed > 0)
                    Log.Write($"compress older recordings: {r.Frames} frames into {r.Videos} videos ({codec.Usable}), {r.Rewritten} rewritten, {r.Failed} failed");
            }
            catch (Exception e) { Log.Write($"compress older recordings failed: {e.Message}"); }
            finally { Interlocked.Exchange(ref running, 0); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "video compactor" };
        t.Start();
    }

    public void Dispose()
    {
        stopping = true;
        timer.Dispose();
    }
}
