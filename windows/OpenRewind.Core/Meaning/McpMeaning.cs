namespace OpenRewind;

/// Search by Meaning for the MCP process (`OpenRewind.exe mcp`), which opens the history read-only
/// and never indexes. It offers search_by_meaning only when the user turned the feature on, the
/// model is on this PC and the app has built an index; it loads the model itself, once, and keeps it
/// while the setting stays on. Anything missing means the tool is simply not offered.
public sealed class McpMeaning : IDisposable
{
    readonly Database db;
    readonly string modelDir;
    readonly Func<string, IEmbedder> load;
    MeaningService? service;
    bool failed;

    public McpMeaning(Database db, string modelDir, Func<string, IEmbedder>? load = null)
    {
        this.db = db;
        this.modelDir = modelDir;
        this.load = load ?? (dir => OnnxEmbedder.Load(dir));
    }

    /// Why search_by_meaning is not offered right now, or null when it is.
    public string? Missing { get; private set; } = "Search by Meaning is off in OpenRewind's Settings.";

    /// The service to give AiTools, or null when search_by_meaning should not be offered.
    public MeaningService? Get(bool on)
    {
        if (!on)
        {
            Drop();
            Missing = "Search by Meaning is off in OpenRewind's Settings.";
            return null;
        }
        if (service != null) return service;
        if (failed) return null;
        if (!MiniLm.Present(modelDir)) { Missing = "OpenRewind has not finished downloading the Search by Meaning model."; return null; }
        if (!db.HasMeaningIndex()) { Missing = "OpenRewind has not built the Search by Meaning index yet. Open OpenRewind and let it run."; return null; }
        try
        {
            service = new MeaningService(db) { Embedder = load(modelDir) };
            Missing = null;
            Log.Write("mcp: search by meaning ready");
        }
        catch (Exception e)
        {
            // Not retried until the setting is turned off and on again: loading reads and hashes 90 MB.
            failed = true;
            Missing = $"The Search by Meaning model could not be loaded ({e.Message}).";
            Log.Write($"mcp: search_by_meaning is not offered: {e.Message}");
        }
        return service;
    }

    void Drop()
    {
        failed = false;
        if (service?.Embedder is IDisposable d) d.Dispose();
        service = null;
    }

    public void Dispose() => Drop();
}
