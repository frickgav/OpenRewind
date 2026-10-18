using NAudio.CoreAudioApi;

namespace OpenRewind;

/// Owns everything that records. Screen and audio can each be turned on and off on their own.
/// The Whisper worker runs for the whole life of the process; the API server only while it is switched on.
sealed class Engine
{
    public readonly Config Config;
    public readonly Database Db;
    public readonly Recorder Recorder;
    public Transcriber? Transcriber { get; private set; }
    Server? server;
    CancellationTokenSource? screenCts, txCts;
    AudioStream? mic, system;
    System.Threading.Timer? watchdog;
    readonly object gate = new();
    /// The user wants audio on, even while a stream is between restarts.
    bool audioWanted;

    public bool ScreenOn => screenCts != null;
    public bool AudioOn => audioWanted;

    public Engine(Config config, Database db)
    {
        Config = config;
        Db = db;
        Recorder = new Recorder(config, db);
    }

    public bool ServerOn => server != null;

    public void StartServer()
    {
        if (server != null) return;
        var api = new Api(Db, Config, () => new()
        {
            // Windows has no screen-recording prompt; kept so API clients see the same fields as on the Mac.
            ["screen_permission"] = true,
            ["accessibility_permission"] = Config.UseAccessibility,
            ["ocr_available"] = Ocr.Available,
            ["platform"] = "windows",
        });
        var s = new Server(Config.Port, api.Handle);
        s.Start();
        server = s;
    }

    public void StopServer()
    {
        if (server == null) return;
        server.Dispose();
        server = null;
        Log.Write("local search server off");
    }

    public void StartTranscriber()
    {
        if (txCts != null || Transcriber.Create(Config, Db) is not Transcriber tx) return;
        Transcriber = tx;
        txCts = new CancellationTokenSource();
        var ct = txCts.Token;
        Task.Run(() => tx.Run(ct));
    }

    public void SetScreen(bool on)
    {
        lock (gate)
        {
            if (on && screenCts == null)
            {
                screenCts = new CancellationTokenSource();
                var ct = screenCts.Token;
                Task.Run(() => Recorder.Run(ct));
                Log.Write("screen recording on");
            }
            else if (!on && screenCts != null)
            {
                screenCts.Cancel();
                screenCts = null;
                Log.Write("screen recording off");
            }
        }
    }

    public void SetAudio(bool on)
    {
        lock (gate)
        {
            if (on && !audioWanted)
            {
                audioWanted = true;
                if (Config.RecordMic) StartMic();
                if (Config.RecordSystemAudio) StartSystem();
                StartTranscriber();
                watchdog ??= new System.Threading.Timer(_ => Watch(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            else if (!on && audioWanted)
            {
                watchdog?.Dispose(); watchdog = null;
                audioWanted = false;
                mic?.Dispose(); mic = null;
                system?.Dispose(); system = null;
                Log.Write("audio recording off");
            }
        }
    }

    /// Streams can stop without an error (device unplugged, default device changed, sleep).
    /// The mic sends data all the time, so 60 s of nothing means it is stuck: restart it.
    /// Loopback sends nothing while nothing plays, so it is restarted only when the default output device changed.
    void Watch()
    {
        lock (gate)
        {
            if (!audioWanted) return;
            if (Config.RecordMic && (mic == null || (DateTime.UtcNow - mic.Writer.LastAppend).TotalSeconds > 60))
            {
                Log.Write(mic == null ? "starting microphone again" : "microphone sent no sound for 60 s, restarting");
                mic?.Dispose(); mic = null;
                StartMic();
            }
            var outId = DefaultOutputId();
            if (Config.RecordSystemAudio && (system == null || outId != systemDevice))
            {
                Log.Write(system == null ? "starting system audio again" : "output device changed, restarting system audio");
                system?.Dispose(); system = null;
                StartSystem();
            }
        }
    }

    string? systemDevice;

    static string? DefaultOutputId()
    {
        try
        {
            using var e = new MMDeviceEnumerator();
            return e.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) ? e.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID : null;
        }
        catch { return null; }
    }

    void StartMic()
    {
        if (!AudioStream.HasDevice(DataFlow.Capture)) { Log.Write("no microphone found"); return; }
        try
        {
            var m = AudioStream.Mic(new ChunkWriter("microphone", true, Config.AudioChunkSeconds, Config, Db));
            m.Start();
            mic = m;
        }
        catch (Exception e)
        {
            Log.Write($"microphone not available: {e.Message} → check Settings → Privacy & security → Microphone → Let desktop apps access your microphone");
        }
    }

    void StartSystem()
    {
        systemDevice = DefaultOutputId();
        if (systemDevice == null) { Log.Write("no speakers or headphones found for system audio"); return; }
        try
        {
            var s = AudioStream.System(new ChunkWriter("system audio", false, Config.AudioChunkSeconds, Config, Db));
            s.Start();
            system = s;
        }
        catch (Exception e) { Log.Write($"system audio not available: {e.Message}"); }
    }

    public void Shutdown()
    {
        SetScreen(false);
        SetAudio(false);
        txCts?.Cancel();
        server?.Dispose();
    }
}
