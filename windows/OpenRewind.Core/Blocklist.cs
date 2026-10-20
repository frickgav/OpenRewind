using System.Text.RegularExpressions;

namespace OpenRewind;

/// An app the user does not want recorded. `Id` is the exe name without ".exe", lower case
/// ("1password"); `Name` is what the user sees ("1Password"). Either one matching blocks the app.
public sealed record BlockedApp(string Id, string Name);

public enum BlockCategory { PasswordManager, Finance }

/// An app we suggest blocking during setup.
public sealed record CatalogApp(string Id, string Name, BlockCategory Category);

/// Apps and windows to hide. Blocked windows are blurred in the screenshot (wherever they are on
/// screen, not only when in front) and their text is never stored. The Mac app has the same rules
/// in Blocklist.swift.
public sealed class Blocklist
{
    public IReadOnlyList<BlockedApp> Apps { get; }
    /// Words or site names: a window whose title has one of them is hidden ("Chase", "PayPal").
    public IReadOnlyList<string> Keywords { get; }
    /// Hide private / incognito browser windows.
    public bool PrivateWindows { get; }
    /// Websites and links (see SiteRules): a browser tab showing one is hidden.
    public IReadOnlyList<string> Sites { get; }

    readonly HashSet<string> keys;
    readonly Regex? keywordRx;

