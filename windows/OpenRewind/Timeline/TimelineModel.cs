using System.Drawing;
using NAudio.Wave;

namespace OpenRewind;

/// State behind the timeline overlay: which moment is shown, its screenshot, and audio at that moment.
/// All times are UTC; the view turns them into local time.
sealed class TimelineModel
{
    public readonly Database Db;
    public List<TimelineFrame> Frames { get; private set; } = new();
    public List<TimelineAudio> Audio { get; private set; } = new();
    /// Recorded stretches, with long breaks between them (see TimelineAxis).
    List<TimelineAxis.Session> sessions = new();
    public int Index { get; private set; }
    public Image? Image { get; private set; }
    public string? Caption { get; private set; }
    public long? PlayingChunk { get; private set; }
    /// Seconds of history shown across the timeline bar.
    public double Span = 15 * 60;
    /// The exact time the playhead is on. Follows audio while it plays.
    public DateTime Playhead { get; private set; } = DateTime.UtcNow;
    public DateTime LoadedSince { get; private set; } = DateTime.UtcNow;

    // Search.
    public string Query = "";
    public List<SearchHit> Hits = new();
    public int SelectedHit;
    /// True while the result list is open under the search box.
    public bool ShowResults;
    /// True while the keyboard shortcut card is open.
    public bool ShowHelp;

    /// Something visible changed; the overlay repaints.
    public event Action? Changed;
    void Notify() => Changed?.Invoke();

    readonly Dictionary<long, Image> imageCache = new();
    readonly List<long> cacheOrder = new();
    readonly Dictionary<long, Image?> thumbCache = new();
    readonly List<long> thumbOrder = new();
    WaveOutEvent? player;
    WaveFileReader? reader;
    TimelineAudio? playing;
    readonly System.Windows.Forms.Timer playTimer = new() { Interval = 250 };

    public TimelineModel(Database db)
    {
        Db = db;
        playTimer.Tick += (_, _) => FollowAudio();
    }

    public TimelineFrame? Current => Index >= 0 && Index < Frames.Count ? Frames[Index] : null;

    /// True when the newest screenshot is shown.
    public bool IsLive => Frames.Count == 0 || Index == Frames.Count - 1;

    /// Axis for the current zoom: each break gets 6% of the visible width.
    public TimelineAxis Axis => new(sessions, Span * 0.06);

    /// Audio chunks that cover the playhead (mic and system audio can overlap).
    public List<TimelineAudio> AudioHere => Audio.Where(a => a.Start <= Playhead && Playhead < a.End).ToList();

    public void Reload(double hours = 24)
    {
        StopAudio();
        LoadedSince = DateTime.UtcNow.AddHours(-hours);
        Frames = Db.TimelineFrames(LoadedSince);
        Audio = Db.TimelineAudio(LoadedSince);
        sessions = TimelineAxis.BuildSessions(Frames, Audio);
        Show(Frames.Count - 1);
    }

    /// Pull in older history when the user scrolls to the start.
    public void LoadOlder(double hours = 24)
    {
        var since = LoadedSince.AddHours(-hours);
        var older = Db.TimelineFrames(since).Where(f => f.Time < LoadedSince).ToList();
        if (older.Count == 0) return;
        var keepId = Current?.Id;
        LoadedSince = since;
        Frames = older.Concat(Frames).ToList();
        Audio = Db.TimelineAudio(since);
        sessions = TimelineAxis.BuildSessions(Frames, Audio);
        if (keepId != null) Index = Math.Max(0, Frames.FindIndex(f => f.Id == keepId));
    }

    public void Step(int n)
    {
        if (Frames.Count == 0) return;
        if (Index + n < 0) LoadOlder();
        StopAudio();
        Show(Index + n);
    }

    public void Jump(DateTime date)
    {
        if (Frames.Count == 0) return;
        if (date < Frames[0].Time) LoadOlder();
        StopAudio();
        Show(FrameIndex(date));
        var now = DateTime.UtcNow;
        Playhead = date > now ? now : date < Frames[0].Time ? Frames[0].Time : date;
        UpdateCaption();
        Notify();
    }

    public void JumpToNow() { StopAudio(); Show(Frames.Count - 1); }

    /// Last frame at or before `date` (binary search; frames are sorted by time).
    public int FrameIndex(DateTime date)
    {
        int lo = 0, hi = Frames.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Frames[mid].Time <= date) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    void Show(int i)
    {
        if (Frames.Count == 0) { Image = null; Notify(); return; }
        Index = Math.Clamp(i, 0, Frames.Count - 1);
        var f = Frames[Index];
        Playhead = f.Time;
        Image = LoadImage(f);
        UpdateCaption();
        // Warm the cache for the next scroll step either way.
        foreach (var j in new[] { Index - 1, Index + 1 }) if (j >= 0 && j < Frames.Count) LoadImage(Frames[j]);
        Notify();
    }

    /// Read the file into memory so the JPEG is not locked (retention may delete it).
    static Image? ReadImage(string path)
    {
        try { return Image.FromStream(new MemoryStream(File.ReadAllBytes(path))); }
        catch { return null; }
    }

