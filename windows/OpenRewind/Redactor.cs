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

    /// Blocked sites: reads the address of each browser window that is new or whose title changed (a tab
    /// switch, a new page), so a blocked page is hidden even when its window is not in front. The window in
    /// front is read by the caller every time. At most a few changed windows are read per screenshot;
    /// new ones always are, so none is shown before its address is known.
    public static void ReadSites(SiteWindows sites, IntPtr front)
    {
        var browsers = new List<(IntPtr Hwnd, string Title)>();
        var paths = new Dictionary<uint, string?>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == myPid) return true;
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = Native.ProcessPath(pid);
            if (UiaText.IsBrowser(path)) browsers.Add((hwnd, Native.WindowTitle(hwnd)));
            return true;
        }, IntPtr.Zero);
        sites.Prune(browsers.Select(b => (long)b.Hwnd));
        int changed = 0;
        foreach (var (hwnd, title) in browsers)
        {
            // Untitled windows are a browser's popups and bubbles, not pages.
            if (hwnd == front || title.Length == 0 || !sites.NeedsRead(hwnd, title)) continue;
            if (sites.Knows(hwnd) && changed++ >= 3) continue;
            sites.Saw(hwnd, title, UiaText.Url(hwnd));
        }
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

    /// Pixelate secrets in `image`: `words` are boxes as fractions of the picture (from OCR), `fields`
    /// are password fields in desktop pixels; `screen` is where the picture is on the desktop.
    public static void BlurSecrets(Bitmap image, IEnumerable<RectangleF> words, IEnumerable<Rectangle> fields, Rectangle? screen)
    {
        var rects = words.Select(b => Rectangle.FromLTRB(
            (int)Math.Floor(b.Left * image.Width), (int)Math.Floor(b.Top * image.Height),
            (int)Math.Ceiling(b.Right * image.Width), (int)Math.Ceiling(b.Bottom * image.Height))).ToList();
        if (screen is Rectangle s && s.Width > 0)
        {
            // Screenshots can be scaled; map desktop pixels onto the picture.
            double sx = (double)image.Width / s.Width, sy = (double)image.Height / s.Height;
            foreach (var f in fields)
                rects.Add(Rectangle.FromLTRB((int)((f.Left - s.Left) * sx), (int)((f.Top - s.Top) * sy),
                                             (int)Math.Ceiling((f.Right - s.Left) * sx), (int)Math.Ceiling((f.Bottom - s.Top) * sy)));
        }
        var bounds = new Rectangle(0, 0, image.Width, image.Height);
        using var g = Graphics.FromImage(image);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        foreach (var raw in rects)
        {
            var r = raw;
            r.Inflate(Math.Max(3, r.Height / 4), Math.Max(2, r.Height / 6));
            r.Intersect(bounds);
            if (r.Width <= 0 || r.Height <= 0) continue;
            // Blocks a little taller than a line of text: no letter survives.
            int bw = Math.Max(1, r.Width / Math.Max(6, r.Height)), bh = 1;
            using var tiny = new Bitmap(bw, bh, PixelFormat.Format32bppRgb);
            using (var tg = Graphics.FromImage(tiny))
            {
                tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                tg.DrawImage(image, new Rectangle(0, 0, bw, bh), r, GraphicsUnit.Pixel);
            }
            g.DrawImage(tiny, r);
        }
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
