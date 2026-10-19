using System.Security.Cryptography;

namespace OpenRewind;

/// Every screenshot and audio clip is read and written through here, so encrypted and plain
/// files can sit side by side. Read() is the one loader: anything that opens a recording
/// (timeline, search, API, Ask AI, Whisper, new features) calls it instead of File.ReadAllBytes.
///
/// Encrypted file (AES-256-GCM, the same on Mac and Windows):
///   "ORENC" | version 1 | key id (4 bytes) | nonce (12 bytes) | ciphertext | tag (16 bytes)
/// The first 22 bytes are authenticated too. Plain files (JPEG, HEIC, WAV) never start with "ORENC".
public static class RecordingFile
{
    static readonly byte[] Magic = "ORENC"u8.ToArray();
    public const byte Version = 1;
    public const int HeaderSize = 5 + 1 + 4 + 12;
    public const int TagSize = 16;
    public const int Overhead = HeaderSize + TagSize;
    /// Half-written files (a conversion or a write that was cut short) end in this.
    public const string TempSuffix = ".orenc-tmp";

    /// The data key, once loaded from the Keychain / DPAPI or a recovery key. Null = none.
    public static volatile byte[]? Key;
    /// Encrypt new recordings (Settings → Encrypt Recordings).
    public static volatile bool EncryptNew;

    /// Held while a file is converted or deleted, so retention never races a conversion.
    public static readonly object FileGate = new();

    // MARK: the loader

    /// The recording's bytes, decrypted if needed. Throws if the file is missing, damaged, or
    /// encrypted with a key we don't have.
    public static byte[] Read(string path) => Open(File.ReadAllBytes(path), Key);

    public static byte[] Read(string path, byte[]? key) => Open(File.ReadAllBytes(path), key);

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

    /// Size of the recording once decrypted (the timeline works out audio length from it). 0 if missing.
    public static long PlainLength(string path)
    {
        try
        {
            var len = new FileInfo(path).Length;
            return IsEncrypted(path) ? Math.Max(0, len - Overhead) : len;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// Write a new recording: encrypted when Encrypt Recordings is on. Atomic (temp file, then rename).
    public static void Write(string path, byte[] plain) => Write(path, plain, EncryptNew ? Key : null);

    public static void Write(string path, byte[] plain, byte[]? encryptWith)
    {
        var data = encryptWith == null ? plain : Seal(plain, encryptWith);
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
        var tmp = Path.Combine(Path.GetTempPath(), $"openrewind-{Guid.NewGuid():N}{Path.GetExtension(path)}");
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

    // MARK: format

    public static bool IsEncrypted(ReadOnlySpan<byte> data) => data.Length >= Magic.Length && data[..Magic.Length].SequenceEqual(Magic);

    public static bool IsEncrypted(string path)
    {
        Span<byte> head = stackalloc byte[5];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && IsEncrypted(head);
    }

    /// 4 bytes that name a key, so a file made with another key says so instead of "damaged".
    public static byte[] KeyId(byte[] key) => SHA256.HashData(key)[..4];

    public static byte[] Seal(byte[] plain, byte[] key)
    {
        if (key.Length != 32) throw new ArgumentException("the data key must be 32 bytes");
        var out_ = new byte[Overhead + plain.Length];
        Magic.CopyTo(out_, 0);
        out_[5] = Version;
        KeyId(key).CopyTo(out_, 6);
        RandomNumberGenerator.Fill(out_.AsSpan(10, 12));
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(out_.AsSpan(10, 12), plain, out_.AsSpan(HeaderSize, plain.Length),
                    out_.AsSpan(HeaderSize + plain.Length, TagSize), out_.AsSpan(0, HeaderSize));
        return out_;
    }

    /// Decrypt an encrypted file's bytes; plain bytes come back as they are.
    public static byte[] Open(byte[] data, byte[]? key)
    {
        if (!IsEncrypted(data)) return data;
        if (data.Length < Overhead) throw new InvalidDataException("the encrypted file is cut short");
        if (data[5] != Version) throw new InvalidDataException($"encrypted with a newer OpenRewind (format {data[5]})");
        if (key == null) throw new InvalidOperationException("this recording is encrypted and the key is not loaded");
        if (!data.AsSpan(6, 4).SequenceEqual(KeyId(key))) throw new CryptographicException("this recording was encrypted with a different key");
        var plain = new byte[data.Length - Overhead];
        using var gcm = new AesGcm(key, TagSize);
        // Throws AuthenticationTagMismatchException (a CryptographicException) if a single byte changed.
        gcm.Decrypt(data.AsSpan(10, 12), data.AsSpan(HeaderSize, plain.Length), data.AsSpan(data.Length - TagSize), plain, data.AsSpan(0, HeaderSize));
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
            Write(path, Open(data, key), encrypt ? key : null);
            return true;
        }
    }

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);
}