    Image? LoadImage(TimelineFrame f)
    {
        if (imageCache.TryGetValue(f.Id, out var img)) return img;
        if (ReadImage(f.Path) is not Image loaded) return null;
        imageCache[f.Id] = loaded;
        cacheOrder.Add(f.Id);
        if (cacheOrder.Count > 24) { imageCache.Remove(cacheOrder[0]); cacheOrder.RemoveAt(0); }
        return loaded;
    }

    /// The frame shown at `date` and a small picture of it, for the hover preview on the bar.
    /// Null when nothing was recorded within a minute before `date`.
    public (TimelineFrame Frame, Image? Thumb)? Preview(DateTime date)
    {
        if (Frames.Count == 0 || date < Frames[0].Time) return null;
        var f = Frames[FrameIndex(date)];
        if ((date - f.Time).TotalSeconds > 60) return null;
        if (thumbCache.TryGetValue(f.Id, out var cached)) return (f, cached);
        Image? thumb = null;
        if (ReadImage(f.Path) is Image full)
        {
            using (full)
            {
                double s = Math.Min(1, 480.0 / Math.Max(full.Width, full.Height));
                thumb = new Bitmap(full, Math.Max(1, (int)(full.Width * s)), Math.Max(1, (int)(full.Height * s)));
            }
        }
        thumbCache[f.Id] = thumb;
        thumbOrder.Add(f.Id);
        if (thumbOrder.Count > 64) { thumbCache.Remove(thumbOrder[0]); thumbOrder.RemoveAt(0); }
        return (f, thumb);
    }

    void UpdateCaption()
    {
        var here = AudioHere;
        var a = here.FirstOrDefault(x => x.Id == PlayingChunk) ?? here.FirstOrDefault();
        if (a == null) { Caption = null; return; }
        var offset = (Playhead - a.Start).TotalSeconds;
        var caps = Db.Captions(a.Id);
        Caption = caps.FirstOrDefault(c => c.Start <= offset + 0.5 && offset <= c.End + 0.5)?.Text
                  ?? caps.LastOrDefault(c => c.Start <= offset)?.Text;
    }

    // MARK: search

    public void RunSearch()
    {
        var q = Query.Trim();
        if (q.Length == 0) { Hits = new(); ShowResults = false; Notify(); return; }
        Hits = SearchHits.Find(Db, q);
        SelectedHit = 0;
        ShowResults = true;
        Notify();
    }

    /// Go to a result: load enough history, move the playhead, play speech from that point.
    public void Open(SearchHit hit)
    {
        if (hit.Time < LoadedSince) Reload((DateTime.UtcNow - hit.Time).TotalHours + 1);
        Jump(hit.Time);
        ShowResults = false;
        if (hit.IsAudio && Audio.FirstOrDefault(a => a.Id == hit.ChunkId) is TimelineAudio a) Play(a);
        Notify();
    }

    public void OpenSelected()
    {
        if (SelectedHit >= 0 && SelectedHit < Hits.Count) Open(Hits[SelectedHit]);
    }

    public void MoveSelection(int d)
    {
        if (Hits.Count == 0) return;
        SelectedHit = ((SelectedHit + d) % Hits.Count + Hits.Count) % Hits.Count;
        Notify();
    }

    /// Next / previous match in time order, after the result list is closed (Ctrl+G / Shift+Ctrl+G).
    public void NextHit(bool older)
    {
        if (Hits.Count == 0) return;
        MoveSelection(older ? 1 : -1);
        Open(Hits[SelectedHit]);
    }

    public void ClearSearch() { Query = ""; Hits = new(); ShowResults = false; Notify(); }

    // MARK: audio

    public void ToggleAudio()
    {
        if (player != null) StopAudio();
        else if (AudioHere.FirstOrDefault() is TimelineAudio a) Play(a);
        Notify();
    }

    public void Play(TimelineAudio a)
    {
        StopAudio();
        try
        {
            reader = new WaveFileReader(a.Path);
            var at = TimeSpan.FromSeconds(Math.Max(0, (Playhead - a.Start).TotalSeconds));
            if (at < reader.TotalTime) reader.CurrentTime = at;
            player = new WaveOutEvent();
            player.Init(reader);
            player.Play();
            playing = a;
            PlayingChunk = a.Id;
            // While audio plays, move the playhead and the screenshot along with it, like a video.
            playTimer.Start();
        }
        catch (Exception e)
        {
            Log.Write($"cannot play audio: {e.Message}");
            StopAudio();
        }
        Notify();
    }

    void FollowAudio()
    {
        if (player == null || reader == null || playing == null) return;
        if (player.PlaybackState != PlaybackState.Playing) { StopAudio(); Notify(); return; }
        var t = playing.Start + reader.CurrentTime;
        Playhead = t;
        var i = FrameIndex(t);
        if (i != Index && Frames.Count > 0)
        {
            Index = i;
            Image = LoadImage(Frames[i]);
        }
        UpdateCaption();
        Notify();
    }

    public void StopAudio()
    {
        playTimer.Stop();
        player?.Stop();
        player?.Dispose(); player = null;
        reader?.Dispose(); reader = null;
        playing = null;
        PlayingChunk = null;
    }

    /// Stable color per app, same hue as on the Mac.
    public static Color AppColor(string app) => FromHsv(SearchHits.AppHue(app), 0.55, 0.95);

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
