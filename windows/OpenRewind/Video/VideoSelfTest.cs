using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace OpenRewind;

/// `OpenRewind video-check`: runs Compress Older Recordings for real on made-up screenshots in a temp
/// folder (encode, check, commit, delete stills), reads every frame back in a jumbled order, deletes part
/// of the video and checks the rewrite, runs doctor's check that videos open, and compresses a capture
/// wider than encoders take. CI runs it; it is also a quick check on a new PC.
static class VideoSelfTest
{
    public static bool Run(TextWriter log)
    {
        var codec = new MfCodec();
        if (codec.Usable == null) { log.WriteLine($"no video encoder: {codec.ProbeError}"); return false; }
        log.WriteLine($"codec: {codec.Usable}");
        var home = Path.Combine(Path.GetTempPath(), "openrewind-video-check-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var config = Config.Load(new[] { "--home", home });
            using var db = new Database(config.DbPath);
            db.ReleasingVideos += paths => { foreach (var v in paths) FrameImages.Release(v); };
            var now = DateTime.UtcNow;
            // Odd size on purpose: videos are cut to even sizes.
            const int w = 965, h = 543, n = 45;
            var start = Compaction.WindowStart(now.AddHours(-2), Compaction.Window);
            var thumbs = new Dictionary<long, byte[]>();
            for (int i = 0; i < n; i++)
            {
                using var pic = Screen(i, w, h);
                var t = start.AddSeconds(i * 2);
                var dir = Path.Combine(config.DataDir, Time.Iso(t)[..10]);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, Time.Iso(t).Replace(':', '-') + ".jpg");
                ImageTools.WriteJpeg(pic, file);
                var id = db.Insert(new Frame(0, Time.Iso(t), "Test", $"Window {i}", true, file, $"frame {i}", "ocr", 0, Display: "test"));
                using var still = FrameImages.Load(new FrameSource(file))!;
                thumbs[id] = FrameImages.Comparable(still);
            }
            var compactor = new VideoCompactor(db, codec, config.DataDir);
            var sw = Stopwatch.StartNew();
            var r = compactor.RunOnce(compress: true, now);
            log.WriteLine($"compressed {r.Frames} frames into {r.Videos} video(s) in {sw.ElapsedMilliseconds} ms, {r.Failed} failed");
            if (r.Frames != n || r.Failed != 0) return false;
            var stats = db.VideoStats();
            log.WriteLine($"video: {stats.Bytes / 1024} KB, saves {stats.Saved / 1024} KB ({(stats.Bytes + stats.Saved) / (double)Math.Max(1, stats.Bytes):0.0}x smaller)");
            if (Directory.GetFiles(config.DataDir, "*.jpg", SearchOption.AllDirectories).Length != 0) { log.WriteLine("stills were not deleted"); return false; }

            if (!CheckAll(db, thumbs, w, h, log)) return false;

            // Delete a stretch in the middle: the video is written again without it.
            int deleted = db.DeleteFrames(start.AddSeconds(20), start.AddSeconds(40));
            foreach (var id in thumbs.Keys.Where(id => db.FrameById(id) == null).ToList()) thumbs.Remove(id);
            var r2 = compactor.RunOnce(compress: false, now);
            log.WriteLine($"deleted {deleted} frames, rewrote {r2.Rewritten} video(s), {r2.Failed} failed");
            if (r2.Rewritten != 1 || r2.Failed != 0 || thumbs.Count != n - deleted) return false;
            if (!CheckAll(db, thumbs, w, h, log)) return false;

            var (seen, bad) = MfCodec.CheckVideos(db);
            log.WriteLine($"doctor's check: {seen} video(s), {bad} do not open");
            if (seen != 1 || bad != 0) return false;

            // A capture wider than every encoder takes is scaled down to fit.
            const int ww = 5000, wh = 1000;
            var wideStart = Compaction.WindowStart(now.AddHours(-3), Compaction.Window);
            var wide = new Dictionary<long, byte[]>();
            for (int i = 0; i < 3; i++)
            {
                using var pic = Screen(i, ww, wh);
                var t = wideStart.AddSeconds(i * 2);
                var dir = Path.Combine(config.DataDir, Time.Iso(t)[..10]);
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, Time.Iso(t).Replace(':', '-') + ".jpg");
                ImageTools.WriteJpeg(pic, file);
                var id = db.Insert(new Frame(0, Time.Iso(t), "Test", $"Wide {i}", true, file, $"wide {i}", "ocr", 0, Display: "wide"));
                using var still = FrameImages.Load(new FrameSource(file))!;
                wide[id] = FrameImages.Comparable(still);
            }
            var r3 = compactor.RunOnce(compress: true, now);
            var (fw, fh) = Compaction.Fit(ww, wh);
            log.WriteLine($"wide capture: {r3.Frames} frames of {ww}x{wh} into {r3.Videos} video(s) of {fw}x{fh}, {r3.Failed} failed");
            if (r3.Frames != 3 || r3.Failed != 0) return false;
            if (!CheckAll(db, wide, fw, fh, log)) return false;

            // Delete the rest: the video goes too.
            db.DeleteFrames(null, now);
            var left = Directory.GetFiles(compactor.VideoDir, "*.mp4", SearchOption.AllDirectories);
            log.WriteLine($"after deleting everything: {left.Length} video files");
            return left.Length == 0;
        }
        catch (Exception e)
        {
            log.WriteLine($"failed: {e}");
            return false;
        }
        finally
        {
            try { Directory.Delete(home, true); } catch { }
        }
    }

    /// Every frame reads back like its screenshot, in a jumbled order (scrubbing back and forth).
    static bool CheckAll(Database db, Dictionary<long, byte[]> thumbs, int w, int h, TextWriter log)
    {
        var ids = thumbs.Keys.OrderBy(id => (id * 7919) % 31).ToList();
        var sw = Stopwatch.StartNew();
        foreach (var id in ids)
        {
            var f = db.FrameById(id)!;
            using var pic = FrameImages.Load(f.Source);
            if (pic == null) { log.WriteLine($"frame {id} did not load"); return false; }
            if (pic.Width != Compaction.Even(w) || pic.Height != Compaction.Even(h)) { log.WriteLine($"frame {id} is {pic.Width}x{pic.Height}"); return false; }
            if (!Compaction.Similar(FrameImages.Comparable(pic), thumbs[id])) { log.WriteLine($"frame {id} looks different from its screenshot"); return false; }
        }
        log.WriteLine($"read {ids.Count} frames back, {sw.ElapsedMilliseconds / (double)ids.Count:0.0} ms each");
        return true;
    }

    /// Something like a screen: a title bar, lines of text, and a box that moves.
    static Bitmap Screen(int i, int w, int h)
    {
        var b = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.White);
        g.FillRectangle(Brushes.DimGray, 0, 0, w, 30);
        using var font = new Font(FontFamily.GenericSansSerif, 11);
        for (int line = 0; line < 20; line++)
            g.DrawString($"Line {line}: the quick brown fox {(line == i % 20 ? "JUMPS" : "jumps")} over frame {i}", font, Brushes.Black, 12, 40 + line * 24);
        g.FillRectangle(i % 2 == 0 ? Brushes.SeaGreen : Brushes.Crimson, 600 + i * 7 % 300, 100 + i * 13 % 300, 120, 90);
        return b;
    }
}
