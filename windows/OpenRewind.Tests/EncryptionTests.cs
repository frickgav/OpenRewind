using System.Security.Cryptography;
using System.Text;
using OpenRewind;
using Xunit;

/// Encrypt Recordings: the file format, the loader, the database and the background migration.
/// Every test passes its key explicitly; none touches RecordingFile.Key, so other tests are unaffected.
public class EncryptionTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-enc-" + Guid.NewGuid());
    readonly Config config;
    readonly byte[] key = RecordingFile.NewKey();

    public EncryptionTests()
    {
        config = Config.Load(new[] { "--home", home });
    }

    public void Dispose()
    {
        try { Directory.Delete(home, true); } catch { }
    }

    const string Id = "2026-10-19/14-30-05.jpg";

    static byte[] Jpeg(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF;
        return b;
    }

    // MARK: file format

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(300_000)]
    public void SealedFilesRoundTrip(int size)
    {
        var plain = Jpeg(Math.Max(size, 3))[..size];
        var sealed_ = RecordingFile.Seal(plain, key, Id);
        Assert.True(RecordingFile.IsEncrypted(sealed_));
        Assert.Equal(plain.Length + RecordingFile.Overhead, sealed_.Length);
        Assert.Equal("ORENC"u8.ToArray(), sealed_[..5]);
        Assert.Equal(RecordingFile.Version, sealed_[5]);
        Assert.Equal(plain, RecordingFile.Open(sealed_, key, Id));
        // A fresh nonce every time: the same picture never gives the same bytes.
        Assert.NotEqual(sealed_, RecordingFile.Seal(plain, key, Id));
    }

    [Fact]
    public void AnyChangedByteIsCaught()
    {
        var sealed_ = RecordingFile.Seal(Jpeg(5000), key, Id);
        // Header (version and key id are checked first), nonce, ciphertext and tag.
        foreach (var at in new[] { 10, 15, RecordingFile.HeaderSize, RecordingFile.HeaderSize + 2500, sealed_.Length - 1 })
        {
            var bad = (byte[])sealed_.Clone();
            bad[at] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(bad, key, Id));
        }
        foreach (byte v in new byte[] { 1, 3 })
        {
            var other = (byte[])sealed_.Clone();
            other[5] = v;
            Assert.Throws<InvalidDataException>(() => RecordingFile.Open(other, key, Id));
        }
        Assert.Throws<InvalidDataException>(() => RecordingFile.Open(sealed_[..20], key, Id));
        Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(sealed_[..^1], key, Id));
    }

    [Fact]
    public void AnotherKeyOrNoKeySaysSo()
    {
        var sealed_ = RecordingFile.Seal(Jpeg(100), key, Id);
        var e = Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(sealed_, RecordingFile.NewKey(), Id));
        Assert.Contains("different key", e.Message);
        var none = Assert.Throws<InvalidOperationException>(() => RecordingFile.Open(sealed_, null, Id));
        Assert.True(RecordingFile.IsNoKey(none));
        Assert.False(RecordingFile.IsNoKey(e));
    }

    [Fact]
    public void PlainAndEncryptedFilesReadTheSame()
    {
        Directory.CreateDirectory(config.DataDir);
        var a = Jpeg(4000); var b = Jpeg(6000);
        var wav = Wav.Bytes(new float[16_000], 16_000);
        string pa = Path.Combine(config.DataDir, "a.jpg"), pb = Path.Combine(config.DataDir, "b.jpg"), pw = Path.Combine(config.DataDir, "c.wav");
        RecordingFile.Write(pa, a, encryptWith: null);
        RecordingFile.Write(pb, b, encryptWith: key);
        RecordingFile.Write(pw, wav, encryptWith: key);

        Assert.False(RecordingFile.IsEncrypted(pa));
        Assert.True(RecordingFile.IsEncrypted(pb));
        Assert.Equal(a, File.ReadAllBytes(pa));
        Assert.NotEqual(b, File.ReadAllBytes(pb)[RecordingFile.HeaderSize..^RecordingFile.TagSize]);
        Assert.Equal(a, RecordingFile.Read(pa, key));
        Assert.Equal(b, RecordingFile.Read(pb, key));
        Assert.Equal(wav, RecordingFile.Read(pw, key));
        // A plain file needs no key at all.
        Assert.Equal(a, RecordingFile.Read(pa, null));
        Assert.Empty(Directory.GetFiles(config.DataDir, "*" + RecordingFile.TempSuffix));
    }

    [Fact]
    public void ConvertingOneFileGoesBothWaysAndSkipsDoneFiles()
    {
        Directory.CreateDirectory(config.DataDir);
        var p = Path.Combine(config.DataDir, "x.jpg");
        var plain = Jpeg(2000);
        File.WriteAllBytes(p, plain);
        Assert.True(RecordingFile.Convert(p, key, encrypt: true));
        Assert.False(RecordingFile.Convert(p, key, encrypt: true));
        Assert.True(RecordingFile.IsEncrypted(p));
        Assert.True(RecordingFile.Convert(p, key, encrypt: false));
        Assert.Equal(plain, File.ReadAllBytes(p));
        // Deleted by retention meanwhile: nothing comes back.
        Assert.False(RecordingFile.Convert(Path.Combine(config.DataDir, "gone.jpg"), key, encrypt: true));
        Assert.False(File.Exists(Path.Combine(config.DataDir, "gone.jpg")));
    }

    // MARK: recovery key

    [Fact]
    public void RecoveryKeyRoundTripsAndCatchesTypos()
    {
        var text = RecoveryKey.Text(key);
        Assert.Equal(9 * 8 + 8, text.Length);  // 9 groups of 8 hex digits, 8 dashes
        Assert.Equal(key, RecoveryKey.Parse(text));
        Assert.Equal(key, RecoveryKey.Parse("  " + text.ToLowerInvariant().Replace("-", " ") + "\n"));
        Assert.Equal(key, RecoveryKey.Parse(RecoveryKey.File(key, DateTime.UtcNow)));
        var typo = (text[0] == 'A' ? "B" : "A") + text[1..];
        Assert.Null(RecoveryKey.Parse(typo));
        Assert.Null(RecoveryKey.Parse(text[..^1]));
        Assert.Null(RecoveryKey.Parse("hello"));
    }

    // MARK: database

    [Fact]
    public void DatabaseEncryptsAndDecryptsWithSearchIntact()
    {
        using (var db = new Database(config.DbPath))
        {
            db.Insert(new Frame(0, Time.Now(), "Chrome", "Budget", true, "x.jpg", "quarterly budget review", "ocr", 0));
            var chunk = db.InsertChunk("y.wav", Time.Now(), "microphone", true);
            db.SaveTranscripts(db.ChunkById(chunk)!, new[] { new Segment(0, 2, "let's talk about the budget") });
            Assert.False(db.IsEncrypted);
            db.Convert(key);
            Assert.True(db.IsEncrypted);
            // Still usable right away, and new rows go into the encrypted file.
            db.Insert(new Frame(0, Time.Now(), "Mail", "Inbox", true, "z.jpg", "budget approved", "ocr", 0));
            Assert.Equal(2, db.Search(new SearchQuery { Q = "budget" }).Total);
        }
        Assert.True(Database.IsEncryptedFile(config.DbPath));
        Assert.DoesNotContain("quarterly", Encoding.Latin1.GetString(File.ReadAllBytes(config.DbPath)));
        Assert.Throws<DatabaseLockedException>(() => new Database(config.DbPath));
        Assert.Throws<DatabaseLockedException>(() => new Database(config.DbPath, RecordingFile.NewKey()));

        using (var db = new Database(config.DbPath, key))
        {
            Assert.True(db.IsEncrypted);
            Assert.Equal(2, db.Search(new SearchQuery { Q = "budget" }).Total);
            Assert.Equal(1, db.SearchAudio(new SearchQuery { Q = "budget" }, 10).Total);
            db.Convert(null);
            Assert.False(db.IsEncrypted);
        }
        Assert.False(Database.IsEncryptedFile(config.DbPath));
        using (var db = new Database(config.DbPath))
            Assert.Equal(2, db.Search(new SearchQuery { Q = "budget" }).Total);
    }

    [Fact]
    public void NewDatabaseStartsEncryptedWhenAsked()
    {
        using (var db = new Database(config.DbPath, key, createEncrypted: true))
        {
            Assert.True(db.IsEncrypted);
            db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "hello", "ocr", 0));
        }
        Assert.True(Database.IsEncryptedFile(config.DbPath));
        // An existing plain database is not switched by opening it: that is the migration's job.
        var other = Path.Combine(home, "plain.sqlite");
        using (new Database(other)) { }
        using (var db = new Database(other, key, createEncrypted: true)) Assert.False(db.IsEncrypted);
    }

    [Fact]
    public void ASwapCutShortGetsTheOriginalBack()
    {
        using (var db = new Database(config.DbPath))
            db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "survivor", "ocr", 0));
        // As if the app died between "database → .old" and ".converting → database".
        File.Move(config.DbPath, config.DbPath + ".old");
        File.WriteAllText(config.DbPath + ".converting", "half written");
        using (var db = new Database(config.DbPath))
            Assert.Equal(1, db.Search(new SearchQuery { Q = "survivor" }).Total);
        Assert.False(File.Exists(config.DbPath + ".converting"));
        Assert.False(File.Exists(config.DbPath + ".old"));
    }

    // MARK: migration

    [Fact]
    public void MigrationStopsAnywhereAndCarriesOn()
    {
        var originals = new Dictionary<string, byte[]>();
        using var db = new Database(config.DbPath);
        for (int i = 0; i < 12; i++)
        {
            var dir = Path.Combine(config.DataDir, i < 8 ? "2026-10-19" : Path.Combine("audio", "2026-10-19"));
            Directory.CreateDirectory(dir);
            var p = Path.Combine(dir, $"f{i:00}" + (i < 8 ? ".jpg" : ".wav"));
            var bytes = i < 8 ? Jpeg(3000 + i) : Wav.Bytes(new float[1600 * (i + 1)], 16_000);
            File.WriteAllBytes(p, bytes);
            originals[p] = bytes;
            if (i < 8) db.Insert(new Frame(0, Time.Now(), "App", "", true, p, $"frame number {i}", "ocr", 0));
            else db.InsertChunk(p, Time.Now(), "microphone", true);
        }
        // A write that was cut short a while ago, and one still being written.
        var stale = Path.Combine(config.DataDir, "2026-10-19", "f99.jpg" + RecordingFile.TempSuffix);
        File.WriteAllBytes(stale, new byte[10]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        var fresh = Path.Combine(config.DataDir, "2026-10-19", "f98.jpg" + RecordingFile.TempSuffix);
        File.WriteAllBytes(fresh, new byte[10]);

        // Stop after 5 files, as a quit or crash would.
        using var cts = new CancellationTokenSource();
        var first = VaultMigration.Run(db, config.DataDir, key, encrypt: true, cts.Token, done => { if (done == 5) cts.Cancel(); });
        Assert.True(first.Cancelled);
        Assert.False(first.Finished);
        Assert.True(db.IsEncrypted);
        Assert.Equal(5, originals.Keys.Count(RecordingFile.IsEncrypted));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        // Halfway, every file still reads.
        foreach (var (p, bytes) in originals) Assert.Equal(bytes, RecordingFile.Read(p, key));

        // The next launch picks up where it stopped.
        var second = VaultMigration.Run(db, config.DataDir, key, encrypt: true, CancellationToken.None);
        Assert.True(second.Finished);
        Assert.Equal(7, second.Converted);
        Assert.All(originals.Keys, p => Assert.True(RecordingFile.IsEncrypted(p)));
        foreach (var (p, bytes) in originals) Assert.Equal(bytes, RecordingFile.Read(p, key));
        Assert.Equal(1, db.Search(new SearchQuery { Q = "number 7" }).Total);
        // Running again finds nothing left to do.
        Assert.Equal(0, VaultMigration.Run(db, config.DataDir, key, encrypt: true, CancellationToken.None).Converted);

        // And back to plain, byte for byte. A screenshot saved encrypted just as the switch flipped
        // (it read the old setting) lands after the list was made; it is decrypted too.
        var late = Path.Combine(config.DataDir, "2026-10-19", "late.jpg");
        var lateBytes = Jpeg(1234);
        var back = VaultMigration.Run(db, config.DataDir, key, encrypt: false, CancellationToken.None, done =>
        {
            if (done == 1) RecordingFile.Write(late, lateBytes, encryptWith: key);
        });
        Assert.Equal(lateBytes, File.ReadAllBytes(late));
        Assert.True(back.Finished);
        Assert.False(db.IsEncrypted);
        foreach (var (p, bytes) in originals) Assert.Equal(bytes, File.ReadAllBytes(p));
    }

    [Fact]
    public void MigrationCountsFilesItCannotOpen()
    {
        using var db = new Database(config.DbPath);
        Directory.CreateDirectory(config.DataDir);
        var p = Path.Combine(config.DataDir, "other-key.jpg");
        File.WriteAllBytes(p, RecordingFile.Seal(Jpeg(100), RecordingFile.NewKey(), RecordingFile.Identity(p)));
        var r = VaultMigration.Run(db, config.DataDir, key, encrypt: false, CancellationToken.None);
        Assert.Equal(1, r.Failed);
        Assert.False(r.Finished);
        Assert.True(RecordingFile.IsEncrypted(p));
    }

    // MARK: format version 2: key separation and file identity

    [Fact]
    public void SubkeysMatchTheSpec()
    {
        // HKDF-SHA256(master, salt "OpenRewind", info …), checked against a separate implementation.
        // The Mac app checks the same values in `OpenRewind selftest-encryption` (run by the Mac CI).
        var master = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        Assert.Equal("59B7C32A10B7CF03E2F945651E2246679A0811D289C866A168A9C485D54D23E8", System.Convert.ToHexString(RecordingFile.FileKey(master)));
        Assert.Equal("666E51B33491C38D6CD711438375F95265F289C70E24096D4724F568F45EFEA6", System.Convert.ToHexString(Database.DbKey(master)));
        Assert.Equal("630DCD29", System.Convert.ToHexString(RecordingFile.KeyId(master)));
    }

    [Fact]
    public void AFileMadeElsewhereOpens()
    {
        // Built with Python's cryptography package from the spec: master 00..1F, nonce 64..6F,
        // file "2026-10-19/14-30-05.jpg". The Mac self-test opens the same bytes.
        var master = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var golden = System.Convert.FromHexString(Golden);
        Assert.Equal("hello, encrypted world", Encoding.UTF8.GetString(RecordingFile.Open(golden, master, Id)));
        Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(golden, master, "2026-10-19/14-30-07.jpg"));
    }

    public const string Golden =
        "4F52454E4302630DCD296465666768696A6B6C6D6E6F0D735A50A82340FCB94E8B260C25BD6D4C9D1238BD873F0735740AC39D139B4097F0C8B9405B";

    [Theory]
    [InlineData(@"C:\Users\a\.openrewind\data\2026-10-19\14-30-05.jpg", "2026-10-19/14-30-05.jpg")]
    [InlineData("/Users/a/.openrewind/data/audio/2026-10-19/14-30-05-mic.wav", "audio/2026-10-19/14-30-05-mic.wav")]
    [InlineData("/Users/data/.openrewind/data/2026-10-19/x.heic", "2026-10-19/x.heic")]
    [InlineData("/tmp/loose.jpg", "loose.jpg")]
    public void IdentityIsThePlaceInTheDataFolder(string path, string id) => Assert.Equal(id, RecordingFile.Identity(path));

    [Fact]
    public void SwappedOrMovedFilesDoNotOpen()
    {
        var day = Path.Combine(config.DataDir, "2026-10-19");
        Directory.CreateDirectory(day);
        string a = Path.Combine(day, "a.jpg"), b = Path.Combine(day, "b.jpg");
        var pa = Jpeg(500); var pb = Jpeg(700);
        RecordingFile.Write(a, pa, encryptWith: key);
        RecordingFile.Write(b, pb, encryptWith: key);
        Assert.Equal(pa, RecordingFile.Read(a, key));
        // Put b where a was: the bytes are genuine, but not this file's.
        File.WriteAllBytes(a, File.ReadAllBytes(b));
        Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Read(a, key));
        Assert.Equal(pb, RecordingFile.Read(b, key));
        // The whole data folder moved (new home, another computer): everything still opens.
        var moved = Path.Combine(home, "elsewhere", "data", "2026-10-19");
        Directory.CreateDirectory(moved);
        File.Copy(b, Path.Combine(moved, "b.jpg"));
        Assert.Equal(pb, RecordingFile.Read(Path.Combine(moved, "b.jpg"), key));
    }

    [Fact]
    public void TheDatabaseAndTheFilesUseDifferentKeys()
    {
        using (var db = new Database(config.DbPath, key, createEncrypted: true))
            db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "hello", "ocr", 0));
        Assert.True(Database.KeyOpens(config.DbPath, key));
        Assert.False(Database.KeyOpens(config.DbPath, RecordingFile.NewKey()));
        // The raw master key is not the database key.
        using var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={config.DbPath};Pooling=False");
        raw.Open();
        using (var k = raw.CreateCommand()) { k.CommandText = $"PRAGMA key = \"x'{System.Convert.ToHexString(key)}'\";"; k.ExecuteNonQuery(); }
        using var probe = raw.CreateCommand();
        probe.CommandText = "SELECT count(*) FROM sqlite_master;";
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => probe.ExecuteScalar());
    }

    [Fact]
    public void ReadOnlyOpenNeedsTheKeyForAnEncryptedDatabase()
    {
        using (var db = new Database(config.DbPath, key, createEncrypted: true))
            db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "read me", "ocr", 0));
        Assert.Throws<DatabaseLockedException>(() => Database.OpenReadOnly(config.DbPath));
        using var ro = Database.OpenReadOnly(config.DbPath, key);
        Assert.True(ro.IsEncrypted);
        Assert.Equal(1, ro.Search(new SearchQuery { Q = "read" }).Total);
    }

    [Fact]
    public void TheInsightsIndexIsBuiltInAnEncryptedDatabase()
    {
        using (var db = new Database(config.DbPath, key, createEncrypted: true))
            db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "hello", "ocr", 0));
        Database.BuildSlowIndexesInBackground(config.DbPath, key).Wait();
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={config.DbPath};Pooling=False");
        c.Open();
        using (var k = c.CreateCommand()) { k.CommandText = $"PRAGMA key = \"x'{System.Convert.ToHexString(Database.DbKey(key))}'\";"; k.ExecuteNonQuery(); }
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frames_insights'";
        Assert.Equal(1L, (long)q.ExecuteScalar()!);
    }

    [Fact]
    public void ARecoveryKeyIsCheckedAgainstARecording()
    {
        var day = Path.Combine(config.DataDir, "2026-10-19");
        Directory.CreateDirectory(day);
        File.WriteAllBytes(Path.Combine(day, "plain.jpg"), Jpeg(100));
        Assert.Null(RecordingFile.FirstEncrypted(config.DataDir));
        var enc = Path.Combine(day, "z.jpg");
        RecordingFile.Write(enc, Jpeg(100), encryptWith: key);
        Assert.Equal(enc, RecordingFile.FirstEncrypted(config.DataDir));
        Assert.True(RecordingFile.KeyOpens(enc, key));
        Assert.False(RecordingFile.KeyOpens(enc, RecordingFile.NewKey()));
    }

    [Fact]
    public void LeftoverPlainCopiesAreRemoved()
    {
        var tmp = Path.Combine(home, "tmp");
        Directory.CreateDirectory(tmp);
        string old = Path.Combine(tmp, "openrewind-old.wav"), json = Path.Combine(tmp, "openrewind-old.json"),
               fresh = Path.Combine(tmp, "openrewind-new.wav"), other = Path.Combine(tmp, "someone-else.wav");
        foreach (var f in new[] { old, json, fresh, other }) File.WriteAllBytes(f, new byte[4]);
        foreach (var f in new[] { old, json, other }) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));
        Assert.Equal(2, RecordingFile.CleanTempCopies(tmp));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(json));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AFailedSwapLeavesTheDatabaseWorking()
    {
        using var db = new Database(config.DbPath);
        db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "still here", "ocr", 0));
        Exception? failure = null;
        // Like antivirus or a backup tool holding the file. Windows refuses to rename it; other
        // systems allow it, and then the switch just works.
        using (new FileStream(config.DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            try { db.Convert(key); } catch (IOException e) { failure = e; }
        }
        if (OperatingSystem.IsWindows()) Assert.NotNull(failure);
        Assert.Equal(failure == null, db.IsEncrypted);
        Assert.Equal(db.IsEncrypted, Database.IsEncryptedFile(config.DbPath));
        Assert.False(db.IsConverting);
        // Either way the open database keeps working.
        db.Insert(new Frame(0, Time.Now(), "B", "", true, "", "still writing", "ocr", 0));
        Assert.Equal(2, db.Search(new SearchQuery { Q = "still" }).Total);
        Assert.False(File.Exists(config.DbPath + ".converting"));
    }

    [Fact]
    public void AFailedDatabaseSwitchIsReportedNotFinished()
    {
        using var db = new Database(config.DbPath);
        VaultMigration.Result r;
        using (new FileStream(config.DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            r = VaultMigration.Run(db, config.DataDir, key, encrypt: true, CancellationToken.None);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(r.DatabaseFailed);
            Assert.False(r.Finished);
        }
        else Assert.True(r.Finished);
    }

    // MARK: app lock

    [Fact]
    public void LockedApiServesNoHistory()
    {
        using var db = new Database(config.DbPath);
        db.Insert(new Frame(0, Time.Now(), "A", "", true, "", "secret plans", "ocr", 0));
        var locked = true;
        var api = new Api(db, config) { Locked = () => locked };
        foreach (var path in new[] { "/search", "/apps", "/frames/1", "/audio/1" })
        {
            var r = api.Handle(new HttpRequest("GET", path, new() { ["q"] = "secret" }));
            Assert.Equal(423, r.Status);
            Assert.DoesNotContain("plans", Encoding.UTF8.GetString(r.Body));
        }
        Assert.Equal(200, api.Handle(new HttpRequest("GET", "/", new())).Status);
        var health = api.Handle(new HttpRequest("GET", "/health", new()));
        Assert.Equal(200, health.Status);
        // While locked, /health says it is up and nothing about the recordings.
        Assert.DoesNotContain("frames_total", Encoding.UTF8.GetString(health.Body));
        Assert.Contains("locked", Encoding.UTF8.GetString(health.Body));
        locked = false;
        Assert.Contains("frames_total", Encoding.UTF8.GetString(api.Handle(new HttpRequest("GET", "/health", new())).Body));
        Assert.Contains("plans", Encoding.UTF8.GetString(api.Handle(new HttpRequest("GET", "/search", new() { ["q"] = "secret" })).Body));
    }
}
