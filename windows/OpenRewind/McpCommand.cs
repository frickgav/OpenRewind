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
        McpMeaning? meaning = null;

        McpState State()
        {
            var settings = Settings.Load(config.Home);
            if (!settings.McpEnabled) return new McpState(false, null);
            if (db == null)
            {
                if (!File.Exists(config.DbPath))
                    return new McpState(true, null, $"OpenRewind has not recorded anything yet (no history in {config.Home}). Open OpenRewind and let it record first.");
                try { db = Database.OpenReadOnly(config.DbPath); }
                catch (Exception e)
                {
                    Log.Write($"mcp: cannot open {config.DbPath}: {e.Message}");
                    return new McpState(true, null, $"OpenRewind cannot read its history ({e.Message}).");
                }
            }
            var tools = new AiTools(db) { Blocked = settings.ToBlocklist() };
            if (settings.AiScreenshots) tools.Screenshots = f => ImageTools.ForAi(f.Source);  // stills and frames kept in a video
            // The model runs in this process too (on the CPU, on this PC); it is loaded on first use.
            meaning ??= new McpMeaning(db, MiniLm.Dir(config.Home));
            tools.Meaning = meaning.Get(settings.SearchByMeaning);
            return new McpState(true, tools, MeaningMissing: meaning.Missing);
        }

        // A WinExe still gets the pipes the AI app passed in. UTF-8 without a byte order mark, "\n" between messages.
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
        Log.Write($"mcp: OpenRewind {Updater.Current} serving {config.Home}");
        new McpServer(Updater.Current, State).Run(input, output);
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
