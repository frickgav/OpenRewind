using System.Runtime.InteropServices;

namespace OpenRewind;

/// Search by Meaning on Windows: gets the model (downloaded once, checked against its pinned
/// SHA-256), loads it, and runs the background indexer while the setting is on.
/// Turning it off stops the indexer and deletes the index; the model file stays so turning it
/// back on needs no second download.
sealed class MeaningController : IDisposable
{
    public enum Phase { Off, Downloading, Loading, Ready, Failed }

    readonly Engine engine;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
    // Guards cts, embedder, task, generation and State. Start/Stop run on the UI thread, the
    // download and load on a background task; the generation tells a late task it was turned off.
    readonly object sync = new();
    CancellationTokenSource? cts;
    OnnxEmbedder? embedder;
    Task task = Task.CompletedTask;
    int generation;

    public Phase State { get; private set; } = Phase.Off;
    /// Download progress, 0…1.
    public double Download { get; private set; }
    public string? Error { get; private set; }
    /// Raised on a background thread when State, Download or the status line changes.
    public event Action? Changed;

    public MeaningController(Engine engine)
    {
        this.engine = engine;
        engine.Meaning.Pace = Pace;
    }

    public MeaningService Service => engine.Meaning;
    public string ModelDir => MiniLm.Dir(engine.Config.Home);
    /// The model is already on this PC, so turning on downloads nothing.
    public bool ModelPresent => MiniLm.Present(ModelDir);

    public void Start()
    {
        Task previous;
        int gen;
        CancellationToken ct;
        lock (sync)
        {
            if (cts != null) return;
            cts = new CancellationTokenSource();
            ct = cts.Token;
            gen = ++generation;
            previous = task;
            Error = null;
        }
        Service.Start();
        lock (sync)
            task = Task.Run(async () =>
            {
                // A download from before a quick off/on writes the same .part file; let it finish first.
                try { await previous.ConfigureAwait(false); } catch (Exception) { }
                try
                {
                    if (!ModelPresent) Set(Phase.Downloading, gen);
                    long last = -1;
                    await MiniLm.Download(ModelDir, http, p =>
                    {
                        Download = p;
                        var pct = (long)(p * 100);
                        if (pct != last) { last = pct; Changed?.Invoke(); }
                    }, ct).ConfigureAwait(false);
                    Set(Phase.Loading, gen);
                    // Download has just checked both files against their pinned hashes; no need to hash 90 MB again.
                    var e = OnnxEmbedder.Load(ModelDir, verified: true);
                    lock (sync)
                    {
                        if (gen != generation || ct.IsCancellationRequested) { e.Dispose(); return; }
                        embedder = e;
                        Service.Embedder = e;
                        State = Phase.Ready;
                    }
                    Log.Write("search by meaning: model ready");
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    if (ct.IsCancellationRequested) return;
                    Log.Write($"search by meaning: {e.Message}");
                    lock (sync)
                    {
                        if (gen != generation) return;
                        Error = e is HttpRequestException or TimeoutException ? "could not download the model. Check the internet connection." : e.Message;
                    }
                    Set(Phase.Failed, gen);
                }
            });
    }

    /// Stop indexing. With `deleteIndex`, the stored moments are removed too.
    public void Stop(bool deleteIndex)
    {
        OnnxEmbedder? old;
        lock (sync)
        {
            generation++;
            cts?.Cancel();
            cts?.Dispose();
            cts = null;
            Service.Embedder = null;
            old = embedder;
            embedder = null;
            State = Phase.Off;
            status = null;
        }
        Service.Stop();
        // A search that already took the embedder gets ObjectDisposedException, never a released session.
        old?.Dispose();
        if (deleteIndex) engine.Db.ClearMeaning();
        Changed?.Invoke();
    }

    /// Try the download again after it failed.
    public void Retry()
    {
        Stop(deleteIndex: false);
        Start();
    }

    void Set(Phase p, int gen)
    {
        lock (sync)
        {
            if (gen != generation) return;
            State = p;
        }
        Changed?.Invoke();
    }

    /// Index briskly while nobody is at the PC, gently while they work, and slowest on battery.
    static MeaningPace Pace()
    {
        try
        {
            if (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline) return MeaningPace.Battery;
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (GetLastInputInfo(ref info) && unchecked((uint)Environment.TickCount - info.Time) > 5 * 60 * 1000) return MeaningPace.Idle;
        }
        catch (Exception) { }
        return MeaningPace.Active;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LastInputInfo { public uint Size; public uint Time; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInputInfo info);

    // The index counts run several COUNT(*) queries, so they are worked out in the background and
    // the menu shows the last answer.
    string? status;
    DateTime statusAt;
    int refreshing;

    /// The line under the switch in the menu. Never touches the database on the calling thread.
    public string StatusLine()
    {
        switch (State)
        {
            case Phase.Downloading: return $"Downloading the model… {(int)(Download * 100)}%";
            case Phase.Loading: return "Loading the model…";
            case Phase.Failed: return $"⚠ Not working: {Error}";
            case Phase.Off: return "Off";
        }
        string? cached;
        lock (sync) cached = status;
        if (cached == null || DateTime.UtcNow - statusAt > TimeSpan.FromSeconds(5)) RefreshStatus();
        return cached ?? "Checking the index…";
    }

    void RefreshStatus()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1) return;
        int gen;
        lock (sync) gen = generation;
        Task.Run(() =>
        {
            try
            {
                var p = Service.Progress();
                var line = Service.Paused ? $"Paused: could not save, will retry (see openrewind.log) · {p.Moments:N0} moments"
                    : p.UpToDate ? $"Up to date · {p.Moments:N0} moments" : $"Indexing in the background… {p.Percent}% · {p.Moments:N0} moments";
                bool changed;
                lock (sync)
                {
                    if (gen != generation) return;
                    changed = line != status;
                    status = line;
                    statusAt = DateTime.UtcNow;
                }
                if (changed) Changed?.Invoke();
            }
            catch (Exception e) { Log.Write($"search by meaning: status: {e.Message}"); }
            finally { Volatile.Write(ref refreshing, 0); }
        });
    }

    public void Dispose() => Stop(deleteIndex: false);
}
