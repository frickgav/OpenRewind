using System.Diagnostics;
using System.Text.Json;

namespace OpenRewind;

/// What the transcriber did last, for the Settings menu.
public sealed record TranscriberStatus(DateTime? LastAt, double LastSeconds, double LastAudioSeconds, string? Problem);

/// Turns pending audio chunks into text, one at a time, with the model picked in Settings →
/// Transcription Model: local Whisper (whisper.cpp `whisper-cli`) or NVIDIA Parakeet v3 (ONNX Runtime).
public sealed class Transcriber
{
    readonly Config config;
    readonly Database db;
    public readonly SpeechModel Model;
    /// whisper-cli (Whisper only).
    public readonly string Binary;
    /// The ggml file (Whisper) or the model folder (Parakeet).
    public readonly string ModelPath;
    /// Stored with each transcript line (audio_transcriptions.engine), so it is known which model wrote it.
    public readonly string EngineTag;
    /// Shown in Settings: "Whisper Small", "Whisper (ggml-base.en.bin)", "NVIDIA Parakeet v3".
    public readonly string DisplayName;
    public TranscriberStatus Status { get; private set; } = new(null, 0, 0, null);
    ParakeetEngine? parakeet;
    DateTime lastUsed;
    /// Parakeet holds about 1 GB of memory while loaded and takes a few seconds to load; let go of it after this long with nothing to do.
    static readonly TimeSpan UnloadAfter = TimeSpan.FromMinutes(5);
    static int Threads => Math.Max(2, Environment.ProcessorCount / 2);

    Transcriber(Config config, Database db, SpeechModel model, string binary, string modelPath, string tag, string name)
    {
        this.config = config; this.db = db; Model = model; Binary = binary; ModelPath = modelPath; EngineTag = tag; DisplayName = name;
    }

    public static Transcriber? Create(Config config, Database db) => Create(config, db, out _);

    /// The picked model, ready to run; or null and why not (also written to the log).
    public static Transcriber? Create(Config config, Database db, out string? problem)
    {
        var m = SpeechModels.ById(config.TranscriptionModel);
        problem = null;
        if (m.Engine == SpeechEngine.Parakeet)
        {
            if (!SpeechModels.IsDownloaded(m, config.Home))
            {
                problem = $"{m.Name} is not downloaded";
                Log.Write($"{problem} → audio is saved but not transcribed. Pick it again in Settings → Audio → Transcription to download it");
                return null;
            }
            Log.Write($"transcription model: {m.Name}");
            return new Transcriber(config, db, m, "", m.Dir(config.Home), ParakeetEngine.Tag, m.Name);
        }
        if (FindBinary(config.WhisperBinary, config.Home) is not string bin)
        {
            problem = "whisper-cli not found";
            Log.Write($"whisper-cli not found → audio is saved but not transcribed. Put whisper-cli in {Path.Combine(config.Home, "whisper")} (see README), or pick NVIDIA Parakeet v3 in Settings → Audio → Transcription");
            return null;
        }
        // --whisper-model wins; then the picked size if it is downloaded; then whatever is on this computer.
        string? path = null;
        string? name = null;
        if (config.WhisperModel == null && m.Files.Count > 0)
        {
            var picked = m.PathOf(config.Home, m.Files[0]);
            if (File.Exists(picked)) { path = picked; name = m.Name; }
            else Log.Write($"{m.Name} is not downloaded; using the Whisper model that is");
        }
        path ??= FindModel(config.WhisperModel, config.Home);
        if (path == null)
        {
            problem = "no Whisper model found";
            Log.Write($"no Whisper model found → audio is saved but not transcribed. Put a ggml-*.bin in {Path.Combine(config.Home, "models")}, pass --whisper-model, or pick a model in Settings → Audio → Transcription");
            return null;
        }
        Log.Write($"whisper: {Path.GetFileName(path)}");
        var stem = Path.GetFileNameWithoutExtension(path);
        if (stem.EndsWith(SpeechModels.OwnSuffix)) stem = stem[..^SpeechModels.OwnSuffix.Length];
        return new Transcriber(config, db, m, bin, path, "whisper.cpp:" + stem, name ?? $"Whisper ({Path.GetFileName(path)})");
    }

    static string ExeName => OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli";

