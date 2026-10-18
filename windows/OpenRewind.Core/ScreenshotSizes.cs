namespace OpenRewind;

/// The Settings → Screenshot Resolution choices: saved size as a percent of the screen.
/// Text is read from the full-size capture either way; this only sets what is kept on disk.
public static class ScreenshotSizes
{
    public static readonly int[] Choices = { 100, 75, 50, 33 };
    public const int Default = 50;

    /// "Medium (50%)", and with the screen size "Medium (50%)  —  1280 × 720".
    public static string Title(int percent, int screenWidth = 0, int screenHeight = 0)
    {
        var pct = Nearest(percent);
        var name = pct switch { 100 => "Full", 75 => "High", 50 => "Medium", _ => "Low" };
        var t = $"{name} ({pct}%)";
        if (screenWidth > 0 && screenHeight > 0)
            t += $"  —  {(int)(screenWidth * pct / 100.0)} × {(int)(screenHeight * pct / 100.0)}";
        return t;
    }

    /// The closest choice, for values from older versions (25) or hand-edited settings.
    public static int Nearest(int percent) => Choices.MinBy(c => Math.Abs(c - percent));

    public static double Scale(int percent) => Nearest(percent) / 100.0;
}
