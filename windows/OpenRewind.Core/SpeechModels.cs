using System.Net;
using System.Security.Cryptography;

namespace OpenRewind;

public enum SpeechEngine { Whisper, Parakeet }

/// One file of a model: where it comes from (a fixed Hugging Face commit) and what it must hash to.
public sealed record ModelFile(string Name, string Url, string Sha256, long Size);

/// A speech-to-text model the user can pick in Settings → Transcription Model.
/// Folder is under ~/.openrewind/models ("" = that folder itself, where Whisper looks for ggml-*.bin).
public sealed record SpeechModel(string Id, string Name, SpeechEngine Engine, string Detail, string Folder, IReadOnlyList<ModelFile> Files)
{
    public long Size => Files.Sum(f => f.Size);
    public string Dir(string home) => Folder.Length == 0 ? SpeechModels.ModelsDir(home) : Path.Combine(SpeechModels.ModelsDir(home), Folder);
    public string PathOf(string home, ModelFile f) => Path.Combine(Dir(home), f.Name);
}

/// The models OpenRewind knows. Same list, ids, files and hashes as the Mac app (SpeechModels.swift).
public static class SpeechModels
{
    /// Whatever ggml Whisper model is already on this computer (what OpenRewind always did).
    public const string DefaultId = "whisper-auto";
    public const string ParakeetId = "parakeet-v3";

    const string WhisperRepo = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/";
    const string ParakeetRepo = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/2bda32ec70b097a55adaa07d9a7173915b43cc78/";

    static ModelFile Whisper(string name, string sha, long size) => new(name, WhisperRepo + name, sha, size);
    static ModelFile Parakeet(string name, string sha, long size) => new(name, ParakeetRepo + name, sha, size);

