using System.Diagnostics;

namespace OpenRewind;

/// Turns text into a sentence vector of length 1, on this computer.
public interface IEmbedder
{
    /// Stored with every vector; a different id means the index is made again.
    string ModelId { get; }
    float[] Embed(string text);
}

/// How hard the indexer may work: Idle (nobody at the computer), Active, or Battery.
public enum MeaningPace { Idle, Active, Battery }

/// Filters for a meaning search. Times are ISO (UTC).
public sealed class MeaningQuery
{
    public string? Start, End;
    /// Only screen moments of this app (exact name).
    public string? App;
    /// Only screen moments whose app and window title pass this.
    public Func<string, string, bool>? Match;
    public bool Screen = true, Audio = true;
    public int Limit = 40;
}

/// A moment could not be stored, or a cursor not moved (the inner exception says why). The cursor stays
/// put. `CursorKey` and `Cursor`: where it would have moved to.
public sealed class MeaningSaveFailed(string cursorKey, long cursor, Exception inner)
    : Exception($"a moment could not be saved: {inner.Message}", inner)
{
    public string CursorKey { get; } = cursorKey;
    public long Cursor { get; } = cursor;
}

/// The indexer's errors in a row. Each one doubles the wait (1 minute up to 1 hour), so a write that keeps
/// failing is tried, and logged, a few times an hour instead of over and over. The same moment failing
/// `TriesPerMoment` times in a row is skipped, so one that can never be saved does not hold up the rest.
public sealed class MeaningRetry
{
    /// With the waits below that is about 15 minutes.
    public const int TriesPerMoment = 5;
    public int Failures { get; private set; }
    (string Key, long Cursor)? stuck;
    int tries;

    public static TimeSpan Backoff(int failures) =>
        TimeSpan.FromSeconds(Math.Min(3600, 60 * Math.Pow(2, Math.Max(0, Math.Min(failures, 7) - 1))));

    public void Succeeded() { Failures = 0; stuck = null; tries = 0; }

    /// How long to wait, and the moment to skip (null for none).
    public (TimeSpan Wait, MeaningSaveFailed? Skip) Failed(Exception e)
    {
        Failures++;
        var wait = Backoff(Failures);
        if (e is not MeaningSaveFailed f) { stuck = null; tries = 0; return (wait, null); }
        if (stuck == (f.CursorKey, f.Cursor)) tries++; else { stuck = (f.CursorKey, f.Cursor); tries = 1; }
        if (tries < TriesPerMoment) return (wait, null);
        stuck = null; tries = 0;
        return (wait, f);
    }
}

/// Search by Meaning: a background indexer that gives each moment (see MomentChunker) a vector,
/// catching up on old recordings and keeping up with new ones, and a search that ranks moments by
/// how close they are to the question. Everything runs on this computer. Moments of blocked apps,
/// windows and sites are never indexed, and are left out of results too (for ones indexed before
/// they were blocked). The Mac app does the same in Meaning.swift.
public sealed class MeaningService : IDisposable
{
    readonly Database db;
    /// Null until the model is ready (Windows downloads it once).
    public IEmbedder? Embedder { get; set; }
    /// Set by the app: whether the user is away (index faster) or on battery (index slower).
    public Func<MeaningPace> Pace = () => MeaningPace.Active;
    public Func<Blocklist> Blocked = () => Blocklist.Current;
    Thread? thread;
    CancellationTokenSource? cts;
    /// Bumped by Stop. A step still running from before writes nothing (see `keep` in SaveMoment),
    /// even if it outlives Stop's wait.
    int generation;
    volatile bool paused;

    /// True while saves keep failing (the indexer waits and tries again); the tray says so.
    public bool Paused => paused;

