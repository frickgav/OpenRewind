namespace OpenRewind;

/// settings.json is saved by the tray app and read by terminal commands (`record`, `serve`, `once`) running
/// beside it in another process.
public static class SettingsFile
{
    /// Writes `text` to `path` in one step (a temporary file, then a rename over the old one): a reader sees
    /// the old file or the new one, never half of one.
    public static void WriteAtomic(string path, string text)
    {
        var tmp = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, text);
        try
        {
            // Windows refuses to rename over a file another process has open. Readers hold it for well under a
            // millisecond, so keep trying for up to 3 seconds rather than losing the save.
            var giveUp = DateTime.UtcNow.AddSeconds(3);
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, path, overwrite: true); return; }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && DateTime.UtcNow < giveUp)
                {
                    Thread.Sleep(i < 10 ? 1 : 10);
                }
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// The text of `path`, or null when there is no such file. Opened so a writer can replace it meanwhile.
    public static string? Read(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            return r.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }
}

/// The block list saved in settings.json, for terminal commands that run without the tray app, which may save
/// it at any moment. Read again on each Get. A read that fails (a half-written file from an older copy of the
/// app, or a broken one) keeps the last list that read, so a search never falls back to the defaults midway
/// through a save.
public sealed class SavedBlocklist
{
    readonly string path;
    /// Settings text to block list (null text: there is no settings file yet). Throws when the text is not settings.
    readonly Func<string?, Blocklist> parse;
    readonly object gate = new();
    Blocklist last;
    string? lastText;

    public SavedBlocklist(string path, Func<string?, Blocklist> parse, int tries = 20)
    {
        this.path = path;
        this.parse = parse;
        // The first read has nothing to fall back on: wait out a save in progress.
        for (int i = 1; ; i++)
        {
            if (TryRead() is Blocklist l) { last = l; return; }
            if (i >= tries) break;
            Thread.Sleep(50);
        }
        Log.Write("cannot read the block list in settings.json; using the standard one");
        last = Blocklist.Defaults();
    }

    public Blocklist Get()
    {
        lock (gate) return TryRead() ?? last;
    }

    Blocklist? TryRead()
    {
        lock (gate)
        {
            try
            {
                var text = SettingsFile.Read(path);
                if (lastText != null && text == lastText) return last;
                var l = parse(text);
                (last, lastText) = (l, text ?? "");
                return l;
            }
            catch (Exception) { return null; }
        }
    }
}
