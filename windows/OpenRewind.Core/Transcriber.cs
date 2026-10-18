using System.Diagnostics;
using System.Text.Json;

namespace OpenRewind;

/// Runs local Whisper (whisper.cpp `whisper-cli`) on pending audio chunks, one at a time.
public sealed class Transcriber
{
    readonly Config config;
    readonly Database db;
    public readonly string Binary;
    public readonly string Model;

    Transcriber(Config config, Database db, string binary, string model)
    {
        this.config = config; this.db = db; Binary = binary; Model = model;
    }

    public static Transcriber? Create(Config config, Database db)
    {
        if (FindBinary(config.WhisperBinary, config.Home) is not string bin)
        {
            Log.Write($"whisper-cli not found → audio is saved but not transcribed. Put whisper-cli in {Path.Combine(config.Home, "whisper")} (see README)");
            return null;
        }
        if (FindModel(config.WhisperModel, config.Home) is not string model)
        {
            Log.Write($"no Whisper model found → audio is saved but not transcribed. Put a ggml-*.bin in {Path.Combine(config.Home, "models")} or pass --whisper-model");
            return null;
        }
        Log.Write($"whisper: {Path.GetFileName(model)}");
        return new Transcriber(config, db, bin, model);
    }

    static string ExeName => OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli";

    /// --whisper-bin, then next to the app, then ~/.openscreen/whisper, then the usual install spots and PATH.
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
        while (!ct.IsCancellationRequested)
        {
            if (db.NextPendingChunk() is AudioChunk chunk) await Task.Run(() => Transcribe(chunk), ct).ConfigureAwait(false);
            else try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch (TaskCanceledException) { return; }
        }
    }

    public void Transcribe(AudioChunk chunk)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var segments = Whisper(chunk.FilePath);
            db.SaveTranscripts(chunk, segments);
            var words = segments.Sum(s => s.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            Log.Write($"transcribed chunk {chunk.Id}: {segments.Count} segments, {words} words in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (Exception e)
        {
            Log.Write($"transcribe chunk {chunk.Id} failed: {e.Message}");
            db.MarkChunk(chunk.Id, "pending", e.Message);
        }
    }

    List<Segment> Whisper(string path)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"openrewind-{Guid.NewGuid()}");
        try
        {
            var psi = new ProcessStartInfo(Binary)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "-m", Model, "-f", path, "-l", config.WhisperLanguage, "-oj", "-of", tmp, "-np",
                                      "-t", Math.Max(2, Environment.ProcessorCount / 2).ToString() })
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
        finally { try { File.Delete(tmp + ".json"); } catch { } }
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
