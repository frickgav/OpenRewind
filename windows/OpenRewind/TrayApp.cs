using System.Diagnostics;
using System.Drawing;

namespace OpenRewind;

/// Tray app: icon with separate screen / audio switches and the timeline overlay.
/// Same menu as the Mac menu bar app.
sealed class TrayApp : ApplicationContext
{
    readonly Engine engine;
    readonly Settings settings;
    readonly NotifyIcon icon;
    readonly ContextMenuStrip menu = new();
    readonly OverlayForm overlay;
    readonly TimelineModel timeline;
    readonly HotKey hotKey;
    readonly RetentionJob retention;
    /// Last measured size of the data folder; measured in the background when the menu opens.
    long? dataSize;
    bool measuring;

    public TrayApp(Engine engine)
    {
        this.engine = engine;
        settings = Settings.Load(engine.Config.Home);
        Blocklist.Current = settings.ToBlocklist();
        timeline = new TimelineModel(engine.Db);
        overlay = new OverlayForm(timeline);
        _ = overlay.Handle;  // create it on the UI thread now

        if (settings.LocalSearchServer) StartServer();
        engine.StartTranscriber();
        engine.SetScreen(settings.RecordScreen);
        engine.SetAudio(settings.RecordAudio);
        retention = new RetentionJob(engine.Db, settings.Screen, settings.Audio);
        retention.Start();

        // Ctrl+Alt+T opens the timeline from anywhere (Option+Command+T on the Mac).
        hotKey = new HotKey(Native.MOD_CONTROL | Native.MOD_ALT, Keys.T, overlay.Toggle);

        menu.Opening += (_, _) => BuildMenu();
        icon = new NotifyIcon { Icon = Program.AppIcon, ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) overlay.Toggle(); };
        BuildMenu();
        UpdateIcon();
        if (!settings.OnboardingDone) ShowSetup();
    }

    /// The first-run setup; ends in the guided tour if the user asks for it.
    void ShowSetup()
    {
        if (setup is { IsDisposed: false }) { setup.Activate(); return; }
        setup = new OnboardingForm(settings, tour => { if (tour) ShowTutorial(); });
        setup.Show();
    }
    OnboardingForm? setup;

    void ShowTutorial() => TutorialCoach.Start(overlay, timeline);

    // Build the menu fresh each time it opens, so it always shows the live state.
    void BuildMenu()
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(StatusLine()) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Toggle("Record Screen", engine.ScreenOn, ToggleScreen));
        if (engine.ScreenOn && !Ocr.Available && !engine.Config.UseAccessibility)
            menu.Items.Add(new ToolStripMenuItem("   No Windows OCR language installed") { Enabled = false });
        menu.Items.Add(Toggle("Record Audio", engine.AudioOn, ToggleAudio));
        if (engine.AudioOn && engine.Transcriber == null)
            menu.Items.Add(Item("   Transcription off: set up Whisper…", () => Open("https://github.com/frickgav/openscreen/blob/main/windows/README.md#whisper")));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Open Timeline", null, (_, _) => overlay.ShowOverlay()) { ShortcutKeyDisplayString = "Ctrl+Alt+T" });
        // The browser page when the local server is on, else the timeline's own search box.
        menu.Items.Add(Item("Search…", () => { if (engine.ServerOn) Open($"http://127.0.0.1:{engine.Config.Port}/"); else overlay.ShowSearch(); }));
        menu.Items.Add(Item("Show Data Folder", () => Open(engine.Config.Home)));
        foreach (var line in StorageLines()) menu.Items.Add(new ToolStripMenuItem("   " + line) { Enabled = false });
        MeasureStorage();
        menu.Items.Add(new ToolStripSeparator());

        var s = new ToolStripMenuItem("Settings");
        s.DropDownItems.Add(Item("Blocked Apps & Sites…", () => BlockedAppsForm.ShowFor(settings)));
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(KeepMenu("Keep Screen Recordings For", screen: true));
        s.DropDownItems.Add(KeepMenu("Keep Audio Recordings For", screen: false));
        s.DropDownItems.Add(new ToolStripSeparator());
        var del = new ToolStripMenuItem("Delete Recordings");
        del.DropDownItems.Add(Item("Last 15 Minutes", () => DeleteRecent("last 15 minutes", DateTime.UtcNow.AddMinutes(-15))));
        del.DropDownItems.Add(Item("Last Hour", () => DeleteRecent("last hour", DateTime.UtcNow.AddHours(-1))));
        del.DropDownItems.Add(Item("Today", () => DeleteRecent("today", DateTime.Today.ToUniversalTime())));
        del.DropDownItems.Add(new ToolStripSeparator());
        del.DropDownItems.Add(Item("All Screen Recordings…", () => DeleteAll(screen: true, audio: false)));
        del.DropDownItems.Add(Item("All Audio Recordings…", () => DeleteAll(screen: false, audio: true)));
        del.DropDownItems.Add(Item("Everything…", () => DeleteAll(screen: true, audio: true)));
        s.DropDownItems.Add(del);
        s.DropDownItems.Add(new ToolStripSeparator());
        var server = Toggle($"Local Search Server (127.0.0.1:{engine.Config.Port})", engine.ServerOn, ToggleServer);
        server.ToolTipText = "Search in the browser and let local scripts read your history through the API. Only this PC can connect.";
        s.DropDownItems.Add(server);
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(Toggle("Launch at Login", Settings.LaunchAtLogin, () =>
        {
            try { Settings.LaunchAtLogin = !Settings.LaunchAtLogin; } catch (Exception e) { Log.Write($"launch at login failed: {e.Message}"); }
        }));
        s.DropDownItems.Add(new ToolStripSeparator());
        s.DropDownItems.Add(Item("Show Tutorial", ShowTutorial));
        s.DropDownItems.Add(Item("Run Setup Again…", ShowSetup));
        menu.Items.Add(s);
        menu.Items.Add(Item("Quit OpenRewind", Quit));
    }

    static ToolStripMenuItem Item(string title, Action act) => new(title, null, (_, _) => act());

    static ToolStripMenuItem Toggle(string title, bool on, Action act) => new(title, null, (_, _) => act()) { Checked = on };

    ToolStripMenuItem KeepMenu(string title, bool screen)
    {
        var item = new ToolStripMenuItem(title);
        var current = screen ? settings.Screen : settings.Audio;
        foreach (var k in Enum.GetValues<KeepFor>())
            item.DropDownItems.Add(Toggle(k.Title(), k == current, () => SetKeep(screen, k)));
        return item;
    }

    void SetKeep(bool screen, KeepFor k)
    {
        var what = screen ? "screenshots" : "audio clips";
        var old = screen ? settings.Screen : settings.Audio;
        if (k.Seconds() is double secs && (old.Seconds() ?? double.PositiveInfinity) > secs)
        {
            if (!Confirm($"Keep {what} for {k.Title().ToLower()}?",
                         $"OpenRewind will delete {what} older than {k.Title().ToLower()} now, and keep doing it every hour. This cannot be undone.")) return;
        }
        if (screen) { settings.KeepScreen = k.Raw(); retention.ScreenKeep = k; }
        else { settings.KeepAudio = k.Raw(); retention.AudioKeep = k; }
        settings.Save();
        retention.RunNow();
    }

    void DeleteRecent(string what, DateTime from)
    {
        if (!Confirm($"Delete {what} of recordings?", $"Screenshots and audio from {what} will be deleted. This cannot be undone.")) return;
        var db = engine.Db;
        var now = DateTime.UtcNow;
        Task.Run(() =>
        {
            int f = db.DeleteFrames(from, now), a = db.DeleteAudio(from, now);
            Log.Write($"deleted {f} screenshots, {a} audio clips ({what})");
        });
    }

    void DeleteAll(bool screen, bool audio)
    {
        var what = screen && audio ? "all screenshots and audio" : screen ? "all screenshots" : "all audio recordings";
        if (!Confirm($"Delete {what}?", $"This removes {what} from this PC. This cannot be undone.")) return;
        var db = engine.Db;
        var now = DateTime.UtcNow;
        Task.Run(() =>
        {
            int f = screen ? db.DeleteFrames(null, now) : 0;
            int a = audio ? db.DeleteAudio(null, now) : 0;
            db.Compact();
            Log.Write($"deleted {f} screenshots, {a} audio clips (all)");
        });
    }

    static bool Confirm(string title, string text) =>
        MessageBox.Show(text, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    // MARK: storage

    List<string> StorageLines()
    {
        var size = dataSize is long n ? FormatBytes(n) : "measuring…";
        var lines = new List<string> { $"Using {size}" };
        if (engine.Db.OldestRecording() is DateTime oldest)
        {
            var local = oldest.ToLocalTime();
            lines.Add($"Goes back to {local:g} ({Ago(oldest)})");
        }
        else lines.Add("No recordings yet");
        return lines;
    }

    static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - utc;
        if (s.TotalDays >= 2) return $"{(int)s.TotalDays} days ago";
        if (s.TotalHours >= 2) return $"{(int)s.TotalHours} hours ago";
        return $"{Math.Max(1, (int)s.TotalMinutes)} minutes ago";
    }

    static string FormatBytes(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024} KB";

    /// Measure the folder off the UI thread, then refresh the open menu with the result.
    void MeasureStorage()
    {
        if (measuring) return;
        measuring = true;
        var home = engine.Config.Home;
        Task.Run(() =>
        {
            var size = Database.FolderSize(home);
            menu.BeginInvoke(() =>
            {
                measuring = false;
                var changed = dataSize != size;
                dataSize = size;
                if (changed && menu.Items.OfType<ToolStripItem>().FirstOrDefault(i => i.Text?.StartsWith("   Using ") == true) is ToolStripItem item)
                    item.Text = "   " + StorageLines()[0];
            });
        });
    }

    string StatusLine()
    {
        var s = engine.Db.Stats();
        var a = engine.Db.AudioStats();
        return $"{(engine.ScreenOn ? "Screen on" : "Screen off")} · {(engine.AudioOn ? "Audio on" : "Audio off")}  —  {s.Count} frames, {a.Segments} audio";
    }

    void UpdateIcon()
    {
        // NotifyIcon tooltips are limited to 127 characters.
        var tip = $"OpenRewind — {(engine.ScreenOn ? "Screen on" : "Screen off")} · {(engine.AudioOn ? "Audio on" : "Audio off")}";
        icon.Text = tip.Length > 127 ? tip[..127] : tip;
        icon.Icon = engine.ScreenOn || engine.AudioOn ? Program.AppIcon : Program.PausedIcon;
    }

    void ToggleServer()
    {
        if (engine.ServerOn) engine.StopServer(); else StartServer();
        settings.LocalSearchServer = engine.ServerOn;
        settings.Save();
    }

    void StartServer()
    {
        try { engine.StartServer(); }
        catch (Exception e) { Log.Write($"cannot start server: {e.Message}"); }
    }

    void ToggleScreen()
    {
        var on = !engine.ScreenOn;
        engine.SetScreen(on);
        settings.RecordScreen = on;
        settings.Save();
        UpdateIcon();
    }

    void ToggleAudio()
    {
        var on = !engine.AudioOn;
        engine.SetAudio(on);
        settings.RecordAudio = on;
        settings.Save();
        UpdateIcon();
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"cannot open {target}: {e.Message}"); }
    }

    void Quit()
    {
        icon.Visible = false;
        overlay.HideOverlay();
        hotKey.Dispose();
        retention.Dispose();
        engine.Shutdown();
        ExitThread();
    }
}
