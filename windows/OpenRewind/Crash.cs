namespace OpenRewind;

/// Anything that would end the app without a word gets written to %LOCALAPPDATA%\OpenRewind\crash.log
/// (and to the normal log), so a launch that quits by itself can be explained.
/// The folder is fixed rather than the data folder, so it works even when the data folder is the problem.
static class Crash
{
    public static string LogPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRewind", "crash.log");

    /// Call first thing in Main, before any window or thread exists.
    public static void Install()
    {
        // Errors on the UI thread go to ThreadException instead of WinForms' own "Continue/Quit" box.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Write("ui thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Write(e.IsTerminating ? "fatal" : "background thread", e.ExceptionObject as Exception);
            if (e.IsTerminating) Tell(e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("background task", e.Exception); e.SetObserved(); };
    }

    public static void Write(string where, Exception? e)
    {
        var text = $"[{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}] OpenRewind {Updater.Current} crashed ({where}) " +
                   $"on {Environment.OSVersion.VersionString}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}\n{e}\n\n";
        try { Log.Write($"crash ({where}): {e?.GetType().Name}: {e?.Message}"); } catch { }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text);
        }
        catch { }
    }

    /// The tray app has no window, so without this a crash at startup just looks like nothing happened.
    public static void Tell(Exception? e)
    {
        try
        {
            MessageBox.Show($"OpenRewind stopped because of an error:\n\n{e?.GetType().Name}: {e?.Message}\n\nDetails are in {LogPath}",
                            "OpenRewind", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
