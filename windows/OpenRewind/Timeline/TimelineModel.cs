using System.Drawing;
using NAudio.Wave;

namespace OpenRewind;

/// State behind the timeline overlay: which moment is shown, its screenshot, and audio at that moment.
/// All times are UTC; the view turns them into local time.
sealed partial class TimelineModel
{
    public readonly Database Db;
    /// The screenshots shown: all of them, or only those of the filtered app.
    public List<TimelineFrame> Frames { get; private set; } = new();
    /// Every screenshot loaded, whatever the app filter.
    List<TimelineFrame> allFrames = new();
    /// Only show moments when this app was in front (null = all apps).
    public string? AppFilterName { get; private set; }
    /// Where the hidden apps are saved (TimelineHiddenApps) and the block list, for H and the apps menu. Set by the tray app.
    public Settings? Settings { get; set; }
    /// Apps never shown in the timeline (see HiddenApps), sorted.
    public IReadOnlyList<string> HiddenAppNames => Settings?.TimelineHiddenApps ?? (IReadOnlyList<string>)Array.Empty<string>();
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
    /// When the timeline was loaded. The bar is laid out against this, not the clock,
    /// so it holds still while the overlay is open (new screenshots appear on the next open).
    public DateTime LoadedAt { get; private set; } = DateTime.UtcNow;

    // Search.
    public string Query = "";
    public List<SearchHit> Hits = new();
    public int SelectedHit;
    /// True while the result list is open under the search box.
    public bool ShowResults;
    /// True while the keyboard shortcut card is open.
    public bool ShowHelp;
    /// Search by Meaning, when the app has it (null otherwise). It can search only once its model is loaded.
    public MeaningService? Meaning;
    public bool MeaningAvailable => Meaning?.Embedder != null;
    /// The user picked Meaning over Keyword in the search box.
    public bool ByMeaning { get; private set; }
    /// The search box finds moments by meaning right now.
    public bool SearchingByMeaning => ByMeaning && MeaningAvailable;
    /// A meaning search is running in the background.
    public bool Searching { get; private set; }
    int searchSeq;
    /// Where the searched words are on the shown screenshot, as fractions of its size.
    public List<RectangleF> Highlights { get; private set; } = new();

    /// Short message shown for a moment at the top (for example, why O did nothing).
    public string? Notice { get; private set; }
    readonly System.Windows.Forms.Timer noticeTimer = new() { Interval = 2500 };

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
    // Reading the words off a screenshot takes a moment, so wait until scrolling settles.
    readonly System.Windows.Forms.Timer highlightTimer = new() { Interval = 150 };
    readonly Dictionary<(long, string), List<RectangleF>> highlightCache = new();
    readonly List<(long, string)> highlightOrder = new();

