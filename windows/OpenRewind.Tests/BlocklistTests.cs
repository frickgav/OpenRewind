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

public class SiteRuleTests
{
    [Theory]
    [InlineData("chase.com", "chase.com")]
    [InlineData("https://www.Chase.com/", "chase.com")]
    [InlineData("  secure.chase.com/login/  ", "secure.chase.com/login")]
    [InlineData("https://docs.google.com/document/d/AbC/edit#heading=h.1", "docs.google.com/document/d/AbC/edit")]
    [InlineData("youtube.com/watch?v=xyz", "youtube.com/watch?v=xyz")]
    [InlineData("localhost:3000/admin", "localhost/admin")]
    public void NormalizesWhatPeopleType(string raw, string rule) => Assert.Equal(rule, SiteRules.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("my bank")]
    [InlineData("notasite")]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("chrome://settings")]
    public void RejectsWhatIsNotASite(string raw) => Assert.Null(SiteRules.Normalize(raw));

    [Fact]
    public void SiteCoversSubdomainsOnly()
    {
        Assert.True(SiteRules.Matches("chase.com", "https://chase.com/"));
        Assert.True(SiteRules.Matches("chase.com", "https://secure07.chase.com/web/auth"));
        Assert.True(SiteRules.Matches("chase.com", "http://www.chase.com"));
        Assert.False(SiteRules.Matches("chase.com", "https://notchase.com/"));
        Assert.False(SiteRules.Matches("chase.com", "https://chase.com.evil.net/"));
        Assert.False(SiteRules.Matches("secure.chase.com", "https://chase.com/"));
        Assert.False(SiteRules.Matches("chase.com", null));
        Assert.False(SiteRules.Matches("chase.com", "file:///chase.com"));
    }

    [Fact]
    public void LinkCoversThatPageAndBelow()
    {
        var rule = SiteRules.Normalize("github.com/acme/secret")!;
        Assert.True(SiteRules.Matches(rule, "https://github.com/acme/secret"));
        Assert.True(SiteRules.Matches(rule, "https://github.com/acme/secret/issues/4"));
        Assert.True(SiteRules.Matches(rule, "https://github.com/ACME/Secret?tab=readme"));
        Assert.False(SiteRules.Matches(rule, "https://github.com/acme/secrets"));
        Assert.False(SiteRules.Matches(rule, "https://github.com/acme"));

        var video = SiteRules.Normalize("https://www.youtube.com/watch?v=abc")!;
        Assert.True(SiteRules.Matches(video, "https://www.youtube.com/watch?v=abc&t=42"));
        Assert.False(SiteRules.Matches(video, "https://www.youtube.com/watch?v=abcd"));
        Assert.False(SiteRules.Matches(video, "https://www.youtube.com/watch?v=other"));
        Assert.True(SiteRules.Matches(video, "https://www.youtube.com/watch?t=42&v=abc"));  // any order
        Assert.False(SiteRules.Matches(video, "https://www.youtube.com/watch/more?v=abc"));
        Assert.False(SiteRules.Matches(video, "https://www.youtube.com/watch"));
    }

    [Fact]
    public void EscapedLettersMatchPlainOnes()
    {
        var rule = SiteRules.Normalize("github.com/acme/secret")!;
        Assert.True(SiteRules.Matches(rule, "https://github.com/acme/%73ecret"));
        Assert.True(SiteRules.Matches(rule, "https://github.com/acme/%53ECRET/issues"));
        Assert.Equal("github.com/acme/secret", SiteRules.Normalize("https://github.com/acme/%73ecret/"));
        Assert.Equal("example.com/a%2Fb", SiteRules.Normalize("example.com/a%2Fb"));  // an escaped "/" stays escaped
        Assert.True(SiteRules.Matches(SiteRules.Normalize("youtube.com/watch?v=abc")!, "https://youtube.com/watch?v=%61bc"));
    }

    [Fact]
    public void NonEnglishDomainsMatchHowBrowsersReportThem()
    {
        Assert.Equal("xn--bcher-kva.de", SiteRules.Normalize("bücher.de"));
        Assert.Equal("xn--bcher-kva.de/konto", SiteRules.Normalize("https://www.BÜCHER.de/konto"));
        Assert.True(SiteRules.Matches(SiteRules.Normalize("bücher.de")!, "https://xn--bcher-kva.de/"));
        Assert.True(SiteRules.Matches(SiteRules.Normalize("bücher.de")!, "https://shop.bücher.de/"));
        Assert.True(SiteRules.Matches("xn--bcher-kva.de", "https://bücher.de/"));
        Assert.Equal("xn--bcher-kva.de", SiteRules.Host("bücher.de"));
    }

    [Fact]
    public void BlocklistUsesSites()
    {
        var b = new Blocklist(Array.Empty<BlockedApp>(), new[] { "Chase" }, true, sites: new[] { "https://www.PayPal.com/", "not a site", "paypal.com" });
        Assert.Equal(new[] { "paypal.com" }, b.Sites);
        Assert.True(b.BlocksUrl("https://www.paypal.com/myaccount/summary"));
        Assert.False(b.BlocksUrl("https://example.com"));
        Assert.False(new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false).BlocksUrl("https://paypal.com"));
        // Stored frames: by address, title word, or private window.
        Assert.True(b.BlocksFrame("Google Chrome", "Summary", "https://paypal.com/x"));
        Assert.True(b.BlocksFrame("Google Chrome", "Chase Online", ""));
        Assert.True(b.BlocksFrame("Google Chrome", "New Tab - Google Chrome (Incognito)", ""));
        Assert.False(b.BlocksFrame("Google Chrome", "News", "https://example.com"));
    }

    [Fact]
    public void BankDomainsAreValidRules() =>
        Assert.All(BlockCatalog.BankingDomains, d => Assert.Equal(d, SiteRules.Normalize(d)));

    [Fact]
    public void GroupsRecentPagesBySite()
    {
        var pages = new[]
        {
            new RecentPage("https://mail.google.com/mail/u/0", "Inbox", "2026-10-19T10:00:00Z"),
            new RecentPage("https://www.chase.com/accounts", "Accounts", "2026-10-19T09:00:00Z"),
            new RecentPage("https://mail.google.com/mail/u/1", "Work inbox", "2026-10-19T08:00:00Z"),
            new RecentPage("http://chase.com/", "Chase", "2026-10-18T08:00:00Z"),
            new RecentPage("about:blank", "", "2026-10-18T07:00:00Z"),
        };
        var sites = RecentSites.Group(pages);
        Assert.Equal(new[] { "mail.google.com", "chase.com" }, sites.Select(s => s.Site));
        Assert.Equal("2026-10-19T10:00:00Z", sites[0].LastSeen);
        Assert.Equal(2, sites[1].Pages.Count);
    }
}

