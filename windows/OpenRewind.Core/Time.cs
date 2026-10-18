using System.Globalization;

namespace OpenRewind;

/// Timestamps are stored as UTC ISO 8601 with milliseconds ("2026-10-18T19:45:05.123Z"),
/// the same text the Mac app writes, so string order is time order in SQLite.
public static class Time
{
    const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string Iso(DateTime d) => d.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture);

    public static string Now() => Iso(DateTime.UtcNow);

    public static DateTime? ParseIso(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    /// Accepts ISO 8601, "30m ago" / "2h ago" / "3d ago", "today", "yesterday", or "YYYY-MM-DD" (local day).
    public static string? Parse(string raw, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var s = raw.Trim().ToLowerInvariant();
        if (s == "now") return Iso(now);
        if (s == "today") return Iso(now.ToLocalTime().Date);
        if (s == "yesterday") return Iso(now.ToLocalTime().Date.AddDays(-1));
        if (s.EndsWith("ago"))
        {
            var body = s[..^3].Trim();
            if (body.Length < 2) return null;
            var secs = body[^1] switch { 's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, 'w' => 604800, _ => 0 };
            if (secs > 0 && double.TryParse(body[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return Iso(now.AddSeconds(-n * secs));
            return null;
        }
        if (DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
            return Iso(day);
        if (raw.Contains('T') && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            return Iso(d);
        return null;
    }
}