    public static readonly IReadOnlyList<SpeechModel> All = new SpeechModel[]
    {
        new(DefaultId, "Whisper (Automatic)", SpeechEngine.Whisper,
            "Uses the Whisper model already on this computer. Needs whisper-cli.", "", Array.Empty<ModelFile>()),
        new("whisper-base", "Whisper Base", SpeechEngine.Whisper,
            "Fast, less accurate. 99 languages. Needs whisper-cli.", "",
            new[] { Whisper("ggml-base.bin", "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 147951465) }),
        new("whisper-small", "Whisper Small", SpeechEngine.Whisper,
            "Good balance. 99 languages. Needs whisper-cli.", "",
            new[] { Whisper("ggml-small.bin", "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", 487601967) }),
        new("whisper-large-v3-turbo", "Whisper Large v3 Turbo", SpeechEngine.Whisper,
            "Most accurate Whisper, slower. 99 languages. Needs whisper-cli.", "",
            new[] { Whisper("ggml-large-v3-turbo-q5_0.bin", "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", 574041195) }),
        new(ParakeetId, "NVIDIA Parakeet v3", SpeechEngine.Parakeet,
            "Very accurate and fast, with punctuation. 25 European languages. Nothing else to install.",
            "parakeet-tdt-0.6b-v3-int8",
            new[]
            {
                Parakeet("encoder.int8.onnx", "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247", 652184281),
                Parakeet("decoder.int8.onnx", "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e", 11845275),
                Parakeet("joiner.int8.onnx", "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3", 6355277),
                Parakeet("tokens.txt", "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d", 93939),
            }),
    };

    /// The picked model, or the default for an unknown or empty id.
    public static SpeechModel ById(string? id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];

    public static string ModelsDir(string home) => Path.Combine(home, "models");

    /// All files are there at the right size. Cheap, for menus; loading checks the hashes (EnsureVerified).
    public static bool IsDownloaded(SpeechModel m, string home) => m.Files.All(f => SizeOf(m.PathOf(home, f)) == f.Size);

    /// Size of the file, following a symbolic link (someone may keep the models on another disk).
    static long? SizeOf(string path) => Real(path) is { Exists: true } fi ? fi.Length : null;

    static FileInfo Real(string path)
    {
        var fi = new FileInfo(path);
        try { return fi.Exists && fi.LinkTarget != null && fi.ResolveLinkTarget(true) is FileInfo target ? target : fi; }
        catch (IOException) { return fi; }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.0} GB" : $"{Math.Max(1, (long)Math.Round(bytes / 1e6))} MB";

    /// "under a minute", "about 25 minutes", "about 3 hours": for download time left and re-transcribe estimates.
    public static string About(double seconds) =>
        seconds < 60 ? "under a minute"
        : seconds < 5400 ? $"about {Math.Max(1, (int)Math.Round(seconds / 60))} minute{(Math.Round(seconds / 60) >= 2 ? "s" : "")}"
        : $"about {(int)Math.Round(seconds / 3600)} hours";

    /// Shown wherever Parakeet can be picked, and in the README (the model's license asks for it).
    public const string ParakeetCredit =
        "NVIDIA Parakeet TDT 0.6B v3 by NVIDIA, licensed CC-BY-4.0 (huggingface.co/nvidia/parakeet-tdt-0.6b-v3); ONNX export by the sherpa-onnx project.";

    // MARK: verification

    /// Next to each verified file: "<sha256> <size> <last-write ticks>". A file that changed since gets hashed again.
    static string StampPath(string path) => path + ".verified";

    static string Stamp(ModelFile f, string path) => $"{f.Sha256} {f.Size} {Real(path).LastWriteTimeUtc.Ticks}";

    /// True when the file on disk is exactly the pinned one. Hashes it once, then trusts the stamp
    /// while the file stays the same size and age (hashing 650 MB on every start would be slow).
    public static bool EnsureVerified(string path, ModelFile f)
    {
        if (SizeOf(path) != f.Size) return false;
        try { if (File.ReadAllText(StampPath(path)).Trim() == Stamp(f, path)) return true; } catch (IOException) { }
        if (!string.Equals(Sha256Of(path), f.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        WriteStamp(path, f);
        return true;
    }

    static void WriteStamp(string path, ModelFile f)
    {
        try { File.WriteAllText(StampPath(path), Stamp(f, path)); }
        catch (Exception e) { Log.Write($"cannot write {StampPath(path)}: {e.Message}"); }
    }

    public static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    /// Every file of the model is there and matches its hash; names the first one that is not.
    public static string? Problem(SpeechModel m, string home)
    {
        foreach (var f in m.Files)
            if (!EnsureVerified(m.PathOf(home, f), f)) return $"{f.Name} is missing or damaged";
        return null;
    }

    // MARK: download

    /// Download every file of the model that is not there yet, check each against its pinned SHA-256,
    /// and only then move it into place. A cut-off download carries on from the `.part` file next time.
    /// Progress is (bytes done, bytes in total) over the whole model.
    public static async Task Download(SpeechModel m, string home, IProgress<(long Done, long Total)>? progress,
                                      CancellationToken ct, HttpMessageHandler? handler = null)
    {
        Directory.CreateDirectory(m.Dir(home));
        using var http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler == null) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenRewind");
        long total = m.Size, before = 0;
        foreach (var f in m.Files)
        {
            var path = m.PathOf(home, f);
            if (EnsureVerified(path, f)) { before += f.Size; progress?.Report((before, total)); continue; }
            long start = before;
            await DownloadFile(http, f, path, done => progress?.Report((start + done, total)), ct).ConfigureAwait(false);
            before += f.Size;
        }
        progress?.Report((total, total));
        Log.Write($"downloaded {m.Name} to {m.Dir(home)}");
    }

    static async Task DownloadFile(HttpClient http, ModelFile f, string path, Action<long> progress, CancellationToken ct)
    {
        var part = path + ".part";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long have = 0;
        // Carry on from an earlier try: hash what is already there, then ask for the rest.
        if (File.Exists(part))
        {
            have = new FileInfo(part).Length;
            if (have >= f.Size) { File.Delete(part); have = 0; }
            else
            {
                using var old = File.OpenRead(part);
                var buf = new byte[1 << 20];
                int n;
                while ((n = await old.ReadAsync(buf, ct).ConfigureAwait(false)) > 0) hash.AppendData(buf, 0, n);
            }
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, f.Url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
        {
            // The server sent the whole file again: start over.
            have = 0;
            hash.GetHashAndReset();
        }
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{f.Name}: the server answered {(int)resp.StatusCode}");
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buf = new byte[1 << 16];
            long done = have, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                if (done + n > f.Size) throw new InvalidDataException($"{f.Name} is bigger than expected");
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                hash.AppendData(buf, 0, n);
                done += n;
                if (done - lastReport >= 1 << 20 || done == f.Size) { progress(done); lastReport = done; }
            }
        }
        var size = new FileInfo(part).Length;
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (size != f.Size || sha != f.Sha256.ToLowerInvariant())
        {
            File.Delete(part);
            throw new InvalidDataException(size != f.Size
                ? $"{f.Name} stopped at {FormatSize(size)} of {FormatSize(f.Size)}; try again"
                : $"{f.Name} does not match its checksum, so it was thrown away; try again");
        }
        File.Move(part, path, overwrite: true);
        WriteStamp(path, f);
    }
}
