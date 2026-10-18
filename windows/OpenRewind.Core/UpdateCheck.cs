using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRewind;

/// The latest GitHub release, as the update check sees it.
public sealed record ReleaseInfo(string Version, string Page, string AssetUrl, string? Digest);

/// New versions come from GitHub Releases (built by .github/workflows/release.yml).
/// The Mac app does the same check in Updater.swift.
public static class UpdateCheck
{
    public const string Repo = "frickgav/openscreen";
    public static string LatestUrl => $"https://api.github.com/repos/{Repo}/releases/latest";

    /// The latest release that has `assetName`, or throws with a short reason.
    public static async Task<ReleaseInfo> FetchLatest(HttpClient http, string assetName, string currentVersion, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("OpenRewind", currentVersion));
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode}");
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json, assetName) ?? throw new HttpRequestException($"the latest release has no {assetName}");
    }

    public static ReleaseInfo? Parse(string json, string assetName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (Bool(o, "draft") || Bool(o, "prerelease")) return null;
            if (Str(o, "tag_name") is not string tag || Str(o, "html_url") is not string page) return null;
            if (!o.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var a in assets.EnumerateArray())
            {
                if (Str(a, "name") != assetName || Str(a, "browser_download_url") is not string url) continue;
                return new ReleaseInfo(tag.TrimStart('v', 'V'), page, url, Str(a, "digest"));
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// "0.10.0" is newer than "0.9.2". Anything after a "-" or "+" is ignored, so are missing parts ("0.2" = "0.2.0.0").
    public static bool IsNewer(string a, string b)
    {
        static int[] Parts(string s) =>
            s.Trim().TrimStart('v', 'V').Split('-', '+')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        int[] x = Parts(a), y = Parts(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int l = i < x.Length ? x[i] : 0, r = i < y.Length ? y[i] : 0;
            if (l != r) return l > r;
        }
        return false;
    }

    /// Compare against the SHA-256 GitHub lists for the file ("sha256:<hex>"). Older API answers have none.
    public static bool Verify(string file, string? digest)
    {
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var s = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(s)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }
}
