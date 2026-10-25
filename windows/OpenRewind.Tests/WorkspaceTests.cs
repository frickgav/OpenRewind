using OpenRewind;

public class WorkspaceTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-ws-test-" + Guid.NewGuid());
    readonly Database db;
    static readonly DateTime T0 = new(2026, 3, 10, 15, 0, 0, DateTimeKind.Utc);
    static readonly Blocklist None = new(Array.Empty<BlockedApp>(), Array.Empty<string>(), false);

    public WorkspaceTests() => db = new Database(Config.Load(new[] { "--home", home }).DbPath);

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    static List<WorkspaceItem> Items(params WorkspaceItem[] i) => i.ToList();
    static readonly WorkspaceItem Chrome = new(Workspace.App, "Chrome", @"C:\Apps\chrome.exe", "Pricing");
    static readonly WorkspaceItem Tab = new(Workspace.Tab, "Chrome", @"C:\Apps\chrome.exe", "Pricing", "https://example.com/pricing");
    static readonly WorkspaceItem Doc = new(Workspace.Document, "Word", @"C:\Apps\winword.exe", "plan.docx", "", @"C:\Docs\plan.docx");

    [Fact]
    public void JsonRoundTrips()
    {
        var items = Items(Chrome, Tab, Doc);
        Assert.Equal(items, Workspace.Parse(Workspace.ToJson(items)));
        Assert.Empty(Workspace.Parse("not json"));
        Assert.Empty(Workspace.Parse("[{\"kind\":\"weird\"}]"));
    }

    [Fact]
    public void SavesOnlyWhenWhatIsOpenChanges()
    {
        Assert.NotNull(db.SaveWorkspace(T0, Items(Chrome, Tab)));
        // A new window title alone is not a change.
        Assert.Null(db.SaveWorkspace(T0.AddMinutes(1), Items(Chrome with { Title = "Other" }, Tab)));
        Assert.NotNull(db.SaveWorkspace(T0.AddMinutes(2), Items(Chrome, Tab, Doc)));
    }

    [Fact]
    public void PicksTheSnapshotAtOrBeforeTheMoment()
    {
        db.SaveWorkspace(T0, Items(Chrome));
        db.SaveWorkspace(T0.AddMinutes(10), Items(Chrome, Tab));
        Assert.Single(db.WorkspaceAt(T0.AddMinutes(5))!.Items);
        Assert.Equal(2, db.WorkspaceAt(T0.AddMinutes(10))!.Items.Count);
        Assert.Null(db.WorkspaceAt(T0.AddMinutes(-1)));
        Assert.Null(db.WorkspaceAt(T0.AddDays(3)));  // too long after
    }

    [Fact]
    public void SnapshotsGoWithTheirRecordings()
    {
        db.SaveWorkspace(T0, Items(Chrome));
        db.SaveWorkspace(T0.AddHours(5), Items(Chrome, Tab));
        db.DeleteFrames(null, T0.AddHours(1));
        Assert.Null(db.WorkspaceAt(T0.AddMinutes(30)));
        Assert.NotNull(db.WorkspaceAt(T0.AddHours(6)));
    }

    [Fact]
    public void RestoresOnlyWhatIsAllowedAndStillThere()
    {
        var blocked = new Blocklist(new[] { new BlockedApp("winword", "Word") }, new[] { "secret" }, true, sites: new[] { "bank.example" });
        var items = Items(
            Chrome, Tab, Tab,                                                    // duplicate tab
            Tab with { Url = "https://bank.example/login" },                    // blocked site
            Tab with { Url = "chrome://settings" },                              // not a web page
            Tab with { Url = "file:///C:/x.html" },
            Tab with { Url = "https://ok.example/", Title = "Top secret plan" }, // blocked word
            Chrome with { Title = "New Incognito Tab" },                         // private window
            Doc,                                                                 // blocked app
            new WorkspaceItem(Workspace.Document, "Notepad", @"C:\Windows\notepad.exe", "", "", @"C:\gone.txt"));
        var ok = Workspace.Restorable(items, blocked, p => p != @"C:\gone.txt");
        Assert.Equal(new[] { Chrome, Tab }, ok);
        Assert.Equal(3, Workspace.Restorable(Items(Chrome, Tab, Doc), None, _ => true).Count);
    }

    [Fact]
    public void DescribesItems()
    {
        Assert.Equal("Chrome — Pricing (example.com)", Workspace.Describe(Tab));
        Assert.Equal("Word — plan.docx", Workspace.Describe(Doc));
    }
}

public class WorkspaceDocumentTests
{
    [Theory]
    [InlineData(@"C:\Users\sam\Documents\Plan.docx", true)]
    [InlineData("/Users/sam/Notes/todo.md", true)]
    [InlineData(@"C:\Users\sam\Downloads\setup.exe", false)]
    [InlineData(@"C:\Users\sam\run.BAT", false)]
    [InlineData(@"C:\Users\sam\Desktop\Thing.lnk", false)]
    [InlineData(@"C:\Users\sam\x.ps1", false)]
    [InlineData("/Users/sam/build.command", false)]
    [InlineData("/Applications/Safari.app", false)]
    [InlineData("/Users/sam/bin/tool", false)]
    [InlineData(@"C:\Users\sam\evil.hta.", false)]
    public void OnlyDocumentsThatDoNotRunAnything(string path, bool safe) => Assert.Equal(safe, Workspace.IsSafeDocument(path));
}