    public Blocklist(IEnumerable<BlockedApp> apps, IEnumerable<string> keywords, bool privateWindows, IEnumerable<string>? extraNames = null,
                     IEnumerable<string>? sites = null)
    {
        Sites = (sites ?? Array.Empty<string>()).Select(SiteRules.Normalize).OfType<string>().Distinct().ToList();
        Apps = apps.ToList();
        Keywords = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        PrivateWindows = privateWindows;
        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Apps) { if (a.Id.Length > 0) keys.Add(a.Id); if (a.Name.Length > 0) keys.Add(a.Name); }
        foreach (var n in extraNames ?? Array.Empty<string>()) if (n.Trim().Length > 0) keys.Add(n.Trim());
        keywordRx = KeywordRegex(Keywords);
    }

    /// What recording uses right now. The tray app replaces it when settings change.
    public static Blocklist Current { get => current; set => current = value; }
    static volatile Blocklist current = Defaults();

    /// Before setup has run: password managers, private windows, and OPENREWIND_EXCLUDE.
    public static Blocklist Defaults() =>
        new(BlockCatalog.Windows.Where(a => a.Category == BlockCategory.PasswordManager).Select(a => new BlockedApp(a.Id, a.Name)),
            Array.Empty<string>(), true, EnvNames());

    /// Extra app names from OPENREWIND_EXCLUDE (comma list), always blocked on top of the settings.
    public static IEnumerable<string> EnvNames() =>
        Config.Env("EXCLUDE")?.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0) ?? Enumerable.Empty<string>();

    /// "C:\...\1Password.exe" → "1password".
    public static string IdFor(string? exePath) =>
        string.IsNullOrEmpty(exePath) ? "" : Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();

    /// Everything on the list, one line each and sorted ("app:1password", "word:chase", "site:…",
    /// "private"). A saved journal recap keeps these, so it can tell when the list has grown since.
    public IReadOnlyList<string> Entries =>
        keys.Select(k => "app:" + k.ToLowerInvariant())
            .Concat(Keywords.Select(k => "word:" + k.ToLowerInvariant()))
            .Concat(Sites.Select(x => "site:" + x))
            .Concat(PrivateWindows ? new[] { "private" } : Array.Empty<string>())
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

    /// The blocked apps' ids and names in lower case (with OPENREWIND_EXCLUDE): the local server leaves
    /// these out in SQL before HidesFrame checks each frame.
    public IReadOnlyList<string> AppKeys => keys.Select(k => k.ToLowerInvariant()).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();

    public bool BlocksApp(string? id, string? name) =>
        (!string.IsNullOrEmpty(id) && keys.Contains(id)) || (!string.IsNullOrEmpty(name) && keys.Contains(name));

    public bool BlocksWindow(string? id, string? name, string? title) =>
        BlocksApp(id, name) || (PrivateWindows && IsPrivateTitle(title)) || HasKeyword(title);

    /// True when the address is one of the blocked sites or links.
    public bool BlocksUrl(string? url) => Sites.Count > 0 && Sites.Any(r => SiteRules.Matches(r, url));

    /// For something already recorded, where only the app name, window title and address are known
    /// (search results, the timeline, anything that reads stored frames).
    public bool BlocksFrame(string? appName, string? windowName, string? url) =>
        BlocksWindow(null, appName, windowName) || BlocksUrl(url);

    /// True for a stored frame saved while a blocked window or site was in front, or one the list covers now
    /// (same rule as Ask AI and AI apps): by app, private window, blocked word in the title or address, or
    /// blocked site. The local search server leaves these out. Mac: Blocklist.hides(_ f: Frame).
    public bool HidesFrame(Frame f) =>
        f.Blocked || BlocksWindow(f.AppName, f.AppName, f.WindowName) || BlocksUrl(f.BrowserUrl) || HasKeyword(f.BrowserUrl);

    public bool HasKeyword(string? title) => keywordRx != null && !string.IsNullOrEmpty(title) && keywordRx.IsMatch(title);

    /// Browsers that mark private windows in the window title.
    public static readonly string[] PrivateMarkers =
        { "incognito", "inprivate", "private browsing", "private window", "(private)" };

    public static bool IsPrivateTitle(string? title) =>
        !string.IsNullOrEmpty(title) && PrivateMarkers.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// Whole words only, any case: "Chase" hides "Chase Online" but not "Purchased".
    static Regex? KeywordRegex(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return null;
        var alts = string.Join("|", words.Select(Regex.Escape));
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?:{alts})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// Suggestions for the setup screen and Settings.
public static class BlockCatalog
{
    public static readonly CatalogApp[] Windows =
    {
        new("1password", "1Password", BlockCategory.PasswordManager),
        new("bitwarden", "Bitwarden", BlockCategory.PasswordManager),
        new("keepass", "KeePass", BlockCategory.PasswordManager),
        new("keepassxc", "KeePassXC", BlockCategory.PasswordManager),
        new("dashlane", "Dashlane", BlockCategory.PasswordManager),
        new("lastpass", "LastPass", BlockCategory.PasswordManager),
        new("enpass", "Enpass", BlockCategory.PasswordManager),
        new("nordpass", "NordPass", BlockCategory.PasswordManager),
        new("proton pass", "Proton Pass", BlockCategory.PasswordManager),
        new("roboform", "RoboForm", BlockCategory.PasswordManager),
        new("qw", "Quicken", BlockCategory.Finance),
        new("ledger live", "Ledger Live", BlockCategory.Finance),
        new("exodus", "Exodus", BlockCategory.Finance),
        new("trezor suite", "Trezor Suite", BlockCategory.Finance),
    };

    /// Bank and money sites, matched against browser window titles (the page title).
    public static readonly string[] BankingSites =
    {
        "Online Banking", "Bank of America", "Chase", "Wells Fargo", "Citibank", "Capital One", "PayPal", "Venmo",
        "Coinbase", "Robinhood", "Fidelity", "Charles Schwab", "Vanguard", "American Express", "Ally Bank",
    };

    /// The same banks and money sites by address, so their tabs are hidden whatever the page title says.
    public static readonly string[] BankingDomains =
    {
        "bankofamerica.com", "chase.com", "wellsfargo.com", "citi.com", "capitalone.com", "paypal.com", "venmo.com",
        "coinbase.com", "robinhood.com", "fidelity.com", "schwab.com", "vanguard.com", "americanexpress.com", "ally.com",
    };
}
