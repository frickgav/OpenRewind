namespace OpenRewind;

/// Blocked websites and links. A rule is a site ("chase.com", which covers its subdomains too) or a
/// link ("docs.google.com/document/d/abc", which covers that page and everything under it).
/// Rules are stored without "https://" or "www.". The Mac app has the same rules in Blocklist.swift.
public static class SiteRules
{
    /// The rule for what the user typed or picked ("https://www.Chase.com/" → "chase.com"), or null
    /// when it is not a site. The fragment (#…) and a trailing "/" are dropped; a query is kept, since
    /// it can be what tells one page from another ("youtube.com/watch?v=…").
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return null;
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        var host = Host(u.IdnHost);
        if (host.Length == 0 || (!host.Contains('.') && host != "localhost")) return null;
        var path = Unescape(u.AbsolutePath).TrimEnd('/');
        var query = u.Query.Length > 1 ? Unescape(u.Query) : "";
        if (query.Length > 0 && path.Length == 0) path = "/";
        return host + path + query;
    }

    /// True when the address is on the rule's site, or is the rule's page or below it. A link with a
    /// query ("youtube.com/watch?v=abc") covers that page with those parameters in any order, among others.
    public static bool Matches(string rule, string? url)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(rule)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        var cut = rule.IndexOfAny(new[] { '/', '?' });
        var ruleHost = Host(cut < 0 ? rule : rule[..cut]);
        var host = Host(u.IdnHost);
        if (host != ruleHost && !host.EndsWith("." + ruleHost, StringComparison.Ordinal)) return false;
        if (cut < 0) return true;
        var q = rule.IndexOf('?', cut);
        var rulePath = Unescape(q < 0 ? rule[cut..] : rule[cut..q]).ToLowerInvariant();
        var path = Unescape(u.AbsolutePath).ToLowerInvariant();
        if (q < 0)
        {
            // "/repo" covers "/repo/issues" and "/repo?tab=x", not "/repository".
            if (!path.StartsWith(rulePath, StringComparison.Ordinal)) return false;
            return path.Length == rulePath.Length || path[rulePath.Length] == '/';
        }
        if (path.TrimEnd('/') != rulePath.TrimEnd('/')) return false;
        var have = Params(u.Query);
        return Params(rule[q..]).All(have.Contains);
    }

    /// "?b=2&a=1" → {"a=1", "b=2"}, lower case.
    static HashSet<string> Params(string query) =>
        Unescape(query.TrimStart('?')).ToLowerInvariant().Split('&', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    /// Decodes escapes of letters, digits and "-._~", which mean the same either way ("%73ecret" is "secret").
    /// Other escapes ("%2F", "%20") are kept, since decoding them can change what the address means.
    public static string Unescape(string s)
    {
        if (!s.Contains('%')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                var c = (char)Convert.ToInt32(s.Substring(i + 1, 2), 16);
                if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~') { sb.Append(c); i += 2; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// The part of a rule people recognize: "chase.com" for both "chase.com" and "chase.com/login".
    public static string SiteOf(string rule)
    {
        var cut = rule.IndexOfAny(new[] { '/', '?' });
        return cut < 0 ? rule : rule[..cut];
    }

    /// Lower case, without "www.", and in the form browsers report ("bücher.de" → "xn--bcher-kva.de").
    public static string Host(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Any(c => c > 127))
        {
            try { h = new System.Globalization.IdnMapping().GetAscii(h); } catch (ArgumentException) { }
        }
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }
}

/// A site in the user's history, for the "block from recent sites" picker.
public sealed record RecentSite(string Site, string LastSeen, IReadOnlyList<RecentPage> Pages);

/// One page of a recent site: its address, the window title it had, and when it was last on screen.
public sealed record RecentPage(string Url, string Title, string LastSeen);

public static class RecentSites
{
    /// Groups pages (newest first) by site, newest site first. Pages that are not web addresses are skipped.
    public static List<RecentSite> Group(IEnumerable<RecentPage> pages, int maxPagesPerSite = 50)
    {
        var bySite = new Dictionary<string, List<RecentPage>>();
        var order = new List<string>();
        foreach (var p in pages)
        {
            if (!Uri.TryCreate(p.Url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) continue;
            var site = SiteRules.Host(u.IdnHost);
            if (site.Length == 0) continue;
            if (!bySite.TryGetValue(site, out var list)) { bySite[site] = list = new(); order.Add(site); }
            if (list.Count < maxPagesPerSite) list.Add(p);
        }
        return order.Select(s => new RecentSite(s, bySite[s][0].LastSeen, bySite[s])).ToList();
    }

    /// "Today", "Yesterday", "3 days ago" or "Oct 2" for a stored timestamp, in local time.
    public static string Ago(string iso, DateTime now)
    {
        if (!DateTime.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) return "";
        var days = (now.ToLocalTime().Date - t.ToLocalTime().Date).Days;
        return days <= 0 ? "Today" : days == 1 ? "Yesterday" : days < 7 ? $"{days} days ago"
            : t.ToLocalTime().ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// Which browser windows show a blocked site. Each window keeps the address read for it and the title it
/// had then. A new title means a tab switch or a new page, so the address is read again (NeedsRead), for
/// windows in front and behind alike. Checked against the current rules, so removing a site shows its
/// windows again at once.
public sealed class SiteWindows
{
    sealed record Seen(string? Url, string Title, bool Unknown);
    readonly Dictionary<long, Seen> seen = new();
    readonly object gate = new();

    /// True when the window's address should be read: it was never read, or its title changed since.
    public bool NeedsRead(long window, string? title)
    {
        lock (gate) return !seen.TryGetValue(window, out var s) || s.Title != (title ?? "");
    }

    public bool Knows(long window) { lock (gate) return seen.ContainsKey(window); }

    /// The window showed `url` with this title. Null means the address could not be read: with the same
    /// title as the last good read it is most likely the same page, so that address stays; otherwise the
    /// window is unknown, and hidden until it can be read (when its title changes again).
    public void Saw(long window, string? title, string? url)
    {
        if (window == 0) return;
        title ??= "";
        lock (gate)
        {
            if (url != null) seen[window] = new Seen(url, title, false);
            else if (!(seen.TryGetValue(window, out var s) && !s.Unknown && s.Title == title)) seen[window] = new Seen(null, title, true);
        }
    }

    /// Forget windows that are gone (closed, minimized, on another desktop); they are read again when they come back.
    public void Prune(IEnumerable<long> onScreen)
    {
        var keep = onScreen.ToHashSet();
        lock (gate) foreach (var w in seen.Keys.Where(w => !keep.Contains(w)).ToList()) seen.Remove(w);
    }

    public bool Blocks(long window, Blocklist list)
    {
        if (list.Sites.Count == 0) return false;
        lock (gate) return seen.TryGetValue(window, out var s) && (s.Unknown || list.BlocksUrl(s.Url));
    }
}
