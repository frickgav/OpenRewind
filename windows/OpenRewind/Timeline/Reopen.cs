using System.Diagnostics;

namespace OpenRewind;

/// What O in the timeline brings back for the shown screenshot.
/// Web: the browser tab, in the same browser. Window: the app, and its window with the same title if it is still open.
/// (The Mac app also reopens documents by file; Windows apps do not say which file a window shows.)
sealed record OpenTarget(bool IsWeb, string? Url, string? ExePath);

static class Reopen
{
    public static OpenTarget? For(TimelineFrame f)
    {
        if (f.AppName.Length == 0 || f.AppName.Equals("OpenRewind", StringComparison.OrdinalIgnoreCase)) return null;
        var exe = ExeFor(f.AppName);
        if (WebAddress.Normalize(f.Url) is string url) return new(true, url, exe);
        return exe == null ? null : new(false, null, exe);
    }

    /// Exe of an app by its name: learned while recording, else from a window that is open now.
    public static string? ExeFor(string appName)
    {
        if (Capture.AppPaths.TryGetValue(appName, out var known) && File.Exists(known)) return known;
        foreach (var (_, exe, _) in Windows())
            if (Capture.AppName(exe).Equals(appName, StringComparison.OrdinalIgnoreCase)) return Capture.AppPaths[appName] = exe;
        return null;
    }

    /// Visible top-level windows with a title: handle, exe path, title.
    static List<(IntPtr Hwnd, string Exe, string Title)> Windows()
    {
        var list = new List<(IntPtr, string, string)>();
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Native.WindowTitle(h) is { Length: > 0 } title && Capture.WindowExe(h) is string exe)
                list.Add((h, exe, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// Returns false when nothing could be opened.
    public static bool Open(TimelineFrame f, OpenTarget t)
    {
        // We had the foreground (the overlay), so we may hand it to the app we start.
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        if (t.IsWeb) return OpenWeb(t.Url!, t.ExePath);

        if (Raise(t.ExePath!, f.WindowName) || Raise(t.ExePath!, null)) return true;
        if (!Start(t.ExePath!, null)) return false;
        // A freshly started app needs a moment before its windows exist.
        var title = f.WindowName;
        foreach (var delay in new[] { 600, 1500, 3000 })
        {
            var timer = new System.Windows.Forms.Timer { Interval = delay };
            timer.Tick += (_, _) => { timer.Dispose(); Raise(t.ExePath!, title); };
            timer.Start();
        }
        return true;
    }

    static bool OpenWeb(string url, string? exe)
    {
        // Same browser as in the screenshot; the default browser when that one cannot be started.
        if (exe != null && UiaText.IsBrowser(exe) && Start(exe, url)) return true;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
        catch (Exception e) { Log.Write($"cannot open {url}: {e.Message}"); return false; }
    }

    static bool Start(string exe, string? arg)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (arg != null) psi.ArgumentList.Add(arg);
            Process.Start(psi);
            return true;
        }
        catch (Exception e) { Log.Write($"cannot start {exe}: {e.Message}"); return false; }
    }

    /// Bring the app's window with this title (any of its windows when title is null) to the front.
    static bool Raise(string exe, string? title)
    {
        if (title is { Length: 0 }) return false;
        foreach (var (h, wexe, wtitle) in Windows())
        {
            if (!wexe.Equals(exe, StringComparison.OrdinalIgnoreCase) || (title != null && wtitle != title)) continue;
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            Native.SetForegroundWindow(h);
            return true;
        }
        return false;
    }
}
