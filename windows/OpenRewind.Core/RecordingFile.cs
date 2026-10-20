using System.Security.Cryptography;
using System.Text;

namespace OpenRewind;

/// Every screenshot and audio clip is read and written through here, so encrypted and plain
/// files can sit side by side. Read() is the one loader: anything that opens a recording
/// (timeline, search, API, Ask AI, MCP, Whisper, new features) calls it instead of File.ReadAllBytes.
///
/// Encrypted file (AES-256-GCM, the same on Mac and Windows, RecordingFile.swift):
///   "ORENC" | version 2 | key id (4 bytes) | nonce (12 bytes) | ciphertext | tag (16 bytes)
/// The key id is the first 4 bytes of SHA-256(master key). The cipher key is
/// HKDF-SHA256(master, salt "OpenRewind", info "openrewind/files/v2"); the database gets its own
/// key from the same master (Database.DbKey). The authenticated data is the 22-byte header followed
/// by the file's place in the data folder (Identity, e.g. "2026-10-19/14-30-05.jpg"), so a file
/// moved, swapped with another or put in another's place does not open.
/// Plain files (JPEG, HEIC, WAV) never start with "ORENC".
public static class RecordingFile
{
    static readonly byte[] Magic = "ORENC"u8.ToArray();
    public const byte Version = 2;
    public const int HeaderSize = 5 + 1 + 4 + 12;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + TagSize;
    /// Half-written files (a conversion or a write that was cut short) end in this.
    public const string TempSuffix = ".orenc-tmp";
    /// Decrypted copies for whisper-cli in the temp folder start with this (see PlainCopy, CleanTempCopies).
    public const string TempCopyPrefix = "openrewind-";

    static readonly byte[] Salt = "OpenRewind"u8.ToArray();

    /// The master data key, once loaded from DPAPI or a recovery key. Null = none.
    /// It stays in memory for the life of the process (recording needs it in the background); under
    /// the threat model, a program running as the user could read it anyway, so it is not wiped.
    public static volatile byte[]? Key;
    /// Encrypt new recordings (Settings → Encrypt Recordings).
    public static volatile bool EncryptNew;
    /// Loads the key on demand (the app sets this to read DPAPI) when an encrypted file turns up and no key is loaded.
    public static Func<byte[]?>? KeyLoader;
    static long lastLoadTry;

    /// Held while a file is converted or deleted, so retention never races a conversion.
    public static readonly object FileGate = new();

    // MARK: the loader

    /// The recording's bytes, decrypted if needed. Throws if the file is missing, damaged, moved, or
    /// encrypted with a key we don't have.
    public static byte[] Read(string path) => Read(path, CurrentKey(path));

    public static byte[] Read(string path, byte[]? key) => Open(File.ReadAllBytes(path), key, Identity(path));