public class SiteWindowsTests
{
    static readonly Blocklist Chase = new(Array.Empty<BlockedApp>(), Array.Empty<string>(), false, sites: new[] { "chase.com" });

    [Fact]
    public void RemembersEachWindowsLastAddress()
    {
        var w = new SiteWindows();
        w.Saw(1, "Accounts", "https://chase.com/accounts");
        w.Saw(2, "Example", "https://example.com");
        Assert.True(w.Blocks(1, Chase));
        Assert.False(w.Blocks(2, Chase));
        Assert.False(w.Blocks(3, Chase));
        w.Saw(1, "Accounts", null);  // could not read it, same title: still the same page
        Assert.True(w.Blocks(1, Chase));
        w.Saw(1, "Example", "https://example.com");
        Assert.False(w.Blocks(1, Chase));
        w.Saw(2, "Chase", "https://secure.chase.com/");
        Assert.False(w.Blocks(2, new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false)));
    }

    [Fact]
    public void ReadsAgainWhenTheTitleChanges()
    {
        var w = new SiteWindows();
        Assert.True(w.NeedsRead(1, "Example"));
        Assert.False(w.Knows(1));
        w.Saw(1, "Example", "https://example.com");
        Assert.True(w.Knows(1));
        Assert.False(w.NeedsRead(1, "Example"));
        Assert.True(w.NeedsRead(1, "Chase - Accounts"));  // switched tabs behind our back
    }

    [Fact]
    public void AnUnreadableNewPageIsHidden()
    {
        var w = new SiteWindows();
        w.Saw(1, "Example", "https://example.com");
        w.Saw(1, "Chase - Accounts", null);  // new title, address unknown: hide it
        Assert.True(w.Blocks(1, Chase));
        Assert.False(w.NeedsRead(1, "Chase - Accounts"));  // tried; read again on the next title change
        w.Saw(1, "Chase - Accounts", null);
        Assert.True(w.Blocks(1, Chase));
        w.Saw(1, "News", "https://example.com/news");
        Assert.False(w.Blocks(1, Chase));
        w.Saw(2, "New Tab", "chrome://newtab/");  // read fine, just not a web page
        Assert.False(w.Blocks(2, Chase));
        Assert.False(w.Blocks(1, new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false)));
    }

    [Fact]
    public void ForgetsWindowsThatAreGone()
    {
        var w = new SiteWindows();
        w.Saw(1, "Chase", "https://chase.com/");
        w.Saw(2, "Chase", "https://chase.com/");
        w.Prune(new long[] { 2 });
        Assert.False(w.Knows(1));
        Assert.True(w.Blocks(2, Chase));
    }
}

public class RecentSitesAgoTests
{
    [Fact]
    public void SaysHowLongAgo()
    {
        var now = DateTime.UtcNow;
        Assert.Equal("Today", RecentSites.Ago(Time.Iso(now.AddMinutes(-1)), now));
        Assert.Equal("Yesterday", RecentSites.Ago(Time.Iso(now.AddDays(-1)), now));
        Assert.Equal("3 days ago", RecentSites.Ago(Time.Iso(now.AddDays(-3)), now));
        Assert.Equal(now.AddDays(-30).ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture), RecentSites.Ago(Time.Iso(now.AddDays(-30)), now));
        Assert.Equal("", RecentSites.Ago("nonsense", now));
    }
}
