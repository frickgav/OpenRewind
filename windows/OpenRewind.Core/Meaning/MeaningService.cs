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

    /// Screenshots newer than this are left for the next round: the window may still be changing.
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(2);
    const int FrameBatch = 400, ChunkBatch = 100;

    public MeaningService(Database db) { this.db = db; }

    public bool Running => thread != null;

    public void Start()
    {
        if (thread != null) return;
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        thread = new Thread(() => Loop(ct)) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "Search by Meaning" };
        thread.Start();
        Log.Write("search by meaning: indexer on");
    }

    /// Stops the indexer and waits for it, so nothing is written after this returns.
    public void Stop()
    {
        if (thread == null) return;
        cts!.Cancel();
        thread.Join(TimeSpan.FromSeconds(10));
        thread = null;
        cts = null;
        Log.Write("search by meaning: indexer off");
    }

    public void Dispose() => Stop();

    void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            try { wait = Embedder == null ? TimeSpan.FromSeconds(5) : Step(ct) ? TimeSpan.Zero : TimeSpan.FromSeconds(30); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Write($"search by meaning: {e.Message}"); wait = TimeSpan.FromMinutes(1); }
            if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
        }
    }

    /// Index one batch of screenshots and one of audio. False when there was nothing to do.
    public bool Step(CancellationToken ct = default, DateTime? nowUtc = null)
    {
        if (Embedder is not IEmbedder e) return false;
        db.MeaningUseModel(e.ModelId);
        var list = Blocked();
        bool worked = false;

        long fc = long.TryParse(db.MeaningState(Database.FrameCursor), out var f) ? f : 0;
        var frames = db.MeaningFrames(fc, Time.Iso((nowUtc ?? DateTime.UtcNow) - Settle), FrameBatch);
        if (frames.Count > 0)
        {
            var (moments, done) = MomentChunker.Screen(frames, frames.Count < FrameBatch,
                                                       fr => Hides(list, fr.AppName, fr.WindowName, fr.Url));
            foreach (var m in moments) { Save(e, m, Database.FrameCursor, m.LastId, ct); worked = true; }
            if (done > fc) { db.SetMeaningState(Database.FrameCursor, done.ToString()); worked = true; }
        }

        long cc = long.TryParse(db.MeaningState(Database.ChunkCursor), out var c) ? c : 0;
        var (segments, doneChunks) = db.MeaningAudio(cc, ChunkBatch);
        foreach (var m in MomentChunker.Audio(segments)) { Save(e, m, Database.ChunkCursor, m.LastId, ct); worked = true; }
        if (doneChunks > cc) { db.SetMeaningState(Database.ChunkCursor, doneChunks.ToString()); worked = true; }
        return worked;
    }

    /// Make and store one vector, then rest a multiple of the time it took (more while the user is busy or on battery).
    void Save(IEmbedder e, Moment m, string cursorKey, long cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        var v = e.Embed(MomentChunker.EmbedText(m));
        db.SaveMoment(m, e.ModelId, Vectors.Quantize(v), cursorKey, cursor);
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
        db.ScanMoments(model, q.Start, q.End, (id, audio, app, window, url, ts, vec) =>
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
