using Microsoft.Data.Sqlite;
using OpenRewind;
using Xunit;

/// The database switch (Encrypt Recordings) when another program is reading the database and the new
/// file can't be put in place: nothing committed is lost. These change Database's test hooks, so they run
/// with the other tests that change process-wide state.
[Collection(EncryptedRecordingsTests.Collection)]
public class DatabaseSwapTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-swap-" + Guid.NewGuid());
    readonly Config config;
    readonly byte[] key = RecordingFile.NewKey();

    public DatabaseSwapTests() { config = Config.Load(new[] { "--home", home }); }

    public void Dispose()
    {
        Database.BeforeSwapIn = null;
        Database.CheckpointTries = 10;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(home, true); } catch { }
    }

    [Fact]
    public void AFailedSwapAfterAnIncompleteCheckpointKeepsTheNewestRows()
    {
        using var db = new Database(config.DbPath);
        db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "older words", "ocr", 0));

        // Another program (the MCP server) reads from an older moment, so the WAL can't all go into the file.
        using var reader = new SqliteConnection($"Data Source={config.DbPath};Mode=ReadOnly;Pooling=False");
        reader.Open();
        using var tx = reader.BeginTransaction();
        using (var q = reader.CreateCommand()) { q.Transaction = tx; q.CommandText = "SELECT COUNT(*) FROM frames"; q.ExecuteScalar(); }
        db.Insert(new Frame(0, Time.Now(), "B", "", true, "", "newest words", "ocr", 0));

        Database.CheckpointTries = 1;
        // The new file can't be put in place (on Windows: antivirus holding it).
        Database.BeforeSwapIn = next => File.Delete(next);
        Assert.ThrowsAny<IOException>(() => db.Convert(key));

        Assert.False(db.IsEncrypted);
        Assert.Equal(1, db.Search(new SearchQuery { Q = "newest" }).Total);
        Assert.Equal(1, db.Search(new SearchQuery { Q = "older" }).Total);
        Assert.False(File.Exists(config.DbPath + ".old-wal"));

        // Once the reader is gone, the switch goes through, with every row.
        tx.Rollback();
        reader.Close();
        Database.BeforeSwapIn = null;
        db.Convert(key);
        Assert.True(db.IsEncrypted);
        Assert.Equal(1, db.Search(new SearchQuery { Q = "newest" }).Total);
        Assert.False(File.Exists(config.DbPath + ".old"));
    }

    [Fact]
    public void ACrashBetweenTheRenamesPutsTheOriginalBackWithItsWal()
    {
        using (var db = new Database(config.DbPath))
            db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "kept words", "ocr", 0));
        // As left by a crash right after the original (and its WAL) moved aside.
        File.Move(config.DbPath, config.DbPath + ".old");
        if (File.Exists(config.DbPath + "-wal")) File.Move(config.DbPath + "-wal", config.DbPath + ".old-wal");
        using var again = new Database(config.DbPath);
        Assert.Equal(1, again.Search(new SearchQuery { Q = "kept" }).Total);
        Assert.False(File.Exists(config.DbPath + ".old"));
        Assert.False(File.Exists(config.DbPath + ".old-wal"));
    }
}
