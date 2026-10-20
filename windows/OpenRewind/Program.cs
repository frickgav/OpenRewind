using System.Drawing;
using System.Drawing.Imaging;

namespace OpenRewind;

static class Program
{
    const string Usage = """
    OpenRewind — record your screen, read the text on it, search it locally.

    Usage:
      OpenRewind                  Run the tray app (what the Start menu shortcut runs)
      OpenRewind record           Record + serve API/UI, no tray icon (logs to this console)
      OpenRewind serve            Serve API/UI only, no recording
      OpenRewind once             Capture one frame and exit
      OpenRewind ingest <image> [--app NAME] [--window TITLE]
                                  OCR an image file and store it (for testing)
      OpenRewind ingest-audio <file.wav>
                                  Transcribe a WAV file and store it (for testing)
      OpenRewind delete <15m|1h|today|all|older:week>
                                  Delete recordings in that range
      OpenRewind doctor           Check what works on this PC
      OpenRewind mcp              MCP server for AI apps, over stdin/stdout (also --mcp).
                                  Turn on Settings → Allow AI Apps to Read My History first

    Options:
      --port N        API port (default 3031)
      --interval S    Seconds between captures (default 2)
      --scale X       Snapshot scale 0.1–1.0 (default 0.5)
      --no-ax         Skip accessibility text, use OCR only
      --no-audio      Do not record audio (or --no-mic / --no-system-audio)
      --chunk S       Audio chunk length in seconds (default 30)
      --whisper-model PATH   ggml Whisper model (default: auto-detect)
      --whisper-bin PATH     whisper-cli.exe (default: auto-detect)
      --language L    Whisper language code, or auto (default auto)
      --model ID      Transcription model for this run: whisper-auto, whisper-base, whisper-small,
                      whisper-large-v3-turbo or parakeet-v3 (default: the one picked in Settings)
      --home DIR      Data folder (default %USERPROFILE%\.openrewind, or %OPENREWIND_HOME%)

    Env:
      OPENREWIND_EXCLUDE         Comma list of extra app names to blur (on top of Blocked Apps in Settings)
      OPENREWIND_WHISPER_MODEL   Path to a ggml Whisper model
      (The old OPENSCREEN_ names still work.)
    """;

    public static Icon AppIcon { get; private set; } = SystemIcons.Application;
    public static Icon PausedIcon { get; private set; } = SystemIcons.Application;

    [STAThread]
    static int Main(string[] args)
    {
        Crash.Install();
        try { return Run(args); }
        catch (Exception e)
        {
            Crash.Write("startup", e);
            if (!args.Any(a => !a.StartsWith('-')) && !args.Contains("--mcp")) Crash.Tell(e);
            return 1;
        }
    }