    public TimelineModel(Database db)
    {
        Db = db;
        playTimer.Tick += (_, _) => FollowAudio();
        highlightTimer.Tick += (_, _) => { highlightTimer.Stop(); FindHighlights(); };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Notice = null; Notify(); };
    }

    public void ShowNotice(string text)
    {
        Notice = text;
        noticeTimer.Stop();
        noticeTimer.Start();
        Notify();
    }

    /// What O opens for the shown screenshot, or null when there is nothing to go back to.
    /// Worked out once per shown screenshot (it may look through the open windows).
    public OpenTarget? CurrentTarget
    {
        get
        {
            if (Current is not TimelineFrame f) return null;
            if (targetFor != f.Id) { targetFor = f.Id; target = Reopen.For(f); }
            return target;
        }
    }
    long? targetFor;
    OpenTarget? target;

    public TimelineFrame? Current => Index >= 0 && Index < Frames.Count ? Frames[Index] : null;

    /// True when the newest screenshot is shown.
    public bool IsLive => Frames.Count == 0 || Index == Frames.Count - 1;

    /// Axis for the current zoom: each break gets 6% of the visible width.
    public TimelineAxis Axis => new(sessions, Span * 0.06);

    /// Apps in the loaded history that are not hidden, the one in front longest first (for the app filter menu).
    public List<string> Apps
    {
        get
        {
            var hidden = HiddenApps.Keys(HiddenAppNames);
            return AppFilter.Apps(allFrames).Where(a => !hidden.Contains(a)).ToList();
        }
    }

    /// The newest screenshot of any app.
    public TimelineFrame? Newest => allFrames.Count > 0 ? allFrames[^1] : null;

    /// Show only the moments when `app` was in front, or every app (null). Stays near the same time.
    public void SetAppFilter(string? app)
    {
        if (app == AppFilterName) return;
        var at = Playhead;
        StopAudio();
        AppFilterName = app;
        ApplyFilter();
        Show(FrameIndex(at));
        if (Query.Trim().Length > 0) RunSearch();
    }

    /// H: hide the app in this screenshot from the timeline, now and on every later open.
    public void HideCurrentApp()
    {
        if (Current is TimelineFrame f && f.AppName.Length > 0) Hide(f.AppName);
        else ShowNotice("No app to hide in this screenshot");
    }

    /// Leave `app` out of the timeline from now on (it is still recorded). `stopRecording` also adds it
    /// to the block list, so it is blurred and its text is not saved from now on.
    public void Hide(string app, bool stopRecording = false)
    {
        if (Settings is not Settings s || app.Length == 0) return;
        HiddenApps.Hide(s.TimelineHiddenApps, app);
        if (stopRecording && !s.ToBlocklist().BlocksApp(null, app))
        {
            s.BlockedApps.Add(new BlockedApp(Blocklist.IdFor(Reopen.ExeFor(app)), app));
            Blocklist.Current = s.ToBlocklist();
        }
        s.Save();
        HiddenChanged();
        ShowNotice(stopRecording ? $"Hid {app} and stopped recording it. Undo in Settings → Blocked Apps & Sites."
                                 : $"Hid {app} from the timeline. Show it again from the apps menu.");
    }

    /// Show `app` in the timeline again.
    public void Unhide(string app)
    {
        if (Settings is not Settings s || !HiddenApps.Show(s.TimelineHiddenApps, app)) return;
        s.Save();
        HiddenChanged();
        ShowNotice($"Showing {app} again");
    }

    /// The hidden apps changed (here or in Settings): show the timeline without them, near the same time.
    public void HiddenChanged()
    {
        var at = Playhead;
        StopAudio();
        if (AppFilterName != null && HiddenApps.Keys(HiddenAppNames).Contains(AppFilterName)) AppFilterName = null;
        ApplyFilter();
        Show(FrameIndex(at));
        if (Query.Trim().Length > 0) RunSearch();
        Notify();
    }

    /// Frames and the bar's stretches for the current app filter and hidden apps. While filtering or hiding,
    /// time in other (or hidden) apps becomes breaks, and audio does not keep those stretches open.
    void ApplyFilter()
    {
        Frames = AppFilter.Apply(allFrames, AppFilterName, HiddenAppNames.ToList());
        sessions = AppFilterName == null && HiddenAppNames.Count == 0
            ? TimelineAxis.BuildSessions(Frames, Audio)
            : TimelineAxis.BuildSessions(Frames, Array.Empty<TimelineAudio>(), gap: AppFilter.GapThreshold);
    }

    /// Audio chunks that cover the playhead (mic and system audio can overlap).
    public List<TimelineAudio> AudioHere => Audio.Where(a => a.Start <= Playhead && Playhead < a.End).ToList();

    /// `keepApp`: keep the app filter (opening an older search result); a fresh open shows every app.
    public void Reload(double hours = 24, bool keepApp = false)
    {
        var at = DateTime.UtcNow;
        var since = at.AddHours(-hours);
        var keep = keepApp ? AppFilterName : null;
        ReloadStars();
        Apply(at, since, Db.TimelineFrames(since), Db.TimelineAudio(since));
        if (keep != null && allFrames.Any(f => f.AppName == keep) && !HiddenApps.Keys(HiddenAppNames).Contains(keep)) { AppFilterName = keep; ApplyFilter(); Show(Frames.Count - 1); }
    }

    /// Bumped by every load, so a slower background load that finishes later is dropped.
    int loadToken;

    /// Show freshly read history (all apps), at its newest screenshot.
    void Apply(DateTime loadedAt, DateTime since, List<TimelineFrame> frames, List<TimelineAudio> audio)
    {
        loadToken++;
        StopAudio();
        targetFor = null;
        LoadedAt = loadedAt;
        LoadedSince = since;
        allFrames = frames;
        Audio = audio;
        AppFilterName = null;
        ApplyFilter();
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
        allFrames = older.Concat(allFrames).ToList();
        Audio = Db.TimelineAudio(since);
        ApplyFilter();
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
        Playhead = date > LoadedAt ? LoadedAt : date < Frames[0].Time ? Frames[0].Time : date;
        UpdateCaption();
        Notify();
    }

    public void JumpToNow() { StopAudio(); Show(Frames.Count - 1); }

    /// Go to a moment, showing only `app` when given (Insights). History back to that moment is read
    /// off the UI thread; the timeline shows what it has until then.
    public void OpenAt(DateTime date, string? app)
    {
        if (app == "") app = null;
        if (date >= LoadedSince) { Finish(); return; }
        var token = ++loadToken;
        var since = date.AddHours(-1);
        var at = DateTime.UtcNow;
        var ui = SynchronizationContext.Current;
        ShowNotice("Loading older recordings…");
        Task.Run(() => (Frames: Db.TimelineFrames(since), Audio: Db.TimelineAudio(since))).ContinueWith(t =>
        {
            void Done()
            {
                if (token != loadToken) return;  // something newer was opened meanwhile
                if (t.IsFaulted) { Log.Write($"could not load the timeline: {t.Exception?.GetBaseException().Message}"); return; }
                Apply(at, since, t.Result.Frames, t.Result.Audio);
                Finish();
            }
            if (ui != null) ui.Post(_ => Done(), null); else Done();
        });

        void Finish()
        {
            // An app that is not in the loaded history (or "Unknown app") shows every app.
            if (app != null && HiddenApps.Keys(HiddenAppNames).Contains(app))
            {
                SetAppFilter(null);
                ShowNotice($"{app} is hidden from the timeline. Show it again from the apps menu.");
            }
            else if (app == null || !allFrames.Any(f => f.AppName == app)) SetAppFilter(null);
            else if (app != AppFilterName)
            {
                SetAppFilter(app);
                ShowNotice($"Only showing {app}. Press A for all apps.");
            }
            Jump(date);
        }
    }

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
        if (Frames.Count == 0) { Image = null; RefreshText(); Notify(); return; }
        Index = Math.Clamp(i, 0, Frames.Count - 1);
        var f = Frames[Index];
        Playhead = f.Time;
        Image = LoadImage(f);
        RefreshHighlights();
        RefreshText();
        UpdateCaption();
        // Warm the cache for the next scroll step either way.
        foreach (var j in new[] { Index - 1, Index + 1 }) if (j >= 0 && j < Frames.Count) LoadImage(Frames[j]);
        Notify();
    }

    /// The frame's picture, still or video frame (looked up again if it moved since the list was read).
    Bitmap? Picture(TimelineFrame f) => FrameImages.LoadCurrent(Db, f.Id, f.Source);

    /// Still or video frame, read into memory (the file is not kept open: retention may delete it).
    Image? LoadImage(TimelineFrame f)
    {
        if (imageCache.TryGetValue(f.Id, out var img)) return img;
        if (Picture(f) is not Image loaded) return null;
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
        if (date > (f.End != null ? AppFilter.Until(f) : f.Time.AddSeconds(60))) return null;
        if (thumbCache.TryGetValue(f.Id, out var cached)) return (f, cached);
        Image? thumb = null;
        if (Picture(f) is Image full)
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

    /// Keyword (the exact words) or Meaning (moments about the same thing, best first).
    public void SetByMeaning(bool on)
    {
        if (ByMeaning == on) return;
        ByMeaning = on;
        if (Query.Trim().Length > 0) RunSearch(); else { RefreshHighlights(); Notify(); }
    }

    public void RunSearch()
    {
        var q = Query.Trim();
        if (StarredOnly) { ShowStarred(); return; }
        var seq = ++searchSeq;
        Searching = false;
        RefreshHighlights();
        if (q.Length == 0) { Hits = new(); ShowResults = false; Notify(); return; }
        if (SearchingByMeaning && Meaning is MeaningService m && SynchronizationContext.Current is SynchronizationContext ui)
        {
            // Turning the question into a vector and reading every stored one takes a moment: do it off the UI thread.
            Searching = true;
            Hits = new();
            SelectedHit = 0;
            ShowResults = true;
            Notify();
            var app = AppFilterName;
            Task.Run(() =>
            {
                List<SearchHit> hits;
                try { hits = SearchHits.FromMeaning(m.Search(q, new MeaningQuery { App = app, Limit = 50 })); }
                catch (Exception e) { Log.Write($"search by meaning: {e.Message}"); hits = new(); }
                ui.Post(_ =>
                {
                    if (seq != searchSeq) return;
                    Searching = false;
                    ShowHits(hits);
                }, null);
            });
            return;
        }
        ShowHits(SearchHits.Find(Db, q, AppFilterName));
    }

    void ShowHits(List<SearchHit> hits)
    {
        // Nothing from a hidden app, nor speech heard while one was in front.
        var hidden = HiddenApps.Keys(HiddenAppNames);
        if (hidden.Count > 0)
            hits = hits.Where(h => h.IsAudio ? !AppFilter.HiddenAt(allFrames, h.Time, hidden) : !hidden.Contains(h.Title)).ToList();
        // While filtering, speech only counts when it was heard with that app in front.
        Hits = AppFilterName == null ? hits : hits.Where(h => !h.IsAudio || AppFilter.Covers(Frames, h.Time)).ToList();
        SelectedHit = 0;
        ShowResults = true;
        Notify();
    }

    /// Go to a result: load enough history, move the playhead, play speech from that point.
    public void Open(SearchHit hit)
    {
        if (hit.Time < LoadedSince) Reload((DateTime.UtcNow - hit.Time).TotalHours + 1, keepApp: true);
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

    public void ClearSearch() { Query = ""; Hits = new(); searchSeq++; Searching = false; ShowResults = false; StarredOnly = false; RefreshHighlights(); Notify(); }

    /// Words to mark on the screenshot. None for a meaning search: its matches need not share a word with the question.
    List<string> HighlightTerms => SearchingByMeaning ? new() : SearchHits.Terms(Query);

    /// Mark the searched words on the shown screenshot. Uses what is cached, otherwise reads it shortly.
    void RefreshHighlights()
    {
        highlightTimer.Stop();
        var terms = HighlightTerms;
        if (terms.Count == 0 || Current is not TimelineFrame f || Image == null || !Ocr.Available) { Highlights = new(); return; }
        if (highlightCache.TryGetValue((f.Id, string.Join(" ", terms)), out var cached)) { Highlights = cached; return; }
        Highlights = new();
        highlightTimer.Start();
    }

    void FindHighlights()
    {
        var terms = HighlightTerms;
        if (terms.Count == 0 || Current is not TimelineFrame f) return;
        var key = (f.Id, string.Join(" ", terms));
        var ui = SynchronizationContext.Current;
        Task.Run(() =>
        {
            // Its own copy of the picture: GDI+ images can't be shared across threads.
            using var bmp = Picture(f);
            var found = bmp == null ? new List<RectangleF>() : Ocr.Find(bmp, terms);
            ui?.Post(_ =>
            {
                highlightCache[key] = found;
                highlightOrder.Add(key);
                if (highlightOrder.Count > 64) { highlightCache.Remove(highlightOrder[0]); highlightOrder.RemoveAt(0); }
                if (Current?.Id != key.Item1 || string.Join(" ", HighlightTerms) != key.Item2) return;
                Highlights = found;
                Notify();
            }, null);
        });
    }

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
            reader = new WaveFileReader(new MemoryStream(RecordingFile.Read(a.Path)));
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
            RefreshHighlights();
            RefreshText();
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
