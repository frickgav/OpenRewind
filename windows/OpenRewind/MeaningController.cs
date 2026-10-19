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
    CancellationTokenSource? cts;
    OnnxEmbedder? embedder;

    public Phase State { get; private set; } = Phase.Off;
    /// Download progress, 0…1.
    public double Download { get; private set; }
    public string? Error { get; private set; }
    /// Raised on a background thread when State or Download changes.
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
        if (cts != null) return;
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        Error = null;
        Service.Start();
        Task.Run(async () =>
        {
            try
            {
                if (!ModelPresent) Set(Phase.Downloading);
                long last = -1;
                await MiniLm.Download(ModelDir, http, p =>
                {
                    Download = p;
                    var pct = (long)(p * 100);
                    if (pct != last) { last = pct; Changed?.Invoke(); }
                }, ct).ConfigureAwait(false);
                Set(Phase.Loading);
                var e = OnnxEmbedder.Load(ModelDir);
                if (ct.IsCancellationRequested) { e.Dispose(); return; }
                embedder = e;
                Service.Embedder = e;
                Log.Write("search by meaning: model ready");
                Set(Phase.Ready);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return;
                Error = e is HttpRequestException ? "could not download the model. Check the internet connection." : e.Message;
                Log.Write($"search by meaning: {e.Message}");
                Set(Phase.Failed);
            }
        }, ct);
    }

    /// Stop indexing. With `deleteIndex`, the stored moments are removed too.
    public void Stop(bool deleteIndex)
    {
        cts?.Cancel();
        cts = null;
        Service.Stop();
        Service.Embedder = null;
        embedder?.Dispose();
        embedder = null;
        if (deleteIndex) engine.Db.ClearMeaning();
        Set(Phase.Off);
    }

    /// Try the download again after it failed.
    public void Retry()
    {
        Stop(deleteIndex: false);
        Start();
    }

    void Set(Phase p) { State = p; Changed?.Invoke(); }

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

    /// The line under the switch in the menu.
    public string StatusLine()
    {
        switch (State)
        {
            case Phase.Downloading: return $"Downloading the model… {(int)(Download * 100)}%";
            case Phase.Loading: return "Loading the model…";
            case Phase.Failed: return $"⚠ Not working: {Error}";
            case Phase.Off: return "Off";
        }
        var p = Service.Progress();
        return p.UpToDate ? $"Up to date · {p.Moments:N0} moments" : $"Indexing in the background… {p.Percent}% · {p.Moments:N0} moments";
    }

    public void Dispose() => Stop(deleteIndex: false);
}
