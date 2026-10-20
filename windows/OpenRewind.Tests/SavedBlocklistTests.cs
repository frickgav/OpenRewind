using OpenRewind;
using Xunit;

/// Terminal `record` and `serve` read the block list from settings.json, which the tray app may be saving at
/// that moment: a read never falls back to the standard list halfway through a save.
public class SavedBlocklistTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "openrewind-test-" + Guid.NewGuid());
    readonly string path;

    public SavedBlocklistTests()
    {
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    /// A stand-in for Settings: one blocked app name per line; throws on anything else, as a half-written
    /// JSON file does.
    static Blocklist Parse(string? text)
    {
        if (text == null) return new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), true);
        if (!text.EndsWith("\n")) throw new FormatException("cut short");
        return new Blocklist(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(n => new BlockedApp(n.ToLowerInvariant(), n)),
                             Array.Empty<string>(), true);
    }

    [Fact]
    public void KeepsTheLastListThatReadWhenTheFileIsHalfWritten()
    {
        File.WriteAllText(path, "BBEdit\nSlack\n");
        var saved = new SavedBlocklist(path, Parse);
        Assert.True(saved.Get().BlocksApp(null, "Slack"));
        File.WriteAllText(path, "BBEd");   // a save cut short
        Assert.True(saved.Get().BlocksApp(null, "Slack"));
        Assert.True(saved.Get().BlocksApp(null, "BBEdit"));
        File.WriteAllText(path, "BBEdit\n");
        Assert.False(saved.Get().BlocksApp(null, "Slack"));
        Assert.True(saved.Get().BlocksApp(null, "BBEdit"));
    }

    [Fact]
    public void NoFileMeansTheListForNoSettings()
    {
        var saved = new SavedBlocklist(path, Parse);
        Assert.Empty(saved.Get().Apps);
        SettingsFile.WriteAtomic(path, "Notes\n");
        Assert.True(saved.Get().BlocksApp(null, "Notes"));
    }

    [Fact]
    public void AnUnreadableFileAtStartUsesTheStandardList()
    {
        File.WriteAllText(path, "broken");
        var saved = new SavedBlocklist(path, Parse, tries: 2);
        Assert.Equal(Blocklist.Defaults().Entries, saved.Get().Entries);
        File.WriteAllText(path, "Notes\n");
        Assert.True(saved.Get().BlocksApp(null, "Notes"));
    }

    [Fact]
    public void AnAtomicWriteReplacesTheFileAndLeavesNoTemporaryFile()
    {
        File.WriteAllText(path, "old");
        SettingsFile.WriteAtomic(path, "new");
        Assert.Equal("new", SettingsFile.Read(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(dir));
        Assert.Null(SettingsFile.Read(Path.Combine(dir, "missing.json")));
    }

    [Fact]
    public void ReadersNeverSeeHalfAFileWhileItIsSaved()
    {
        string Big(char c) => new string(c, 200_000) + "\n";
        SettingsFile.WriteAtomic(path, Big('a'));
        var stop = DateTime.UtcNow.AddSeconds(1);
        var writer = Task.Run(() => { for (int i = 0; DateTime.UtcNow < stop; i++) SettingsFile.WriteAtomic(path, Big(i % 2 == 0 ? 'b' : 'a')); });
        while (!writer.IsCompleted)
        {
            string? text;
            // Windows may refuse the open for an instant while the rename happens; that read just fails
            // (SavedBlocklist then keeps its last list). What must never happen is half a file.
            try { text = SettingsFile.Read(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            Assert.NotNull(text);
            Assert.True(text == Big('a') || text == Big('b'), $"read {text!.Length} characters");
        }
        writer.Wait();
    }
}
