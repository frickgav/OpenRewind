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

    public static string? FocusedWindowText(IntPtr hwnd, int maxNodes = 4000, int maxChars = 60_000, int maxMillis = 1500)
    {
        if (hwnd == IntPtr.Zero) return null;
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
                var deadline = Environment.TickCount64 + maxMillis;
                var stack = new Stack<(IUIAutomationElement El, int Depth)>();
                stack.Push((root, 0));
                while (stack.Count > 0 && visited < maxNodes && chars < maxChars && Environment.TickCount64 < deadline)
                {
                    var (el, depth) = stack.Pop();
                    visited++;
                    try
                    {
                        if (el.CurrentIsPassword != 0) continue;
                        foreach (var id in new[] { UIA_PropertyIds.UIA_NamePropertyId, UIA_PropertyIds.UIA_ValueValuePropertyId, UIA_PropertyIds.UIA_HelpTextPropertyId })
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
                return text.Length >= 20 ? text : null;
            }
            catch (Exception e)
            {
                Log.Write($"accessibility text failed: {e.Message}");
                return null;
            }
        }
    }
}
