namespace OpenRewind;

/// Search by Meaning for the MCP process (`OpenRewind.exe mcp`), which opens the history read-only
/// and never indexes. It offers search_by_meaning only when the user turned the feature on, the
/// model is on this PC and the app has built an index; it loads the model itself, once, and keeps it
/// while the setting stays on. Anything missing means the tool is simply not offered.
/// The MCP process opens the database for each message and closes it after (so the app can encrypt it
/// meanwhile): the model is kept, and the search is bound to whichever database is open now.
public sealed class McpMeaning : IDisposable
{
    readonly string modelDir;
    readonly Func<string, IEmbedder> load;
    IEmbedder? embedder;
    MeaningService? service;
    bool failed;

    public McpMeaning(string modelDir, Func<string, IEmbedder>? load = null)
    {
        this.modelDir = modelDir;
        this.load = load ?? (dir => OnnxEmbedder.Load(dir));
    }

    /// Why search_by_meaning is not offered right now, or null when it is.
    public string? Missing { get; private set; } = "Search by Meaning is off in OpenRewind's Settings.";

    /// The service to give AiTools, or null when search_by_meaning should not be offered.
    public MeaningService? Get(Database db, bool on)
    {
        if (!on)
        {
            Drop();
            Missing = "Search by Meaning is off in OpenRewind's Settings.";
            return null;
        }
        if (service != null && service.Db == db) return service;
        service = null;
        if (failed) return null;
        if (!MiniLm.Present(modelDir)) { Missing = "OpenRewind has not finished downloading the Search by Meaning model."; return null; }
        if (!db.HasMeaningIndex()) { Missing = "OpenRewind has not built the Search by Meaning index yet. Open OpenRewind and let it run."; return null; }
        try
        {
            if (embedder == null)
            {
                embedder = load(modelDir);
                Log.Write("mcp: search by meaning ready");
            }
            service = new MeaningService(db) { Embedder = embedder };
            Missing = null;
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
        if (embedder is IDisposable d) d.Dispose();
        embedder = null;
        service = null;
    }

    public void Dispose() => Drop();
}
