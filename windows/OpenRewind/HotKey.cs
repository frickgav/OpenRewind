namespace OpenRewind;

/// System-wide hotkey through RegisterHotKey. Needs no extra permission.
sealed class HotKey : NativeWindow, IDisposable
{
    readonly Action action;
    const int Id = 0x4F52;  // "OR"

    public HotKey(uint modifiers, Keys key, Action action)
    {
        this.action = action;
        CreateHandle(new CreateParams());
        if (!Native.RegisterHotKey(Handle, Id, modifiers | Native.MOD_NOREPEAT, (uint)key))
            Log.Write("hotkey registration failed; another app may use it");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam == (IntPtr)Id) action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Native.UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
}
