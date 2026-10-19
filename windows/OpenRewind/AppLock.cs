using Microsoft.Win32;
using Windows.Security.Credentials.UI;

namespace OpenRewind;

/// Settings → Encryption & Lock → Require Windows Hello to Open Timeline. The timeline, search,
/// Ask AI and the windows that show your history ask for Windows Hello (face, fingerprint or PIN)
/// first. An unlock lasts until the PC locks or sleeps, or until the chosen time has passed since
/// any of them was last used. Recording keeps going while locked. Same as AppLock.swift on the Mac.
sealed class AppLock
{
    readonly Settings settings;
    /// When a guarded window was last opened after an unlock (UTC ticks); 0 = locked.
    long lastUsed;
    bool asking;

    /// The lock closed: hide everything that shows history. May arrive on any thread.
    public event Action? Locked;

    public AppLock(Settings settings)
    {
        this.settings = settings;
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) Lock();
        };
        SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == PowerModes.Suspend) Lock(); };
    }

    public bool Enabled => settings.RequireUnlock;

    /// Minutes an unlock lasts; 0 = until the PC locks.
    public static readonly (int Minutes, string Title)[] TimeoutChoices =
        { (5, "5 Minutes"), (15, "15 Minutes"), (60, "1 Hour"), (0, "Only When the PC Locks") };

    /// Also read by the local search server's thread.
    public bool IsUnlocked
    {
        get
        {
            if (!Enabled) return true;
            var t = Interlocked.Read(ref lastUsed);
            if (t == 0) return false;
            var mins = settings.LockAfterMinutes;
            return mins <= 0 || DateTime.UtcNow.Ticks - t < TimeSpan.FromMinutes(mins).Ticks;
        }
    }

    /// Forget an earlier unlock without closing anything (turning the lock on asks once right away).
    public void Forget() => Interlocked.Exchange(ref lastUsed, 0);

    public void Lock()
    {
        Interlocked.Exchange(ref lastUsed, 0);
        if (Enabled) Locked?.Invoke();
    }

    /// Run `action` once the user has unlocked (right away when the lock is off or still open).
    public async void Run(string reason, Action action)
    {
        if (await Unlock(reason)) action();
    }

    public async Task<bool> Unlock(string reason)
    {
        if (IsUnlocked) { Touch(); return true; }
        if (asking) return false;
        asking = true;
        try
        {
            if (!await Verify(reason)) return false;
            Touch();
            return true;
        }
        finally { asking = false; }
    }

    void Touch() { if (Enabled) Interlocked.Exchange(ref lastUsed, DateTime.UtcNow.Ticks); }

    /// Windows Hello can be used on this PC (it needs at least a PIN set up).
    public static async Task<bool> Available()
    {
        try { return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available; }
        catch (Exception e) { Log.Write($"cannot check Windows Hello: {e.Message}"); return false; }
    }

    public const string SetUpHello = "Turn on Windows Hello first (a PIN is enough): Settings → Accounts → Sign-in options.";

    /// Show the Windows Hello prompt. A tray app has no window of its own, so the prompt gets a
    /// see-through one under the mouse to belong to; without it Windows may open it behind other windows.
    static async Task<bool> Verify(string reason)
    {
        if (!await Available())
        {
            MessageBox.Show($"OpenRewind is set to ask for Windows Hello, but Windows Hello is not set up on this PC.\n\n{SetUpHello}",
                            "OpenRewind is locked", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        using var owner = new Form
        {
            FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true, Opacity = 0.01,
            StartPosition = FormStartPosition.Manual, Size = new Size(1, 1),
            Location = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2),
        };
        owner.Show();
        owner.Activate();
        try
        {
            var r = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner.Handle, reason);
            if (r != UserConsentVerificationResult.Verified) Log.Write($"unlock not given ({r})");
            return r == UserConsentVerificationResult.Verified;
        }
        catch (Exception e)
        {
            Log.Write($"Windows Hello failed: {e.Message}");
            return false;
        }
        finally { owner.Close(); }
    }
}
