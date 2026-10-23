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
    /// Compress Older Recordings: keep frames older than an hour as video. Off until it has been tested on more PCs.
    public bool CompressOlderRecordings { get; set; } = false;
    /// The local HTTP API + browser search page. Off unless the user turns it on.
    public bool LocalSearchServer { get; set; } = false;
    /// Search by Meaning: index moments in the background to find them by meaning. Off unless the user turns it on.
    public bool SearchByMeaning { get; set; } = false;
    /// Let AI apps (Claude, Cursor, …) read the history through `OpenRewind.exe mcp`. Off unless the user turns it on.
    public bool McpEnabled { get; set; } = false;
    /// Apps that are blurred in screenshots and never have their text stored.
    public List<BlockedApp> BlockedApps { get; set; } =
        BlockCatalog.Windows.Where(a => a.Category == BlockCategory.PasswordManager).Select(a => new BlockedApp(a.Id, a.Name)).ToList();
    /// Window titles with any of these words are hidden too (bank sites in a browser).
    public List<string> BlockedKeywords { get; set; } = new();
    public bool BlockPrivateWindows { get; set; } = true;
    /// Blur passwords, API keys, tokens and card numbers out of screenshots and text before they are saved.
    public bool RedactSecrets { get; set; } = true;
    /// Websites and links whose tabs are hidden (see SiteRules): "chase.com", "docs.google.com/document/d/…".
    public List<string> BlockedSites { get; set; } = new();
    /// Apps left out of the timeline (see HiddenApps), by the name the timeline shows. Still recorded.
    public List<string> TimelineHiddenApps { get; set; } = new();
    /// The first-run setup has been finished (or skipped).
    public bool OnboardingDone { get; set; } = false;
    /// Ctrl+Shift + mouse wheel in any app opens the timeline (see ScrollOpener).
    public bool ScrollOpensTimeline { get; set; } = true;
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
    /// Settings → Transcription Model: an id from SpeechModels ("whisper-auto", "parakeet-v3", …).
    public string TranscriptionModel { get; set; } = SpeechModels.DefaultId;
    /// Timeline Look: "system", "light" or "dark".
    public string TimelineLook { get; set; } = "system";
    /// Encrypt Recordings (see Encryption). The key is in encryption-key.dpapi, never here.
    public bool EncryptRecordings { get; set; }
    /// Everything already recorded matches EncryptRecordings. False while a switch is still converting.
    public bool EncryptionDone { get; set; } = true;
    /// Ask for Windows Hello before showing the timeline, search, Ask AI and history (see AppLock).
    public bool RequireUnlock { get; set; }
    /// Minutes an unlock lasts after the last use; 0 = until the PC locks.
    public int LockAfterMinutes { get; set; } = 15;

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
    /// Journal: write each day's AI recap by itself. Off by default because it spends tokens.
    public bool JournalAutoRecap { get; set; }
    /// The day (yyyy-MM-dd) the automatic recap last ran for, so each day is written once.
    public string? JournalLastAutoDay { get; set; }
    /// The AI provider it was turned on for (Journal.ProviderKey); it only runs while that is still the one in AI Settings.
    public string? JournalAutoProvider { get; set; }
    /// Failed automatic tries for JournalAutoAttemptDay (at most Journal.AutoAttempts).
    public string? JournalAutoAttemptDay { get; set; }
    public int JournalAutoAttempts { get; set; }

    /// "Write Each Day's Recap Automatically" is on, for the AI provider set up now.
    public bool JournalAutoRecapOn(AiSettings ai) => JournalAutoRecap && JournalAutoProvider == Journal.ProviderKey(ai);

    /// Send each meeting's transcript to the AI provider when it ends, for a summary and action items.
    /// Off unless the user turns it on: it spends their tokens and sends what was said.
    public bool SummarizeMeetings { get; set; }
    /// When it was turned on (UTC). Only meetings that end after this are summarized by themselves.
    public DateTime? SummarizeMeetingsSince { get; set; }
    /// The provider and server the user agreed to send transcripts to (MeetingNotes.ConsentKey).
    /// Summaries pause, and the user is asked again, when AI Settings point somewhere else.
    public string? SummarizeMeetingsProvider { get; set; }
    /// Show "Meeting detected. Take notes?" when a call starts. On by default.
    public bool AskBeforeMeetingNotes { get; set; } = true;
    /// Answers remembered per meeting app ("Zoom" → "always" or "never"), from the popup.
    public Dictionary<string, string> MeetingChoices { get; set; } = new();
    /// "You promised" reminders (Settings → AI). Off until turned on; `PromisesProvider` is the provider
    /// agreed to (MeetingNotes.ConsentKey); it pauses when AI Settings point elsewhere.
    public bool PromisesOn { get; set; }
    public string? PromisesProvider { get; set; }
    public DateTime? PromisesLastScan { get; set; }

    string path = "";

    public static Settings Load(string home)
    {
        var p = Path.Combine(home, "settings.json");
        Settings s;
        try { s = Parse(SettingsFile.Read(p)); }
        catch { s = new(); }
        s.path = p;
        return s;
    }

    /// The settings in `json`; new ones when it is null (no file yet). Throws when it is not a settings file.
    public static Settings Parse(string? json) =>
        json == null ? new() : JsonSerializer.Deserialize<Settings>(json) ?? throw new JsonException("settings.json is empty");

    /// The block list in settings.json, for terminal commands that run beside the tray app (see SavedBlocklist).
    public static SavedBlocklist SavedBlocklistIn(string home) =>
        new(Path.Combine(home, "settings.json"), json => Parse(json).ToBlocklist());

    /// In one step, so a terminal `record` or `serve` reading it meanwhile never sees half a file.
    public void Save()
    {
        try { SettingsFile.WriteAtomic(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
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

    /// Turns it on for the AI provider set up now, or off. Turning it on starts with today: the days
    /// before were not asked for.
    public void SetJournalAutoRecap(bool on)
    {
        var ai = Ai();
        if (on == JournalAutoRecapOn(ai)) return;
        JournalAutoRecap = on;
        JournalAutoProvider = on ? Journal.ProviderKey(ai) : null;
        if (on) JournalLastAutoDay = Journal.Key(DateTime.Today.AddDays(-1));
        JournalAutoAttemptDay = null;
        JournalAutoAttempts = 0;
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