    /// --whisper-bin, then next to the app, then ~/.openrewind/whisper, then the usual install spots and PATH.
    public static string? FindBinary(string? overridePath, string? home = null)
    {
        home ??= Config.DefaultHome();
        var candidates = new List<string?>
        {
            overridePath,
            Path.Combine(AppContext.BaseDirectory, ExeName),
            Path.Combine(AppContext.BaseDirectory, "whisper", ExeName),
            Path.Combine(home, "whisper", ExeName),
            Path.Combine(home, "whisper", "Release", ExeName),
            "/opt/homebrew/bin/whisper-cli",
            "/usr/local/bin/whisper-cli",
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir, ExeName));
        return candidates.FirstOrDefault(p => p != null && File.Exists(p));
    }

    /// Our own models folder first.
    public static string? FindModel(string? overridePath, string home)
    {
        if (overridePath != null) return File.Exists(overridePath) ? overridePath : null;
        var dirs = new[] { Path.Combine(home, "models"), Path.Combine(home, "whisper", "models"), "/opt/homebrew/share/whisper-cpp" };
        var preferred = new[] { "large-v3-turbo", "medium", "small", "base", "tiny" };
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            var bins = Directory.GetFiles(dir, "ggml-*.bin").Select(Path.GetFileName).OfType<string>()
                                .Where(n => !n.Contains("for-tests")).OrderBy(n => n).ToList();
            foreach (var p in preferred)
                if (bins.FirstOrDefault(b => b.Contains(p)) is string hit) return Path.Combine(dir, hit);
            if (bins.Count > 0) return Path.Combine(dir, bins[0]);
        }
        return null;
    }

    public async Task Run(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (db.NextPendingChunk() is AudioChunk chunk)
                {
                    // An encrypted clip and no key loaded: wait a minute (the key may come back) instead of using up its tries.
                    if (!await Task.Run(() => Transcribe(chunk), ct).ConfigureAwait(false))
                        await Task.Delay(60_000, ct).ConfigureAwait(false);
                }
                else
                {
                    if (parakeet != null && DateTime.UtcNow - lastUsed > UnloadAfter) Unload();
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                }
                // A model that cannot load would fail every chunk: stop until the user picks a model again.
                if (loadFailed) return;
            }
        }
        catch (OperationCanceledException) { }
        finally { Unload(); }
    }

    bool loadFailed;

    /// The one place both engines read a recorded clip: 16 kHz mono samples. A clip saved with Encrypt
    /// Recordings on is decrypted in memory here (RecordingFile.Read; a plain clip is read as it is), so
    /// neither engine ever needs a decrypted copy on disk. Throws RecordingFile's no-key error when the
    /// clip is encrypted and the key is not loaded.
    public static float[] LoadClipSamples(string path) => Wav.Load16k(RecordingFile.Read(path));

    /// False when the clip is encrypted and no key is loaded: it is left as it is (no try used up).
    public bool Transcribe(AudioChunk chunk)
    {
        var sw = Stopwatch.StartNew();
        List<Segment> segments;
        try
        {
            var samples = LoadClipSamples(chunk.FilePath);
            lastClipSeconds = samples.Length / (double)ParakeetMath.SampleRate;
            segments = Model.Engine == SpeechEngine.Parakeet ? Parakeet(samples) : Whisper(samples);
            if (!db.SaveTranscripts(chunk, segments, EngineTag))
            {
                Log.Write($"chunk {chunk.Id} was deleted while it was transcribed; its text is dropped");
                return true;
            }
        }
        catch (ModelLoadException e)
        {
            Log.Write($"cannot load {Model.Name}: {e.Message}");
            loadFailed = true;
            Status = Status with { Problem = e.Message };
            return true;
        }
        catch (Exception e) when (RecordingFile.IsNoKey(e))
        {
            Log.Write($"transcribe chunk {chunk.Id} waits: it is encrypted and the key is not loaded");
            return false;
        }
        catch (Exception e)
        {
            Log.Write($"transcribe chunk {chunk.Id} failed: {e.Message}");
            db.MarkChunk(chunk.Id, "pending", e.Message);
            Status = Status with { Problem = e.Message };
            return true;
        }
        loadFailed = false;
        var secs = sw.Elapsed.TotalSeconds;
        Status = new TranscriberStatus(DateTime.UtcNow, secs, lastClipSeconds, null);
        var words = segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        Log.Write($"transcribed chunk {chunk.Id}: {segments.Count} segments, {words} words in {secs:0.0}s");
        return true;
    }

    double lastClipSeconds;

    sealed class ModelLoadException(string message) : Exception(message);

    List<Segment> Parakeet(float[] samples)
    {
        if (parakeet == null)
        {
            // Check the files against their pinned hashes before loading them (once per run of the app).
            if (SpeechModels.Problem(Model, config.Home) is string bad)
                throw new ModelLoadException($"{bad}; pick {Model.Name} again in Settings → Audio → Transcription to download it again");
            var sw = Stopwatch.StartNew();
            // Anything that goes wrong while loading (missing or wrong-architecture runtime, a model that does
            // not fit, no memory) would go wrong for every clip: stop instead of failing each one.
            try { parakeet = new ParakeetEngine(ModelPath, Threads); }
            catch (Exception e) { throw new ModelLoadException(e.Message); }
            Log.Write($"loaded {Model.Name} in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        lastUsed = DateTime.UtcNow;
        var segs = parakeet.Transcribe(samples);
        lastUsed = DateTime.UtcNow;
        return segs.Where(s => !IsNoise(s.Text)).ToList();
    }

    bool whisperChecked;

    void Unload()
    {
        if (parakeet == null) return;
        parakeet.Dispose();
        parakeet = null;
        Log.Write($"unloaded {Model.Name} until there is audio to transcribe");
    }

    List<Segment> Whisper(float[] samples)
    {
        // A Whisper file OpenRewind downloaded is checked against its hash before its first use in each run.
        if (!whisperChecked && Model.Files.Count > 0 && ModelPath == Model.PathOf(config.Home, Model.Files[0]))
        {
            if (SpeechModels.Problem(Model, config.Home) is string bad)
                throw new ModelLoadException($"{bad}; pick {Model.Name} again in Settings → Audio → Transcription to download it again");
        }
        whisperChecked = true;
        var tmp = Path.Combine(Path.GetTempPath(), $"openrewind-{Guid.NewGuid()}");
        var path = tmp + ".wav";
        try
        {
            // whisper-cli reads a file: give it the clip as LoadClipSamples read it. A plain temp copy only this
            // user can read, deleted below (and at the next launch, if the app is killed meanwhile).
            File.WriteAllBytes(path, Wav.Bytes(samples, ParakeetMath.SampleRate));
            var psi = new ProcessStartInfo(Binary)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-m", ModelPath, "-f", path, "-l", config.WhisperLanguage, "-oj", "-of", tmp, "-np",
                                      "-t", Threads.ToString() })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start whisper-cli");
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                var msg = err.Result;
                throw new InvalidOperationException($"whisper-cli exit {p.ExitCode}: {(msg.Length > 400 ? msg[^400..] : msg)}");
            }
            return Parse(File.ReadAllText(tmp + ".json"));
        }
        finally
        {
            try { File.Delete(tmp + ".json"); } catch { }
            try { File.Delete(path); } catch { }
        }
    }

    /// whisper.cpp JSON: {"transcription":[{"offsets":{"from":ms,"to":ms},"text":"..."}]}
    public static List<Segment> Parse(string json)
    {
        var kept = new List<Segment>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("transcription", out var segs) || segs.ValueKind != JsonValueKind.Array) return kept;
            foreach (var seg in segs.EnumerateArray())
            {
                var text = (seg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").Trim();
                if (IsNoise(text) || IsFakeLine(text)) continue;
                double from = 0, to = 0;
                if (seg.TryGetProperty("offsets", out var off))
                {
                    if (off.TryGetProperty("from", out var f)) from = f.GetDouble();
                    to = off.TryGetProperty("to", out var tt) ? tt.GetDouble() : from;
                }
                kept.Add(new Segment(from / 1000, to / 1000, text));
            }
        }
        catch (JsonException) { return new(); }
        // A clip that only says "Thank you." or "You" is almost always Whisper guessing on silence.
        if (kept.Count > 0 && kept.All(s => WeakLines.Contains(Normalize(s.Text)))) return new();
        return kept;
    }

    /// Lines Whisper makes up on quiet audio. It learned them from video subtitle credits.
    static readonly string[] FakeLines =
    {
        "transcription by castingwords", "transcribed by", "subtitles by", "subtitled by",
        "captions by", "captioning by", "amaraorg", "thanks for watching", "thank you for watching",
        "thanks for listening", "thank you for listening", "please subscribe", "like and subscribe",
        "dont forget to subscribe", "see you in the next video", "see you next time",
        "translated by", "copyright", "all rights reserved", "www.", ".com",
    };

    /// Real speech can be just these words, so they are dropped only when they are the whole clip.
    static readonly HashSet<string> WeakLines = new() { "thank you", "thanks", "you", "bye", "okay", "ok", "so", "yeah", "hmm", "uh", "um" };

    public static string Normalize(string t)
    {
        var kept = new string(t.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
        return string.Join(" ", kept.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static bool IsFakeLine(string t)
    {
        var n = Normalize(t);
        var raw = t.ToLowerInvariant();
        // Only short lines: a long sentence that happens to contain "copyright" is real speech.
        if (n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 12) return false;
        return FakeLines.Any(f => f.Contains('.') ? raw.Contains(f) : n.Contains(f));
    }

    /// Drop Whisper's usual filler on silence: "[BLANK_AUDIO]", "(music)", "♪", etc.
    public static bool IsNoise(string t)
    {
        if (t.Length == 0) return true;
        if ((t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')')) || (t.StartsWith('*') && t.EndsWith('*'))) return true;
        return !t.Any(char.IsLetterOrDigit);
    }
}
