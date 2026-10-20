using System.Text.Json.Nodes;
using OpenRewind;

/// Search by Meaning: tokenizer, moments, vectors, and that blocked content never comes back.
public class MeaningTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-test-" + Guid.NewGuid());
    readonly Database db;

    public MeaningTests() { db = new Database(Path.Combine(home, "db.sqlite")); }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    // MARK: tokenizer

    /// What Hugging Face's tokenizer for all-MiniLM-L6-v2 gives for these (tokenizers 0.x, no special tokens).
    static readonly (string Text, string Pieces)[] Reference =
    {
        ("Hello, world!", "hello , world !"),
        ("The Q3 budget review is on Thursday at 2:15 PM.", "the q ##3 budget review is on thursday at 2 : 15 pm ."),
        ("unaffable naïve café résumé", "una ##ffa ##ble naive cafe resume"),
        ("Ünïcödé ÀÉÎÕÜ straße", "unicode ae ##io ##u st ##raße"),
        ("e-mail: someone@example.com, https://github.com/frickgav/OpenRewind?tab=readme",
         "e - mail : someone @ example . com , https : / / gi ##th ##ub . com / fr ##ick ##ga ##v / open ##rew ##ind ? tab = read ##me"),
        ("中文字符 and 日本語 mixed", "中 文 [UNK] [UNK] and 日 本 語 mixed"),
        ("tabs\tand\nnewlines\r\n  spaces", "tab ##s and new ##lines spaces"),
        ("don't won't it's (parentheses) [brackets] {braces}",
         "don ' t won ' t it ' s ( parentheses ) [ brackets ] { brace ##s }"),
        ("supercalifragilisticexpialidocious antidisestablishmentarianism",
         "super ##cal ##if ##rag ##ilis ##tic ##ex ##pia ##lid ##oc ##ious anti ##dis ##est ##ab ##lish ##ment ##arian ##ism"),
        ("Price: $1,299.99 — 50% off!!!", "price : $ 1 , 299 . 99 — 50 % off ! ! !"),
        ("emoji 😀 test", "em ##oj ##i [UNK] test"),
        // Control characters (\v, \f, U+0085, zero-width space) are dropped; other Unicode spaces split words.
        ("a\vb a\fb a\u0085b", "ab ab ab"),
        ("a\u2028b a\u2029b a\u00a0b a\u3000b", "a b a b a b a b"),
        ("zero\u200bwidth line\u2028break", "zero ##wi ##dt ##h line break"),
        (new string('x', 120), "[UNK]"),
        ("", ""),
    };

    /// The pieces above plus the special tokens. Greedy longest-match picks the same pieces from
    /// this smaller vocabulary as from the full one, since every piece the full one picked is in it.
    static WordPiece SmallTokenizer(int maxLength = 256)
    {
        var tokens = new List<string> { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" };
        foreach (var (_, pieces) in Reference)
            foreach (var p in pieces.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!tokens.Contains(p)) tokens.Add(p);
        // Decoys that greedy matching must not pick over the longer pieces.
        tokens.AddRange(new[] { "un", "##f", "##b", "super", "s", "##a" });
        return new WordPiece(tokens.Distinct().ToList(), maxLength);
    }

    [Fact]
    public void TokenizerMatchesBert()
    {
        var tok = SmallTokenizer();
        foreach (var (text, pieces) in Reference)
            Assert.Equal(pieces, string.Join(' ', tok.Tokenize(text)));
    }

    [Fact]
    public void EncodeAddsClsAndSepAndCuts()
    {
        var tok = SmallTokenizer(maxLength: 8);
        var ids = tok.Encode("Hello, world!");
        Assert.Equal(new long[] { tok.Cls, ids[1], ids[2], ids[3], ids[4], tok.Sep }, ids);
        var long_ = tok.Encode(string.Join(' ', Enumerable.Repeat("hello world", 20)));
        Assert.Equal(8, long_.Length);
        Assert.Equal(tok.Cls, long_[0]);
        Assert.Equal(tok.Sep, long_[^1]);
        Assert.Equal(new long[] { tok.Cls, tok.Sep }, tok.Encode("   "));
    }

    [Fact]
    public void TokenizerLoadsVocabFile()
    {
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "vocab.txt");
        File.WriteAllLines(path, new[] { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "rewind", "##ing", "open" });
        var tok = WordPiece.Load(path);
        Assert.Equal(new long[] { 2, 6, 4, 5, 3 }, tok.Encode("Open rewinding"));
    }

    // MARK: vectors

    [Fact]
    public void QuantizedScoreIsCosine()
    {
        var rnd = new Random(7);
        float[] Rand() => Vectors.Normalize(Enumerable.Range(0, 384).Select(_ => (float)(rnd.NextDouble() * 2 - 1)).ToArray());
        for (int i = 0; i < 20; i++)
        {
            var a = Rand(); var b = Rand();
            var exact = a.Zip(b, (x, y) => x * y).Sum();
            Assert.InRange(Vectors.Score(Vectors.Quantize(a), b), exact - 0.02f, exact + 0.02f);
        }
        var v = Rand();
        Assert.InRange(Vectors.Score(Vectors.Quantize(v), v), 0.999f, 1.001f);
        Assert.Equal(0, Vectors.Score(new byte[384], v));
        Assert.Equal(float.NegativeInfinity, Vectors.Score(new byte[3], v));
    }

    [Fact]
    public void MeanPoolAveragesTokensToLengthOne()
    {
        var v = Vectors.MeanPool(new float[] { 1, 0, 3, 0, 2, 0 }, 3, 2);
        Assert.Equal(1f, v[0], 1e-5f);
        Assert.Equal(0f, v[1], 1e-5f);
        var w = Vectors.MeanPool(new float[] { 3, 4 }, 1, 2);
        Assert.Equal(0.6f, w[0], 1e-5f);
        Assert.Equal(0.8f, w[1], 1e-5f);
    }

    [Fact]
    public void TopKKeepsTheBestInOrder()
    {
        var top = new TopK<string>(3);
        foreach (var (s, score) in new[] { ("a", 0.1f), ("b", 0.9f), ("c", 0.5f), ("d", 0.7f), ("e", -1f), ("f", 0.8f), ("g", float.NaN) })
            top.Add(s, score);
        Assert.Equal(new[] { "b", "f", "d" }, top.Best().Select(x => x.Item));
        Assert.Equal(0.7f, top.Floor);
        Assert.Empty(new TopK<int>(5).Best());
    }

    // MARK: moments

    static MomentFrame F(long id, int sec, string app, string window, string text, string url = "") =>
        new(id, Time.Iso(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc).AddSeconds(sec)), app, window, url, text);

    static readonly Func<MomentFrame, bool> None = _ => false;

    [Fact]
    public void OneMomentPerWindowStretchWithoutRepeats()
    {
        var frames = new[]
        {
            F(1, 0, "Chrome", "Budget", "Q3 budget\nTravel 1200"),
            F(2, 2, "Chrome", "Budget", "Q3 budget\nTravel 1200"),          // same screen
            F(3, 4, "Chrome", "Budget", "Q3 budget\nTravel 1200\nHotels 800"), // one new line
            F(4, 6, "Code", "main.cs", "class Program"),
            F(5, 8, "Chrome", "Budget", "Q3 budget"),
        };
        var (moments, done) = MomentChunker.Screen(frames, complete: true, None);
        Assert.Equal(5, done);
        Assert.Equal(3, moments.Count);
        Assert.Equal("Q3 budget\nTravel 1200\nHotels 800", moments[0].Text);
        Assert.Equal((1L, 3L), (moments[0].FirstId, moments[0].LastId));
        Assert.Equal(frames[0].Timestamp, moments[0].Start);
        Assert.Equal(frames[2].Timestamp, moments[0].End);
        Assert.Equal("Code", moments[1].AppName);
        Assert.Equal((5L, 5L), (moments[2].FirstId, moments[2].LastId));
        Assert.Equal("Budget — Chrome\nQ3 budget\nTravel 1200\nHotels 800", MomentChunker.EmbedText(moments[0]));
    }

    [Fact]
    public void BreaksAndPageChangesStartNewMoments()
    {
        var frames = new[]
        {
            F(1, 0, "Chrome", "Docs", "alpha line", "https://a.example/1"),
            F(2, 10, "Chrome", "Docs", "beta line", "https://a.example/2"),   // new page
            F(3, 400, "Chrome", "Docs", "gamma line", "https://a.example/2"), // after a long break
            F(4, 402, "Chrome", "Docs", ""),                                  // no text: skipped
            F(5, 404, "Chrome", "Docs", "delta line", "https://a.example/2"),
        };
        var (moments, _) = MomentChunker.Screen(frames, complete: true, None);
        Assert.Equal(new[] { "alpha line", "beta line", "gamma line\ndelta line" }, moments.Select(m => m.Text));

        // A long stretch is cut every 10 minutes.
        var reading = Enumerable.Range(0, 400).Select(i => F(i + 1, i * 2, "Books", "Novel", $"page {i}")).ToList();
        var (parts, _) = MomentChunker.Screen(reading, complete: true, None);
        Assert.True(parts.Count >= 2);
        Assert.All(parts, m => Assert.True(Time.ParseIso(m.End) - Time.ParseIso(m.Start) <= MomentChunker.MaxLength));
    }

    [Fact]
    public void LongTextIsSplitAtTheBudget()
    {
        var frames = Enumerable.Range(0, 40).Select(i => F(i + 1, i * 2, "Slack", "general", $"message number {i} " + new string('x', 80))).ToList();
        var (moments, _) = MomentChunker.Screen(frames, complete: true, None);
        Assert.True(moments.Count >= 2);
        Assert.All(moments, m => Assert.True(m.Text.Length <= MomentChunker.TextBudget));
        Assert.Equal(40, moments.Sum(m => m.Text.Split('\n').Length));
    }

    [Fact]
    public void BlockedFramesAreNeverInAMoment()
    {
        var frames = new[]
        {
            F(1, 0, "Chrome", "Mail", "lunch on friday"),
            F(2, 2, "1Password", "Vault", "bank password hunter2"),
            F(3, 4, "Chrome", "Mail", "lunch on friday\nsee you there"),
        };
        var (moments, _) = MomentChunker.Screen(frames, complete: true, f => f.AppName == "1Password");
        Assert.Equal(2, moments.Count);
        Assert.DoesNotContain(moments, m => m.Text.Contains("hunter2") || m.AppName == "1Password");
    }

    [Fact]
    public void AnUnfinishedStretchWaitsForTheNextBatch()
    {
        var frames = new[] { F(1, 0, "Code", "a.cs", "one"), F(2, 2, "Chrome", "b", "two"), F(3, 4, "Chrome", "b", "three") };
        var (moments, done) = MomentChunker.Screen(frames, complete: false, None);
        Assert.Single(moments);
        Assert.Equal(1, done);
        // When the stretch is the whole batch, it is closed anyway so indexing moves on.
        var (again, done2) = MomentChunker.Screen(frames[1..], complete: false, None);
        Assert.Single(again);
        Assert.Equal(3, done2);
    }

    [Fact]
    public void OneAudioMomentPerClip()
    {
        var segs = new[]
        {
            new MomentSegment(7, "2026-10-18T09:00:01.000Z", " Let's hire  a designer ", true),
            new MomentSegment(7, "2026-10-18T09:00:05.000Z", "next quarter.", true),
            new MomentSegment(8, "2026-10-18T09:00:31.000Z", "[music]", false),
            new MomentSegment(9, "2026-10-18T09:01:01.000Z", " ", false),
        };
        var m = MomentChunker.Audio(segs);
        Assert.Equal(2, m.Count);
        Assert.Equal("Let's hire a designer next quarter.", m[0].Text);
        Assert.Equal(("Microphone", 7L, "2026-10-18T09:00:01.000Z"), (m[0].AppName, m[0].FirstId, m[0].Start));
        Assert.Equal("System audio", m[1].AppName);
    }

    // MARK: index and search

    /// Words about the same thing land on the same axis, so "bill" finds "invoice".
    sealed class FakeEmbedder : IEmbedder
    {
        public string ModelId { get; set; } = "fake-1";
        public int Calls;
        static readonly string[][] Topics =
        {
            new[] { "invoice", "bill", "payment", "paid" },
            new[] { "dog", "puppy", "walk" },
            new[] { "hire", "hiring", "designer", "interview" },
            new[] { "password", "vault", "bank" },
        };
        public float[] Embed(string text)
        {
            Calls++;
            var v = new float[16];
            foreach (var w in WordPiece.Words(text))
            {
                var t = Array.FindIndex(Topics, ws => ws.Contains(w));
                v[t >= 0 ? t : 4 + (Math.Abs(w.GetHashCode()) % 12)] += t >= 0 ? 3 : 0.2f;
            }
            return Vectors.Normalize(v);
        }
    }

    long AddFrame(DateTime t, string app, string window, string text, string url = "") =>
        db.Insert(new Frame(0, Time.Iso(t), app, window, true, "", text, "ocr", 0, url));

    long AddClip(DateTime t, bool mic, params string[] lines)
    {
        var id = db.InsertChunk(Path.Combine(home, $"{t.Ticks}.wav"), Time.Iso(t), mic ? "microphone" : "system audio", mic);
        db.SaveTranscripts(db.ChunkById(id)!, lines.Select((l, i) => new Segment(i * 3, i * 3 + 3, l)).ToList());
        return id;
    }

    static readonly Blocklist Nothing = new(Array.Empty<BlockedApp>(), Array.Empty<string>(), false);

    MeaningService Indexed(Blocklist list, out FakeEmbedder e)
    {
        var fake = e = new FakeEmbedder();
        var s = new MeaningService(db) { Embedder = fake, Blocked = () => list, Pace = () => MeaningPace.Idle };
        while (s.Step()) { }
        return s;
    }

    [Fact]
    public void FindsByMeaningAndSkipsBlockedApps()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        AddFrame(t.AddMinutes(3), "Notes", "Weekend", "take the puppy to the park");
        AddFrame(t.AddMinutes(6), "1Password", "Vault", "bank login and password");
        AddFrame(t.AddMinutes(9), "Chrome", "Chase", "bank statement", "https://www.chase.com/statements");
        AddClip(t.AddMinutes(12), true, "we should start hiring", "a designer soon");

        var list = new Blocklist(new[] { new BlockedApp("1password", "1Password") }, Array.Empty<string>(), true, sites: new[] { "chase.com" });
        var s = Indexed(list, out _);
        var p = s.Progress();
        Assert.True(p.UpToDate);
        Assert.Equal(3, p.Moments);   // invoice, puppy, the clip; nothing from the vault or the bank site

        var bill = s.Search("did I pay that bill", new MeaningQuery(), list);
        Assert.Equal("Chrome", bill[0].AppName);
        Assert.Contains("Invoice 1042", bill[0].Text);
        var hire = s.Search("interview candidates", new MeaningQuery(), list);
        Assert.True(hire[0].IsAudio);
        Assert.Equal("we should start hiring a designer soon", hire[0].Text);
        Assert.DoesNotContain(s.Search("bank password", new MeaningQuery(), Nothing), h => h.AppName == "1Password" || h.Url.Contains("chase"));

        // Screen only, and one app only.
        Assert.All(s.Search("hiring", new MeaningQuery { Audio = false }, list), h => Assert.False(h.IsAudio));
        Assert.All(s.Search("bill", new MeaningQuery { App = "Notes" }, list), h => Assert.True(h.IsAudio || h.AppName == "Notes"));
    }

    [Fact]
    public void BlockingLaterHidesWhatWasIndexedBefore()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Chrome", "Acme portal", "Invoice 1042 due Friday", "https://billing.acme.com/1042");
        AddFrame(t.AddMinutes(3), "Notes", "Weekend", "take the puppy to the park");
        var s = Indexed(Nothing, out _);
        Assert.Contains(s.Search("invoice", new MeaningQuery(), Nothing), h => h.AppName == "Chrome");

        foreach (var later in new[]
        {
            new Blocklist(new[] { new BlockedApp("chrome", "Chrome") }, Array.Empty<string>(), false),
            new Blocklist(Array.Empty<BlockedApp>(), new[] { "Acme" }, false),
            new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false, sites: new[] { "acme.com" }),
        })
        {
            var hits = s.Search("invoice", new MeaningQuery(), later);
            Assert.DoesNotContain(hits, h => h.AppName == "Chrome");
            Assert.Contains(hits, h => h.AppName == "Notes");
        }

        // The Ask AI lookup goes through the same filter.
        var tools = new AiTools(db) { Meaning = s, Blocked = new Blocklist(Array.Empty<BlockedApp>(), new[] { "Acme" }, false) };
        Assert.Contains(tools.Offered, x => x.Name == "search_by_meaning");
        var answer = tools.Run(new AiToolCall("1", "search_by_meaning", new JsonObject { ["query"] = "unpaid invoice" }));
        Assert.DoesNotContain("1042", answer);
        Assert.Contains("puppy", answer);
        Assert.Equal("Searched by meaning for “x”", AiTools.Describe(new AiToolCall("2", "search_by_meaning", new JsonObject { ["query"] = "x" })));
        Assert.DoesNotContain(new AiTools(db).Offered, x => x.Name == "search_by_meaning");
    }

    [Fact]
    public void KeepsUpAndWaitsForWhisperAndNewScreens()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        var pending = db.InsertChunk(Path.Combine(home, "p.wav"), Time.Iso(t.AddMinutes(1)), "microphone", true);
        AddClip(t.AddMinutes(2), true, "let's walk the dog");
        AddFrame(DateTime.UtcNow, "Notes", "Now", "still typing this");   // too new: the window may still change
        var s = Indexed(Nothing, out var e);
        Assert.Equal(1, s.Progress().Moments);
        Assert.True(s.Progress().UpToDate);      // the clip after the one Whisper hasn't done waits for it

        db.SaveTranscripts(db.ChunkById(pending)!, new List<Segment> { new(0, 3, "payment went through") });
        while (s.Step()) { }
        Assert.Equal(3, s.Progress().Moments);
        while (s.Step(nowUtc: DateTime.UtcNow.AddMinutes(5))) { }
        Assert.Equal(4, s.Progress().Moments);
        var calls = e.Calls;
        Assert.False(s.Step(nowUtc: DateTime.UtcNow.AddMinutes(5)));   // nothing new: nothing is embedded again
        Assert.Equal(calls, e.Calls);
    }

    [Fact]
    public void RetentionDeletesVectorsWithTheirRecordings()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        AddFrame(old, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        AddClip(old.AddMinutes(1), true, "the invoice is paid");
        AddFrame(DateTime.UtcNow.AddHours(-1), "Notes", "Weekend", "take the puppy to the park");
        AddClip(DateTime.UtcNow.AddHours(-1), false, "walk the dog");
        var s = Indexed(Nothing, out _);
        Assert.Equal(4, s.Progress().Moments);

        db.DeleteFrames(null, DateTime.UtcNow.AddDays(-1));
        db.DeleteAudio(null, DateTime.UtcNow.AddDays(-1));
        var left = s.Search("invoice payment", new MeaningQuery(), Nothing);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain(left, h => h.Text.Contains("1042") || h.Text.Contains("paid"));
    }

    [Fact]
    public void ANewModelStartsTheIndexOver()
    {
        AddFrame(DateTime.UtcNow.AddHours(-1), "Chrome", "Acme portal", "Invoice 1042 due Friday");
        var s = Indexed(Nothing, out var e);
        Assert.Equal(1, s.Progress().Moments);
        e.ModelId = "fake-2";
        Assert.True(s.Step());
        Assert.Equal("fake-2", db.MeaningState("model"));
        Assert.Equal(1, s.Progress().Moments);
        Assert.Single(s.Search("bill", new MeaningQuery(), Nothing));
        db.ClearMeaning();
        Assert.Equal(0, s.Progress().Moments);
    }

    [Fact]
    public void ApiSearchesByMeaning()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        AddFrame(t.AddMinutes(3), "Notes", "Weekend", "take the puppy to the park");
        var s = Indexed(Nothing, out _);
        var config = Config.Load(new[] { "--home", home });
        var on = new Api(db, config, meaning: () => s);
        var body = System.Text.Encoding.UTF8.GetString(on.Handle(new HttpRequest("GET", "/search",
            new() { ["q"] = "dog", ["mode"] = "meaning", ["start_time"] = "1d ago" })).Body);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("Notes", (string?)json["data"]![0]!["content"]!["app_name"]);
        Assert.NotNull(json["data"]![0]!["content"]!["score"]);
        Assert.Contains("\"search_by_meaning\":true", System.Text.Encoding.UTF8.GetString(on.Handle(new HttpRequest("GET", "/health", new())).Body));
        var off = new Api(db, config);
        Assert.Equal(400, off.Handle(new HttpRequest("GET", "/search", new() { ["q"] = "dog", ["mode"] = "meaning" })).Status);
    }

    [Fact]
    public void ModelFilesAreCheckedAgainstTheirHash()
    {
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "vocab.txt");
        File.WriteAllText(path, "not the vocabulary");
        Assert.False(MiniLm.Verify(path, MiniLm.Vocab));
        Assert.False(MiniLm.Verify(Path.Combine(home, "missing"), MiniLm.Vocab));
        Assert.False(MiniLm.Present(home));
        var f = new MiniLm.File("", "x", 3, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");   // SHA-256 of "abc"
        File.WriteAllText(path, "abc");
        Assert.True(MiniLm.Verify(path, f));
        Assert.StartsWith("https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/" + MiniLm.Commit, MiniLm.Model.Url);
    }

    [Fact]
    public void TimelineHitsKeepTheRankOrder()
    {
        var hits = SearchHits.FromMeaning(new[]
        {
            new MeaningHit(2, false, "2026-10-18T09:00:00.000Z", "2026-10-18T09:01:00.000Z", 10, "Chrome", "Budget", "", "Q3\nbudget  review", 0.9f),
            new MeaningHit(1, true, "2026-10-18T10:00:00.000Z", "2026-10-18T10:00:30.000Z", 7, "Microphone", "", "", "hello", 0.5f),
        });
        Assert.Equal(new[] { "m2", "m1" }, hits.Select(h => h.Id));
        Assert.Equal("Q3 budget review", hits[0].Snippet);
        Assert.Null(hits[0].ChunkId);
        Assert.Equal(7, hits[1].ChunkId);
    }

    [Fact]
    public void SearchPageOffersMeaningOnlyWhenTheAppSaysSo()
    {
        // The switch starts hidden and /health's search_by_meaning shows it; a meaning search sends mode=meaning.
        Assert.Contains("<select id=\"mode\" hidden", WebUI.Html);
        Assert.Contains("h.search_by_meaning", WebUI.Html);
        Assert.Contains("p.set('mode', 'meaning')", WebUI.Html);
        Assert.Contains("if (meaning) health();", WebUI.Html);   // a refused meaning search switches back, not a script error
    }

    // MARK: review fixes

    [Fact]
    public void BlockedFramesAreNeverIndexed()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        db.Insert(new Frame(0, Time.Iso(t.AddSeconds(5)), "Chrome", "Acme portal", true, "", "secret vault password", "ocr", 0, "", Blocked: true));
        AddFrame(t.AddSeconds(10), "Chrome", "Acme portal", "payment received");
        var s = Indexed(Nothing, out _);
        Assert.Equal(2, s.Progress().Moments);   // the blocked frame ends the stretch
        var hits = s.Search("password vault", new MeaningQuery(), Nothing);
        Assert.DoesNotContain(hits, h => h.Text.Contains("secret"));
    }

    [Fact]
    public void AStoppedIndexerWritesNothing()
    {
        AddFrame(DateTime.UtcNow.AddHours(-1), "Chrome", "Acme portal", "Invoice 1042 due Friday");
        var s = new MeaningService(db) { Embedder = new FakeEmbedder(), Blocked = () => Nothing, Pace = () => MeaningPace.Idle };
        s.Stop();   // a newer generation: the old one's writes are refused under the database lock
        Assert.Throws<OperationCanceledException>(() => s.Step(gen: -1));
        Assert.Equal(0, s.Progress().Moments);
        Assert.Null(db.MeaningState(Database.FrameCursor));
        Assert.Null(db.MeaningState("model"));

        var id = AddFrame(DateTime.UtcNow.AddHours(-1), "Notes", "x", "walk the dog");
        db.SaveMoment(new Moment(false, Time.Now(), Time.Now(), id, id, "Notes", "x", "", "walk the dog", 1), "fake-1",
                      new byte[16], Database.FrameCursor, id, keep: () => false);
        Assert.Equal(0, s.Progress().Moments);
        Assert.Null(db.MeaningState(Database.FrameCursor));
    }

    [Fact]
    public void AMomentWhoseFramesWereDeletedMeanwhileIsDropped()
    {
        var t = DateTime.UtcNow.AddHours(-2);
        var a = AddFrame(t, "Chrome", "Acme portal", "Invoice 1042");
        AddFrame(t.AddSeconds(30), "Chrome", "Acme portal", "account number 12345");
        var c = AddFrame(t.AddSeconds(60), "Chrome", "Acme portal", "due Friday");
        var m = new Moment(false, Time.Iso(t), Time.Iso(t.AddSeconds(60)), a, c, "Chrome", "Acme portal", "",
                           "Invoice 1042\naccount number 12345\ndue Friday", 3);
        db.DeleteFrames(t.AddSeconds(20), t.AddSeconds(40));   // the middle one, while the vector was being made
        db.SaveMoment(m, "fake-1", new byte[16], Database.FrameCursor, c);
        Assert.Equal(0, db.MeaningProgress(DateTime.UtcNow).Moments);
        Assert.Equal(c.ToString(), db.MeaningState(Database.FrameCursor));   // the indexer still moves on

        var whole = m with { Frames = 2 };
        db.SaveMoment(whole, "fake-1", new byte[16], Database.FrameCursor, c);
        Assert.Equal(1, db.MeaningProgress(DateTime.UtcNow).Moments);
    }

    [Fact]
    public void DeletingFramesTakesEveryMomentMadeFromThem()
    {
        var old = DateTime.UtcNow.AddDays(-3);
        var a = AddFrame(old, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        AddFrame(DateTime.UtcNow.AddHours(-1), "Notes", "Weekend", "take the puppy to the park");
        var s = Indexed(Nothing, out _);
        Assert.Equal(2, s.Progress().Moments);
        // One whose times don't match its frame (as one stored in a race would be): it goes because its frame is gone.
        db.SaveMoment(new Moment(false, "2999-01-01T00:00:00.000Z", "2999-01-01T00:00:01.000Z", a, a, "Chrome", "Acme portal", "",
                                 "Invoice 1042 due Friday", 1), "fake-1", new byte[] { 1, 2, 3 }, Database.FrameCursor, a);
        Assert.Equal(3, s.Progress().Moments);

        Assert.Equal(1, db.DeleteFrames(null, DateTime.UtcNow.AddDays(-1)));
        Assert.Equal(1, s.Progress().Moments);
        Assert.DoesNotContain(s.Search("invoice payment", new MeaningQuery(), Nothing), h => h.Text.Contains("1042"));
        Assert.Single(db.MomentsById(new long[] { 1, 2, 3 }));   // the vectors went with their moments (trigger)
    }

    [Fact]
    public void AnEmptyVectorIsNeverStored()
    {
        var id = AddFrame(DateTime.UtcNow.AddHours(-1), "Notes", "x", "walk the dog");
        Assert.Throws<ArgumentException>(() => db.SaveMoment(new Moment(false, Time.Now(), Time.Now(), id, id, "Notes", "x", "", "walk the dog", 1),
                                                             "fake-1", Array.Empty<byte>(), Database.FrameCursor, id));
        Assert.Equal(0, db.MeaningProgress(DateTime.UtcNow).Moments);
        Assert.Null(db.MeaningState(Database.FrameCursor));
    }

    [Fact]
    public void AFailedSaveKeepsTheCursorAndIsRetriedThenSkipped()
    {
        var t = DateTime.UtcNow.AddHours(-1);
        AddFrame(t, "Chrome", "Acme portal", "Invoice 1042 due Friday");
        var s = Indexed(Nothing, out _);
        var cursor = db.MeaningState(Database.FrameCursor);
        db.Exec("CREATE TRIGGER test_refuse BEFORE INSERT ON meaning_vectors BEGIN SELECT RAISE(ABORT, 'refused by the test'); END;");
        var b = AddFrame(t.AddMinutes(10), "Notes", "Weekend", "take the puppy to the park");
        var failed = Assert.Throws<MeaningSaveFailed>(() => s.Step());
        Assert.Equal((Database.FrameCursor, b), (failed.CursorKey, failed.Cursor));
        Assert.Equal(cursor, db.MeaningState(Database.FrameCursor));   // not moved past the moment
        Assert.Equal(1, s.Progress().Moments);                          // and nothing half-saved
        db.Exec("DROP TRIGGER test_refuse");
        while (s.Step()) { }
        Assert.Equal(2, s.Progress().Moments);
        Assert.Equal(b.ToString(), db.MeaningState(Database.FrameCursor));

        // The wait doubles from 1 minute up to 1 hour; the same moment is skipped after a few tries.
        Assert.Equal(TimeSpan.FromMinutes(1), MeaningRetry.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(4), MeaningRetry.Backoff(3));
        Assert.Equal(TimeSpan.FromHours(1), MeaningRetry.Backoff(50));
        var retry = new MeaningRetry();
        var skips = Enumerable.Range(0, MeaningRetry.TriesPerMoment).Select(_ => retry.Failed(failed).Skip != null).ToList();
        Assert.Equal(Enumerable.Repeat(false, MeaningRetry.TriesPerMoment - 1).Append(true), skips);
        Assert.Null(retry.Failed(failed).Skip);   // the count starts over after a skip
        Assert.Null(retry.Failed(new MeaningSaveFailed(Database.ChunkCursor, 1, new Exception())).Skip);
        Assert.Null(retry.Failed(failed).Skip);   // ...and after another moment failed
        Assert.Equal(TimeSpan.FromHours(1), retry.Failed(new IOException()).Wait);
        retry.Succeeded();
        Assert.Equal(TimeSpan.FromMinutes(1), retry.Failed(failed).Wait);
    }

    [Fact]
    public void ScanReadsPageByPageWithoutHoldingTheLock()
    {
        var id = AddFrame(DateTime.UtcNow.AddHours(-1), "Chrome", "Acme portal", "Invoice 1042 due Friday");
        int n = Database.ScanPage + 7;
        for (int i = 0; i < n; i++)
            db.SaveMoment(new Moment(false, Time.Now(), Time.Now(), id, id, "Chrome", "Acme portal", "", "x", 1), "fake-1",
                          new byte[] { (byte)(i % 100), 1, 2, 3 }, Database.FrameCursor, id);
        db.SaveMoment(new Moment(false, Time.Now(), Time.Now(), id, id, "Chrome", "Acme portal", "", "x", 1), "other-model",
                      new byte[4], Database.FrameCursor, id);
        var seen = new List<long>();
        bool otherThreadGotIn = true;
        db.ScanMoments("fake-1", null, null, (mid, audio, app, window, url, vec) =>
        {
            if (seen.Count == 0 || seen.Count == Database.ScanPage)
                otherThreadGotIn &= Task.Run(() => db.MeaningState("model")).Wait(TimeSpan.FromSeconds(5));
            Assert.Equal(4, vec.Length);
            Assert.Equal((byte)(seen.Count % 100), vec[0]);
            seen.Add(mid);
        });
        Assert.Equal(n, seen.Count);
        Assert.Equal(seen.OrderBy(x => x), seen);
        Assert.True(otherThreadGotIn);
    }

    [Fact]
    public void AnIndexFromATestBuildIsStartedOver()
    {
        db.Dispose();
        var path = Path.Combine(home, "old.sqlite");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE meaning_moments (id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT NOT NULL, timestamp TEXT NOT NULL,
                    end_time TEXT NOT NULL, first_id INTEGER NOT NULL, last_id INTEGER NOT NULL, app_name TEXT NOT NULL DEFAULT '',
                    window_name TEXT NOT NULL DEFAULT '', browser_url TEXT NOT NULL DEFAULT '', text TEXT NOT NULL DEFAULT '',
                    model TEXT NOT NULL, vector BLOB NOT NULL);
                CREATE TABLE meaning_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, text, model, vector) VALUES ('screen', 'a', 'b', 1, 1, 't', 'm', x'00');
                INSERT INTO meaning_state VALUES ('frame_cursor', '1');
                """;
            cmd.ExecuteNonQuery();
        }
        using var upgraded = new Database(path);
        Assert.True(upgraded.HasMeaningIndex());
        Assert.Null(upgraded.MeaningState(Database.FrameCursor));
        Assert.Equal(0, upgraded.MeaningProgress(DateTime.UtcNow).Moments);
        Assert.Empty(upgraded.MomentsById(new long[] { 1 }));
        using var again = new Database(path);   // the second open keeps what is there
        Assert.True(again.HasMeaningIndex());
    }

    /// A response body that sends a little, then nothing.
    sealed class StallingHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StreamContent(new StallStream()) });
        }
    }

    sealed class StallStream : Stream
    {
        bool sent;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (!sent) { sent = true; buffer.Span[0] = (byte)'a'; return 1; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class WrongFileHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[MiniLm.Vocab.Size]) });
    }

    [Fact]
    public async Task ADownloadThatStallsFailsAndLeavesNoPartFile()
    {
        var dir = Path.Combine(home, "model");
        using var http = new HttpClient(new StallingHandler());
        await Assert.ThrowsAsync<TimeoutException>(() => MiniLm.Download(dir, http, null, CancellationToken.None, TimeSpan.FromMilliseconds(300)));
        Assert.Empty(Directory.GetFiles(dir));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MiniLm.Download(dir, http, null, cts.Token, TimeSpan.FromSeconds(30)));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task ADownloadOfTheWrongFileLeavesNothing()
    {
        var dir = Path.Combine(home, "model");
        using var http = new HttpClient(new WrongFileHandler());
        await Assert.ThrowsAsync<InvalidDataException>(() => MiniLm.Download(dir, http, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(dir));
    }

    // MARK: the real model (opt-in)

    /// Runs only when OPENREWIND_MODEL_DIR names a folder with the pinned model.onnx and vocab.txt
    /// (as downloaded by the app, e.g. %USERPROFILE%\.openrewind\models\all-MiniLM-L6-v2). Checks the
    /// tokenizer against the full vocabulary and the ONNX path against numbers from sentence-transformers.
    [Fact]
    public void RealModelMatchesSentenceTransformers()
    {
        var dir = Environment.GetEnvironmentVariable("OPENREWIND_MODEL_DIR");
        if (string.IsNullOrEmpty(dir) || !MiniLm.Present(dir)) return;

        var tok = WordPiece.Load(Path.Combine(dir, MiniLm.Vocab.Name), MiniLm.MaxTokens);
        foreach (var (text, pieces) in Reference)
            Assert.Equal(pieces, string.Join(' ', tok.Tokenize(text)));
        Assert.Equal(new long[] { 101, 7592, 1010, 2088, 999, 102 }, tok.Encode("Hello, world!"));

        var e = OnnxEmbedder.Load(dir);   // also checks both files against their pinned SHA-256
        var happy = e.Embed("That is a happy person");
        var expected = new[] { -0.03388f, 0.09194f, 0.04870f, -0.03488f, -0.06483f };
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], happy[i], 0.001f);
        float Cos(string other) { var v = e.Embed(other); float d = 0; for (int i = 0; i < v.Length; i++) d += v[i] * happy[i]; return d; }
        Assert.Equal(0.6946f, Cos("That is a happy dog"), 0.002f);
        Assert.Equal(0.9429f, Cos("That is a very happy person"), 0.002f);
        Assert.Equal(0.2569f, Cos("Today is a sunny day"), 0.002f);

        e.Dispose();
        Assert.Throws<ObjectDisposedException>(() => e.Embed("after turning off"));
    }
}
