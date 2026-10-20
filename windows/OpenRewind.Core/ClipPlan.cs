namespace OpenRewind;

public enum ClipFormat { Mp4, Gif }

/// One picture in an exported clip: the screenshot, when it starts in the clip and how long it stays (clip seconds).
public record ClipFrame(TimelineFrame Frame, double At, double Duration);

/// Which screenshots go into an exported clip, and for how long each one shows.
/// Clips are made only from screenshots already saved (blocked apps are blurred in them), never from a new capture.
/// The Mac app has the same rules in ClipExport.swift.
public static class ClipPlan
{
    /// Longest stretch of time one clip can cover.
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(2);
    /// Export Clip… without marks: the 5 minutes up to the shown screenshot.
    public static readonly TimeSpan DefaultLength = TimeSpan.FromMinutes(5);
    /// 1 = real time.
    public static readonly double[] Speeds = { 1, 2, 5, 10, 30 };
    /// A screenshot stays at most this long (seconds), like a block on the bar; longer breaks are skipped.
    public const double MaxHold = 60;
    /// The last screenshot of a clip stays at least this long (seconds of real time).
    public const double LastHold = 2;
    /// GIFs get big fast: at most this many pictures, and browsers slow down anything shown under 2/100 s.
    public const int MaxGifFrames = 600;
    public const double MinGifDelay = 0.05;
    public const int MaxVideoFrames = 20_000;
    public const double MinVideoStep = 1.0 / 30;
    /// Long side of the output, in pixels. Screenshots are scaled down to fit, never up.
    public const int MaxGifSide = 1280, MaxVideoSide = 1920;
    /// A video repeats a held picture at least this often (seconds), so players can seek and show it.
    public const double MaxVideoGap = 1;

    public sealed record Result(List<ClipFrame> Frames, int Blocked, double RealSeconds)
    {
        /// Length of the clip in seconds.
        public double Duration => Frames.Count == 0 ? 0 : Frames[^1].At + Frames[^1].Duration;
    }

    /// The range Export Clip… starts with: the marked range, or the 5 minutes up to the shown moment,
    /// never longer than MaxLength.
    public static (DateTime Start, DateTime End) DefaultRange(DateTime shown, DateTime? markStart, DateTime? markEnd)
    {
        DateTime start, end;
        if (markStart is DateTime a && markEnd is DateTime b) (start, end) = a <= b ? (a, b) : (b, a);
        else if (markStart is DateTime s) (start, end) = shown > s ? (s, shown) : (s, s + DefaultLength);
        else if (markEnd is DateTime e) (start, end) = (e - DefaultLength, e);
        else (start, end) = (shown - DefaultLength, shown);
        if (end - start > MaxLength) start = end - MaxLength;
        return (start, end);
    }

    /// The screenshots taken from `start` to `end` (inclusive) out of `frames` (time-sorted, as the timeline shows
    /// them, so an app filter applies), at `speed` times real time. Each one lasts until the next screenshot, at
    /// most MaxHold; frames that `hides` (the block list now) are left out and their time skipped.
    public static Result Make(IReadOnlyList<TimelineFrame> frames, DateTime start, DateTime end, double speed,
                              ClipFormat format, Func<TimelineFrame, bool>? hides = null)
    {
        speed = Math.Max(0.01, speed);
        var raw = new List<(TimelineFrame Frame, double Secs)>();
        int blocked = 0;
        int i = LowerBound(frames, start);
        for (; i < frames.Count && frames[i].Time <= end; i++)
        {
            var f = frames[i];
            // While filtering by app, a frame lasts until the next screenshot of any app (its End).
            var next = f.End != null ? AppFilter.Until(f)
                     : i + 1 < frames.Count ? frames[i + 1].Time : f.Time.AddSeconds(LastHold);
            double secs = Math.Min((next - f.Time).TotalSeconds, MaxHold);
            bool last = i + 1 >= frames.Count || frames[i + 1].Time > end;
            if (last) secs = Math.Min(secs, Math.Max(LastHold, (end - f.Time).TotalSeconds));
            if (hides != null && hides(f)) { blocked++; continue; }
            if (secs <= 0) continue;
            raw.Add((f, secs));
        }
        double real = raw.Sum(r => r.Secs);
        double total = real / speed;
        double step = format == ClipFormat.Gif
            ? Math.Max(MinGifDelay, total / MaxGifFrames)
            : Math.Max(MinVideoStep, total / MaxVideoFrames);
        return new Result(Merge(raw.Select(r => (r.Frame, r.Secs / speed)).ToList(), step), blocked, real);
    }

    /// Pictures shorter than `step` give way to the next one, so the clip has no more pictures than it can show.
    static List<ClipFrame> Merge(List<(TimelineFrame Frame, double Secs)> items, double step)
    {
        var out_ = new List<ClipFrame>();
        if (items.Count == 0) return out_;
        double at = 0;
        var cur = new ClipFrame(items[0].Frame, 0, items[0].Secs);
        at += items[0].Secs;
        for (int i = 1; i < items.Count; i++)
        {
            if (cur.Duration < step) cur = cur with { Frame = items[i].Frame, Duration = cur.Duration + items[i].Secs };
            else { out_.Add(cur); cur = new ClipFrame(items[i].Frame, at, items[i].Secs); }
            at += items[i].Secs;
        }
        out_.Add(cur with { Duration = Math.Max(cur.Duration, step) });
        return out_;
    }

