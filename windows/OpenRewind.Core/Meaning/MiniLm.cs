using System.Security.Cryptography;

namespace OpenRewind;

/// The sentence model Windows uses for Search by Meaning: all-MiniLM-L6-v2 (Apache 2.0), about
/// 90 MB, downloaded once from Hugging Face. The address names one exact commit and every file
/// is checked against its SHA-256 before it is used, so the download can't be swapped.
/// (The Mac app uses Apple's built-in sentence embeddings and downloads nothing.)
public static class MiniLm
{
    public const string Commit = "1110a243fdf4706b3f48f1d95db1a4f5529b4d41";
    /// Stored with the vectors (see IEmbedder.ModelId).
    public const string ModelId = "all-MiniLM-L6-v2@" + "1110a24";
    public const int Dimension = 384;
    /// Tokens the model reads at most (its training length).
    public const int MaxTokens = 256;

    public sealed record File(string Url, string Name, long Size, string Sha256);

    const string Base = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/" + Commit + "/";

    public static readonly File Model = new(Base + "onnx/model.onnx", "model.onnx", 90_405_214,
                                            "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452");
    public static readonly File Vocab = new(Base + "vocab.txt", "vocab.txt", 231_508,
                                            "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3");
    public static readonly File[] Files = { Vocab, Model };

    public static long DownloadSize => Files.Sum(f => f.Size);

    /// ~/.openrewind/models/all-MiniLM-L6-v2
    public static string Dir(string home) => Path.Combine(home, "models", "all-MiniLM-L6-v2");

    /// Both files are there with the right size (the hash is checked when the model loads).
    public static bool Present(string dir) =>
        Files.All(f => new FileInfo(Path.Combine(dir, f.Name)) is { Exists: true } i && i.Length == f.Size);

    /// True when the file on disk is exactly the pinned one.
    public static bool Verify(string path, File f)
    {
        try
        {
            using var s = System.IO.File.OpenRead(path);
            return s.Length == f.Size && Convert.ToHexString(SHA256.HashData(s)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// Download what is missing into `dir`. `progress` gets 0…1 over all files. A file whose hash
    /// does not match is deleted and the download fails. A download that receives nothing for
    /// `stall` (default 60 s) fails with a TimeoutException. A half-downloaded file is never left behind.
    public static async Task Download(string dir, HttpClient http, Action<double>? progress, CancellationToken ct, TimeSpan? stall = null)
    {
        Directory.CreateDirectory(dir);
        var quiet = stall ?? TimeSpan.FromSeconds(60);
        long before = 0, total = DownloadSize;
        foreach (var f in Files)
        {
            var path = Path.Combine(dir, f.Name);
            if (Verify(path, f)) { before += f.Size; progress?.Invoke((double)before / total); continue; }
            var part = path + ".part";
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                idle.CancelAfter(quiet);
                using (var resp = await http.GetAsync(f.Url, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
                    await using var dst = System.IO.File.Create(part);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buf = new byte[1 << 16];
                    long got = 0;
                    while (true)
                    {
                        idle.CancelAfter(quiet);   // restarts the clock: only a stalled read times out
                        int n = await src.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                        if (n <= 0) break;
                        await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        sha.AppendData(buf, 0, n);
                        got += n;
                        if (got > f.Size) break;
                        progress?.Invoke((double)(before + got) / total);
                    }
                    var hash = Convert.ToHexString(sha.GetHashAndReset());
                    if (got != f.Size || !hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{f.Name} did not match its checksum (got {got} bytes)");
                }
                System.IO.File.Move(part, path, overwrite: true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryDelete(part);
                throw new TimeoutException($"the download of {f.Name} stopped receiving data");
            }
            catch
            {
                TryDelete(part);
                throw;
            }
            before += f.Size;
        }
        progress?.Invoke(1);
    }

    static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
