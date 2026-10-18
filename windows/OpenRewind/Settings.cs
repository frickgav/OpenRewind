using System.Text.Json;
using Microsoft.Win32;

namespace OpenRewind;

/// What the tray menu remembers (the Mac app keeps the same things in UserDefaults).
/// Saved as ~/.openrewind/settings.json.
sealed class Settings
{
    public bool RecordScreen { get; set; } = true;
    public bool RecordAudio { get; set; } = true;
    public string KeepScreen { get; set; } = "forever";
    public string KeepAudio { get; set; } = "forever";
    /// The local HTTP API + browser search page. Off unless the user turns it on.
    public bool LocalSearchServer { get; set; } = false;
    /// Apps that are blurred in screenshots and never have their text stored.
    public List<BlockedApp> BlockedApps { get; set; } =
        BlockCatalog.Windows.Where(a => a.Category == BlockCategory.PasswordManager).Select(a => new BlockedApp(a.Id, a.Name)).ToList();
    /// Window titles with any of these words are hidden too (bank sites in a browser).
    public List<string> BlockedKeywords { get; set; } = new();
    public bool BlockPrivateWindows { get; set; } = true;
    /// The first-run setup has been finished (or skipped).
    public bool OnboardingDone { get; set; } = false;

    string path = "";

    public static Settings Load(string home)
    {
        var p = Path.Combine(home, "settings.json");
        Settings s;
        try { s = File.Exists(p) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(p)) ?? new() : new(); }
        catch { s = new(); }
        s.path = p;
        return s;
    }

    public void Save()
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log.Write($"cannot save settings: {e.Message}"); }
    }

    /// What recording should hide, from these settings plus OPENREWIND_EXCLUDE.
    public Blocklist ToBlocklist() => new(BlockedApps, BlockedKeywords, BlockPrivateWindows, Blocklist.EnvNames());

    public KeepFor Screen => KeepForInfo.FromRaw(KeepScreen) ?? KeepFor.Forever;
    public KeepFor Audio => KeepForInfo.FromRaw(KeepAudio) ?? KeepFor.Forever;

    // Launch at login = a value under HKCU\...\Run (no admin needed).
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "OpenRewind";

    public static bool LaunchAtLogin
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
    }
}
