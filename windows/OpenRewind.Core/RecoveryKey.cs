using System.Security.Cryptography;
using System.Text;

namespace OpenRewind;

/// The data key written out for the user to keep: 32 bytes and a 4-byte check, as 72 hex digits
/// in groups of 8. The same text works in the Mac and the Windows app.
public static class RecoveryKey
{
    public static string Text(byte[] key)
    {
        var hex = Convert.ToHexString(key.Concat(RecordingFile.KeyId(key)).ToArray());
        return string.Join("-", Enumerable.Range(0, hex.Length / 8).Select(i => hex.Substring(i * 8, 8)));
    }

    /// The file "Export Recovery Key…" saves.
    public static string File(byte[] key, DateTime now) => $"""
        OpenRewind recovery key

        {Text(key)}

        This key opens your encrypted OpenRewind recordings (screenshots, audio and the database).
        Keep it somewhere safe and private, like a password manager.
        - If this computer's {(OperatingSystem.IsWindows() ? "Windows account" : "Keychain")} is lost or reset, or you move your recordings
          to another computer, OpenRewind asks for this key. Without it the recordings cannot be opened.
        - Anyone with this key and a copy of your recordings can read them.

        Saved {now:yyyy-MM-dd}.
        """.Replace("\r\n", "\n");

    /// The key in pasted text or a saved file, or null if there is none or a digit is wrong.
    /// Spaces, dashes and line breaks are ignored.
    public static byte[]? Parse(string text)
    {
        foreach (var line in text.Split('\n').Prepend(text))
        {
            var hex = new StringBuilder();
            foreach (var c in line) if (Uri.IsHexDigit(c)) hex.Append(c); else if (c is not ('-' or ' ' or '\t' or '\r' or '\n')) { hex.Clear(); break; }
            if (hex.Length != 72) continue;
            var raw = System.Convert.FromHexString(hex.ToString());
            var key = raw[..32];
            if (CryptographicOperations.FixedTimeEquals(raw[32..], RecordingFile.KeyId(key))) return key;
        }
        return null;
    }
}
