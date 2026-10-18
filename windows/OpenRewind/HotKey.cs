namespace OpenRewind;

/// System-wide hotkey through RegisterHotKey. Needs no extra permission.
sealed class HotKey : NativeWindow, IDisposable
{
    readonly Action action;
    const int Id = 0x4F52;  // "OR"
    /// False when Windows refused the key, usually because another app already uses it.
    public bool Registered { get; }

    public HotKey(HotkeyShortcut shortcut, Action action)
    {
        this.action = action;
        CreateHandle(new CreateParams());
        Registered = Native.RegisterHotKey(Handle, Id, shortcut.Modifiers | Native.MOD_NOREPEAT, (uint)shortcut.Key);
        if (!Registered) Log.Write($"hotkey {shortcut.Label} registration failed; another app may use it");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam == (IntPtr)Id) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Registered) Native.UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}

/// Small window that waits for the next key combination. Esc cancels.
sealed class ShortcutPicker : Form
{
    public HotkeyShortcut? Picked { get; private set; }
    readonly Label hint;

    public ShortcutPicker(HotkeyShortcut current)
    {
        Text = "Timeline Shortcut";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(400, 150);
        var title = new Label { Text = "Press the new shortcut", Font = new Font("Segoe UI Semibold", 12), AutoSize = true, Location = new Point(20, 18) };
        hint = new Label
        {
            Text = $"Use Ctrl, Alt or Win with a key, for example Ctrl+Alt+T. F-keys work alone.\nNow: {current.Label}",
            AutoSize = false, Location = new Point(20, 50), Size = new Size(360, 44),
        };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(300, 108), Size = new Size(80, 28), TabStop = false };
        Controls.AddRange(new Control[] { title, hint, cancel });
        CancelButton = cancel;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        if (key == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        uint mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= HotkeyShortcut.Ctrl;
        if ((keyData & Keys.Alt) != 0) mods |= HotkeyShortcut.Alt;
        if ((keyData & Keys.Shift) != 0) mods |= HotkeyShortcut.Shift;
        if (Native.GetKeyState(Native.VK_LWIN) < 0 || Native.GetKeyState(Native.VK_RWIN) < 0) mods |= HotkeyShortcut.Win;
        var ch = Native.MapVirtualKey((uint)key, Native.MAPVK_VK_TO_CHAR) & 0x7FFF;
        var sc = HotkeyShortcut.FromPress(mods, (int)key, ch == 0 ? null : (char)ch);
        if (sc != null) { Picked = sc; DialogResult = DialogResult.OK; return true; }
        // Just a modifier so far: keep waiting. Anything else is not a usable hotkey.
        if (key is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)) System.Media.SystemSounds.Beep.Play();
        return true;
    }
}
