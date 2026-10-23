using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Saves what is open about once a minute while the screen is recorded, for Restore Workspace:
/// each app's windows and, for browsers, the address of the tab each window shows. Cheap: listing
/// windows takes a few milliseconds, and a browser window's address is read again only when its title
/// changed. Blocked apps, private windows, blocked words and sites are never saved; secrets in titles
/// and addresses are taken out (Secrets). Only stored when the set changed (Database.SaveWorkspace).
sealed class WorkspaceWatcher : IDisposable
{
    readonly Database db;
    readonly Func<bool> recording;
    readonly System.Threading.Timer timer;
    readonly Dictionary<IntPtr, (string Title, string? Url)> urls = new();
    int busy;
    static readonly uint myPid = (uint)Environment.ProcessId;
    /// At most this many browser windows have their address read per minute.
    const int MaxReads = 6;

    public WorkspaceWatcher(Database db, Func<bool> recording)
    {
        this.db = db;
        this.recording = recording;
        timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    }

    void Tick()
    {
        if (Interlocked.Exchange(ref busy, 1) == 1) return;
        try
        {
            if (!recording()) return;
            var items = Snapshot();
            if (items.Count > 0 && db.SaveWorkspace(DateTime.UtcNow, items) is long id) Log.Write($"workspace {id}: {items.Count} items");
        }
        catch (Exception e) { Log.Write($"workspace snapshot failed: {e.Message}"); }
        finally { busy = 0; }
    }

    /// What is open now.
    List<WorkspaceItem> Snapshot()
    {
        var list = Blocklist.Current;
        var redact = Database.RedactSecrets;
        var items = new List<WorkspaceItem>();
        var seen = new HashSet<IntPtr>();
        int reads = 0;
        foreach (var (hwnd, exe, title) in Windows())
        {
            seen.Add(hwnd);
            var app = Capture.AppName(exe);
            if (app.Equals("OpenRewind", StringComparison.OrdinalIgnoreCase)) continue;
            if (list.BlocksWindow(Blocklist.IdFor(exe), app, title)) continue;
            var t = redact ? Secrets.Redact(title) : title;
            items.Add(new WorkspaceItem(Workspace.App, app, exe, t));
            if (!UiaText.IsBrowser(exe)) continue;
            if (!urls.TryGetValue(hwnd, out var known) || known.Title != title)
            {
                if (reads++ >= MaxReads) continue;
                urls[hwnd] = known = (title, UiaText.Url(hwnd));
            }
            if (known.Url is not string url || !Workspace.IsWebUrl(url) || list.BlocksUrl(url) || list.HasKeyword(url)) continue;
            items.Add(new WorkspaceItem(Workspace.Tab, app, exe, t, redact ? Secrets.RedactUrl(url) : url));
        }
        foreach (var gone in urls.Keys.Where(h => !seen.Contains(h)).ToList()) urls.Remove(gone);
        return items;
    }

    /// Main windows of other apps that a user would call open: visible (minimized counts), with a title,
    /// not tool windows, not owned by another window, not on another desktop.
    static List<(IntPtr Hwnd, string Exe, string Title)> Windows()
    {
        var found = new List<(IntPtr, string, string)>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == myPid) return true;
            var title = Native.WindowTitle(h);
            if (title.Length == 0 || Capture.WindowExe(h) is not string exe) return true;
            if (Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && title == "Program Manager") return true;
            found.Add((h, exe, title));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public void Dispose() => timer.Dispose();

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, DWMWA_CLOAKED = 14;
}

/// Opens again what was open at a moment (Shift+O on the timeline), after the user picks what.
/// Apps that are running already are left as they are; nothing is ever closed.
static class WorkspaceRestore
{
    /// Returns how many things were opened.
    public static int Open(IEnumerable<WorkspaceItem> items)
    {
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        int opened = 0;
        var running = RunningExes();
        foreach (var i in items)
        {
            switch (i.Kind)
            {
                case Workspace.Tab when Workspace.IsWebUrl(i.Url):
                    if (File.Exists(i.AppId) && UiaText.IsBrowser(i.AppId) ? Reopen.Start(i.AppId, i.Url) : OpenShell(i.Url)) opened++;
                    break;
                case Workspace.Document when File.Exists(i.Path):
                    if (OpenShell(i.Path)) opened++;
                    break;
                case Workspace.App:
                    // Browsers come back with their tabs; other apps only when not running.
                    if (running.Contains(i.AppId) || UiaText.IsBrowser(i.AppId) && items.Any(t => t.Kind == Workspace.Tab && t.AppId == i.AppId)) continue;
                    if (File.Exists(i.AppId) && Reopen.Start(i.AppId, null)) { opened++; running.Add(i.AppId); }
                    break;
            }
        }
        return opened;
    }

    static bool OpenShell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return true; }
        catch (Exception e) { Log.Write($"cannot open {(RecordingFile.EncryptNew ? "an item" : target)}: {e.Message}"); return false; }
    }

    static HashSet<string> RunningExes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Native.EnumWindows((h, _) =>
        {
            if (Native.IsWindowVisible(h) && Capture.WindowExe(h) is string exe) set.Add(exe);
            return true;
        }, IntPtr.Zero);
        return set;
    }
}
