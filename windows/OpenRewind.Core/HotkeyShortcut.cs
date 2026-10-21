namespace OpenRewind;

/// A key combination for the timeline hotkey (Settings → Timeline Shortcut).
/// Modifiers are RegisterHotKey flags, Key a Windows virtual-key code, Name what the key shows ("T", "F5").
public record HotkeyShortcut(uint Modifiers, int Key, string Name)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    /// Ctrl+Alt+T (Option+Command+T on the Mac).
    public static readonly HotkeyShortcut Standard = new(Ctrl | Alt, 0x54, "T");

    /// Star Moment from anywhere: Ctrl+Alt+S (Option+Command+S on the Mac; Rewind's Shift+Command+S is Save As in most apps).
    public static readonly HotkeyShortcut Star = new(Ctrl | Alt, 0x53, "S");

    /// Text like "Ctrl+Alt+T", in the order Windows writes it.
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Win) != 0) parts.Add("Win");
            if ((Modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            parts.Add(Name);
            return string.Join("+", parts);
        }
    }

    /// Null when the press is not a usable hotkey: it needs Ctrl, Alt or Win, except for F-keys.
    /// A bare modifier (still holding Ctrl) is null too. `typed` is the character the key makes,
    /// for keys like ; or [ whose name depends on the keyboard layout.
    public static HotkeyShortcut? FromPress(uint modifiers, int key, char? typed = null)
    {
        modifiers &= Alt | Ctrl | Shift | Win;
        var name = KeyName(key, typed);
        if (name == null) return null;
        bool fkey = key is >= 0x70 and <= 0x87;
        if ((modifiers & (Alt | Ctrl | Win)) == 0 && !fkey) return null;
        return new(modifiers, key, name);
    }

    static string? KeyName(int vk, char? typed)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        if (vk is >= 0x60 and <= 0x69) return $"Num {vk - 0x60}";
        var named = vk switch
        {
            0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
            0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
            0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down", 0x13 => "Pause", 0x2C => "PrintScreen",
            _ => null,
        };
        if (named != null) return named;
        // Modifiers on their own, Escape (cancels the picker) and the Windows keys are never the key.
        if (vk is 0x10 or 0x11 or 0x12 or 0x1B or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return null;
        return typed is char c && !char.IsControl(c) && !char.IsWhiteSpace(c) ? char.ToUpperInvariant(c).ToString() : null;
    }
}
