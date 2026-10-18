using Microsoft.Win32;

namespace OpenRewind;

/// The Windows privacy switches OpenRewind needs. Screenshots and window text need none on Windows;
/// only the microphone has a switch (Settings → Privacy & security → Microphone).
static class Permissions
{
    const string Mic = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// False when any of the three microphone switches is off: for this PC, for this user, or for desktop apps.
    public static bool MicrophoneOn =>
        !Denied(Registry.LocalMachine, Mic) && !Denied(Registry.CurrentUser, Mic) && !Denied(Registry.CurrentUser, Mic + @"\NonPackaged");

    public const string MicrophoneSettings = "ms-settings:privacy-microphone";

    static bool Denied(RegistryKey root, string path)
    {
        try
        {
            using var k = root.OpenSubKey(path);
            return k?.GetValue("Value") is string v && v.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
