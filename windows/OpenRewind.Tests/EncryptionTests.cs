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
        var sealed_ = RecordingFile.Seal(plain, key);
        Assert.True(RecordingFile.IsEncrypted(sealed_));
        Assert.Equal(plain.Length + RecordingFile.Overhead, sealed_.Length);
        Assert.Equal("ORENC"u8.ToArray(), sealed_[..5]);
        Assert.Equal(RecordingFile.Version, sealed_[5]);
        Assert.Equal(plain, RecordingFile.Open(sealed_, key));
        // A fresh nonce every time: the same picture never gives the same bytes.
        Assert.NotEqual(sealed_, RecordingFile.Seal(plain, key));
    }

    [Fact]
    public void AnyChangedByteIsCaught()
    {
        var sealed_ = RecordingFile.Seal(Jpeg(5000), key);
        // Header (version and key id are checked first), nonce, ciphertext and tag.
        foreach (var at in new[] { 10, 15, RecordingFile.HeaderSize, RecordingFile.HeaderSize + 2500, sealed_.Length - 1 })
        {
            var bad = (byte[])sealed_.Clone();
            bad[at] ^= 0x01;
            Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(bad, key));
        }
        var newer = (byte[])sealed_.Clone();
        newer[5] = 2;
        Assert.Throws<InvalidDataException>(() => RecordingFile.Open(newer, key));
        Assert.Throws<InvalidDataException>(() => RecordingFile.Open(sealed_[..20], key));
        Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(sealed_[..^1], key));
    }

    [Fact]
    public void AnotherKeyOrNoKeySaysSo()
    {
        var sealed_ = RecordingFile.Seal(Jpeg(100), key);
        var e = Assert.ThrowsAny<CryptographicException>(() => RecordingFile.Open(sealed_, RecordingFile.NewKey()));
        Assert.Contains("different key", e.Message);
        Assert.Throws<InvalidOperationException>(() => RecordingFile.Open(sealed_, null));
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
        // The timeline works out audio length from the plain size.
        Assert.Equal(wav.Length, RecordingFile.PlainLength(pw));
        Assert.Equal(a.Length, RecordingFile.PlainLength(pa));
        Assert.Equal(0, RecordingFile.PlainLength(Path.Combine(config.DataDir, "missing.wav")));
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

        // And back to plain, byte for byte.
        var back = VaultMigration.Run(db, config.DataDir, key, encrypt: false, CancellationToken.None);
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
        File.WriteAllBytes(p, RecordingFile.Seal(Jpeg(100), RecordingFile.NewKey()));
        var r = VaultMigration.Run(db, config.DataDir, key, encrypt: false, CancellationToken.None);
        Assert.Equal(1, r.Failed);
        Assert.False(r.Finished);
        Assert.True(RecordingFile.IsEncrypted(p));
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
        Assert.Equal(200, api.Handle(new HttpRequest("GET", "/health", new())).Status);
        locked = false;
        Assert.Contains("plans", Encoding.UTF8.GetString(api.Handle(new HttpRequest("GET", "/search", new() { ["q"] = "secret" })).Body));
    }
}