    /// Video samples: each picture, repeated every `maxGap` seconds while it is held.
    public static List<ClipFrame> Samples(IReadOnlyList<ClipFrame> frames, double maxGap = MaxVideoGap)
    {
        var out_ = new List<ClipFrame>();
        foreach (var f in frames)
        {
            int n = Math.Max(1, (int)Math.Ceiling(f.Duration / maxGap - 1e-9));
            double d = f.Duration / n;
            for (int k = 0; k < n; k++) out_.Add(f with { At = f.At + k * d, Duration = d });
        }
        return out_;
    }

    /// How many video samples `Samples` makes, without making them.
    public static int SampleCount(IReadOnlyList<ClipFrame> frames, double maxGap = MaxVideoGap) =>
        frames.Sum(f => Math.Max(1, (int)Math.Ceiling(f.Duration / maxGap - 1e-9)));

    /// H.264 bitrate (bits per second) for a `w`×`h` video: screens are mostly still, so a modest one keeps text
    /// sharp without big files.
    public static long VideoBitrate(int w, int h) => Math.Clamp((long)w * h * 2, 1_000_000, 6_000_000);

    /// A rough guess, shown before exporting, of the file's size (bytes) and how long making it takes (seconds)
    /// for a `w`×`h` output. Screens compress very differently, so this only gives the order of magnitude.
    public static (long Bytes, double Seconds) Estimate(Result plan, ClipFormat format, int w, int h)
    {
        int pictures = plan.Frames.Count;
        double px = (double)w * h;
        if (format == ClipFormat.Gif)
        {
            // The first picture in full, then only what changed, which is usually a small part of the screen.
            double bytes = px * 0.5 + Math.Max(0, pictures - 1) * px * 0.03 + 1024;
            return ((long)bytes, pictures * (0.05 + px * 5e-8));
        }
        // A mostly still screen uses about a third of the bitrate allowed.
        double size = VideoBitrate(w, h) / 8.0 * plan.Duration / 3 + 4096;
        return ((long)size, pictures * 0.05 + SampleCount(plan.Frames) * 0.01);
    }

    /// "820 KB" or "12.4 MB".
    public static string FileSize(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / 1048576.0:0.0} MB";

    /// What is going into the clip, shown before exporting: "0:42 clip · 312 frames · 1280×800 · about 4.1 MB · takes about 0:20".
    public static string Summary(Result plan, ClipFormat format, int w, int h)
    {
        var (bytes, secs) = Estimate(plan, format, w, h);
        int n = plan.Frames.Count;
        return $"{Length(plan.Duration)} clip · {n} frame{(n == 1 ? "" : "s")} · {w}×{h} · about {FileSize(bytes)} · takes about {Length(Math.Max(1, secs))}";
    }

    /// While exporting: "Frame 120 of 600 · 20% · 0:12 elapsed · about 0:48 left".
    public static string ProgressText(int done, int total, double elapsed)
    {
        total = Math.Max(1, total);
        done = Math.Clamp(done, 0, total);
        var s = $"Frame {done} of {total} · {done * 100 / total}% · {Length(elapsed)} elapsed";
        if (done >= total) return s + " · finishing the file…";
        if (done == 0 || elapsed < 1) return s + " · working out the time left…";
        return s + $" · about {Length(elapsed * (total - done) / done)} left";
    }

    /// Output size for a `w`×`h` screenshot: scaled down to fit, even sizes for video (H.264 needs them).
    public static (int W, int H) OutputSize(int w, int h, ClipFormat format)
    {
        int max = format == ClipFormat.Gif ? MaxGifSide : MaxVideoSide;
        double s = Math.Min(1, (double)max / Math.Max(1, Math.Max(w, h)));
        int ow = Math.Max(1, (int)(w * s)), oh = Math.Max(1, (int)(h * s));
        if (format == ClipFormat.Mp4) { ow = Math.Max(2, ow & ~1); oh = Math.Max(2, oh & ~1); }
        return (ow, oh);
    }

    /// True for a frame the block list covers now (same rule as Ask AI): by app, window title, site or word.
    public static bool Hides(Blocklist list, TimelineFrame f) => f.Blocked ||
        list.BlocksWindow(f.AppName, f.AppName, f.WindowName) || list.BlocksUrl(f.Url) || list.HasKeyword(f.Url);

    /// "OpenRewind 2026-10-19 14.30.05.gif", from a local time.
    public static string FileName(DateTime local, string ext) => $"OpenRewind {local:yyyy-MM-dd HH.mm.ss}.{ext}";

    /// "0:42", "12:05" or "1:02:03".
    public static string Length(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// "1×", "2×", "30×".
    public static string SpeedLabel(double speed) => speed == 1 ? "1× (real time)" : $"{speed:0.#}×";

    static int LowerBound(IReadOnlyList<TimelineFrame> frames, DateTime t)
    {
        int lo = 0, hi = frames.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (frames[mid].Time < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
