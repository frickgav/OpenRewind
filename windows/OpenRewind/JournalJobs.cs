namespace OpenRewind;

/// Recaps being written, by day, for the Journal window and the automatic recap alike: one request
/// per day at a time, and closing the window does not stop one (its tokens are paid for either way).
/// Used on the UI thread only. The Mac app keeps the same in JournalModel.
static class JournalJobs
{
    static readonly Dictionary<string, CancellationTokenSource> running = new();

    /// A recap started or finished: its day, and what came back once it is done.
    public static event Action<string, JournalWriter.Result?>? Changed;

    public static bool IsWriting(string day) => running.ContainsKey(day);

    /// Another day being written, for the line "Writing the recap for …".
    public static string? WritingOtherThan(string day) => running.Keys.FirstOrDefault(k => k != day);

    public static void Stop(string day) { if (running.TryGetValue(day, out var c)) c.Cancel(); }

    /// Writes and saves a day's recap with the AI provider set up now. Null when that day is already
    /// being written, no provider is set up, or (`auto`) the day has a recap of the whole day.
    public static async Task<JournalWriter.Result?> Write(DateTime day, Settings settings, Database db, AiTools tools, bool auto)
    {
        var key = Journal.Key(day);
        var ai = settings.Ai();
        if (running.ContainsKey(key) || !ai.IsReady || (auto && db.Recap(key) is { Partial: false })) return null;
        var cts = new CancellationTokenSource();
        running[key] = cts;
        Changed?.Invoke(key, null);
        JournalWriter.Result? r = null;
        try
        {
            // The list the lookups leave out, saved with the recap.
            var blocked = Journal.Fingerprint(tools.Blocked ?? Blocklist.Current);
            var price = settings.AiPriceFor(ai.Model);
            r = await JournalWriter.Write(day, new AiClient(ai), tools, price, cts.Token);
            settings.AddAiUsage(ai.Kind, r.Usage, r.Cost);
            if (r.Error == null)
                db.SaveRecap(new JournalRecap(key, r.Text, DateTime.UtcNow, ai.Kind.Id(), ai.Model, r.Usage, r.Cost, blocked));
            else Log.Write($"journal recap for {key} failed: {r.Error}");
            return r;
        }
        finally
        {
            running.Remove(key);
            cts.Dispose();
            Changed?.Invoke(key, r);
        }
    }
}
