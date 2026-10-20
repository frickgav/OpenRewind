using System.Drawing;
using OpenRewind;

public class ClipTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-clip-test-" + Guid.NewGuid());
    Database? db;

    public void Dispose()
    {
        db?.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    [Fact]
    public void ExportReadsOnlyTheRangeAndTheNextScreenshot()
    {
        db = new Database(Config.Load(new[] { "--home", home }).DbPath);
        for (int i = 0; i < 20; i++)
            db.Insert(new Frame(0, Time.Iso(T0.AddSeconds(i * 10)), "Code", "w", true, "", "", "ocr", 0, ""));
        var got = db.TimelineFrames(T0.AddSeconds(50), T0.AddSeconds(100));
        Assert.Equal(Enumerable.Range(5, 7).Select(i => T0.AddSeconds(i * 10)), got.Select(f => f.Time));
        // The last screenshot of all has nothing after it.
        Assert.Equal(2, db.TimelineFrames(T0.AddSeconds(180), T0.AddSeconds(500)).Count);
    }

    [Fact]
    public void ScreenshotsSavedWithABlockedWindowInFrontAreNeverExported()
    {
        db = new Database(Config.Load(new[] { "--home", home }).DbPath);
        db.Insert(new Frame(0, Time.Iso(T0), "Chrome", "", true, "", "", "ocr", 0, "", Blocked: true));
        db.Insert(new Frame(0, Time.Iso(T0.AddSeconds(10)), "Chrome", "News", true, "", "", "ocr", 0, ""));
        var frames = db.TimelineFrames(T0, T0.AddSeconds(20));
        Assert.Equal(new[] { true, false }, frames.Select(f => f.Blocked));
        // Even with nothing on the block list now, and through the app filter.
        var list = new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false);
        var plan = ClipPlan.Make(AppFilter.Apply(frames, "Chrome"), T0, T0.AddSeconds(20), 1, ClipFormat.Mp4, f => ClipPlan.Hides(list, f));
        Assert.Equal(1, plan.Blocked);
        Assert.Equal(new long[] { frames[1].Id }, plan.Frames.Select(f => f.Frame.Id));
    }

    static readonly DateTime T0 = new(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc);

    static TimelineFrame F(int id, double secs, string app = "Code", string window = "main.cs", string url = "") =>
        new(id, T0.AddSeconds(secs), app, window, $"/tmp/{id}.jpg", url);

    // MARK: range

    [Fact]
    public void DefaultRangeIsTheFiveMinutesBeforeTheShownMoment()
    {
        var shown = T0.AddHours(1);
        Assert.Equal((shown.AddMinutes(-5), shown), ClipPlan.DefaultRange(shown, null, null));
    }

    [Fact]
    public void DefaultRangeUsesMarksInEitherOrder()
    {
        var a = T0.AddMinutes(10); var b = T0.AddMinutes(20);
        Assert.Equal((a, b), ClipPlan.DefaultRange(T0, a, b));
        Assert.Equal((a, b), ClipPlan.DefaultRange(T0, b, a));
        // Only a start: up to the shown moment, or 5 minutes when that is before the start.
        Assert.Equal((a, b), ClipPlan.DefaultRange(b, a, null));
        Assert.Equal((a, a.AddMinutes(5)), ClipPlan.DefaultRange(T0, a, null));
        // Only an end: the 5 minutes before it.
        Assert.Equal((b.AddMinutes(-5), b), ClipPlan.DefaultRange(T0, null, b));
    }

    [Fact]
    public void DefaultRangeIsCappedAtTheLongestClip()
    {
        var (start, end) = ClipPlan.DefaultRange(T0, T0, T0.AddHours(5));
        Assert.Equal(T0.AddHours(5), end);
        Assert.Equal(ClipPlan.MaxLength, end - start);
    }

    // MARK: frames

    [Fact]
    public void EachScreenshotLastsUntilTheNextAtRealTime()
    {
        var frames = new List<TimelineFrame> { F(1, 0), F(2, 2), F(3, 6), F(4, 8), F(5, 20) };
        var r = ClipPlan.Make(frames, T0.AddSeconds(1), T0.AddSeconds(8), 1, ClipFormat.Mp4);
        // Frame 1 is before the start; frame 5 after the end. The last one lasts the minimum 2 s.
        Assert.Equal(new long[] { 2, 3, 4 }, r.Frames.Select(f => f.Frame.Id));
        Assert.Equal(new double[] { 0, 4, 6 }, r.Frames.Select(f => f.At));
        Assert.Equal(new double[] { 4, 2, 2 }, r.Frames.Select(f => f.Duration));
        Assert.Equal(8, r.RealSeconds);
        Assert.Equal(8, r.Duration);
    }

    [Fact]
    public void LongBreaksAreCutToAMinute()
    {
        var frames = new List<TimelineFrame> { F(1, 0), F(2, 3600), F(3, 3602) };
        var r = ClipPlan.Make(frames, T0, T0.AddSeconds(3602), 1, ClipFormat.Mp4);
        Assert.Equal(ClipPlan.MaxHold, r.Frames[0].Duration);
        Assert.Equal(ClipPlan.MaxHold, r.Frames[1].At);
    }

    [Fact]
    public void SpeedShortensTheClip()
    {
        var frames = Enumerable.Range(0, 31).Select(i => F(i, i * 2)).ToList();
        var r = ClipPlan.Make(frames, T0, T0.AddSeconds(60), 10, ClipFormat.Mp4);
        Assert.Equal(31, r.Frames.Count);
        Assert.Equal(62, r.RealSeconds, 6);
        Assert.Equal(6.2, r.Duration, 6);
        Assert.Equal(0.2, r.Frames[0].Duration, 6);
    }

    [Fact]
    public void FastGifsDropPicturesTooShortToShow()
    {
        // 2 s apart at 100× is 0.02 s each, under the 0.05 s a GIF can show: about every third one is kept.
        var frames = Enumerable.Range(0, 100).Select(i => F(i, i * 2)).ToList();
        var r = ClipPlan.Make(frames, T0, T0.AddSeconds(200), 100, ClipFormat.Gif);
        Assert.True(r.Frames.Count < 40 && r.Frames.Count > 30, $"{r.Frames.Count} pictures");
        Assert.All(r.Frames, f => Assert.True(f.Duration >= ClipPlan.MinGifDelay - 1e-9));
        Assert.InRange(r.Duration, r.RealSeconds / 100, r.RealSeconds / 100 + ClipPlan.MinGifDelay);
        Assert.Equal(99, r.Frames[^1].Frame.Id);
        // Back to back, no gaps.
        for (int i = 1; i < r.Frames.Count; i++) Assert.Equal(r.Frames[i - 1].At + r.Frames[i - 1].Duration, r.Frames[i].At, 6);
    }

    [Fact]
    public void LongGifsAreCappedInPictures()
    {
        var frames = Enumerable.Range(0, 3600).Select(i => F(i, i * 2)).ToList();
        var r = ClipPlan.Make(frames, T0, T0.AddHours(2), 1, ClipFormat.Gif);
        Assert.True(r.Frames.Count <= ClipPlan.MaxGifFrames + 1, $"{r.Frames.Count} pictures");
        Assert.Equal(7200, r.Duration, 3);
    }

    [Fact]
    public void BlockedScreenshotsAreLeftOutAndTheirTimeSkipped()
    {
        var frames = new List<TimelineFrame> { F(1, 0), F(2, 2, "1Password"), F(3, 4), F(4, 6, "Chrome", "Bank", "https://chase.com/x") };
        var list = new Blocklist(new[] { new BlockedApp("1password", "1Password") }, Array.Empty<string>(), false,
                                 sites: new[] { "chase.com" });
        var r = ClipPlan.Make(frames, T0, T0.AddSeconds(6), 1, ClipFormat.Mp4, f => ClipPlan.Hides(list, f));
        Assert.Equal(new long[] { 1, 3 }, r.Frames.Select(f => f.Frame.Id));
        Assert.Equal(2, r.Blocked);
        Assert.Equal(new double[] { 0, 2 }, r.Frames.Select(f => f.At));
    }

    [Fact]
    public void WithAnAppFilterScreenshotsEndWhenAnotherAppCameToTheFront()
    {
        var all = new List<TimelineFrame> { F(1, 0), F(2, 4, "Chrome"), F(3, 30), F(4, 32, "Chrome") };
        var shown = AppFilter.Apply(all, "Code");
        var r = ClipPlan.Make(shown, T0, T0.AddSeconds(40), 1, ClipFormat.Mp4);
        Assert.Equal(new long[] { 1, 3 }, r.Frames.Select(f => f.Frame.Id));
        Assert.Equal(new double[] { 4, 2 }, r.Frames.Select(f => f.Duration));
    }

    [Fact]
    public void NothingInRangeGivesAnEmptyClip()
    {
        var r = ClipPlan.Make(new List<TimelineFrame> { F(1, 0) }, T0.AddMinutes(1), T0.AddMinutes(2), 1, ClipFormat.Gif);
        Assert.Empty(r.Frames);
        Assert.Equal(0, r.Duration);
    }

    [Fact]
    public void VideoSamplesRepeatHeldPictures()
    {
        var clip = new List<ClipFrame> { new(F(1, 0), 0, 2.5), new(F(2, 2), 2.5, 0.5) };
        var s = ClipPlan.Samples(clip, 1);
        Assert.Equal(new long[] { 1, 1, 1, 2 }, s.Select(x => x.Frame.Id));
        Assert.Equal(new[] { 0, 2.5 / 3, 5.0 / 3, 2.5 }, s.Select(x => x.At));
        Assert.Equal(3.0, s[^1].At + s[^1].Duration, 9);
    }

    [Fact]
    public void OutputSizeFitsAndIsEvenForVideo()
    {
        Assert.Equal((1920, 1200), ClipPlan.OutputSize(2880, 1800, ClipFormat.Mp4));
        Assert.Equal((1280, 800), ClipPlan.OutputSize(2880, 1800, ClipFormat.Gif));
        Assert.Equal((1024, 640), ClipPlan.OutputSize(1024, 640, ClipFormat.Gif));
        Assert.Equal((1366, 768), ClipPlan.OutputSize(1366, 768, ClipFormat.Mp4));
        Assert.Equal((1036, 582), ClipPlan.OutputSize(1037, 583, ClipFormat.Mp4));
    }

    [Fact]
    public void NamesAndLengths()
    {
        Assert.Equal("OpenRewind 2026-10-19 09.05.03.png", ClipPlan.FileName(new DateTime(2026, 10, 19, 9, 5, 3), "png"));
        Assert.Equal("0:42", ClipPlan.Length(42));
        Assert.Equal("12:05", ClipPlan.Length(725));
        Assert.Equal("1:02:03", ClipPlan.Length(3723));
    }

    // MARK: GIF

    /// Width×height BGRA pixels from a color function.
    static byte[] Picture(int w, int h, Func<int, int, int> rgb)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int c = rgb(x, y), o = (y * w + x) * 4;
                px[o] = (byte)c; px[o + 1] = (byte)(c >> 8); px[o + 2] = (byte)(c >> 16); px[o + 3] = 255;
            }
        return px;
    }

    static int At(byte[] bgra, int w, int x, int y)
    {
        int o = (y * w + x) * 4;
        return bgra[o + 2] << 16 | bgra[o + 1] << 8 | bgra[o];
    }

    [Fact]
    public void GifRoundTripsPicturesWithFewColors()
    {
        int w = 40, h = 30;
        var a = Picture(w, h, (x, y) => x < 20 ? 0xFFFFFF : 0x1E1E1E);
        var b = Picture(w, h, (x, y) => x >= 10 && x < 14 && y >= 5 && y < 9 ? 0x0A84FF : x < 20 ? 0xFFFFFF : 0x1E1E1E);
        var c = Picture(w, h, (x, y) => (x + y) % 3 == 0 ? 0xFF0000 : 0x00FF00);
        using var ms = new MemoryStream();
        var gif = new GifWriter(ms, w, h);
        gif.AddFrame(a, w * 4, 0.5);
        gif.AddFrame(b, w * 4, 0.25);
        gif.AddFrame(b, w * 4, 0.25);  // unchanged: makes the last one longer
        gif.AddFrame(c, w * 4, 1);
        gif.Finish();

        var d = GifDecoder.Decode(ms.ToArray());
        Assert.Equal((w, h), (d.Width, d.Height));
        Assert.True(d.Loops);
        Assert.Equal(new[] { 50, 50, 100 }, d.Frames.Select(f => f.DelayCs));
        foreach (var (want, got) in new[] { a, b, c }.Zip(d.Frames))
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Assert.Equal(At(want, w, x, y), got.Rgb[y * w + x]);
        // Only the changed square was written for the second picture.
        Assert.Equal(new Rectangle(10, 5, 4, 4), d.Frames[1].Rect);
    }

    [Fact]
    public void GifRoundTripsALongPictureThatFillsTheCodeTable()
    {
        // 255 colors in a noisy pattern: the LZW table fills and restarts many times.
        int w = 301, h = 207;
        var rnd = new Random(7);
        var pal = Enumerable.Range(0, 255).Select(i => (i & 31) << 19 | (i >> 5 & 31) << 11 | 0x40).ToArray();
        var cells = Enumerable.Range(0, w * h).Select(_ => pal[rnd.Next(pal.Length)]).ToArray();
        var px = Picture(w, h, (x, y) => cells[y * w + x]);
        using var ms = new MemoryStream();
        var gif = new GifWriter(ms, w, h);
        gif.AddFrame(px, w * 4, 0.1);
        gif.Finish();
        var d = GifDecoder.Decode(ms.ToArray());
        Assert.Single(d.Frames);
        for (int i = 0; i < w * h; i++) Assert.Equal(cells[i], d.Frames[0].Rgb[i]);
    }

    [Fact]
    public void GifWithManyColorsKeepsTheMostUsedOnesAndStaysClose()
    {
        int w = 64, h = 64;
        // A smooth gradient (4096 colors) over a big white area.
        var px = Picture(w, h, (x, y) => y < 32 ? 0xFFFFFF : (x * 4) << 16 | (y * 4) << 8 | 128);
        using var ms = new MemoryStream();
        var gif = new GifWriter(ms, w, h);
        gif.AddFrame(px, w * 4, 1);
        gif.Finish();
        var d = GifDecoder.Decode(ms.ToArray());
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int want = At(px, w, x, y), got = d.Frames[0].Rgb[y * w + x];
                if (y < 32) Assert.Equal(want, got);
                else for (int s = 0; s < 24; s += 8) Assert.True(Math.Abs((want >> s & 255) - (got >> s & 255)) <= 40, $"{want:X6} vs {got:X6}");
            }
    }

    [Fact]
    public void GifDelaysDoNotDrift()
    {
        int w = 4, h = 4;
        using var ms = new MemoryStream();
        var gif = new GifWriter(ms, w, h);
        for (int i = 0; i < 30; i++) gif.AddFrame(Picture(w, h, (_, _) => i % 2 == 0 ? 0 : 0xFFFFFF), w * 4, 1.0 / 15);
        gif.Finish();
        var d = GifDecoder.Decode(ms.ToArray());
        Assert.Equal(30, d.Frames.Count);
        Assert.Equal(200, d.Frames.Sum(f => f.DelayCs));
    }

    [Fact]
    public void GifPictureLongerThanAGifCanSayDoesNotStretchTheNextOnes()
    {
        int w = 2, h = 2;
        using var ms = new MemoryStream();
        var gif = new GifWriter(ms, w, h);
        gif.AddFrame(Picture(w, h, (_, _) => 0), w * 4, 1000);  // more than 655.35 s
        gif.AddFrame(Picture(w, h, (_, _) => 0xFFFFFF), w * 4, 0.5);
        gif.AddFrame(Picture(w, h, (_, _) => 0), w * 4, 0.5);
        gif.Finish();
        var d = GifDecoder.Decode(ms.ToArray());
        Assert.Equal(new[] { 65535, 50, 50 }, d.Frames.Select(f => f.DelayCs).ToArray());
    }

    // MARK: estimates and progress

    [Fact]
    public void BlockedScreenshotsAreCountedEvenWhenTheyHaveNoTime()
    {
        // Two screenshots at the same moment: the first lasts no time but is still a blocked one left out.
        var frames = new List<TimelineFrame> { F(1, 0, "Signal"), F(2, 0, "Signal"), F(3, 10) };
        var plan = ClipPlan.Make(frames, T0, T0.AddSeconds(20), 1, ClipFormat.Mp4, f => f.AppName == "Signal");
        Assert.Equal(2, plan.Blocked);
    }

    [Fact]
    public void EstimatesGrowWithTheClip()
    {
        var shortPlan = ClipPlan.Make(Enumerable.Range(0, 10).Select(i => F(i, i * 2)).ToList(), T0, T0.AddSeconds(20), 1, ClipFormat.Gif);
        var longPlan = ClipPlan.Make(Enumerable.Range(0, 100).Select(i => F(i, i * 2)).ToList(), T0, T0.AddSeconds(200), 1, ClipFormat.Gif);
        var a = ClipPlan.Estimate(shortPlan, ClipFormat.Gif, 1280, 800);
        var b = ClipPlan.Estimate(longPlan, ClipFormat.Gif, 1280, 800);
        Assert.True(b.Bytes > a.Bytes && b.Seconds > a.Seconds);
        Assert.True(a.Bytes > 100_000 && a.Bytes < 10_000_000);
        var v = ClipPlan.Estimate(longPlan, ClipFormat.Mp4, 1920, 1200);
        Assert.True(v.Bytes > 0 && v.Seconds > 0);
        Assert.Equal(ClipPlan.Samples(longPlan.Frames).Count, ClipPlan.SampleCount(longPlan.Frames));
        Assert.StartsWith("0:20 clip · 10 frames · 1280×800 · about ", ClipPlan.Summary(shortPlan, ClipFormat.Gif, 1280, 800));
        Assert.Equal("820 KB", ClipPlan.FileSize(820 * 1024));
        Assert.Equal("12.5 MB", ClipPlan.FileSize(12 * 1048576 + 524288));
    }

    [Fact]
    public void ProgressTextSaysHowFarAndHowLong()
    {
        Assert.Equal("Frame 0 of 600 · 0% · 0:00 elapsed · working out the time left…", ClipPlan.ProgressText(0, 600, 0.2));
        Assert.Equal("Frame 120 of 600 · 20% · 0:12 elapsed · about 0:48 left", ClipPlan.ProgressText(120, 600, 12));
        Assert.Equal("Frame 600 of 600 · 100% · 1:00 elapsed · finishing the file…", ClipPlan.ProgressText(600, 600, 60));
    }

    // MARK: text on a screenshot

    static FrameText Sample() => new(new[]
    {
        // Out of order on purpose: the second line first, words right to left.
        new[] { ("world", new RectangleF(0.30f, 0.20f, 0.10f, 0.03f)), ("big", new RectangleF(0.10f, 0.20f, 0.10f, 0.03f)) },
        new[] { ("Hello", new RectangleF(0.10f, 0.10f, 0.10f, 0.03f)), ("there", new RectangleF(0.25f, 0.10f, 0.10f, 0.03f)) },
        new[] { ("  ", new RectangleF(0.5f, 0.5f, 0.1f, 0.1f)) },
    }.Select(l => l.Select(w => (w.Item1, w.Item2))));

    [Fact]
    public void FrameTextReadsLineByLine()
    {
        var t = Sample();
        Assert.Equal(new[] { "Hello", "there", "big", "world" }, t.Words.Select(w => w.Text));
        Assert.Equal("Hello there\nbig world", t.AllText);
    }

    [Fact]
    public void FrameTextSelectsFromWordToWordInEitherDirection()
    {
        var t = Sample();
        Assert.Equal("there\nbig", t.TextOf(1, 2));
        Assert.Equal("there\nbig", t.TextOf(2, 1));
        Assert.Equal("", t.TextOf(-1, 2));
        var boxes = t.Boxes(0, 3);
        Assert.Equal(2, boxes.Count);
        Assert.Equal(0.10, boxes[0].X, 3);
        Assert.Equal(0.35, boxes[0].Right, 3);
    }

    [Fact]
    public void FrameTextFindsTheWordUnderOrNearAPoint()
    {
        var t = Sample();
        Assert.Equal(1, t.WordAt(new PointF(0.3f, 0.11f)));
        Assert.Equal(-1, t.WordAt(new PointF(0.9f, 0.9f)));
        // Right of the first line: its last word. Left of the second: its first word.
        Assert.Equal(1, t.NearestWord(new PointF(0.9f, 0.115f)));
        Assert.Equal(2, t.NearestWord(new PointF(0.01f, 0.21f)));
        // Far below everything: the last line.
        Assert.Equal(3, t.NearestWord(new PointF(0.5f, 0.95f)));
        Assert.Equal(-1, new FrameText(Array.Empty<IEnumerable<(string, RectangleF)>>()).NearestWord(new PointF(0, 0)));
    }
}

