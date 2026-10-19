using System.Security.Cryptography;

namespace OpenRewind;

/// The data key that encrypts recordings, sealed with DPAPI for the current Windows user and kept
/// in ~/.openrewind/encryption-key.dpapi. Only this Windows account on this PC can unseal it, so a
/// copy of the folder, the disk or a backup is useless without the user's sign-in. The Mac app
/// keeps the same key in the Keychain.
static class DataKeyStore
{
    static readonly byte[] Entropy = "OpenRewind data key v1"u8.ToArray();

    static string FilePath(string home) => Path.Combine(home, "encryption-key.dpapi");

    public static bool Exists(string home) => File.Exists(FilePath(home));

    /// The key, or null when there is none or it can't be unsealed (another user or PC, or a reset Windows account).
    public static byte[]? Load(string home)
    {
        var p = FilePath(home);
        if (!File.Exists(p)) return null;
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser);
            return key.Length == 32 ? key : null;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"cannot unseal the encryption key: {e.Message}");
            return null;
        }
    }

    public static void Save(string home, byte[] key)
    {
        Directory.CreateDirectory(home);
        var p = FilePath(home);
        File.WriteAllBytes(p + ".tmp", ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        File.Move(p + ".tmp", p, overwrite: true);
    }
}