    static int Run(string[] args)
    {
        var command = args.Contains("--mcp") ? "mcp" : args.FirstOrDefault(a => !a.StartsWith('-')) ?? "app";
        // Started by an AI app with stdin/stdout as pipes: no console, no tray, no windows.
        if (command == "mcp") return McpCommand.Run(args);
        // A WinExe has no console; print to the terminal that started us, if any.
        if (command != "app") Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
        if (args.Contains("-h") || args.Contains("--help") || command == "help") { Console.WriteLine(Usage); return 0; }

        ApplicationConfiguration.Initialize();
        LoadIcons();
        var config = Config.Load(args);
        // Settings → Transcription Model, unless --model picks one for this run.
        config.TranscriptionModel ??= Settings.Load(config.Home).TranscriptionModel;
        if (command == "app")
        {
            Log.OpenFile(config.Home);
            Log.Write($"OpenRewind {Updater.Current} starting ({Environment.ProcessPath})");
        }

        Database db;
        try { db = new Database(config.DbPath); }
        catch (Exception e)
        {
            Log.Write($"cannot open database at {config.DbPath}: {e.Message}");
            Crash.Write("opening the database", e);
            if (command == "app") Crash.Tell(e);
            return 1;
        }
        var engine = new Engine(config, db);

        string? Value(string flag)
        {
            int i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        string? Operand() => args.SkipWhile(a => a != command).Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        switch (command)
        {
            case "app":
                // After an update the new copy starts while the old one is quitting.
                Updater.CleanUp(args);
                // One copy at a time: a second launch just opens the timeline of the first.
                using (var single = new Mutex(true, @"Local\OpenRewind.App", out var first))
                {
                    if (!first)
                    {
                        Log.Write("already running");
                        MessageBox.Show("OpenRewind is already running. Click its icon by the clock (under ^ if it is hidden), or press "
                                        + $"{(Settings.Load(config.Home).TimelineShortcut ?? HotkeyShortcut.Standard).Label} to open the timeline.",
                                        "OpenRewind", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 0;
                    }
                    Application.Run(new TrayApp(engine));
                }
                return 0;

            case "record":
                _ = Database.BuildSlowIndexesInBackground(config.DbPath);
                engine.StartServer();
                engine.SetScreen(true);
                if (config.RecordMic || config.RecordSystemAudio) engine.SetAudio(true);
                Console.CancelKeyPress += (_, _) => engine.Shutdown();
                Application.Run();
                return 0;

            case "serve":
                engine.StartServer();
                Thread.Sleep(Timeout.Infinite);
                return 0;

            case "once":
            {
                var win = Capture.ActiveWindow();
                using var img = Capture.Screenshot();
                var id = engine.Recorder.Ingest(img, win);
                Console.WriteLine(id is long n ? $"stored frame {n}" : "skipped (duplicate)");
                return 0;
            }

            case "ingest":
            {
                if (Operand() is not string path || !File.Exists(path)) { Log.Write("usage: OpenRewind ingest <image>"); return 2; }
                using var img = new Bitmap(path);
                var win = new ActiveWindow(Value("--app") ?? "Imported", Value("--window") ?? path, IntPtr.Zero, null);
                var id = engine.Recorder.Ingest(img, win);
                Console.WriteLine(id is long n ? $"stored frame {n}" : "skipped (duplicate)");
                return 0;
            }

            case "ingest-audio":
            {
                if (Operand() is not string path) { Log.Write("usage: OpenRewind ingest-audio <file.wav>"); return 2; }
                if (Transcriber.Create(config, db) is not Transcriber tx) return 1;
                try
                {
                    var samples = Wav.Load16k(path);
                    var ts = Time.Now();
                    var dir = Path.Combine(config.DataDir, "audio", ts[..10]);
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, ts.Replace(':', '-') + "-import.wav");
                    Wav.Write(samples, Resampler.Rate, file);
                    var id = db.InsertChunk(file, ts, Value("--device") ?? "imported", true);
                    tx.Transcribe(db.ChunkById(id)!);
                    Console.WriteLine($"stored audio chunk {id} → {db.ChunkById(id)?.Status}");
                    return 0;
                }
                catch (Exception e) { Log.Write($"ingest-audio failed: {e.Message}"); return 1; }
            }

            case "delete":
            {
                if (Operand() is not string what) { Log.Write("usage: OpenRewind delete <15m|1h|today|all|older:day|older:week|older:month|older:year>"); return 2; }
                var now = DateTime.UtcNow;
                DateTime? from = null, to = now;
                if (what == "all") from = null;
                else if (what == "today") from = DateTime.Today.ToUniversalTime();
                else if (what.StartsWith("older:"))
                {
                    if (KeepForInfo.FromRaw(what[6..]) is not KeepFor k || k.Seconds() is not double secs) { Log.Write($"bad value {what}"); return 2; }
                    to = now.AddSeconds(-secs);
                }
                else if (Time.Parse(what + " ago") is string t && Time.ParseIso(t) is DateTime d) from = d;
                else { Log.Write($"bad value {what}"); return 2; }
                int f = args.Contains("--audio-only") ? 0 : db.DeleteFrames(from, to);
                int a = args.Contains("--screen-only") ? 0 : db.DeleteAudio(from, to);
                Console.WriteLine($"deleted {f} screenshots, {a} audio clips");
                return 0;
            }

            case "doctor":
            {
                Console.WriteLine($"data dir:        {config.Home}");
                Console.WriteLine($"frames stored:   {db.Stats().Count}");
                Console.WriteLine($"windows ocr:     {(Ocr.Available ? "ok" : "MISSING (add a language with OCR in Settings → Time & language)")}");
                Console.WriteLine($"accessibility:   {(config.UseAccessibility ? "on (UI Automation)" : "off")}");
                Console.WriteLine($"microphone:      {(AudioStream.HasDevice(NAudio.CoreAudioApi.DataFlow.Capture) ? "found" : "none")}");
                Console.WriteLine($"speakers:        {(AudioStream.HasDevice(NAudio.CoreAudioApi.DataFlow.Render) ? "found (system audio via loopback)" : "none")}");
                var a = db.AudioStats();
                Console.WriteLine($"audio chunks:    {a.Chunks} ({a.Pending} pending), {a.Segments} transcript segments");
                Console.WriteLine($"whisper-cli:     {Transcriber.FindBinary(config.WhisperBinary, config.Home) ?? $"MISSING (put whisper-cli.exe in {Path.Combine(config.Home, "whisper")})"}");
                Console.WriteLine($"whisper model:   {Transcriber.FindModel(config.WhisperModel, config.Home) ?? $"MISSING (put ggml-*.bin in {Path.Combine(config.Home, "models")})"}");
                var picked = SpeechModels.ById(config.TranscriptionModel);
                var ready = picked.Files.Count == 0 || SpeechModels.IsDownloaded(picked, config.Home);
                Console.WriteLine($"transcription:   {picked.Name}{(ready ? "" : " (not downloaded: pick it in Settings → Transcription Model)")}");
                return 0;
            }

            default:
                Console.WriteLine(Usage);
                return 2;
        }
    }

    /// The logo from the .ico built into the exe, at the tray's size (16 px, or more on scaled screens).
    /// The exe's own icon is the fallback: it only holds one 32 px picture, which looks soft in the tray.
    static void LoadIcons()
    {
        try
        {
            using var s = typeof(Program).Assembly.GetManifestResourceStream("OpenRewind.ico");
            if (s != null) AppIcon = new Icon(s, SystemInformation.SmallIconSize);
            else if (Environment.ProcessPath is string exe && Icon.ExtractAssociatedIcon(exe) is Icon ico)
                AppIcon = new Icon(ico, SystemInformation.SmallIconSize);
            PausedIcon = Gray(AppIcon);
        }
        catch (Exception e) { Log.Write($"cannot load the app icon: {e.Message}"); }
    }

    /// Explorer keeps an exe's icon in its cache by path, so a new OpenRewind.exe copied over an old one
    /// (or swapped in by the updater) still shows the old logo on the file, the taskbar and Start pins.
    /// Once per new version, tell the shell to read the icon again.
    public static void RefreshIconIfNew(Settings settings)
    {
        if (settings.LastRunVersion == Updater.Current) return;
        try
        {
            if (Environment.ProcessPath is string exe)
                Native.SHChangeNotify(Native.SHCNE_UPDATEITEM, Native.SHCNF_PATHW | Native.SHCNF_FLUSHNOWAIT, exe, IntPtr.Zero);
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST | Native.SHCNF_FLUSHNOWAIT, null, IntPtr.Zero);
            Log.Write($"asked Windows to refresh the app icon ({settings.LastRunVersion ?? "first run"} -> {Updater.Current})");
        }
        catch (Exception e) { Log.Write($"cannot refresh the app icon: {e.Message}"); }
        settings.LastRunVersion = Updater.Current;
        settings.Save();
    }

    /// The tray icon goes gray while nothing is recording.
    static Icon Gray(Icon src)
    {
        using var bmp = src.ToBitmap();
        using var gray = new Bitmap(bmp.Width, bmp.Height);
        using (var g = Graphics.FromImage(gray))
        {
            var m = new ColorMatrix(new[]
            {
                new[] { .3f, .3f, .3f, 0, 0 }, new[] { .59f, .59f, .59f, 0, 0 }, new[] { .11f, .11f, .11f, 0, 0 },
                new[] { 0f, 0, 0, .7f, 0 }, new[] { 0f, 0, 0, 0, 1 },
            });
            using var ia = new ImageAttributes();
            ia.SetColorMatrix(m);
            g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
        }
        return Icon.FromHandle(gray.GetHicon());
    }
}
