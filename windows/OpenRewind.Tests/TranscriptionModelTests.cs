using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using OpenRewind;
using Xunit.Abstractions;

public class TranscriptionModelTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-models-" + Guid.NewGuid());
    readonly ITestOutputHelper output;

    public TranscriptionModelTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(home);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(home, true); } catch { }
    }

    // MARK: registry

    [Fact]
    public void EveryDownloadIsPinnedToACommitAndAHash()
    {
        Assert.Equal(SpeechModels.All.Count, SpeechModels.All.Select(m => m.Id).Distinct().Count());
        foreach (var m in SpeechModels.All)
            foreach (var f in m.Files)
            {
                Assert.Matches(new Regex("^https://huggingface\\.co/[^/]+/[^/]+/resolve/[0-9a-f]{40}/"), f.Url);
                Assert.EndsWith("/" + f.Name, f.Url);
                Assert.Matches(new Regex("^[0-9a-f]{64}$"), f.Sha256);
                Assert.True(f.Size > 0);
            }
        var p = SpeechModels.ById(SpeechModels.ParakeetId);
        Assert.Equal(SpeechEngine.Parakeet, p.Engine);
        Assert.Equal(new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" }, p.Files.Select(f => f.Name));
        Assert.Equal("670 MB", SpeechModels.FormatSize(p.Size));
    }

    [Fact]
    public void UnknownOrMissingModelFallsBackToAutomaticWhisper()
    {
        Assert.Equal(SpeechModels.DefaultId, SpeechModels.ById(null).Id);
        Assert.Equal(SpeechModels.DefaultId, SpeechModels.ById("gpt-voice-9").Id);
        Assert.Empty(SpeechModels.ById(SpeechModels.DefaultId).Files);
        Assert.Equal("whisper-small", SpeechModels.ById("whisper-small").Id);
    }

    [Fact]
    public void WhisperDownloadsLandWhereWhisperLooks()
    {
        var m = SpeechModels.ById("whisper-small");
        Assert.Equal(Path.Combine(home, "models", "ggml-small.bin"), m.PathOf(home, m.Files[0]));
        Directory.CreateDirectory(Path.Combine(home, "models"));
        File.WriteAllBytes(m.PathOf(home, m.Files[0]), new byte[3]);
        Assert.Equal(m.PathOf(home, m.Files[0]), Transcriber.FindModel(null, home));
        Assert.False(SpeechModels.IsDownloaded(m, home));  // wrong size
    }

    [Fact]
    public void PickingParakeetBeforeItIsDownloadedSaysSo()
    {
        var config = Config.Load(new[] { "--home", home, "--model", SpeechModels.ParakeetId });
        using var db = new Database(config.DbPath);
        Assert.Null(Transcriber.Create(config, db, out var problem));
        Assert.Equal("NVIDIA Parakeet v3 is not downloaded", problem);
    }

    // MARK: download and verification

    static ModelFile Fake(string name, byte[] data) =>
        new(name, "https://example.invalid/" + name, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), data.Length);

    sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Ok(byte[] body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task DownloadChecksEveryFileAndReportsProgress()
    {
        var a = Encoding.UTF8.GetBytes(new string('a', 3_000_000));
        var b = Encoding.UTF8.GetBytes("tokens");
        var model = new SpeechModel("test", "Test", SpeechEngine.Parakeet, "", "test", new[] { Fake("a.onnx", a), Fake("tokens.txt", b) });
        var server = new FakeServer(r => Ok(r.RequestUri!.AbsolutePath.EndsWith("a.onnx") ? a : b));
        var seen = new List<(long Done, long Total)>();
        await SpeechModels.Download(model, home, new SyncProgress(seen.Add), CancellationToken.None, server);
        Assert.True(SpeechModels.IsDownloaded(model, home));
        Assert.Null(SpeechModels.Problem(model, home));
        Assert.Equal((model.Size, model.Size), seen[^1]);
        Assert.True(seen.Count >= 3);
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.First.Done <= p.Second.Done));
        Assert.False(File.Exists(model.PathOf(home, model.Files[0]) + ".part"));

        // Already there and verified: nothing is fetched again.
        server.Requests.Clear();
        await SpeechModels.Download(model, home, null, CancellationToken.None, server);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task DownloadThrowsAwayAFileWithTheWrongHash()
    {
        var good = Encoding.UTF8.GetBytes("the real model");
        var model = new SpeechModel("test", "Test", SpeechEngine.Parakeet, "", "test", new[] { Fake("m.onnx", good) });
        var server = new FakeServer(_ => Ok(Encoding.UTF8.GetBytes("a fake model!!")));  // same length, other bytes
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => SpeechModels.Download(model, home, null, CancellationToken.None, server));
        Assert.Contains("checksum", e.Message);
        var path = model.PathOf(home, model.Files[0]);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task DownloadCarriesOnFromAPartFile()
    {
        var data = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 7)).ToArray();
        var model = new SpeechModel("test", "Test", SpeechEngine.Parakeet, "", "test", new[] { Fake("m.onnx", data) });
        var path = model.PathOf(home, model.Files[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".part", data[..40_000]);
        var server = new FakeServer(r =>
        {
            var from = (int)r.Headers.Range!.Ranges.First().From!.Value;
            return Ok(data[from..], HttpStatusCode.PartialContent);
        });
        await SpeechModels.Download(model, home, null, CancellationToken.None, server);
        Assert.Equal(40_000, server.Requests.Single().Headers.Range!.Ranges.First().From);
        Assert.Equal(data, File.ReadAllBytes(path));

        // A server that ignores the range sends the whole file: that works too.
        File.Delete(path);
        File.WriteAllBytes(path + ".part", data[..10]);
        await SpeechModels.Download(model, home, null, CancellationToken.None, new FakeServer(_ => Ok(data)));
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void AFileChangedAfterItWasVerifiedIsCheckedAgain()
    {
        var data = Encoding.UTF8.GetBytes("model bytes");
        var f = Fake("m.onnx", data);
        var path = Path.Combine(home, "m.onnx");
        File.WriteAllBytes(path, data);
        Assert.True(SpeechModels.EnsureVerified(path, f));
        Assert.True(File.Exists(path + ".verified"));
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("model bytez"));  // same size, new contents and time
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.False(SpeechModels.EnsureVerified(path, f));
    }

    sealed class SyncProgress(Action<(long, long)> report) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) v) => report(v);
    }

    // MARK: features and decoding

    [Fact]
    public void FftMatchesAPlainDft()
    {
        var rnd = new Random(1);
        var x = Enumerable.Range(0, 64).Select(_ => rnd.NextDouble() - 0.5).ToArray();
        var re = (double[])x.Clone();
        var im = new double[64];
        ParakeetMath.Fft(re, im);
        for (int k = 0; k < 64; k++)
        {
            double r = 0, i = 0;
            for (int n = 0; n < 64; n++) { r += x[n] * Math.Cos(2 * Math.PI * k * n / 64); i -= x[n] * Math.Sin(2 * Math.PI * k * n / 64); }
            Assert.Equal(r, re[k], 9);
            Assert.Equal(i, im[k], 9);
        }
    }

    /// Reference values from NeMo's preprocessor settings in numpy (librosa mel filters), which gave the
    /// same tokens and timestamps as sherpa-onnx on its test recordings.
    [Fact]
    public void FeaturesMatchNeMo()
    {
        var mel = ParakeetMath.MelFilters();
        Assert.Equal(0.028377542974119204, mel[0 * 257 + 1], 6);
        Assert.Equal(0.0008705314052759349, mel[127 * 257 + 255], 6);
        Assert.Equal(0, mel[60 * 257 + 100]);

        var x = Enumerable.Range(0, 8000).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / 16000.0) + 0.1 * Math.Sin(2 * Math.PI * 3000 * i / 16000.0))).ToArray();
        var (feats, frames) = ParakeetMath.Features(x);
        Assert.Equal(51, frames);
        float At(int m, int f) => feats[m * frames + f];
        Assert.Equal(3.6614162921905518, At(0, 0), 3);
        Assert.Equal(-0.2807884216308594, At(10, 5), 3);
        Assert.Equal(-0.20978738367557526, At(20, 25), 3);
        Assert.Equal(-0.26814982295036316, At(64, 40), 3);
        Assert.Equal(1.957103967666626, At(100, 49), 3);
        Assert.Equal(4.164984703063965, At(127, 50), 3);
    }

    [Fact]
    public void TdtDecodingFollowsTokensAndDurations()
    {
        const int vocab = 4, blank = 3;
        // Per call: (token, duration index). Frame 0 says token 1 and stays; then token 2 and jumps 2;
        // frame 2 is blank with duration 0 (moves on by one); frame 3 says 0 and jumps 4 (past the end).
        var script = new Queue<(int Token, int Dur)>(new[] { (1, 0), (2, 2), (blank, 0), (0, 4) });
        var visited = new List<int>();
        var fed = new List<int>();
        float[] Joint(int t)
        {
            visited.Add(t);
            var (tok, dur) = script.Dequeue();
            var l = new float[vocab + ParakeetMath.Durations.Length];
            l[tok] = 5;
            l[vocab + dur] = 5;
            return l;
        }
        var hits = ParakeetMath.GreedyTdt(5, vocab, blank, Joint, fed.Add);
        Assert.Equal(new[] { (1, 0), (2, 0), (0, 3) }, hits);
        Assert.Equal(new[] { 0, 0, 2, 3 }, visited);
        Assert.Equal(new[] { 1, 2, 0 }, fed);
    }

    [Fact]
    public void TdtDecodingNeverSticksOnOneFrame()
    {
        int calls = 0;
        var hits = ParakeetMath.GreedyTdt(2, 3, 2, _ => { calls++; var l = new float[8]; l[0] = 1; l[3] = 1; return l; }, _ => { });
        Assert.Equal(20, hits.Count);  // ten tokens a frame at most
        Assert.Equal(20, calls);
    }

    [Fact]
    public void TokensBecomeTimedLines()
    {
        var tokens = ParakeetMath.ReadTokens(new[] { "<unk> 0", "▁Hello 1", "▁world 2", ". 3", "▁How 4", "▁are 5", "▁you 6", "? 7", "▁again 8", "<blk> 9" });
        Assert.Equal(10, tokens.Length);
        Assert.Equal("▁world", tokens[2]);
        var hits = new List<(int, int)> { (1, 0), (2, 3), (3, 5), (4, 8), (5, 10), (6, 12), (0, 13), (8, 40) };
        var lines = ParakeetMath.Segments(hits, tokens, 30);
        Assert.Equal(new[] { "Hello world.", "How are you", "again" }, lines.Select(l => l.Text));
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(0.64, lines[1].Start);  // frame 8 × 80 ms
        Assert.True(lines[1].End <= lines[2].Start);
        Assert.Equal(3.2, lines[2].Start);  // after a pause
        Assert.Empty(ParakeetMath.Segments(new List<(int, int)> { (3, 0), (7, 2) }, tokens, 30));  // punctuation only
    }

    [Fact]
    public void LongSpeechIsCutIntoLines()
    {
        var tokens = ParakeetMath.ReadTokens(new[] { "▁word 0" });
        var hits = Enumerable.Range(0, 300).Select(i => (0, i * 5)).ToList();  // a word every 0.4 s for 2 minutes
        var lines = ParakeetMath.Segments(hits, tokens, 120);
        Assert.True(lines.Count >= 9);
        Assert.All(lines, l => Assert.True(l.End - l.Start <= 12.5));
    }

    // MARK: database

    [Fact]
    public void TranscriptsRecordTheModelAndReTranscribingReplacesThem()
    {
        var config = Config.Load(new[] { "--home", home });
        using (var db = new Database(config.DbPath))
        {
            var t = DateTime.UtcNow.AddHours(-3);
            var old = db.InsertChunk("old.wav", Time.Iso(DateTime.UtcNow.AddDays(-3)), "microphone", true);
            var id = db.InsertChunk("a.wav", Time.Iso(t), "microphone", true);
            db.SaveTranscripts(db.ChunkById(old)!, new[] { new Segment(0, 1, "older") });
            db.SaveTranscripts(db.ChunkById(id)!, new[] { new Segment(0, 1, "whisper heard this") }, "whisper.cpp:ggml-small");

            Assert.Equal((1, 30.0), db.RetranscribeCount(DateTime.UtcNow.AddDays(-1), 30));
            Assert.Equal(1, db.RequeueAudio(DateTime.UtcNow.AddDays(-1)));
            Assert.Equal("pending", db.ChunkById(id)!.Status);
            Assert.Equal("transcribed", db.ChunkById(old)!.Status);
            // The old line stays searchable until the new one is in.
            Assert.Equal(1, db.SearchAudio(new SearchQuery { Q = "whisper" }, 10).Total);

            db.SaveTranscripts(db.NextPendingChunk()!, new[] { new Segment(0, 1, "parakeet heard this") }, ParakeetEngine.Tag);
            Assert.Equal(0, db.SearchAudio(new SearchQuery { Q = "whisper" }, 10).Total);
            Assert.Equal(1, db.SearchAudio(new SearchQuery { Q = "parakeet" }, 10).Total);
            Assert.Equal("transcribed", db.ChunkById(id)!.Status);
        }
        using var conn = new SqliteConnection($"Data Source={config.DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT engine FROM audio_transcriptions ORDER BY id";
        using var r = cmd.ExecuteReader();
        var engines = new List<string>();
        while (r.Read()) engines.Add(r.GetString(0));
        Assert.Equal(new[] { "whisper.cpp", ParakeetEngine.Tag }, engines);
    }

    [Fact]
    public void FreshClipsGoBeforeAReTranscribe()
    {
        var config = Config.Load(new[] { "--home", home });
        using var db = new Database(config.DbPath);
        var old = db.InsertChunk("old.wav", Time.Iso(DateTime.UtcNow.AddDays(-2)), "microphone", true);
        var fresh = db.InsertChunk("new.wav", Time.Iso(DateTime.UtcNow.AddMinutes(-1)), "microphone", true);
        Assert.Equal(fresh, db.NextPendingChunk()!.Id);
        db.SaveTranscripts(db.ChunkById(fresh)!, new[] { new Segment(0, 1, "hi there") });
        Assert.Equal(old, db.NextPendingChunk()!.Id);
    }

    // MARK: the real model (opt-in)

    /// Runs only when OPENREWIND_PARAKEET_DIR points at the downloaded model folder (the four files).
    /// The recording is OPENREWIND_PARAKEET_WAV, or test_wavs/en.wav from the sherpa-onnx model archive.
    [Fact]
    public void ParakeetTranscribesARealRecording()
    {
        var dir = Environment.GetEnvironmentVariable("OPENREWIND_PARAKEET_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "encoder.int8.onnx")))
        {
            output.WriteLine("skipped: set OPENREWIND_PARAKEET_DIR to run Parakeet on a real recording");
            return;
        }
        var wav = Environment.GetEnvironmentVariable("OPENREWIND_PARAKEET_WAV") ?? Path.Combine(dir, "test_wavs", "en.wav");
        var samples = Wav.Load16k(wav);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var engine = new ParakeetEngine(dir, Math.Max(2, Environment.ProcessorCount / 2));
        var load = sw.Elapsed.TotalSeconds;
        // 30 s, like a recorded chunk: the clip repeated.
        var chunk = Enumerable.Repeat(samples, (int)Math.Ceiling(30.0 * 16000 / samples.Length)).SelectMany(s => s).Take(30 * 16000).ToArray();
        engine.Transcribe(samples);  // warm up
        sw.Restart();
        var segs = engine.Transcribe(chunk);
        var secs = sw.Elapsed.TotalSeconds;
        var text = string.Join(" ", segs.Select(s => s.Text));
        output.WriteLine($"load {load:0.00}s, 30 s of audio in {secs:0.00}s, real-time factor {secs / 30:0.000} on {Environment.ProcessorCount} cores");
        foreach (var s in segs) output.WriteLine($"{s.Start,6:0.00} {s.End,6:0.00} {s.Text}");
        if (Environment.GetEnvironmentVariable("OPENREWIND_PARAKEET_WAV") == null)
            Assert.Contains("ask not what your country can do for you", text.ToLowerInvariant().Replace(",", ""));
        Assert.NotEmpty(segs);
        Assert.All(segs, s => Assert.True(s.Start >= 0 && s.End <= 30.5 && s.End >= s.Start));

        // The whole path the app takes: picked in settings, files checked, a recorded chunk in, lines in the database.
        var m = SpeechModels.ById(SpeechModels.ParakeetId);
        Directory.CreateDirectory(m.Dir(home));
        foreach (var f in m.Files) File.CreateSymbolicLink(m.PathOf(home, f), Path.Combine(dir, f.Name));
        var config = Config.Load(new[] { "--home", home, "--model", SpeechModels.ParakeetId });
        using var db = new Database(config.DbPath);
        var tx = Transcriber.Create(config, db, out var problem);
        Assert.Null(problem);
        var file = Path.Combine(config.DataDir, "chunk.wav");
        Wav.Write(chunk, 16000, file);
        var id = db.InsertChunk(file, Time.Iso(DateTime.UtcNow.AddMinutes(-1)), "microphone", true);
        tx!.Transcribe(db.ChunkById(id)!);
        Assert.Equal("transcribed", db.ChunkById(id)!.Status);
        Assert.Null(tx.Status.Problem);
        Assert.Equal(30, tx.Status.LastAudioSeconds, 1);
        Assert.True(db.SearchAudio(new SearchQuery { Q = "country" }, 50).Total >= 10);
    }
}
