using Microsoft.Data.Sqlite;

namespace OpenRewind;

/// The database is encrypted and the key is not loaded (Keychain / DPAPI lost, or copied from another computer).
public sealed class DatabaseLockedException : Exception
{
    public DatabaseLockedException(string? msg = null) : base(msg ?? "the database is encrypted and the key is not on this computer") { }
}

/// Database encryption with SQLCipher. Whether the file is encrypted is read from the file itself
/// (a plain SQLite file starts with "SQLite format 3"), so a half-finished switch is never mistaken.
/// The SQLCipher key is derived from the master data key (HKDF, "openrewind/db/v2"), so the
/// database and the files never share a key. Same as Vault.swift on the Mac.
public sealed partial class Database
{
    static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public bool IsEncrypted { get; private set; }
    /// The master key the open connection was opened with (null for a plain database).
    byte[]? openKey;
    volatile bool converting;

    /// True while Convert rewrites the database. Everything else waits on the lock meanwhile, so the
    /// menu shows a note instead of asking for counts (which would hang the tray until it is done).
    public bool IsConverting => converting;

    /// True when the file exists, has data, and is not plain SQLite.
    public static bool IsEncryptedFile(string path)
    {
        if (!HasData(path)) return false;
        var head = new byte[16];
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return f.ReadAtLeast(head, 16, throwOnEndOfStream: false) < 16 || !head.AsSpan().SequenceEqual(SqliteHeader);
    }

