using System.Drawing;
using Microsoft.Win32;

namespace OpenRewind;

/// Takes a screenshot every couple of seconds, skips ones that did not change, pulls out the text, stores it.
sealed class Recorder
{
    readonly Config config;
    readonly Database db;
    readonly HashSet<string> excluded;
    byte[]? lastThumb;
    string lastText = "", lastKey = "";
    DateTime lastWrite = DateTime.MinValue;
    /// Seconds between stored frames while only a few words change. At or below the capture interval: every capture.
    public double MinorChangeGap { get; set; } = 2;
    static volatile bool locked;

    static Recorder()
    {
        // Never record the lock screen.
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) locked = true;
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) locked = false;
        };
    }

    public Recorder(Config config, Database db)
    {
        this.config = config;
        this.db = db;
        // Never recorded at all: the lock screen and the sign-in screen. Apps the user blocks are
        // blurred instead (see Blocklist and Redactor).
        excluded = new HashSet<string> { "lockapp", "logonui", "windows logon user interface host" };
    }

    public async Task Run(CancellationToken ct)
    {
        Log.Write($"recording every {config.Interval}s → {config.Home}");
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try { Tick(); } catch (Exception e) { Log.Write($"capture error: {e.Message}"); }
            var wait = Math.Max(0.2, config.Interval - (DateTime.UtcNow - started).TotalSeconds);
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); } catch (TaskCanceledException) { return; }
        }
    }

    void Tick()
    {
        if (locked || Native.ScreenSaverRunning) return;
        var win = Capture.ActiveWindow();
        if (excluded.Contains(win.AppName.ToLowerInvariant())) return;
        if (win.ExePath != null && excluded.Contains(Path.GetFileNameWithoutExtension(win.ExePath).ToLowerInvariant())) return;
        // Our own timeline is open: nothing new to record.
        if (Capture.IsOwnWindow(win)) return;
        var list = Blocklist.Current;
        var screen = Screen.FromPoint(Cursor.Position);
        var wins = Redactor.Windows(list);
        using var image = Capture.Screenshot(screen);
        Redactor.Apply(image, screen.Bounds, wins);
        // A blocked window in front: keep the app name for the timeline, but not its title or text.
        if (list.BlocksWindow(Blocklist.IdFor(win.ExePath), win.AppName, win.WindowName))
            Ingest(image, win with { WindowName = "" }, readWindowText: false);
        else
            Ingest(image, win);
    }

    /// Dedup, extract text, store snapshot, insert row. Returns the new frame id, or null if skipped.
    public long? Ingest(Bitmap image, ActiveWindow window, bool readWindowText = true)
    {
        var hash = ImageTools.DHash(image);
        var thumb = ImageTools.Thumbnail(image);
        var key = window.AppName + "\u001F" + window.WindowName;
        var heartbeatDue = (DateTime.UtcNow - lastWrite).TotalSeconds >= 60;
        // Same window and (almost) no pixels changed: skip. Still store one frame per minute as a heartbeat.
        if (key == lastKey && lastThumb != null && ImageTools.ChangedPixels(lastThumb, thumb) < config.MinChangedPixels && !heartbeatDue)
            return null;

        var text = "";
        var source = "ocr";
        var url = "";
        if (config.UseAccessibility && readWindowText)
        {
            var w = UiaText.FocusedWindow(window.Hwnd, UiaText.IsBrowser(window.ExePath));
            if (w.Text is string ax) { text = ax; source = "accessibility"; }
            url = w.Url ?? "";
        }
        if (text.Length == 0) text = Ocr.Recognize(image);
        // Pixels moved (cursor blink, animation) but the text is the same: nothing new to search.
        if (key == lastKey && text == lastText && !heartbeatDue)
        {
            lastThumb = thumb;
            return null;
        }
        // Only a few words changed (typing, a clock) and the user chose a longer gap: wait for it.
        // lastThumb stays on the stored frame, so the screen it settles on is still saved once the gap is up.
        if (key == lastKey && !heartbeatDue && MinorChangeGap > config.Interval
            && (DateTime.UtcNow - lastWrite).TotalSeconds < MinorChangeGap && FrameDedup.IsMinorChange(lastText, text))
            return null;

        var ts = Time.Now();
        var dayDir = Path.Combine(config.DataDir, ts[..10]);
        Directory.CreateDirectory(dayDir);
        var file = Path.Combine(dayDir, ts.Replace(':', '-') + ".jpg");
        using (var small = ImageTools.Scaled(image, config.SnapshotScale)) ImageTools.WriteJpeg(small, file);

        var id = db.Insert(new Frame(0, ts, window.AppName, window.WindowName, true, file, text, source, unchecked((long)hash), url));
        lastThumb = thumb; lastText = text; lastKey = key; lastWrite = DateTime.UtcNow;
        Log.Write($"frame {id} {window.AppName} — {text.Length} chars via {source}");
        return id;
    }
}
