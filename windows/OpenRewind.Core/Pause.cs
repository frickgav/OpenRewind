using System.Globalization;

namespace OpenRewind;

/// Pause Recording: stop screen and audio for a while, then turn them back on by themselves.
public static class Pause
{
    /// The tray menu choices. 0 minutes = until the user resumes.
    public static readonly (string Title, int Minutes)[] Choices =
        { ("For 30 Minutes", 30), ("For 1 Hour", 60), ("For 1 Day", 24 * 60), ("Until I Resume", 0) };

    /// When recording comes back on (UTC). DateTime.MaxValue means only when the user resumes.
    public static DateTime Until(int minutes, DateTime nowUtc) =>
        minutes <= 0 ? DateTime.MaxValue : nowUtc.AddMinutes(minutes);

    /// A saved pause that is still running, or null once it has passed.
    public static DateTime? Active(DateTime? untilUtc, DateTime nowUtc) =>
        untilUtc is DateTime u && u > nowUtc ? u : null;

    /// "paused", or "paused until 3:45 PM" (with the date when it is not today).
    public static string Text(DateTime untilUtc, DateTime nowUtc, CultureInfo? culture = null)
    {
        if (untilUtc == DateTime.MaxValue) return "paused";
        var c = culture ?? CultureInfo.CurrentCulture;
        var local = untilUtc.ToLocalTime();
        var time = local.ToString("t", c);
        return local.Date == nowUtc.ToLocalTime().Date ? $"paused until {time}" : $"paused until {local.ToString("d", c)} {time}";
    }
}
