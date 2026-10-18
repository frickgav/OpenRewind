using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

namespace OpenRewind;

public record ActiveWindow(string AppName, string WindowName, IntPtr Hwnd, string? ExePath);

static class Capture
{
    /// App name → exe path, learned while recording. The timeline uses it for app icons.
    public static readonly ConcurrentDictionary<string, string> AppPaths = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> nameCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly uint myPid = (uint)Environment.ProcessId;

    public static ActiveWindow ActiveWindow()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new("", "", IntPtr.Zero, null);
        var path = WindowExe(hwnd);
        var name = path == null ? "" : AppName(path);
        if (path != null && name.Length > 0) AppPaths[name] = path;
        return new(name, Native.WindowTitle(hwnd), hwnd, path);
    }

    /// Exe path of the app that owns a window.
    public static string? WindowExe(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var path = Native.ProcessPath(pid);
        // Store apps live inside ApplicationFrameHost; the real app owns a child window.
        if (path != null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint inner = 0;
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                Native.GetWindowThreadProcessId(child, out var cp);
                if (cp != pid) { inner = cp; return false; }
                return true;
            }, IntPtr.Zero);
            if (inner != 0 && Native.ProcessPath(inner) is string p) path = p;
        }
        return path;
    }

    public static bool IsOwnWindow(ActiveWindow w)
    {
        if (w.Hwnd == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(w.Hwnd, out var pid);
        return pid == myPid;
    }

    /// Friendly name from the exe's version info ("Google Chrome"), else the file name ("chrome").
    public static string AppName(string path) => nameCache.GetOrAdd(path, p =>
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(p).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d)) return d;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(p);
    });

    /// Grab the monitor that holds the mouse. Our own overlay hides itself from capture.
    public static Bitmap Screenshot()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var b = screen.Bounds;
        var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }
}