/// Just enough of a GIF decoder to check what GifWriter writes: palettes, transparency, LZW, delays.
static class GifDecoder
{
    public record Frame(int[] Rgb, int DelayCs, Rectangle Rect);
    public record Gif(int Width, int Height, bool Loops, List<Frame> Frames);

    public static Gif Decode(byte[] d)
    {
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(d, 0, 6));
        int w = d[6] | d[7] << 8, h = d[8] | d[9] << 8;
        Assert.Equal(0, d[10] & 0x80);  // no global palette
        int p = 13;
        var canvas = new int[w * h];
        var frames = new List<Frame>();
        bool loops = false;
        int delay = 0, transparent = -1;
        while (true)
        {
            byte b = d[p++];
            if (b == 0x3B) break;
            if (b == 0x21)
            {
                byte label = d[p++];
                if (label == 0xF9)
                {
                    Assert.Equal(4, d[p]);
                    Assert.Equal(1, d[p + 1] >> 2 & 7);  // keep under the next one
                    transparent = (d[p + 1] & 1) != 0 ? d[p + 4] : -1;
                    delay = d[p + 2] | d[p + 3] << 8;
                }
                if (label == 0xFF && System.Text.Encoding.ASCII.GetString(d, p + 1, 11) == "NETSCAPE2.0") loops = true;
                while (d[p] != 0) p += d[p] + 1;
                p++;
                continue;
            }
            Assert.Equal(0x2C, b);
            int x0 = d[p] | d[p + 1] << 8, y0 = d[p + 2] | d[p + 3] << 8, rw = d[p + 4] | d[p + 5] << 8, rh = d[p + 6] | d[p + 7] << 8;
            int flags = d[p + 8];
            p += 9;
            Assert.NotEqual(0, flags & 0x80);
            Assert.Equal(0, flags & 0x40);  // not interlaced
            int size = 1 << ((flags & 7) + 1);
            var pal = new int[size];
            for (int i = 0; i < size; i++, p += 3) pal[i] = d[p] << 16 | d[p + 1] << 8 | d[p + 2];
            int minCode = d[p++];
            var data = new List<byte>();
            while (d[p] != 0) { data.AddRange(d.Skip(p + 1).Take(d[p])); p += d[p] + 1; }
            p++;
            var idx = Lzw(data.ToArray(), minCode, rw * rh);
            Assert.Equal(rw * rh, idx.Count);
            for (int i = 0; i < idx.Count; i++)
            {
                if (idx[i] == transparent) continue;
                canvas[(y0 + i / rw) * w + x0 + i % rw] = pal[idx[i]];
            }
            frames.Add(new Frame((int[])canvas.Clone(), delay, new Rectangle(x0, y0, rw, rh)));
        }
        return new Gif(w, h, loops, frames);
    }

    static List<int> Lzw(byte[] data, int minCode, int count)
    {
        int clear = 1 << minCode, eoi = clear + 1;
        int size = minCode + 1, next = eoi + 1;
        var dict = new List<List<int>>();
        void Reset()
        {
            dict.Clear();
            for (int i = 0; i < clear; i++) dict.Add(new List<int> { i });
            dict.Add(new List<int>()); dict.Add(new List<int>());
            size = minCode + 1; next = eoi + 1;
        }
        Reset();
        var out_ = new List<int>();
        int bitPos = 0;
        List<int>? prev = null;
        while (bitPos + size <= data.Length * 8)
        {
            int code = 0;
            for (int i = 0; i < size; i++, bitPos++) code |= (data[bitPos >> 3] >> (bitPos & 7) & 1) << i;
            if (code == clear) { Reset(); prev = null; continue; }
            if (code == eoi) break;
            List<int> entry;
            if (code < dict.Count) entry = dict[code];
            else { Assert.NotNull(prev); Assert.Equal(dict.Count, code); entry = new List<int>(prev!) { prev![0] }; }
            out_.AddRange(entry);
            if (prev != null && dict.Count < 4096)
            {
                dict.Add(new List<int>(prev) { entry[0] });
                next++;
            }
            prev = entry;
            if (dict.Count == 1 << size && size < 12) size++;
        }
        Assert.True(out_.Count >= count);
        return out_.Take(count).ToList();
    }
}