    /// Screenshots newer than this are left for the next round: the window may still be changing.
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);
    const int FrameBatch = 400, ChunkBatch = 100;

    public MeaningService(Database db) { this.db = db; }

    /// The database this searches (McpMeaning rebinds when the MCP process reopened it).
    public Database Db => db;

    public bool Running => thread != null;

    public void Start()
    {
        if (thread != null) return;
        paused = false;
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        var gen = Volatile.Read(ref generation);
        thread = new Thread(() => Loop(ct, gen)) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Search by Meaning" };
        thread.Start();
        Log.Write("search by meaning: indexer on");
    }

    /// Stops the indexer and waits for it, so nothing is written after this returns.
    public void Stop()
    {
        Interlocked.Increment(ref generation);
        if (thread == null) return;
        cts!.Cancel();
        if (!thread.Join(TimeSpan.FromSeconds(10))) Log.Write("search by meaning: indexer still busy; it will write nothing more");
        thread = null;
        cts = null;
        Log.Write("search by meaning: indexer off");
    }

    public void Dispose() => Stop();

    void Loop(CancellationToken ct, int gen)
    {
        var retry = new MeaningRetry();
        Func<bool> keep = () => Volatile.Read(ref generation) == gen && !ct.IsCancellationRequested;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                if (Embedder == null) wait = TimeSpan.FromSeconds(5);
                else
                {
                    wait = Step(ct, gen: gen) ? TimeSpan.Zero : TimeSpan.FromSeconds(30);
                    retry.Succeeded();
                    paused = false;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                paused = true;
                var (w, skip) = retry.Failed(e);
                wait = w;
                Log.Write($"search by meaning: {e.Message}; trying again in {(int)wait.TotalMinutes} min");
                if (skip != null)
                {
                    try
                    {
                        db.SetMeaningState(skip.CursorKey, skip.Cursor.ToString(), keep);
                        Log.Write($"search by meaning: skipped a moment that could not be saved {MeaningRetry.TriesPerMoment} times");
                    }
                    catch (Exception x) { Log.Write($"search by meaning: {x.Message}"); }
                }
            }
            if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
        }
    }

    /// Moves a cursor; a failure is a MeaningSaveFailed, so the indexer waits longer each time.
    void SetCursor(string key, long cursor, Func<bool> keep)
    {
        try { db.SetMeaningState(key, cursor.ToString(), keep); }
        catch (Exception x) when (x is not OperationCanceledException) { throw new MeaningSaveFailed(key, cursor, x); }
    }

    /// Index one batch of screenshots and one of audio. False when there was nothing to do.
    public bool Step(CancellationToken ct = default, DateTime? nowUtc = null, int? gen = null)
    {
        if (Embedder is not IEmbedder e) return false;
        var g = gen ?? Volatile.Read(ref generation);
        Func<bool> keep = () => Volatile.Read(ref generation) == g && !ct.IsCancellationRequested;
        db.MeaningUseModel(e.ModelId, keep);
        var list = Blocked();
        bool worked = false;

        long fc = long.TryParse(db.MeaningState(Database.FrameCursor), out var f) ? f : 0;
        var frames = db.MeaningFrames(fc, Time.Iso((nowUtc ?? DateTime.UtcNow) - Settle), FrameBatch);
        if (frames.Count > 0)
        {
            var (moments, done) = MomentChunker.Screen(frames, frames.Count < FrameBatch,
                                                       fr => Hides(list, fr.AppName, fr.WindowName, fr.Url));
            foreach (var m in moments) { Save(e, m, Database.FrameCursor, m.LastId, keep, ct); worked = true; }
            if (done > fc) { SetCursor(Database.FrameCursor, done, keep); worked = true; }
        }

        long cc = long.TryParse(db.MeaningState(Database.ChunkCursor), out var c) ? c : 0;
        var (segments, doneChunks) = db.MeaningAudio(cc, ChunkBatch);
        foreach (var m in MomentChunker.Audio(segments)) { Save(e, m, Database.ChunkCursor, m.LastId, keep, ct); worked = true; }
        if (doneChunks > cc) { SetCursor(Database.ChunkCursor, doneChunks, keep); worked = true; }
        return worked;
    }

    /// Make and store one vector, then rest a multiple of the time it took (more while the user is busy or on battery).
    void Save(IEmbedder e, Moment m, string cursorKey, long cursor, Func<bool> keep, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var v = Vectors.Quantize(e.Embed(MomentChunker.EmbedText(m)));
        // A failed write ends the round: the rest of the batch would fail the same way, and the cursor
        // must not move past moments that were never stored. An empty vector is never stored: move on.
        if (v.Length == 0) SetCursor(cursorKey, cursor, keep);
        else
        {
            try { db.SaveMoment(m, e.ModelId, v, cursorKey, cursor, keep); }
            catch (Exception x) when (x is not OperationCanceledException) { throw new MeaningSaveFailed(cursorKey, cursor, x); }
        }
        if (!keep()) throw new OperationCanceledException();
        var rest = sw.Elapsed * (Pace() switch { MeaningPace.Idle => 1, MeaningPace.Battery => 9, _ => 4 });
        if (rest > TimeSpan.Zero && ct.WaitHandle.WaitOne(rest + TimeSpan.FromMilliseconds(20))) ct.ThrowIfCancellationRequested();
    }

    /// Same rule as the Ask AI lookups: app, private window, blocked word in the title or address, or blocked site.
    public static bool Hides(Blocklist list, string app, string window, string url) =>
        list.BlocksWindow(app, app, window) || list.BlocksUrl(url) || list.HasKeyword(url);

    public MeaningProgress Progress() => db.MeaningProgress(DateTime.UtcNow);

    /// The moments closest in meaning to `text`, best first. Blocked ones are left out.
    public List<MeaningHit> Search(string text, MeaningQuery q, Blocklist? blocked = null)
    {
        if (Embedder is not IEmbedder e || text.Trim().Length == 0) return new();
        var query = e.Embed(text.Trim());
        return Rank(db, e.ModelId, query, q, blocked ?? Blocked());
    }

    /// Streams every stored vector and keeps the best `q.Limit`.
    public static List<MeaningHit> Rank(Database db, string model, float[] query, MeaningQuery q, Blocklist blocked)
    {
        var top = new TopK<long>(q.Limit);
        db.ScanMoments(model, q.Start, q.End, (id, audio, app, window, url, vec) =>
        {
            if (audio ? !q.Audio : !q.Screen) return;
            if (!audio && q.App != null && app != q.App) return;
            if (!audio && q.Match != null && !q.Match(app, window)) return;
            if (!audio && Hides(blocked, app, window, url)) return;
            top.Add(id, Vectors.Score(vec, query));
        });
        var best = top.Best();
        var rows = db.MomentsById(best.Select(b => b.Item));
        return best.Where(b => rows.ContainsKey(b.Item)).Select(b => rows[b.Item] with { Score = b.Score }).ToList();
    }
}
