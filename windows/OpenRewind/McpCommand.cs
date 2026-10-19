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
            if (settings.AiScreenshots) tools.Screenshots = f => ImageTools.ForAi(f.SnapshotPath);
            return new McpState(true, tools);
        }

        // A WinExe still gets the pipes the AI app passed in. UTF-8 without a byte order mark, "\n" between messages.
        var utf8 = new UTF8Encoding(false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { NewLine = "\n" };
        Log.Write($"mcp: OpenRewind {Updater.Current} serving {config.Home}");
        new McpServer(Updater.Current, State).Run(input, output);
        db?.Dispose();
        return 0;
    }

    /// The --home part of the setup, only when this copy uses a data folder other than the usual one.
    public static string? HomeArg(Config config) =>
        string.Equals(Path.GetFullPath(config.Home).TrimEnd('\\', '/'), Path.GetFullPath(Config.DefaultHome()).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? null : config.Home;
}
