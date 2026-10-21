using System.Drawing;

namespace OpenRewind;

/// What the Settings window reads and changes. Each change goes through the same code the tray menu
/// used, so Windows Hello prompts, confirmations and logging stay the same.
sealed partial class TrayApp
{
    internal Settings AppSettings => settings;
    internal Database Db => engine.Db;

    // General
    internal static bool LaunchAtLogin => Settings.LaunchAtLogin;
    internal static void ToggleLaunchAtLogin()
    {
        try { Settings.LaunchAtLogin = !Settings.LaunchAtLogin; } catch (Exception e) { Log.Write($"launch at login failed: {e.Message}"); }
    }
    internal bool AutoDownload { get => updater.AutoDownload; set => updater.AutoDownload = value; }
    internal void CheckUpdatesNow() => CheckForUpdates();
    internal void OpenTutorial() => ShowTutorial();
    internal void OpenSetupAgain() => ShowSetup();
    internal static void OpenTarget(string target) => Open(target);
    internal string Look
    {
        get => settings.TimelineLook;
        set
        {
            settings.TimelineLook = value;
            overlay.Look = value;
            settings.Save();
            Log.Write($"timeline look set to {value}");
        }
    }

    // Screen
    /// While paused, the switches show and change what comes back on after the pause.
    internal bool RecordsScreen => PausedUntil != null ? settings.RecordScreen : engine.ScreenOn;
    internal void ToggleRecordScreen() => ToggleScreen();
    internal bool NoOcr => !Ocr.Available && !engine.Config.UseAccessibility;
    internal int ScreenshotPercent
    {
        get => ScreenshotSizes.Nearest(settings.ScreenshotPercent);
        set
        {
            settings.ScreenshotPercent = value;
            engine.Config.SnapshotScale = ScreenshotSizes.Scale(value);
            settings.Save();
            Log.Write($"screenshot resolution set to {value}%");
        }
    }
    internal static string ScreenshotTitle(int pct)
    {
        var screen = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        return ScreenshotSizes.Title(pct, screen.Width, screen.Height);
    }
    internal int TypingGap
    {
        get => settings.TypingFrameGap;
        set
        {
            settings.TypingFrameGap = value;
            engine.Recorder.MinorChangeGap = value;
            settings.Save();
        }
    }
    internal bool MeaningOn => settings.SearchByMeaning;
    internal void ToggleSearchByMeaning() => ToggleMeaning();
    internal string MeaningLine => meaning.StatusLine();
    internal bool MeaningFailed => meaning.State == MeaningController.Phase.Failed;
    internal void RetryMeaning() => meaning.Retry();

    // Audio
    internal bool RecordsAudio => PausedUntil != null ? settings.RecordAudio : engine.AudioOn;
    internal void ToggleRecordAudio() => ToggleAudio();
    internal bool TranscriberRunning => engine.Transcriber != null;
    internal List<string> TranscriptionLines => TranscriptionStatus();
    internal string Home => engine.Config.Home;
    internal void UseTranscriptionModel(SpeechModel m) => PickModel(m);
    internal void RetranscribeRecent(string what, TimeSpan span) => Retranscribe(what, span);
    internal static void OpenWhisperHelp() => Open(WhisperHelp);
    internal static void OpenModelsHelp() => Open(ModelsHelp);

    // Meetings
    internal void ToggleSummaries() => ToggleMeetingSummaries();

    // AI
    internal bool AutoRecapOn => settings.JournalAutoRecapOn(settings.Ai());
    internal void ToggleDailyRecap()
    {
        settings.SetJournalAutoRecap(!settings.JournalAutoRecapOn(settings.Ai()));
        JournalForm.Reload();
    }
    internal void ToggleAiApps() => ToggleMcp();
    internal void ConnectAiApps() => ShowConnectAiApps();

    // Security
    internal bool ServerOn => engine.ServerOn;
    internal int ServerPort => engine.Config.Port;
    internal void ToggleLocalServer() => ToggleServer();
    internal bool EncryptionOn => encryption.On;
    internal string EncryptionStatus => encryption.Status();
    internal int EncryptionFailed => VaultMigration.Running ? 0 : encryption.Failed;
    internal bool HasRecoveryKey => encryption.HasKey;
    internal void ToggleEncrypt()
    {
        if (!encryption.On) encryption.TurnOn();
        else appLock.Run("Unlock OpenRewind to stop encrypting recordings", encryption.TurnOff);
    }
    internal void StopRetryingFiles() => encryption.SkipFailed();
    internal void SaveRecoveryKey() => appLock.Run("Unlock OpenRewind to save the recovery key", () => encryption.ExportKey());
    internal bool AppLockOn => settings.RequireUnlock;
    internal void ToggleLock() => ToggleAppLock();
    internal int LockMinutes
    {
        get => settings.LockAfterMinutes;
        set { settings.LockAfterMinutes = value; settings.Save(); }
    }

    // Storage
    internal List<string> StorageSummary => StorageLines();
    internal void MeasureDataFolder() => MeasureStorage();
    internal void ShowDataFolder() => appLock.Run("Unlock OpenRewind to open its data folder", () => Open(engine.Config.Home));
    internal KeepFor KeepScreen => settings.Screen;
    internal KeepFor KeepAudio => settings.Audio;
    /// Deleting is behind App Lock too: someone at the PC should not be able to wipe the history.
    internal void SetKeepFor(bool screen, KeepFor k) =>
        appLock.Run("Unlock OpenRewind to change how long recordings are kept", () => SetKeep(screen, k));
    internal bool CompressOn => settings.CompressOlderRecordings;
    internal string? CompressUnavailable => video.Unavailable;
    internal void ToggleCompression()
    {
        settings.CompressOlderRecordings = !settings.CompressOlderRecordings;
        video.Enabled = settings.CompressOlderRecordings;
        settings.Save();
        Log.Write($"compress older recordings {(video.Enabled ? "on" : "off")}");
        if (video.Enabled) video.Kick();
    }
    const string UnlockDelete = "Unlock OpenRewind to delete recordings";
    internal void DeleteRecordings(string what, DateTime from) => appLock.Run(UnlockDelete, () => DeleteRecent(what, from));
    internal void DeleteAllRecordings(bool screen, bool audio) => appLock.Run(UnlockDelete, () => DeleteAll(screen, audio));

    // Shortcuts
    internal HotkeyShortcut TimelineShortcut => Shortcut;
    internal void RecordTimelineShortcut() => ChangeShortcut();
    internal void RestoreTimelineShortcut() => SetShortcut(HotkeyShortcut.Standard);
    /// Hidden apps were shown again in Settings → Privacy: the open timeline picks them up.
    internal void TimelineHiddenChanged() => timeline.HiddenChanged();
    internal bool ScrollOpensTimeline => settings.ScrollOpensTimeline;
    internal void ToggleScrollOpensTimeline()
    {
        settings.ScrollOpensTimeline = !settings.ScrollOpensTimeline;
        settings.Save();
        scrollOpener.Enable(settings.ScrollOpensTimeline);
    }
}
