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
    /// Search by Meaning: index moments in the background to find them by meaning. Off unless the user turns it on.
    public bool SearchByMeaning { get; set; } = false;
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

    /// AI: the picked provider, and per provider (by AiProviders.Id) the model, server address and
    /// models seen. API keys are in the Windows Credential Manager (AiKeyStore), not here.
    public string AiProvider { get; set; } = "anthropic";
    public Dictionary<string, string> AiModel { get; set; } = new();
    public Dictionary<string, string> AiBaseUrl { get; set; } = new();
    public Dictionary<string, List<string>> AiModels { get; set; } = new();
    /// The user has read what gets sent to the provider.
    public bool AiPrivacyAccepted { get; set; }
    /// Let the AI ask for screenshots (view_screenshot). Off unless the user turns it on.
    public bool AiScreenshots { get; set; }
    /// Prices the user typed, by model: US dollars per million tokens in and out.
    public Dictionary<string, double[]> AiPrices { get; set; } = new();
    /// Prices that came with a provider's model list (OpenRouter), by model.
    public Dictionary<string, double[]> AiListedPrices { get; set; } = new();
    /// Everything used per provider (by AiProviders.Id) since the user last reset it.
    public Dictionary<string, AiTotal> AiTotals { get; set; } = new();

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

    public AiSettings Ai() => Ai(AiProviders.FromId(AiProvider));

    public AiSettings Ai(AiProviderKind k) =>
        new(k, AiModel.GetValueOrDefault(k.Id()) ?? k.SuggestedModels().FirstOrDefault() ?? "",
            AiBaseUrl.GetValueOrDefault(k.Id()) ?? k.DefaultBaseUrl(), AiKeyStore.Get(k.Id()));

    public void SaveAi(AiSettings s)
    {
        AiProvider = s.Kind.Id();
        AiModel[s.Kind.Id()] = s.Model;
        AiBaseUrl[s.Kind.Id()] = s.BaseUrl;
        AiKeyStore.Set(s.Kind.Id(), s.Key);
        RememberModels(s.Kind, new[] { s.Model });
    }

    /// Models the user typed or fetched, so new ones stay in the list without an app update.
    public List<string> SavedModels(AiProviderKind k) =>
        k.SuggestedModels().Concat(AiModels.GetValueOrDefault(k.Id()) ?? new()).Distinct().ToList();

    public void RememberModels(AiProviderKind k, IEnumerable<string> models)
    {
        if (!AiModels.TryGetValue(k.Id(), out var list)) AiModels[k.Id()] = list = new();
        foreach (var m in models) if (m.Length > 0 && !list.Contains(m)) list.Add(m);
        Save();
    }

    /// The typed price for a model, else the built-in one (Claude models only).
    public AiPrice? AiPriceFor(string model) =>
        AiPrices.TryGetValue(model, out var p) && p.Length == 2 ? new AiPrice(p[0], p[1])
        : AiListedPrices.TryGetValue(model, out var l) && l.Length == 2 ? new AiPrice(l[0], l[1])
        : AiPrice.Default(model);

    public void AddAiUsage(AiProviderKind k, AiUsage used, double? cost)
    {
        if (used.IsZero) return;
        var t = AiTotals.GetValueOrDefault(k.Id()) ?? new AiTotal { Since = DateTime.UtcNow };
        t.Input += used.Input;
        t.Output += used.Output;
        t.Cost += cost ?? 0;
        if (cost == null) t.Unpriced = true;
        AiTotals[k.Id()] = t;
        Save();
    }

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

/// Running total for one AI provider.
sealed class AiTotal
{
    public long Input { get; set; }
    public long Output { get; set; }
    public double Cost { get; set; }
    /// Some requests went to a model with no price, so Cost is too low.
    public bool Unpriced { get; set; }
    public DateTime Since { get; set; }
}
