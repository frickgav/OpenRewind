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
                Assert.EndsWith("/" + f.Name.Replace(SpeechModels.OwnSuffix, ""), f.Url);
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
    public void WhisperDownloadsLandWhereWhisperLooksWithoutReplacingTheUsersOwnFile()
    {
        var m = SpeechModels.ById("whisper-small");
        Assert.Equal(Path.Combine(home, "models", "ggml-small.openrewind.bin"), m.PathOf(home, m.Files[0]));
        Assert.EndsWith("/ggml-small.bin", m.Files[0].Url);
        Directory.CreateDirectory(Path.Combine(home, "models"));
        File.WriteAllBytes(m.PathOf(home, m.Files[0]), new byte[3]);
        Assert.Equal(m.PathOf(home, m.Files[0]), Transcriber.FindModel(null, home));
        Assert.False(SpeechModels.IsDownloaded(m, home));  // wrong size
        // The user's own ggml-small.bin is another file: a download never writes over it.
        Assert.NotEqual(Path.Combine(home, "models", "ggml-small.bin"), m.PathOf(home, m.Files[0]));
    }

    [Fact]
    public void ParakeetWarnsAboutLanguagesItDoesNotKnow()
    {
        var p = SpeechModels.ById(SpeechModels.ParakeetId);
        Assert.Null(SpeechModels.LanguageWarning(p, "auto"));
        Assert.Null(SpeechModels.LanguageWarning(p, "de"));
        Assert.Null(SpeechModels.LanguageWarning(p, "EN"));
        Assert.Contains("(ja)", SpeechModels.LanguageWarning(p, "ja"));
        Assert.Null(SpeechModels.LanguageWarning(SpeechModels.ById("whisper-small"), "ja"));
        Assert.Equal(25, SpeechModels.ParakeetLanguages.Count);
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
        Assert.False(File.Exists(path + ".verified"));  // nothing on disk that could vouch for a swapped file
        var written = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("model bytez"));  // same size, new contents and time
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.False(SpeechModels.EnsureVerified(path, f));
        // Even with its old date put back, a new run of the app hashes it again.
        File.SetLastWriteTimeUtc(path, written);
        SpeechModels.ForgetVerified();
        Assert.False(SpeechModels.EnsureVerified(path, f));
    }

    [Fact]
    public async Task ADownloadCutShortKeepsWhatCame()
    {
        var data = Enumerable.Range(0, 50_000).Select(i => (byte)(i * 3)).ToArray();
        var model = new SpeechModel("test", "Test", SpeechEngine.Parakeet, "", "test", new[] { Fake("m.onnx", data) });
        var path = model.PathOf(home, model.Files[0]);
        var e = await Assert.ThrowsAsync<IOException>(() =>
            SpeechModels.Download(model, home, null, CancellationToken.None, new FakeServer(_ => Ok(data[..20_000]))));
        Assert.Contains("Try Again", e.Message);
        Assert.Equal(20_000, new FileInfo(path + ".part").Length);
        // Try Again asks for the rest only.
        var server = new FakeServer(r => Ok(data[(int)r.Headers.Range!.Ranges.First().From!.Value..], HttpStatusCode.PartialContent));
        await SpeechModels.Download(model, home, null, CancellationToken.None, server);
        Assert.Equal(20_000, server.Requests.Single().Headers.Range!.Ranges.First().From);
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    /// Sends a few bytes, then nothing.
    sealed class StalledStream(byte[] start) : Stream
    {
        int at;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (at < start.Length)
            {
                int n = Math.Min(buffer.Length, start.Length - at);
                start.AsMemory(at, n).CopyTo(buffer);
                at += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
    }

    [Fact]
    public async Task ADownloadThatStallsGivesUpAndKeepsWhatCame()
    {
        var data = Enumerable.Range(0, 30_000).Select(i => (byte)i).ToArray();
        var model = new SpeechModel("test", "Test", SpeechEngine.Parakeet, "", "test", new[] { Fake("m.onnx", data) });
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream(data[..1000])) });
        var e = await Assert.ThrowsAsync<IOException>(() =>
            SpeechModels.Download(model, home, null, CancellationToken.None, server, TimeSpan.FromMilliseconds(300)));
        Assert.Contains("no data from the server", e.Message);
        Assert.Equal(1000, new FileInfo(model.PathOf(home, model.Files[0]) + ".part").Length);
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
    public void LongAudioGoesThroughTheEncoderInBoundedWindows()
    {
        // Five minutes of a tone, with a quiet stretch at 27 s into each 30 s.
        var samples = new float[5 * 60 * 16000];
        for (int i = 0; i < samples.Length; i++)
        {
            var sec = i / 16000.0 % 30;
            samples[i] = sec is > 26.9 and < 27.2 ? 0 : (float)(0.3 * Math.Sin(i * 0.05));
        }
        var windows = ParakeetMath.Windows(samples);
        Assert.True(windows.Count >= 10);
        Assert.Equal(0, windows[0].Start);
        int next = 0;
        foreach (var (start, length) in windows)
        {
            Assert.Equal(next, start);  // end to end, nothing left out or read twice
            Assert.Equal(0, start % ParakeetMath.StepSamples);  // frame numbers add up
            Assert.InRange(length, 1, ParakeetMath.MaxWindowSamples);
            next = start + length;
        }
        Assert.Equal(samples.Length, next);
        // The first cut lands in the quiet stretch, not in the middle of the tone.
        var cut = windows[0].Length / 16000.0;
        Assert.InRange(cut, 26.9, 27.2);
        // Each window's features are bounded: 30 s is 3001 frames.
        var (_, frames) = ParakeetMath.Features(samples[windows[1].Start..(windows[1].Start + windows[1].Length)]);
        Assert.True(frames <= 3001);
        // Short audio is one window.
        Assert.Single(ParakeetMath.Windows(new float[16000 * 30]));
        Assert.Empty(ParakeetMath.Windows(Array.Empty<float>()));
    }

    /// Clip lengths around the 30 s window and the 80 ms slice each cut is scored on. A 30.01 s clip
    /// used to read past the end of the audio (and crash the Mac app).
    public static IEnumerable<object[]> WindowEdgeLengths() => new[]
    {
        0, 1, 16000 * 29 + 8000, 16000 * 30 - 1, 16000 * 30, 16000 * 30 + 1, 480160 /* 30.01 s */,
        16000 * 30 + 320, 16000 * 30 + 639, 16000 * 30 + 640, 16000 * 30 + 641,
        16000 * 30 + 1280, 16000 * 34, 16000 * 60, 16000 * 60 + 1, 960160 /* 60.01 s */, 1440160 /* 90.01 s */,
    }.SelectMany(n => new[] { "silence", "tone", "noise" }.Select(kind => new object[] { n, kind }));

    [Theory]
    [MemberData(nameof(WindowEdgeLengths))]
    public void WindowsCoverAnyLengthWithoutReadingPastTheEnd(int n, string kind)
    {
        var rng = new Random(n);
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = kind switch { "tone" => (float)(0.3 * Math.Sin(i * 0.05)), "noise" => (float)(rng.NextDouble() - 0.5), _ => 0 };
        AssertGoodWindows(samples);
    }

    [Fact]
    public void WindowCutsStayInsideTheSearchStretch()
    {
        const int Sec = 16000, Half = ParakeetMath.StepSamples / 2;
        float[] ToneWithQuietAt(int n, int at)
        {
            var s = new float[n];
            for (int i = 0; i < n; i++) s[i] = i >= at - Half && i < at + Half ? 0 : (float)(0.3 * Math.Sin(i * 0.05));
            return s;
        }
        // Quiet right at the far edge of the 4 s search (26 s): the cut goes there.
        Assert.Equal(26 * Sec, AssertGoodWindows(ToneWithQuietAt(31 * Sec, 26 * Sec))[0].Length);
        // Quiet right at 30 s, and just enough audio after it to score that spot: cut at 30 s.
        var w = AssertGoodWindows(ToneWithQuietAt(30 * Sec + Half, 30 * Sec));
        Assert.Equal(30 * Sec, w[0].Length);
        Assert.Equal(Half, w[1].Length);
        // One sample short of that: 30 s cannot be scored, so the cut is a step earlier, not past the end.
        w = AssertGoodWindows(ToneWithQuietAt(30 * Sec + Half - 1, 30 * Sec));
        Assert.True(w[0].Length <= 30 * Sec - ParakeetMath.StepSamples);
        // Quiet just past 26 s, out of reach: the cut still lands within the last 4 s.
        w = AssertGoodWindows(ToneWithQuietAt(40 * Sec, 26 * Sec - ParakeetMath.StepSamples));
        Assert.InRange(w[0].Length, 26 * Sec, 30 * Sec);
    }

    /// End to end, nothing left out or read twice; starts on encoder steps; at most 30 s; every cut in the last 4 s.
    static List<(int Start, int Length)> AssertGoodWindows(float[] samples)
    {
        var windows = ParakeetMath.Windows(samples);
        int next = 0;
        for (int k = 0; k < windows.Count; k++)
        {
            var (start, length) = windows[k];
            Assert.Equal(next, start);
            Assert.Equal(0, start % ParakeetMath.StepSamples);
            Assert.InRange(length, 1, ParakeetMath.MaxWindowSamples);
            if (k < windows.Count - 1) Assert.True(length >= ParakeetMath.MaxWindowSamples - 4 * 16000);
            next = start + length;
        }
        Assert.Equal(samples.Length, next);
        if (samples.Length <= ParakeetMath.MaxWindowSamples) Assert.True(windows.Count == (samples.Length == 0 ? 0 : 1));
        return windows;
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
    public void ReTranscribeLeavesSilentClipsAndMeetingNotesAlone()
    {
        var config = Config.Load(new[] { "--home", home });
        using var db = new Database(config.DbPath);
        var t = DateTime.UtcNow.AddHours(-2);
        var silent = db.InsertChunk("quiet.wav", Time.Iso(t), "microphone", true);
        var spoken = db.InsertChunk("talk.wav", Time.Iso(t.AddMinutes(1)), "microphone", true);
        db.SaveTranscripts(db.ChunkById(silent)!, Array.Empty<Segment>());
        db.SaveTranscripts(db.ChunkById(spoken)!, new[] { new Segment(0, 1, "the plan") });
        var meeting = db.InsertMeeting(new MeetingSpan(t, t.AddMinutes(10), "Zoom", "zoom.us", "", "", false), MeetingStatus.Ended);
        var earlier = db.InsertMeeting(new MeetingSpan(t.AddDays(-3), t.AddDays(-3).AddMinutes(10), "Zoom", "zoom.us", "", "", false), MeetingStatus.Ended);

        Assert.Equal(1, db.RetranscribeCount(DateTime.UtcNow.AddDays(-1), 30).Clips);
        Assert.Equal(1, db.RequeueAudio(DateTime.UtcNow.AddDays(-1)));
        Assert.Equal("silent", db.ChunkById(silent)!.Status);
        Assert.Equal("pending", db.ChunkById(spoken)!.Status);
        // Notes of that day are not made again because of the new lines; older meetings are untouched.
        Assert.True(db.MeetingById(meeting)!.Redone);
        Assert.False(db.MeetingById(earlier)!.Redone);
    }

    [Fact]
    public void ReTranscribeLetsSearchByMeaningIndexTheNewTextAgain()
    {
        var config = Config.Load(new[] { "--home", home });
        using (var db = new Database(config.DbPath))
        {
            var old = db.InsertChunk("old.wav", Time.Iso(DateTime.UtcNow.AddDays(-3)), "microphone", true);
            var recent = db.InsertChunk("recent.wav", Time.Iso(DateTime.UtcNow.AddHours(-2)), "microphone", true);
            db.SaveTranscripts(db.ChunkById(old)!, new[] { new Segment(0, 1, "old words") });
            db.SaveTranscripts(db.ChunkById(recent)!, new[] { new Segment(0, 1, "whisper words") });
            db.SetMeaningState(Database.ChunkCursor, recent.ToString());
            using (var conn = new SqliteConnection($"Data Source={config.DbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, text) VALUES
                        ('audio', '', '', {old}, {old}, 'old words'), ('audio', '', '', {recent}, {recent}, 'whisper words');
                    """;
                cmd.ExecuteNonQuery();
            }
            Assert.Equal(1, db.RequeueAudio(DateTime.UtcNow.AddDays(-1)));
            // The clip's old vector is gone and the indexer goes back to just before it; the older clip keeps its own.
            Assert.Equal((recent - 1).ToString(), db.MeaningState(Database.ChunkCursor));
        }
        using var check = new SqliteConnection($"Data Source={config.DbPath}");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT text FROM meaning_moments ORDER BY id";
        using var r = q.ExecuteReader();
        var left = new List<string>();
        while (r.Read()) left.Add(r.GetString(0));
        Assert.Equal(new[] { "old words" }, left);
    }

    [Fact]
    public void WordsOfAClipDeletedWhileItWasTranscribedAreDropped()
    {
        var config = Config.Load(new[] { "--home", home });
        using var db = new Database(config.DbPath);
        var id = db.InsertChunk("gone.wav", Time.Iso(DateTime.UtcNow.AddMinutes(-5)), "microphone", true);
        var chunk = db.ChunkById(id)!;
        db.DeleteAudio(null, null);
        Assert.False(db.SaveTranscripts(chunk, new[] { new Segment(0, 1, "deleted words") }));
        Assert.Equal(0, db.AudioStats().Segments);
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

        // Five minutes in one go (ingest-audio of a talk) goes through in 30 s windows, times in order to the end.
        var five = Enumerable.Repeat(chunk, 10).SelectMany(s => s).ToArray();
        sw.Restart();
        var long_ = engine.Transcribe(five);
        output.WriteLine($"5 minutes in {sw.Elapsed.TotalSeconds:0.0}s, {long_.Count} lines, peak memory {System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / 1_000_000} MB");
        Assert.True(long_.Count >= 10 * segs.Count - 10);
        Assert.True(long_.Zip(long_.Skip(1)).All(p => p.First.Start <= p.Second.Start && p.First.End <= p.Second.Start + 0.01));
        Assert.InRange(long_[^1].End, 285, 300.5);

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
