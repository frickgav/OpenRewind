using System.Security.Cryptography;

namespace OpenRewind;

/// The data key that encrypts recordings, sealed with DPAPI for the current Windows user and kept
/// in ~/.openrewind/encryption-key.dpapi. Only this Windows account on this PC can unseal it, so a
/// copy of the folder, the disk or a backup is useless without the user's sign-in. The Mac app
/// keeps the same key in the Keychain.
///
/// The stored key is the only copy besides the recovery key, so it is never overwritten: a new
/// one is written only when there is none, and replacing one (a recovery key typed at launch)
/// keeps the old file as encryption-key.dpapi.&lt;time&gt;.bak.
static class DataKeyStore
{
    static readonly byte[] Entropy = "OpenRewind data key v1"u8.ToArray();

    public enum State { Found, Missing, Unreadable }

    static string FilePath(string home) => Path.Combine(home, "encryption-key.dpapi");

    public static bool Exists(string home) => File.Exists(FilePath(home));

    /// The key and whether there is one. `Unreadable` means a file is there but can't be unsealed
    /// (another user or PC, a reset Windows password, a damaged file): never treat that as "no key".
    public static (byte[]? Key, State State) Read(string home)
    {
        var p = FilePath(home);
        if (!File.Exists(p)) return (null, State.Missing);
        try
        {
            var key = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.CurrentUser);
            if (key.Length == 32) return (key, State.Found);
            Log.Write("the stored encryption key has the wrong length");
            return (null, State.Unreadable);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Write($"cannot unseal the encryption key: {e.Message}");
            return (null, State.Unreadable);
        }
    }

    public static byte[]? Load(string home) => Read(home).Key;

    /// Save a key. Refuses to replace an existing file unless `replace` (then the old one is kept as a .bak).
    public static void Save(string home, byte[] key, bool replace = false)
    {
        Directory.CreateDirectory(home);
        var p = FilePath(home);
        if (File.Exists(p))
        {
            if (!replace) throw new IOException("an encryption key is already stored; it was not replaced");
            File.Copy(p, $"{p}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.bak", overwrite: false);
        }
        var sealed_ = ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser);
        using (var f = new FileStream(p + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(sealed_);
            f.Flush(true);
        }
        File.Move(p + ".tmp", p, overwrite: true);
    }
}
