using System.Diagnostics;
using Microsoft.Win32;

namespace OpenRewind;

/// An app found on this PC, for the block list pickers.
sealed record FoundApp(string Id, string Name, string? Path);

/// Finds apps to offer in the block list: the ones open now plus the installed ones
/// (from the uninstall list in the registry, which is what Settings → Apps shows).
static class AppDiscovery
{
    public static List<FoundApp> Find()
    {
        var found = new Dictionary<string, FoundApp>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path, string? name)
        {
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
            var id = Blocklist.IdFor(path);
            if (id.Length == 0 || Junk(id) || found.ContainsKey(id)) return;
            if (string.IsNullOrWhiteSpace(name))
            {
                try { name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); } catch { }
                if (string.IsNullOrWhiteSpace(name)) name = System.IO.Path.GetFileNameWithoutExtension(path);
            }
            found[id] = new FoundApp(id, name!.Trim(), path);
        }

        // Open apps first: their exe is exactly what recording sees.
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                Add(Native.ProcessPath((uint)p.Id), null);
            }
            catch { }
            finally { p.Dispose(); }
        }

        foreach (var (hive, key) in new[]
        {
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(key);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    if (k == null || k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    var name = k.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    Add(ExeFromIcon(k.GetValue("DisplayIcon") as string), name);
                }
            }
            catch { }
        }
        return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// DisplayIcon looks like "C:\path\app.exe,0" or "\"C:\path\app.exe\"".
    static string? ExeFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim();
        int comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..], out _)) s = s[..comma];
        s = s.Trim().Trim('"');
        return File.Exists(s) ? s : null;
    }

    /// Uninstallers, setup programs and Windows' own helpers are not apps anyone wants to pick.
    static bool Junk(string id) =>
        id.StartsWith("unins") || id.StartsWith("uninst") || id.Contains("setup") || id.Contains("update") ||
        id is "explorer" or "applicationframehost" or "textinputhost" or "searchhost" or "shellexperiencehost"
            or "startmenuexperiencehost" or "systemsettings" or "openrewind" or "msiexec" or "rundll32";
}
