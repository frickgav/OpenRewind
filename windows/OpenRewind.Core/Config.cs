namespace OpenRewind;

public sealed class Config
{
    public string Home { get; set; }
    public int Port { get; set; } = 3031;
    public double Interval { get; set; } = 2.0;
    /// A frame is new when at least this many of the 192x120 thumbnail pixels changed.
    public int MinChangedPixels { get; set; } = 4;
    /// Snapshot scale (1.0 = full resolution).
    public double SnapshotScale { get; set; } = 0.5;
    public bool UseAccessibility { get; set; } = true;
    public bool RecordMic { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    /// Length of each audio chunk sent to Whisper (screenpipe uses 30 s).
    public double AudioChunkSeconds { get; set; } = 30;
    public string? WhisperModel { get; set; }
    public string? WhisperBinary { get; set; }
    public string WhisperLanguage { get; set; } = "auto";

    public Config(string home) { Home = home; }

    public string DbPath => Path.Combine(Home, "db.sqlite");
    public string DataDir => Path.Combine(Home, "data");

    /// Same folder name as the Mac app, so the layout and the docs match on both.
    public static string DefaultHome() =>
        Environment.GetEnvironmentVariable("OPENSCREEN_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openscreen");

    public static Config Load(string[] args)
    {
        var c = new Config(DefaultHome()) { WhisperModel = Environment.GetEnvironmentVariable("OPENSCREEN_WHISPER_MODEL") };
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--port": if (int.TryParse(Next(), out var p) && p is > 0 and < 65536) c.Port = p; break;
                case "--interval": if (Num(Next()) is double s) c.Interval = s; break;
                case "--scale": if (Num(Next()) is double sc) c.SnapshotScale = sc; break;
                case "--no-ax": c.UseAccessibility = false; break;
                case "--no-audio": c.RecordMic = false; c.RecordSystemAudio = false; break;
                case "--no-mic": c.RecordMic = false; break;
                case "--no-system-audio": c.RecordSystemAudio = false; break;
                case "--chunk": if (Num(Next()) is double ch && ch >= 5) c.AudioChunkSeconds = ch; break;
                case "--whisper-model": c.WhisperModel = Next(); break;
                case "--whisper-bin": c.WhisperBinary = Next(); break;
                case "--language": if (Next() is string l) c.WhisperLanguage = l; break;
                case "--home": if (Next() is string h) c.Home = h; break;
            }
        }
        Directory.CreateDirectory(c.DataDir);
        return c;
    }

    static double? Num(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}

public static class Log
{
    static StreamWriter? file;
    static readonly object gate = new();

    /// The tray app has no console, so it also writes its log to ~/.openscreen/openrewind.log.
    public static void OpenFile(string home)
    {
        Directory.CreateDirectory(home);
        file = new StreamWriter(new FileStream(Path.Combine(home, "openrewind.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public static void Write(string msg)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}] {msg}";
        lock (gate)
        {
            try { Console.Error.WriteLine(line); } catch { }
            file?.WriteLine(line);
        }
    }
}
