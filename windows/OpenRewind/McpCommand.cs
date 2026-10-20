using System.Text;

namespace OpenRewind;

/// `OpenRewind.exe mcp` (or --mcp): the MCP server that AI apps start (see McpServer in Core).
/// No tray icon, no windows, no recording: it only reads the database, so it works whether or not
/// the app is running. Settings are read again for every request, so turning the switch off in
/// Settings takes effect right away.
static class McpCommand
{
    public static int Run(string[] args)
    {
        var config = Config.Load(args);
        Database? db = null;
        // Encrypt Recordings: the key comes from DPAPI, the same as in the app (this is the same
        // Windows user). Screenshots are read through RecordingFile, which decrypts them.
        RecordingFile.KeyLoader = () => DataKeyStore.Load(config.Home);
        McpMeaning? meaning = null;

        McpState State()
        {
            var settings = Settings.Load(config.Home);
            if (!settings.McpEnabled) return new McpState(false, null);
            if (db == null)
            {
                if (!File.Exists(config.DbPath))
                    return new McpState(true, null, $"OpenRewind has not recorded anything yet (no history in {config.Home}). Open OpenRewind and let it record first.");
                if (RecordingFile.Key == null && (settings.EncryptRecordings || !settings.EncryptionDone || Database.IsEncryptedFile(config.DbPath)))
                    RecordingFile.Key = DataKeyStore.Load(config.Home);
                try { db = Database.OpenReadOnly(config.DbPath, RecordingFile.Key); }
                catch (DatabaseLockedException)
                {
                    return new McpState(true, null, "OpenRewind's history is encrypted and its key is not available to this Windows account. Open OpenRewind once (it asks for the recovery key), then try again.");
                }
                catch (Exception e)
                {
                    Log.Write($"mcp: cannot open {config.DbPath}: {e.Message}");
                    return new McpState(true, null, $"OpenRewind cannot read its history ({e.Message}).");
                }
            }
            var tools = new AiTools(db) { Blocked = settings.ToBlocklist() };
            if (settings.AiScreenshots) tools.Screenshots = f => ImageTools.ForAi(f.Source);  // stills and frames kept in a video
            // The model runs in this process too (on the CPU, on this PC); it is loaded on first use.
            meaning ??= new McpMeaning(MiniLm.Dir(config.Home));
            tools.Meaning = meaning.Get(db, settings.SearchByMeaning);
            return new McpState(true, tools, MeaningMissing: meaning.Missing);
        }

        // A WinExe still gets the pipes the AI app passed in. UTF-8 without a byte order mark, "\n" between messages.
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
        Log.Write($"mcp: OpenRewind {Updater.Current} serving {config.Home}");
        // Open the database for each message and close it after, so the app can convert it (Encrypt
        // Recordings) while the AI app keeps this process running: Windows can't rename an open file.
        // The Search by Meaning model stays loaded; it is bound to each new open (McpMeaning).
        new McpServer(Updater.Current, State) { AfterMessage = () => { db?.Dispose(); db = null; } }.Run(input, output);
        meaning?.Dispose();
        db?.Dispose();
        return 0;
    }

    /// The data folder for the setup, only when this copy uses one other than %USERPROFILE%\.openrewind.
    /// Compared with that literal folder, not Config.DefaultHome(): an OPENREWIND_HOME set only for the
    /// app's own session would not reach the AI app's child process.
    public static string? HomeArg(Config config)
    {
        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openrewind");
        return string.Equals(Path.GetFullPath(config.Home).TrimEnd('\\', '/'), Path.GetFullPath(usual).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? null : config.Home;
    }

    /// True when this exe sits somewhere that goes away, like the temporary folder Explorer uses when
    /// you run it straight from inside a zip. AI apps could not start it there later.
    public static bool InTemporaryFolder(string exe)
    {
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
        return Path.GetFullPath(exe).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
    }
}
