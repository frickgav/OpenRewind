using OpenRewind;

public class BlocklistTests
{
    static Blocklist List(bool privateWindows = true, params string[] keywords) =>
        new(new[] { new BlockedApp("1password", "1Password"), new BlockedApp("", "Signal") }, keywords, privateWindows);

    [Fact]
    public void BlocksByExeOrName()
    {
        var b = List();
        Assert.True(b.BlocksApp("1password", "Something"));
        Assert.True(b.BlocksApp(null, "1PASSWORD"));
        Assert.True(b.BlocksApp("signal", "Signal"));
        Assert.False(b.BlocksApp("chrome", "Google Chrome"));
        Assert.False(b.BlocksApp("", ""));
    }

    [Fact]
    public void ExeIdFromPath()
    {
        Assert.Equal("1password", Blocklist.IdFor(Path.Combine("apps", "1Password", "1Password.exe")));
        Assert.Equal("", Blocklist.IdFor(null));
    }

    [Fact]
    public void PrivateWindowsByTitle()
    {
        var on = List();
        Assert.True(on.BlocksWindow("msedge", "Microsoft Edge", "New tab - [InPrivate] - Microsoft Edge"));
        Assert.True(on.BlocksWindow("firefox", "Firefox", "Mozilla Firefox Private Browsing"));
        Assert.True(on.BlocksWindow("chrome", "Google Chrome", "New Tab - Google Chrome (Incognito)"));
        Assert.False(on.BlocksWindow("chrome", "Google Chrome", "News - Google Chrome"));
        Assert.False(List(privateWindows: false).BlocksWindow("msedge", "Microsoft Edge", "[InPrivate]"));
    }

    [Fact]
    public void KeywordsMatchWholeWordsOnly()
    {
        var b = List(true, "Chase", "Bank of America", "C++");
        Assert.True(b.BlocksWindow("chrome", "Chrome", "Chase Online - Google Chrome"));
        Assert.True(b.BlocksWindow("chrome", "Chrome", "bank of america | Sign in"));
        Assert.True(b.BlocksWindow("code", "Code", "Learning C++ today"));
        Assert.False(b.BlocksWindow("chrome", "Chrome", "Purchased items"));
        Assert.False(b.BlocksWindow("chrome", "Chrome", "Chased by bees"));
        Assert.False(b.BlocksWindow("chrome", "Chrome", null));
    }

    [Fact]
    public void ExtraNamesAndBlankKeywords()
    {
        var b = new Blocklist(Array.Empty<BlockedApp>(), new[] { " ", "" }, false, new[] { " keepass " });
        Assert.True(b.BlocksApp("keepass", null));
        Assert.Empty(b.Keywords);
        Assert.False(b.BlocksWindow("x", "x", "anything"));
    }

    [Fact]
    public void DefaultsCoverPasswordManagers()
    {
        var d = Blocklist.Defaults();
        Assert.True(d.BlocksApp("bitwarden", null));
        Assert.True(d.PrivateWindows);
        Assert.False(d.BlocksApp("qw", "Quicken"));  // finance apps are chosen in setup
    }
}
