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
        var host = Host(u.Host);
        if (host.Length == 0 || (!host.Contains('.') && host != "localhost")) return null;
        var path = u.AbsolutePath.TrimEnd('/');
        var query = u.Query.Length > 1 ? u.Query : "";
        if (query.Length > 0 && path.Length == 0) path = "/";
        return host + path + query;
    }

    /// True when the address is on the rule's site, or is the rule's page or below it.
    public static bool Matches(string rule, string? url)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(rule)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        var cut = rule.IndexOfAny(new[] { '/', '?' });
        var ruleHost = cut < 0 ? rule : rule[..cut];
        var host = Host(u.Host);
        if (host != ruleHost && !host.EndsWith("." + ruleHost, StringComparison.Ordinal)) return false;
        if (cut < 0) return true;
        var rest = rule[cut..];
        var have = u.AbsolutePath + (u.Query.Length > 1 ? u.Query : "");
        if (!have.StartsWith(rest, StringComparison.OrdinalIgnoreCase)) return false;
        // "/repo" covers "/repo/issues" and "/repo?tab=x", not "/repository".
        if (have.Length == rest.Length) return true;
        return have[rest.Length] is '/' or '?' or '&' or '#';
    }

    /// The part of a rule people recognize: "chase.com" for both "chase.com" and "chase.com/login".
    public static string SiteOf(string rule)
    {
        var cut = rule.IndexOfAny(new[] { '/', '?' });
        return cut < 0 ? rule : rule[..cut];
    }

    /// Lower case, without "www.".
    public static string Host(string host)
    {
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
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
            var site = SiteRules.Host(u.Host);
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

/// Which browser windows show a blocked site. Only the window in front can be asked for its address,
/// so each window keeps the last address seen while it was in front; that keeps a blocked tab hidden
/// after you click away to another window. Checked against the current rules, so removing a site
/// shows its windows again at once.
public sealed class SiteWindows
{
    readonly Dictionary<long, string> lastUrl = new();
    readonly object gate = new();

    /// The window in front shows `url`. Null means the address could not be read: keep what we knew.
    public void Saw(long window, string? url)
    {
        if (window == 0 || url == null) return;
        lock (gate)
        {
            if (lastUrl.Count > 500) lastUrl.Clear();  // closed windows; the ones in use are seen again soon
            lastUrl[window] = url;
        }
    }

    public bool Blocks(long window, Blocklist list)
    {
        if (list.Sites.Count == 0) return false;
        lock (gate) return lastUrl.TryGetValue(window, out var url) && list.BlocksUrl(url);
    }
}
