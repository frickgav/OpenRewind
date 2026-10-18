namespace OpenRewind;

/// Browser tab addresses, as read from the address bar and saved in frames.browser_url.
public static class WebAddress
{
    /// A full http(s) address, or null. Address bars often hide "https://", so it is added back.
    /// Typed searches ("weather today") and other schemes (file:, about:, chrome:) give null.
    public static string? Normalize(string? raw)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return null;
        if (!s.Contains("://"))
        {
            // Without a scheme, only something that looks like a host: "example.com/x", "localhost:3000".
            var host = s.Split('/', '?', '#')[0].Split(':')[0];
            if (!host.Contains('.') && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
            s = "https://" + s;
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Host.Length == 0) return null;
        return u.Scheme is "http" or "https" ? u.AbsoluteUri : null;
    }

    /// "example.com" for "https://example.com/page", or "" when it is not a web address.
    public static string Host(string url) => Normalize(url) is string u ? new Uri(u).Host : "";
}
