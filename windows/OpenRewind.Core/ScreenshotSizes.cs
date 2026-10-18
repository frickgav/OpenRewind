namespace OpenRewind;

/// The Settings → Screenshot Resolution choices: saved size as a percent of the screen.
/// Text is read from the full-size capture either way; this only sets what is kept on disk.
public static class ScreenshotSizes
{
    public static readonly int[] Choices = { 100, 75, 50, 25 };
    public const int Default = 50;

    public static string Title(int percent) => percent switch
    {
        100 => "Full (100%)",
        50 => "Half (50%, Default)",
        25 => "Quarter (25%)",
        _ => $"{percent}%",
    };

    /// An unknown value (hand-edited settings) falls back to the default.
    public static double Scale(int percent) => (Choices.Contains(percent) ? percent : Default) / 100.0;
}
