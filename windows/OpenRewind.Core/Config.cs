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
    /// Length of each audio chunk sent to Whisper.
    public double AudioChunkSeconds { get; set; } = 30;
    public string? WhisperModel { get; set; }
    public string? WhisperBinary { get; set; }
    public string WhisperLanguage { get; set; } = "auto";

    public Config(string home) { Home = home; }

    public string DbPath => Path.Combine(Home, "db.sqlite");
    public string DataDir => Path.Combine(Home, "data");

    /// OPENREWIND_<name>, or the old OPENSCREEN_<name> so existing setups keep working.
    public static string? Env(string name) =>
        Environment.GetEnvironmentVariable("OPENREWIND_" + name) ?? Environment.GetEnvironmentVariable("OPENSCREEN_" + name);

    /// Same folder name as the Mac app, so the layout and the docs match on both.
    public static string DefaultHome() =>
        Env("HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openrewind");

    /// Written into the new data folder after a move; holds the old folder path.
    public const string MovedFromFile = ".moved-from";

    /// The app was called openscreen and kept its data in ~/.openscreen: move that folder to
    /// ~/.openrewind once. The database still holds the old file paths, so leave a note for
    /// Database to rewrite them.
    static string ResolveHome()
    {
        if (Env("HOME") is string set) return set;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fresh = Path.Combine(profile, ".openrewind"), old = Path.Combine(profile, ".openscreen");
        if (Directory.Exists(fresh) || !Directory.Exists(old)) return fresh;
        try
        {
            Directory.Move(old, fresh);
            File.WriteAllText(Path.Combine(fresh, MovedFromFile), old + Path.DirectorySeparatorChar);
            Log.Write($"moved your recordings from {old} to {fresh}");
            return fresh;
        }
        catch (Exception e)
        {
            Log.Write($"could not move {old} to {fresh} ({e.Message}); using the old folder");
            return old;
        }
    }

    public static Config Load(string[] args)
    {
        // --home given: use it as is, and do not move anything.
        var c = new Config(args.Contains("--home") ? DefaultHome() : ResolveHome()) { WhisperModel = Env("WHISPER_MODEL") };
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

    /// The tray app has no console, so it also writes its log to ~/.openrewind/openrewind.log.
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
