using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Windows.Security.Credentials.UI;

namespace OpenRewind;

/// Settings → Encryption & Lock → Require Windows Hello to Open Timeline. The timeline, search,
/// Ask AI and the windows that show your history ask for Windows Hello (face, fingerprint or PIN)
/// first. An unlock lasts until the PC locks or sleeps, or until the chosen time has passed with
/// none of them in use. Recording keeps going while locked. Same as AppLock.swift on the Mac.
///
/// This guards against someone using the PC while you are away from it. It is not a wall against
/// programs running as you: they could edit settings.json or run OpenRewind.exe serve themselves.
/// When Windows Hello is not set up (any more), the Windows password works instead.
sealed class AppLock
{
    readonly Settings settings;
    /// When a guarded window was last opened or used after an unlock (UTC ticks); 0 = locked.
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

    /// Called every few seconds by the tray. A guarded window in use keeps the unlock going;
    /// once the time has passed with none in use, everything that shows history closes.
    public void Tick(bool historyInUse)
    {
        if (!Enabled || Interlocked.Read(ref lastUsed) == 0) return;
        if (IsUnlocked) { if (historyInUse) Touch(); }
        else Lock();
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

    /// Show the Windows Hello prompt, or the Windows password prompt when Hello is not set up.
    /// A tray app has no window of its own, so the prompt gets a see-through one under the mouse
    /// to belong to; without it Windows may open it behind other windows.
    static async Task<bool> Verify(string reason)
    {
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
            if (!await Available())
            {
                Log.Write("Windows Hello is not set up; asking for the Windows password");
                return WindowsPassword.Verify(owner.Handle, "OpenRewind is locked", reason + "\n\nWindows Hello is not set up, so enter your Windows password.");
            }
            var r = await UserConsentVerifierInterop.RequestVerificationForWindowAsync(owner.Handle, reason);
            if (r != UserConsentVerificationResult.Verified) Log.Write($"unlock not given ({r})");
            return r == UserConsentVerificationResult.Verified;
        }
        catch (Exception e)
        {
            Log.Write($"unlock failed: {e.Message}");
            return false;
        }
        finally { owner.Close(); }
    }
}

/// The Windows credential prompt for the signed-in account, checked with LogonUser. Used when
/// Windows Hello is not set up, so a lock that was turned on can always be opened or turned off.
static class WindowsPassword
{
    public static bool Verify(IntPtr owner, string caption, string message)
    {
        var info = new CREDUI_INFO { cbSize = Marshal.SizeOf<CREDUI_INFO>(), hwndParent = owner, pszCaptionText = caption, pszMessageText = message };
        uint package = 0;
        var save = false;
        var err = CredUIPromptForWindowsCredentials(ref info, 0, ref package, IntPtr.Zero, 0, out var buf, out var size, ref save, CREDUIWIN_ENUMERATE_CURRENT_USER);
        if (err != 0) return false;  // cancelled
        var user = new System.Text.StringBuilder(513);
        var domain = new System.Text.StringBuilder(338);
        var pass = new System.Text.StringBuilder(256);
        int userLen = user.Capacity, domainLen = domain.Capacity, passLen = pass.Capacity;
        try
        {
            if (!CredUnPackAuthenticationBuffer(CRED_PACK_PROTECTED_CREDENTIALS, buf, size, user, ref userLen, domain, ref domainLen, pass, ref passLen))
                return false;
            var name = user.ToString();
            var dom = domain.ToString();
            if (dom.Length == 0 && name.Contains('\\')) { dom = name[..name.IndexOf('\\')]; name = name[(name.IndexOf('\\') + 1)..]; }
            if (!LogonUser(name, dom.Length == 0 ? null : dom, pass.ToString(), LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out var token))
            {
                Log.Write("Windows password not accepted");
                return false;
            }
            try
            {
                // Only the signed-in account may unlock, not any account on the PC.
                using var who = new WindowsIdentity(token);
                return who.User != null && who.User == WindowsIdentity.GetCurrent().User;
            }
            finally { CloseHandle(token); }
        }
        finally
        {
            pass.Clear();
            ZeroAndFree(buf, size);
        }
    }

    static void ZeroAndFree(IntPtr buf, uint size)
    {
        if (buf == IntPtr.Zero) return;
        for (var i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
        CoTaskMemFree(buf);
    }

    const int CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200;
    const int CRED_PACK_PROTECTED_CREDENTIALS = 0x1;
    const int LOGON32_LOGON_INTERACTIVE = 2;
    const int LOGON32_PROVIDER_DEFAULT = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    static extern int CredUIPromptForWindowsCredentials(ref CREDUI_INFO info, int authError, ref uint authPackage, IntPtr inBuffer,
                                                        uint inBufferSize, out IntPtr outBuffer, out uint outBufferSize, ref bool save, int flags);

    [DllImport("credui.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredUnPackAuthenticationBuffer(int flags, IntPtr buffer, uint size, System.Text.StringBuilder user, ref int userLen,
                                                      System.Text.StringBuilder domain, ref int domainLen, System.Text.StringBuilder password, ref int passwordLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LogonUser(string user, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    static extern void CoTaskMemFree(IntPtr ptr);
}