    static bool HasData(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// The SQLCipher key for a master data key.
    public static byte[] DbKey(byte[] master) => RecordingFile.Derive(master, "openrewind/db/v2");

    /// SQLCipher reads x'…' as a raw 256-bit key (no password stretching: it is random already).
    static string KeyLiteral(byte[] master) => $"x'{System.Convert.ToHexString(DbKey(master))}'";

    static SqliteConnection Open(string file, byte[]? key, SqliteOpenMode mode)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = mode, Pooling = false }.ToString());
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
        return c;
    }

    SqliteConnection Connect(string file, byte[]? key, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var c = Open(file, key, mode);
        IsEncrypted = key != null;
        openKey = key;
        return c;
    }

    /// True when `key` opens the database at `path`. Only opens it: no schema changes, no home move.
    public static bool KeyOpens(string path, byte[] key)
    {
        try { using var c = Open(path, key, SqliteOpenMode.ReadOnly); return true; }
        catch (DatabaseLockedException) { return false; }
    }

    /// The files that go with a database in WAL mode: its newest changes can still be in the -wal.
    static readonly string[] WalSuffixes = { "-wal", "-shm" };

    /// A switch that was cut short between its two renames: put the original back, with its WAL.
    static void RecoverSwap(string path)
    {
        var old = path + ".old";
        if (!File.Exists(path) && File.Exists(old))
        {
            File.Move(old, path);
            foreach (var s in WalSuffixes) if (File.Exists(old + s)) File.Move(old + s, path + s, overwrite: true);
        }
        foreach (var f in new[] { path + ".converting", path + ".converting-journal", old, old + "-wal", old + "-shm" })
            if (File.Exists(f) && File.Exists(path)) File.Delete(f);
    }

    /// Tries at writing the WAL into the database file before a switch; a reader in another process
    /// (the MCP server, a CLI) can hold part of it back for a moment.
    public static int CheckpointTries = 10;
    /// Test hook: runs just before the new file is moved into place.
    public static Action<string>? BeforeSwapIn;

    /// Write the WAL into the database file. True when all of it went in.
    bool Checkpoint()
    {
        for (var i = 1; ; i++)
        {
            using (var c = Cmd("PRAGMA wal_checkpoint(TRUNCATE);"))
            using (var r = c.ExecuteReader())
            {
                // busy, frames in the WAL, frames written into the database (-1, -1 when not in WAL mode).
                if (r.Read() && r.GetInt64(0) == 0 && r.GetInt64(1) == r.GetInt64(2)) return true;
            }
            if (i >= CheckpointTries) return false;
            Thread.Sleep(500);
        }
    }

    /// Antivirus, the search indexer, backup tools or the MCP server can hold the file for a moment,
    /// and Windows does not rename a file someone has open. Try a few times before giving up.
    public static int MoveAttempts = 6;

    static void Move(string from, string to)
    {
        for (var i = 1; ; i++)
        {
            try { File.Move(from, to, overwrite: true); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < MoveAttempts) { Thread.Sleep(250 * i); }
        }
    }

    /// Room for the new copy: its size plus 10% and 100 MB.
    static void CheckFreeSpace(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            if (free < size + size / 10 + (100L << 20))
                throw new IOException($"not enough free disk space to convert the database (needs {(size + size / 10 + (100L << 20)) >> 20} MB, {free >> 20} MB free)");
        }
        catch (Exception e) when (e is ArgumentException or UnauthorizedAccessException) { }  // can't tell: try anyway
    }

    /// Rewrite the whole database encrypted with `key`, or as plain SQLite (`key` null).
    /// Everything else waits on the lock meanwhile (recording pauses until it is done). On any
    /// failure the database stays usable: whichever file is in place is opened again.
    public void Convert(byte[]? key)
    {
        lock (gate)
        {
            if ((key != null) == IsEncrypted) return;
            converting = true;
            try { ConvertLocked(key); }
            finally { converting = false; }
        }
    }

    void ConvertLocked(byte[]? key)
    {
        CheckFreeSpace(path);
        var next = path + ".converting";
        var old = path + ".old";
        foreach (var f in new[] { next, next + "-journal", next + "-wal", next + "-shm" }) if (File.Exists(f)) File.Delete(f);
        // The export below reads through the WAL either way; when part of it stays behind, the WAL is kept
        // with the original until the new file is in place, so a failed swap loses nothing.
        if (!Checkpoint()) Log.Write("the database is open in another program; its newest changes stay in its WAL until the switch is done");
        using (var attach = Cmd("ATTACH DATABASE ? AS conv KEY ?", next, key == null ? "" : KeyLiteral(key))) attach.ExecuteNonQuery();
        try
        {
            using var export = Cmd("SELECT sqlcipher_export('conv');");
            export.ExecuteScalar();
        }
        catch
        {
            using (var d = Cmd("DETACH DATABASE conv;")) d.ExecuteNonQuery();
            try { File.Delete(next); } catch { }
            throw;
        }
        using (var detach = Cmd("DETACH DATABASE conv;")) detach.ExecuteNonQuery();

        var before = openKey;
        conn.Dispose();
        // Swap: database → .old, new → database, then drop .old. RecoverSwap undoes a crash in between.
        Exception? failure = null;
        var swapped = false;
        try
        {
            Move(path, old);
            // The WAL goes with the original: it is the original's, and may hold changes not in it yet.
            foreach (var s in WalSuffixes) if (File.Exists(path + s)) Move(path + s, old + s);
            try
            {
                BeforeSwapIn?.Invoke(next);
                Move(next, path);
                swapped = true;
            }
            catch
            {
                Move(old, path);
                foreach (var s in WalSuffixes) if (File.Exists(old + s)) Move(old + s, path + s);
                throw;
            }
            foreach (var f in new[] { old, old + "-wal", old + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failure = e;
            try { File.Delete(next); } catch { }
        }
        finally
        {
            // Open whichever file is in place, so the app keeps working even if the swap failed.
            if (!File.Exists(path) && File.Exists(old))
                try
                {
                    Move(old, path);
                    foreach (var s in WalSuffixes) if (File.Exists(old + s)) Move(old + s, path + s);
                }
                catch (IOException) { }
            conn = Connect(path, swapped ? key : before);
            Exec("PRAGMA journal_mode=WAL;");
            Exec("PRAGMA busy_timeout=5000;");
        }
        if (failure != null) throw new IOException($"could not replace the database file ({failure.Message}); it was left as it was", failure);
        Log.Write(key == null ? "database decrypted" : "database encrypted");
    }
}

/// Moves existing recordings to the chosen state: the database first, then every file under
/// data/. Each file is converted on its own and is never half-written, so stopping at any point
/// (quit, crash, power cut) is safe and the next run carries on where this one stopped.
public static class VaultMigration
{
    /// `DatabaseFailed`: the database could not be converted (it is left as it was and tried again next launch).
    public record Result(int Converted, int Failed, bool Cancelled, bool DatabaseFailed = false, int NoKey = 0)
    {
        public bool Finished => !Cancelled && Failed == 0 && !DatabaseFailed;
    }

    /// Progress for the menu: files done and files in all, while a migration runs.
    public static volatile int Done, Total;
    public static volatile bool Running;

    public static Result Run(Database db, string dataDir, byte[] key, bool encrypt, CancellationToken ct, Action<int>? afterFile = null)
    {
        Running = true;
        try
        {
            Done = 0; Total = 0;
            if (db.IsEncrypted != encrypt)
            {
                try { db.Convert(encrypt ? key : null); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or DatabaseLockedException)
                {
                    Log.Write($"cannot {(encrypt ? "encrypt" : "decrypt")} the database: {e.Message}");
                    return new Result(0, 0, false, DatabaseFailed: true);
                }
            }
            var files = Files(dataDir);
            Done = 0; Total = files.Count;
            int converted = 0, failed = 0;
            var failedFiles = new List<string>();
            bool Each(IEnumerable<string> list)
            {
                foreach (var f in list)
                {
                    if (ct.IsCancellationRequested) return false;
                    try { if (RecordingFile.Convert(f, key, encrypt)) converted++; }
                    catch (Exception e)
                    {
                        failed++;
                        failedFiles.Add(f);
                        Log.Write($"cannot {(encrypt ? "encrypt" : "decrypt")} {Path.GetFileName(f)}: {e.Message}");
                    }
                    Done++;
                    afterFile?.Invoke(Done);
                }
                return true;
            }
            if (!Each(files)) return new Result(converted, failed, true);
            // A recording saved just as the switch flipped (it read the old setting) can land after
            // the list above was made: look once more at anything written since. A video the compactor is
            // putting in place right now is waited for (CommitGate); later ones read the new setting.
            lock (RecordingFile.CommitGate) { }
            var seen = files.ToHashSet(StringComparer.Ordinal);
            var late = Files(dataDir).Where(f => !seen.Contains(f)).ToList();
            Total += late.Count;
            if (!Each(late)) return new Result(converted, failed, true);
            // Files that failed (most often one another process had open, which Windows can't replace):
            // once more, a moment later.
            if (failedFiles.Count > 0)
            {
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(RetryDelaySeconds))) return new Result(converted, failed, true);
                var again = failedFiles.ToList();
                failedFiles.Clear();
                failed -= again.Count;
                Total += again.Count;
                if (!Each(again)) return new Result(converted, failed, true);
            }
            Log.Write($"{(encrypt ? "encrypted" : "decrypted")} {converted} recordings" + (failed > 0 ? $", {failed} failed (tried again next launch)" : ""));
            return new Result(converted, failed, false);
        }
        finally { Running = false; }
    }

    /// Seconds to wait before the files that failed are tried once more (tests make it 0).
    public static double RetryDelaySeconds = 2;

    /// The kinds of file OpenRewind records (stills, audio, videos). Nothing else under data/ is touched.
    public static readonly HashSet<string> RecordingExtensions = new(StringComparer.OrdinalIgnoreCase) { ".heic", ".jpg", ".jpeg", ".png", ".wav", ".mp4", ".mov" };

    /// Every recording under data/, after removing temp files left by a write that was cut short.
    static List<string> Files(string dataDir)
    {
        if (!Directory.Exists(dataDir)) return new();
        static bool IsTemp(string f) => f.EndsWith(RecordingFile.TempSuffix);
        var all = Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories).ToList();
        // Older than a minute: a write that is still going on is left alone.
        foreach (var t in all.Where(IsTemp))
            try { if (File.GetLastWriteTimeUtc(t) < DateTime.UtcNow.AddMinutes(-1)) File.Delete(t); } catch { }
        // A video the compactor is still writing: it checks it and moves it into place (or deletes it) itself.
        return all.Where(f => !IsTemp(f) && !f.EndsWith(Compaction.TempSuffix, StringComparison.OrdinalIgnoreCase)
                              && RecordingExtensions.Contains(Path.GetExtension(f)))
                  .OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
}
