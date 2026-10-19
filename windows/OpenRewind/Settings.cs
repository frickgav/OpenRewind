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
    /// Seconds between saved frames while only a few words change (2 = every capture).
    public int TypingFrameGap { get; set; } = 2;
    /// Saved screenshot size as a percent of the screen (see ScreenshotSizes.Choices).
    public int ScreenshotPercent { get; set; } = ScreenshotSizes.Default;
    /// The local HTTP API + browser search page. Off unless the user turns it on.
    public bool LocalSearchServer { get; set; } = false;
    /// Apps that are blurred in screenshots and never have their text stored.
    public List<BlockedApp> BlockedApps { get; set; } =
        BlockCatalog.Windows.Where(a => a.Category == BlockCategory.PasswordManager).Select(a => new BlockedApp(a.Id, a.Name)).ToList();
    /// Window titles with any of these words are hidden too (bank sites in a browser).
    public List<string> BlockedKeywords { get; set; } = new();
    public bool BlockPrivateWindows { get; set; } = true;
    /// Websites and links whose tabs are hidden (see SiteRules): "chase.com", "docs.google.com/document/d/…".
    public List<string> BlockedSites { get; set; } = new();
    /// The first-run setup has been finished (or skipped).
    public bool OnboardingDone { get; set; } = false;
    /// Download new versions in the background; they are installed only on a click.
    public bool AutoDownloadUpdates { get; set; } = true;
    /// The version whose timeline banner was closed; a newer version shows it again.
    public string? DismissedUpdateVersion { get; set; }
    /// The version that ran last time. A different one means a new exe sits at the same path (see Program.RefreshIconIfNew).
    public string? LastRunVersion { get; set; }
    /// Pause Recording: when screen and audio come back on (UTC); DateTime.MaxValue = until resumed. Null when not paused.
    public DateTime? PausedUntil { get; set; }
    /// Timeline hotkey (see HotkeyShortcut). Null = Ctrl+Alt+T.
    public HotkeyShortcut? TimelineShortcut { get; set; }
    /// Timeline Look: "system", "light" or "dark".
    public string TimelineLook { get; set; } = "system";

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
    public Blocklist ToBlocklist() => new(BlockedApps, BlockedKeywords, BlockPrivateWindows, Blocklist.EnvNames(), BlockedSites);

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
