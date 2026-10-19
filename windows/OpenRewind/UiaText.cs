using Interop.UIAutomationClient;

namespace OpenRewind;

/// Reads text from the focused window's UI Automation tree (Windows' accessibility API).
/// The Mac app does the same with the AX tree; OCR is the fallback. Never reads password fields.
static class UiaText
{
    static IUIAutomation? uia;
    static readonly object gate = new();

    static IUIAutomation Uia()
    {
        if (uia != null) return uia;
        var a = new CUIAutomation8();
        // Slow or hung apps must not stall recording.
        if (a is IUIAutomation2 a2) { a2.ConnectionTimeout = 1000; a2.TransactionTimeout = 500; }
        return uia = a;
    }

    /// Browsers whose tab address is saved with each screenshot (exe names). Same set as the Mac app, plus Windows-only ones.
    static readonly HashSet<string> browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "arc", "dia", "brave", "firefox", "vivaldi", "opera", "chromium" };

    public static bool IsBrowser(string? exePath) => exePath != null && browsers.Contains(Path.GetFileNameWithoutExtension(exePath));

    /// Only the address of the tab a browser window shows, quickly: checked before every screenshot when
    /// sites are blocked, so a blocked tab is hidden in the same screenshot. Pages that are not on the web
    /// ("chrome://newtab/") are returned as they are; null means the address could not be read.
    public static string? Url(IntPtr hwnd) => FocusedWindow(hwnd, true, maxNodes: 1500, maxMillis: 400, urlOnly: true).Url;

    /// Text of the window, and for a browser the address of the tab on screen (or null).
    public static (string? Text, string? Url) FocusedWindow(IntPtr hwnd, bool browser, int maxNodes = 4000, int maxChars = 60_000, int maxMillis = 1500,
                                                           bool urlOnly = false)
    {
        if (hwnd == IntPtr.Zero) return (null, null);
        lock (gate)
        {
            try
            {
                var auto = Uia();
                var walker = auto.ControlViewWalker;
                var root = auto.ElementFromHandle(hwnd);
                var lines = new List<string>();
                var seen = new HashSet<string>();
                int chars = 0, visited = 0;
                // Chromium browsers and Firefox put the tab's address on the page's document element;
                // the address bar (an edit box) is the fallback. The first document is the top page.
                string? docUrl = null, barUrl = null;
                var deadline = Environment.TickCount64 + maxMillis;
                var stack = new Stack<(IUIAutomationElement El, int Depth)>();
                stack.Push((root, 0));
                while (stack.Count > 0 && visited < maxNodes && chars < maxChars && Environment.TickCount64 < deadline && !(urlOnly && docUrl != null))
                {
                    var (el, depth) = stack.Pop();
                    visited++;
                    try
                    {
                        if (el.CurrentIsPassword != 0) continue;
                        if (browser && docUrl == null)
                        {
                            var type = el.CurrentControlType;
                            if (type == UIA_ControlTypeIds.UIA_DocumentControlTypeId || (type == UIA_ControlTypeIds.UIA_EditControlTypeId && barUrl == null))
                            {
                                var raw = el.GetCurrentPropertyValue(UIA_PropertyIds.UIA_ValueValuePropertyId) as string;
                                // For blocked sites, a page that is not on the web (a new tab, settings) still counts as read.
                                var u = WebAddress.Normalize(raw) ?? (urlOnly && raw != null && raw.Contains("://") ? raw.Trim() : null);
                                if (type == UIA_ControlTypeIds.UIA_DocumentControlTypeId) docUrl = u; else barUrl = u;
                            }
                        }
                        if (!urlOnly) foreach (var id in new[] { UIA_PropertyIds.UIA_NamePropertyId, UIA_PropertyIds.UIA_ValueValuePropertyId, UIA_PropertyIds.UIA_HelpTextPropertyId })
                        {
                            if (el.GetCurrentPropertyValue(id) is string s)
                            {
                                var t = s.Trim();
                                if (t.Length > 1 && seen.Add(t)) { lines.Add(t); chars += t.Length; }
                            }
                        }
                        if (depth < 40)
                        {
                            var kids = new List<IUIAutomationElement>();
                            for (var k = walker.GetFirstChildElement(el); k != null && kids.Count < 2000; k = walker.GetNextSiblingElement(k))
                                kids.Add(k);
                            for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], depth + 1));
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException) { }  // element went away mid-walk
                }
                var text = string.Join("\n", lines);
                return (text.Length >= 20 ? text : null, docUrl ?? barUrl);
            }
            catch (Exception e)
            {
                Log.Write($"accessibility text failed: {e.Message}");
                return (null, null);
            }
        }
    }
}
