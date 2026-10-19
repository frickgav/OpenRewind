using Microsoft.Data.Sqlite;

namespace OpenRewind;

/// The database is encrypted and the key is not loaded (Keychain / DPAPI lost, or copied from another computer).
public sealed class DatabaseLockedException : Exception
{
    public DatabaseLockedException(string? msg = null) : base(msg ?? "the database is encrypted and the key is not on this computer") { }
}

/// Database encryption with SQLCipher. Whether the file is encrypted is read from the file itself
/// (a plain SQLite file starts with "SQLite format 3"), so a half-finished switch is never mistaken.
public sealed partial class Database
{
    static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public bool IsEncrypted { get; private set; }

    /// True when the file exists, has data, and is not plain SQLite.
    public static bool IsEncryptedFile(string path)
    {
        if (!HasData(path)) return false;
        var head = new byte[16];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return f.ReadAtLeast(head, 16, throwOnEndOfStream: false) < 16 || !head.AsSpan().SequenceEqual(SqliteHeader);
    }

    static bool HasData(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// SQLCipher reads x'…' as a raw 256-bit key (no password stretching: it is random already).
    static string KeyLiteral(byte[] key) => $"x'{System.Convert.ToHexString(key)}'";

    SqliteConnection Connect(string file, byte[]? key)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open();
        try
        {
            if (key != null)
            {
                using (var k = c.CreateCommand()) { k.CommandText = $"PRAGMA key = '{KeyLiteral(key).Replace("'", "''")}';"; k.ExecuteNonQuery(); }
            }
            // A wrong key (or an encrypted file opened without one) fails here: "file is not a database".
            using var probe = c.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            probe.ExecuteScalar();
        }
        catch (SqliteException e)
        {
            c.Dispose();
            throw new DatabaseLockedException(key == null ? null : $"the key does not open this database ({e.Message})");
        }
        IsEncrypted = key != null;
        return c;
    }

    /// A switch that was cut short between its two renames: put the original back.
    static void RecoverSwap(string path)
    {
        var old = path + ".old";
        if (!File.Exists(path) && File.Exists(old)) File.Move(old, path);
        foreach (var f in new[] { path + ".converting", path + ".converting-journal", old })
            if (File.Exists(f) && File.Exists(path)) File.Delete(f);
    }

    /// Rewrite the whole database encrypted with `key`, or as plain SQLite (`key` null).
    /// Everything else waits on the lock meanwhile; on any failure the old file stays in use.
    public void Convert(byte[]? key)
    {
        lock (gate)
        {
            if ((key != null) == IsEncrypted) return;
            var next = path + ".converting";
            foreach (var f in new[] { next, next + "-journal", next + "-wal", next + "-shm" }) if (File.Exists(f)) File.Delete(f);
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
            using (var attach = Cmd("ATTACH DATABASE ? AS conv KEY ?", next, key == null ? "" : KeyLiteral(key))) attach.ExecuteNonQuery();
            try
            {
                using var export = Cmd("SELECT sqlcipher_export('conv');");
                export.ExecuteScalar();
            }
            finally
            {
                using var detach = Cmd("DETACH DATABASE conv;");
                detach.ExecuteNonQuery();
            }
            conn.Dispose();
            // Swap: database → .old, new → database, then drop .old. RecoverSwap undoes a crash in between.
            File.Move(path, path + ".old", overwrite: true);
            foreach (var f in new[] { path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
            File.Move(next, path);
            File.Delete(path + ".old");
            conn = Connect(path, key);
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA busy_timeout=5000;");
            Log.Write(key == null ? "database decrypted" : "database encrypted");
        }
    }
}

/// Moves existing recordings to the chosen state: the database first, then every file under
/// data/. Each file is converted on its own and is never half-written, so stopping at any point
/// (quit, crash, power cut) is safe and the next run carries on where this one stopped.
public static class VaultMigration
{
    public record Result(int Converted, int Failed, bool Cancelled)
    {
        public bool Finished => !Cancelled && Failed == 0;
    }

    /// Progress for the menu: files done and files in all, while a migration runs.
    public static volatile int Done, Total;
    public static volatile bool Running;

    public static Result Run(Database db, string dataDir, byte[] key, bool encrypt, CancellationToken ct, Action<int>? afterFile = null)
    {
        Running = true;
        try
        {
            if (db.IsEncrypted != encrypt) db.Convert(encrypt ? key : null);
            var files = Files(dataDir);
            Done = 0; Total = files.Count;
            int converted = 0, failed = 0;
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) return new Result(converted, failed, true);
                try { if (RecordingFile.Convert(f, key, encrypt)) converted++; }
                catch (Exception e)
                {
                    failed++;
                    Log.Write($"cannot {(encrypt ? "encrypt" : "decrypt")} {f}: {e.Message}");
                }
                Done++;
                afterFile?.Invoke(Done);
            }
            Log.Write($"{(encrypt ? "encrypted" : "decrypted")} {converted} recordings" + (failed > 0 ? $", {failed} failed (tried again next launch)" : ""));
            return new Result(converted, failed, false);
        }
        finally { Running = false; }
    }

    /// Every recording under data/, after removing temp files left by a write that was cut short.
    static List<string> Files(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return new();
        static bool IsTemp(string f) => f.EndsWith(RecordingFile.TempSuffix) || f.EndsWith(".tmp");
        var all = Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).ToList();
        // Older than a minute: a write that is still going on is left alone.
        foreach (var t in all.Where(IsTemp))
            try { if (File.GetLastWriteTimeUtc(t) < DateTime.UtcNow.AddMinutes(-1)) File.Delete(t); } catch { }
        return all.Where(f => !IsTemp(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
}