    /// Read() for callers that just show nothing on failure.
    public static byte[]? TryRead(string path)
    {
        try { return Read(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or InvalidOperationException)
        {
            if (e is not FileNotFoundException and not DirectoryNotFoundException) Log.Write($"cannot read {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    /// The loaded key, or one loaded now when `path` is encrypted and none is (at most every 30 s).
    static byte[]? CurrentKey(string path)
    {
        if (Key is byte[] k) return k;
        if (KeyLoader is not { } load) return null;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastLoadTry) < 30_000) return null;
        try { if (!IsEncrypted(path)) return null; } catch (IOException) { return null; }
        Interlocked.Exchange(ref lastLoadTry, now);
        var loaded = load();
        if (loaded != null) { Key = loaded; Log.Write("encryption key loaded for an encrypted recording"); }
        return loaded;
    }

    /// True when the error means "encrypted and no key loaded" (not damage): callers retry later instead of giving up.
    public static bool IsNoKey(Exception e) => e is InvalidOperationException && e.Message.Contains(NoKeyMessage);
    const string NoKeyMessage = "the key is not loaded";

    /// Write a new recording: encrypted when Encrypt Recordings is on. Atomic (temp file, then rename).
    public static void Write(string path, byte[] plain) => Write(path, plain, EncryptNew ? Key : null);

    public static void Write(string path, byte[] plain, byte[]? encryptWith)
    {
        var data = encryptWith == null ? plain : Seal(plain, encryptWith, Identity(path));
        var tmp = path + TempSuffix;
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            f.Write(data);
            f.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// Delete a recording (retention and Delete Recordings).
    public static void Delete(string path)
    {
        lock (FileGate)
        {
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { File.Delete(path + TempSuffix); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// A plain copy for tools that need a file path (whisper-cli). For a plain file it is the file
    /// itself; an encrypted one is decrypted into the user's temp folder and deleted on Dispose.
    public static PlainFile PlainCopy(string path)
    {
        if (!IsEncrypted(path)) return new PlainFile(path, temporary: false);
        var tmp = Path.Combine(Path.GetTempPath(), $"{TempCopyPrefix}{Guid.NewGuid():N}{Path.GetExtension(path)}");
        File.WriteAllBytes(tmp, Read(path));
        return new PlainFile(tmp, temporary: true);
    }

    public sealed class PlainFile : IDisposable
    {
        public readonly string Path;
        readonly bool temporary;
        internal PlainFile(string path, bool temporary) { Path = path; this.temporary = temporary; }
        public void Dispose() { if (temporary) try { File.Delete(Path); } catch { } }
    }

    /// At launch: remove decrypted copies a crash or kill left in the temp folder. Copies made in the
    /// last 10 minutes are left alone, in case another OpenRewind process is using them right now.
    public static int CleanTempCopies(string? dir = null)
    {
        dir ??= Path.GetTempPath();
        var removed = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, TempCopyPrefix + "*"))
            {
                var name = Path.GetFileName(f);
                if (!(name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    if (File.GetLastWriteTimeUtc(f) > DateTime.UtcNow.AddMinutes(-10)) continue;
                    File.Delete(f);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (removed > 0) Log.Write($"removed {removed} leftover temporary audio files");
        return removed;
    }

    // MARK: format

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static bool IsEncrypted(string path)
    {
        Span<byte> head = stackalloc byte[5];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && IsEncrypted(head);
    }

    /// 4 bytes that name a master key, so a file made with another key says so instead of "damaged".
    /// Also the check digits of the recovery key.
    public static byte[] KeyId(byte[] key) => SHA256.HashData(key)[..4];

    /// The key the file cipher uses: derived from the master, never the master itself.
    public static byte[] FileKey(byte[] master) => Derive(master, "openrewind/files/v2");

    internal static byte[] Derive(byte[] master, string purpose)
    {
        if (master.Length != 32) throw new ArgumentException("the data key must be 32 bytes");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, Salt, Encoding.UTF8.GetBytes(purpose));
    }

    /// Where a recording sits in the data folder, with "/" between folders: everything after the last
    /// folder named "data" (".../data/2026-10-19/a.jpg" → "2026-10-19/a.jpg"), or the file name when
    /// the path has no such folder. It does not depend on where the data folder is, so moving the
    /// whole folder (a new home, another computer) keeps every file readable.
    public static string Identity(string path)
    {
        var parts = path.Split('/', '\\').Where(p => p.Length > 0).ToArray();
        var i = Array.FindLastIndex(parts, p => p.Equals("data", StringComparison.OrdinalIgnoreCase));
        var rel = i >= 0 && i < parts.Length - 1 ? string.Join('/', parts[(i + 1)..]) : parts.LastOrDefault() ?? "";
        return rel.Normalize(NormalizationForm.FormC);
    }

    static byte[] Aad(ReadOnlySpan<byte> header, string identity)
    {
        var id = Encoding.UTF8.GetBytes(identity);
        var aad = new byte[header.Length + id.Length];
        header.CopyTo(aad);
        id.CopyTo(aad, header.Length);
        return aad;
    }

    public static byte[] Seal(byte[] plain, byte[] key, string identity)
    {
        var out_ = new byte[Overhead + plain.Length];
        Magic.CopyTo(out_, 0);
        out_[5] = Version;
        KeyId(key).CopyTo(out_, 6);
        RandomNumberGenerator.Fill(out_.AsSpan(10, 12));
        var fileKey = FileKey(key);
        try
        {
            using var gcm = new AesGcm(fileKey, TagSize);
            gcm.Encrypt(out_.AsSpan(10, 12), plain, out_.AsSpan(HeaderSize, plain.Length),
                        out_.AsSpan(HeaderSize + plain.Length, TagSize), Aad(out_.AsSpan(0, HeaderSize), identity));
        }
        finally { CryptographicOperations.ZeroMemory(fileKey); }
        return out_;
    }

    /// Decrypt an encrypted file's bytes; plain bytes come back as they are.
    public static byte[] Open(byte[] data, byte[]? key, string identity)
    {
        if (!IsEncrypted(data)) return data;
        if (data.Length < Overhead) throw new InvalidDataException("the encrypted file is cut short");
        if (data[5] > Version) throw new InvalidDataException($"encrypted with a newer OpenRewind (format {data[5]})");
        if (data[5] != Version) throw new InvalidDataException($"encrypted with a test version of OpenRewind (format {data[5]}) that this one cannot read");
        if (key == null) throw new InvalidOperationException("this recording is encrypted and " + NoKeyMessage);
        if (!data.AsSpan(6, 4).SequenceEqual(KeyId(key))) throw new CryptographicException("this recording was encrypted with a different key");
        var plain = new byte[data.Length - Overhead];
        var fileKey = FileKey(key);
        try
        {
            using var gcm = new AesGcm(fileKey, TagSize);
            // Throws AuthenticationTagMismatchException (a CryptographicException) if a single byte
            // changed, or the file is not where it was written.
            gcm.Decrypt(data.AsSpan(10, 12), data.AsSpan(HeaderSize, plain.Length), data.AsSpan(data.Length - TagSize), plain,
                        Aad(data.AsSpan(0, HeaderSize), identity));
        }
        finally { CryptographicOperations.ZeroMemory(fileKey); }
        return plain;
    }

    /// Bring one file to the wanted state (encrypted with `key`, or plain). Returns true if it changed.
    /// Safe to stop at any point: the file is either the old or the new version, never half of one.
    public static bool Convert(string path, byte[] key, bool encrypt)
    {
        lock (FileGate)
        {
            if (!File.Exists(path)) return false;  // deleted meanwhile (retention)
            var data = File.ReadAllBytes(path);
            if (IsEncrypted(data) == encrypt) return false;
            Write(path, Open(data, key, Identity(path)), encrypt ? key : null);
            return true;
        }
    }

    /// True when `key` opens the encrypted file at `path` (for a recovery key typed at launch).
    public static bool KeyOpens(string path, byte[] key)
    {
        try { Read(path, key); return true; }
        catch (Exception e) when (e is IOException or CryptographicException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException) { return false; }
    }

    /// The first encrypted recording under `dataDir` (looks at no more than `limit` files), or null.
    public static string? FirstEncrypted(string dataDir, int limit = 5000)
    {
        if (!Directory.Exists(dataDir)) return null;
        foreach (var f in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).Take(limit))
        {
            if (f.EndsWith(TempSuffix)) continue;
            try { if (IsEncrypted(f)) return f; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);
}
