using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Blurs blocked apps and windows out of a screenshot before anything reads or saves it.
/// Works out which parts of the screen they cover, back to front: a blocked window adds its
/// rectangle, any normal window on top of it takes its rectangle away again.
static class Redactor
{
    /// One top-level window on screen. `Blocked` windows are hidden; others hide what is under them.
    public readonly record struct Win(Rectangle Rect, bool Blocked, bool Occludes, string Label);

    static readonly uint myPid = (uint)Environment.ProcessId;
    static readonly Dictionary<string, string> nameCache = new(StringComparer.OrdinalIgnoreCase);

    /// Visible top-level windows, back to front. `sites` knows which browser windows show a blocked site.
    public static List<Win> Windows(Blocklist list, SiteWindows? sites = null)
    {
        var wins = new List<Win>();
        var paths = new Dictionary<uint, string?>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;  // other desktops, hidden UWP
            if (!Bounds(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == myPid) return true;  // our overlay is never in the screenshot, so it hides nothing
            pid = RealPid(hwnd, pid, paths);
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = Native.ProcessPath(pid);
            var name = path == null ? "" : AppName(path);
            var title = Native.WindowTitle(hwnd);
            bool site = sites?.Blocks(hwnd, list) == true;
            bool blocked = site || list.BlocksWindow(Blocklist.IdFor(path), name, title);
            // Click-through overlays (game bars, screen dimmers) are see-through: they do not hide a blocked window.
            bool occludes = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) == 0;
            var label = list.BlocksApp(Blocklist.IdFor(path), name) && name.Length > 0 ? name
                : site ? "Blocked site"
                : Blocklist.IsPrivateTitle(title) ? "Private window" : "Hidden window";
            wins.Add(new Win(r, blocked, occludes, label));
            return true;
        }, IntPtr.Zero);
        wins.Reverse();  // EnumWindows goes front to back
        return wins;
    }

    /// Blur the parts of `image` (a capture of `screen`) that blocked windows cover. Returns true if anything was hidden.
    public static bool Apply(Bitmap image, Rectangle screen, List<Win> wins)
    {
        if (!wins.Any(w => w.Blocked && w.Rect.IntersectsWith(screen))) return false;
        using var region = new Region();
        region.MakeEmpty();
        foreach (var w in wins)
        {
            var r = w.Rect;
            r.Offset(-screen.Left, -screen.Top);
            if (w.Blocked) region.Union(r);
            else if (w.Occludes) region.Exclude(r);
        }
        using var g = Graphics.FromImage(image);
        if (region.IsEmpty(g)) return false;

        // Shrink to 1/40 and stretch back: nothing readable survives, but colors still hint at the layout.
        int tw = Math.Max(1, image.Width / 40), th = Math.Max(1, image.Height / 40);
        using var tiny = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
        using (var tg = Graphics.FromImage(tiny))
        {
            tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            tg.DrawImage(image, new Rectangle(0, 0, tw, th));
        }
        g.Clip = region;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(tiny, new Rectangle(0, 0, image.Width, image.Height));
        using (var tint = new SolidBrush(Color.FromArgb(150, 20, 20, 24))) g.FillRectangle(tint, 0, 0, image.Width, image.Height);

        // Say what was hidden, in the middle of each blocked window's visible part.
        float px = Math.Max(14, image.Height / 60f);
        using var font = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
        using var small = new Font("Segoe UI", px * 0.75f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        foreach (var w in wins.Where(w => w.Blocked))
        {
            var r = w.Rect;
            r.Offset(-screen.Left, -screen.Top);
            using var part = region.Clone();
            part.Intersect(r);
            var b = part.GetBounds(g);
            if (b.Width < px * 8 || b.Height < px * 3) continue;
            var c = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
            g.DrawString("\U0001F512 " + w.Label, font, Brushes.White, new RectangleF(b.X, c.Y - px * 1.2f, b.Width, px * 1.4f), fmt);
            using var dim = new SolidBrush(Color.FromArgb(190, 255, 255, 255));
            g.DrawString("Hidden by OpenRewind", small, dim, new RectangleF(b.X, c.Y + px * 0.2f, b.Width, px * 1.2f), fmt);
        }
        return true;
    }

    /// Store apps live inside ApplicationFrameHost; the real app owns a child window.
    static uint RealPid(IntPtr hwnd, uint pid, Dictionary<uint, string?> paths)
    {
        if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = Native.ProcessPath(pid);
        if (path == null || !Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return pid;
        uint inner = 0;
        Native.EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out var cp);
            if (cp != pid) { inner = cp; return false; }
            return true;
        }, IntPtr.Zero);
        return inner != 0 ? inner : pid;
    }

    /// Friendly name from the exe's version info ("1Password"), else the file name.
    static string AppName(string path)
    {
        lock (nameCache)
        {
            if (nameCache.TryGetValue(path, out var n)) return n;
            try { n = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { n = null; }
            if (string.IsNullOrEmpty(n)) n = Path.GetFileNameWithoutExtension(path);
            return nameCache[path] = n;
        }
    }

    /// The window's visible frame (without the invisible resize border), in screen pixels.
    static bool Bounds(IntPtr hwnd, out Rectangle r)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT d, Marshal.SizeOf<RECT>()) == 0 ||
            GetWindowRect(hwnd, out d))
        {
            r = Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom);
            return true;
        }
        r = Rectangle.Empty;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool EnumWindows(Native.EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
}
