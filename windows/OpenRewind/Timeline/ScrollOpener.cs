using System.Runtime.InteropServices;

namespace OpenRewind;

/// Ctrl+Shift + mouse wheel in any app opens the timeline and moves back in time (⌘⇧ + scroll on the Mac,
/// like Rewind). A low-level mouse hook sees the wheel everywhere and needs no extra permission.
/// The hook runs on the UI thread's message loop, so it only posts the work and returns at once.
sealed class ScrollOpener : IDisposable
{
    const int WH_MOUSE_LL = 14, WM_MOUSEWHEEL = 0x020A, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    readonly HookProc proc;  // kept so the delegate is not collected while Windows holds it
    readonly Control ui;
    readonly Func<bool> isOpen;
    readonly Action<int> open;
    IntPtr hook;

    /// `open` gets the wheel delta (120 per notch, positive = away from you = back in time).
    public ScrollOpener(Control ui, Func<bool> isOpen, Action<int> open)
    {
        this.ui = ui;
        this.isOpen = isOpen;
        this.open = open;
        proc = Callback;
    }

    /// Starts or stops the hook.
    public void Enable(bool on)
    {
        if (on && hook == IntPtr.Zero)
        {
            hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) Log.Write($"Ctrl+Shift+scroll: hook not set ({Marshal.GetLastWin32Error()})");
        }
        else if (!on && hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WM_MOUSEWHEEL && Down(VK_CONTROL) && Down(VK_SHIFT) && !Down(VK_MENU) && !isOpen())
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int delta = (short)(info.MouseData >> 16);
            ui.BeginInvoke(() => open(delta));
            return 1;  // the app under the pointer does not also zoom or scroll
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose() => Enable(false);
}
