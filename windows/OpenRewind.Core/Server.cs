using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OpenRewind;

public record HttpRequest(string Method, string Path, Dictionary<string, string> Query);

public sealed class HttpResponse
{
    public int Status = 200;
    public string ContentType = "application/json";
    public byte[] Body = Array.Empty<byte>();

    static readonly JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static HttpResponse Json(object obj, int status = 200) =>
        new() { Status = status, Body = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts) };

    public static HttpResponse Error(string msg, int status) => Json(new Dictionary<string, object> { ["error"] = msg }, status);
}

/// Tiny HTTP/1.1 server on 127.0.0.1 only. Same as the Mac app's.
/// No CORS headers are sent, so other websites cannot read this data from the browser.
/// A raw socket instead of HttpListener: HttpListener needs an admin-made URL reservation on Windows.
public sealed class Server : IDisposable
{
    readonly TcpListener listener;
    readonly Func<HttpRequest, HttpResponse> router;
    public int Port { get; }

    public Server(int port, Func<HttpRequest, HttpResponse> router)
    {
        Port = port;
        this.router = router;
        listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        listener.Start();
        Log.Write($"api + ui at http://127.0.0.1:{Port}");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    async Task Handle(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            var buf = new byte[65536];
            var data = new MemoryStream();
            int end = -1;
            while (end < 0)
            {
                int n = await stream.ReadAsync(buf).ConfigureAwait(false);
                if (n <= 0 || data.Length > 1_000_000) return;
                data.Write(buf, 0, n);
                end = IndexOf(data.GetBuffer(), (int)data.Length, "\r\n\r\n"u8);
            }
            var head = Encoding.UTF8.GetString(data.GetBuffer(), 0, end);
            var r = Route(head);
            var reason = r.Status switch { 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed", 423 => "Locked", 500 => "Internal Server Error", _ => "OK" };
            var h = $"HTTP/1.1 {r.Status} {reason}\r\nContent-Type: {r.ContentType}\r\nContent-Length: {r.Body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(h)).ConfigureAwait(false);
            await stream.WriteAsync(r.Body).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    HttpResponse Route(string head)
    {
        var first = head.Split("\r\n")[0];
        var parts = first.Split(' ');
        if (parts.Length < 2) return HttpResponse.Error("bad request", 400);
        var target = parts[1];
        var qi = target.IndexOf('?');
        var path = Uri.UnescapeDataString(qi < 0 ? target : target[..qi]);
        var q = new Dictionary<string, string>();
        if (qi >= 0)
            foreach (var pair in target[(qi + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                string Dec(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
                q[Dec(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Dec(pair[(eq + 1)..]);
            }
        try { return router(new HttpRequest(parts[0], path, q)); }
        catch (Exception e) { Log.Write($"api error: {e.Message}"); return HttpResponse.Error("internal error", 500); }
    }

    static int IndexOf(byte[] hay, int len, ReadOnlySpan<byte> needle) => hay.AsSpan(0, len).IndexOf(needle);

    public void Dispose() => listener.Stop();
}

/// REST routes for scripts and the browser search page, the same as the Mac app's.
public sealed class Api
{
    readonly Database db;
    readonly Config config;
    /// Platform facts for /health (permissions etc.).
    readonly Func<Dictionary<string, object?>> healthExtras;
    /// True while the app lock is on and the timeline has not been unlocked: no history is served.
    public Func<bool>? Locked { get; set; }

    public const string LockedMessage = "OpenRewind is locked. Open the timeline to unlock it, then try again.";
    /// A frame's picture as JPEG (the app's FrameImages: stills and frames inside videos). Without it only
    /// JPEG stills can be served.
    readonly Func<Frame, byte[]?>? frameJpeg;

    /// Search by Meaning, while it is on (mode=meaning on /search).
    readonly Func<MeaningService?> meaning;

    public Api(Database db, Config config, Func<Dictionary<string, object?>>? healthExtras = null, Func<Frame, byte[]?>? frameJpeg = null,
               Func<MeaningService?>? meaning = null)
    {
        this.db = db; this.config = config;
        this.healthExtras = healthExtras ?? (() => new());
        this.frameJpeg = frameJpeg;
        this.meaning = meaning ?? (() => null);
    }

    public HttpResponse Handle(HttpRequest req)
    {
        if (req.Method != "GET") return HttpResponse.Error("method not allowed", 405);
        switch (req.Path)
        {
            case "/":
            case "/index.html":
                return new HttpResponse { ContentType = "text/html; charset=utf-8", Body = Encoding.UTF8.GetBytes(WebUI.Html) };
            case "/health" when Locked?.Invoke() == true:
                // While locked: only that it is up, nothing about when or how much was recorded.
                return HttpResponse.Json(new SortedDictionary<string, object?> { ["status"] = "healthy", ["locked"] = true });
            case "/health":
                var s = db.Stats();
                var a = db.AudioStats();
                var h = new SortedDictionary<string, object?>
                {
                    ["audio_chunks_total"] = a.Chunks,
                    ["audio_chunks_pending"] = a.Pending,
                    ["audio_segments_total"] = a.Segments,
                    ["status"] = "healthy",
                    ["frames_total"] = s.Count,
                    ["last_frame_at"] = s.Last,
                    ["data_dir"] = config.Home,
                    ["search_by_meaning"] = meaning()?.Embedder != null,
                };
                foreach (var kv in healthExtras()) h[kv.Key] = kv.Value;
                return HttpResponse.Json(h);
        }
        if (Locked?.Invoke() == true) return HttpResponse.Error(LockedMessage, 423);
        switch (req.Path)
        {
            case "/search":
                return Search(req.Query);
            case "/apps":
                var since = req.Query.TryGetValue("start_time", out var st) ? Time.Parse(st) : null;
                return HttpResponse.Json(new { data = db.Apps(since).Select(x => new { app_name = x.App, frames = x.Frames }) });
        }
        if (req.Path.StartsWith("/frames/")) return FrameImage(req.Path["/frames/".Length..]);
        if (req.Path.StartsWith("/audio/")) return AudioFile(req.Path["/audio/".Length..]);
        return HttpResponse.Error("not found", 404);
    }

    /// The largest `offset` the search accepts.
    public const int MaxOffset = 1_000_000;

    HttpResponse Search(Dictionary<string, string> q)
    {
        string? Get(string k) => q.TryGetValue(k, out var v) ? v : null;
        var type = (Get("content_type") ?? "all").ToLowerInvariant();
        var limit = Math.Clamp(int.TryParse(Get("limit"), out var l) ? l : 20, 1, 100);
        // Clamped so offset + limit can never overflow.
        var offset = Math.Clamp(int.TryParse(Get("offset"), out var o) ? o : 0, 0, MaxOffset);
        if (type is "input" or "memory" or "parsed")  // not captured by OpenRewind
            return HttpResponse.Json(new { data = Array.Empty<object>(), pagination = new { limit, offset, total = 0 } });

        var s = new SearchQuery { Q = Get("q"), AppName = Get("app_name"), WindowName = Get("window_name") };
        if (Get("start_time") is string rs)
        {
            if (Time.Parse(rs) is not string t) return HttpResponse.Error($"bad start_time: {rs}", 400);
            s.StartTime = t;
        }
        if (Get("end_time") is string re)
        {
            if (Time.Parse(re) is not string t) return HttpResponse.Error($"bad end_time: {re}", 400);
            s.EndTime = t;
        }
        int? maxLen = int.TryParse(Get("max_content_length"), out var ml) ? Math.Max(ml, 0) : null;
        string Clip(string t) => maxLen is int m && t.Length > m ? t[..m] + "…" : t;

        var wantScreen = type != "audio";
        // Audio has no app/window, so those filters mean "screen only".
        var wantAudio = (type == "audio" || type == "all") && string.IsNullOrEmpty(s.AppName) && string.IsNullOrEmpty(s.WindowName);
        // To page through a merged list: take the top (offset + limit) of each source, merge by time, then slice.
        var fetch = offset + limit;
        if (Get("mode")?.ToLowerInvariant() == "meaning") return SearchByMeaning(s, wantScreen, wantAudio, offset, limit, Clip);
        var items = new List<(string Ts, object Item)>();
        int total = 0;
        if (wantScreen)
        {
            s.Limit = fetch; s.Offset = 0;
            var (rows, n) = db.Search(s);
            total += n;
            items.AddRange(rows.Select(f => (f.Timestamp, (object)new
            {
                type = "OCR",
                content = new
                {
                    frame_id = f.Id, text = Clip(f.Text), timestamp = f.Timestamp, app_name = f.AppName, window_name = f.WindowName,
                    focused = f.Focused, file_path = f.VideoPath.Length > 0 ? f.VideoPath : f.SnapshotPath, text_source = f.TextSource,
                    offset_index = f.VideoFrame, tags = Array.Empty<string>(),
                },
            })));
        }
        if (wantAudio)
        {
            var (rows, n) = db.SearchAudio(s, fetch);
            total += n;
            items.AddRange(rows.Select(t => (t.Timestamp, (object)new
            {
                type = "Audio",
                content = new
                {
                    chunk_id = t.ChunkId, transcription_id = t.Id, transcription = Clip(t.Text), timestamp = t.Timestamp,
                    start_time = t.StartTime, end_time = t.EndTime, device_name = t.Device, device_type = t.IsInput ? "Input" : "Output",
                    file_path = t.FilePath, speaker = (object?)null, tags = Array.Empty<string>(),
                },
            })));
        }
        var page = items.OrderByDescending(i => i.Ts, StringComparer.Ordinal).Skip(offset).Take(limit).Select(i => i.Item);
        return HttpResponse.Json(new { data = page, pagination = new { limit, offset, total } });
    }

    /// mode=meaning: the moments closest in meaning to q, best first, in the same shape as the word search
    /// plus a "score". Blocked apps and sites are left out.
    HttpResponse SearchByMeaning(SearchQuery s, bool screen, bool audio, int offset, int limit, Func<string, string> clip)
    {
        if (meaning() is not MeaningService m || m.Embedder == null) return HttpResponse.Error("Search by Meaning is off", 400);
        if (string.IsNullOrWhiteSpace(s.Q)) return HttpResponse.Json(new { data = Array.Empty<object>(), pagination = new { limit, offset, total = 0 } });
        bool Has(string have, string? want) => string.IsNullOrEmpty(want) || have.Contains(want, StringComparison.OrdinalIgnoreCase);
        var hits = m.Search(s.Q, new MeaningQuery
        {
            Start = s.StartTime, End = s.EndTime, Screen = screen, Audio = audio, Limit = Math.Min(200, offset + limit),
            Match = (app, window) => Has(app, s.AppName) && Has(window, s.WindowName),
        });
        var page = hits.Skip(offset).Take(limit).Select(h =>
        {
            if (h.IsAudio)
            {
                var c = db.ChunkById(h.FirstId);
                var start = c != null && Time.ParseIso(c.Timestamp) is DateTime cs && Time.ParseIso(h.Timestamp) is DateTime hs ? (hs - cs).TotalSeconds : 0;
                return (object)new
                {
                    type = "Audio",
                    content = new
                    {
                        chunk_id = h.FirstId, transcription_id = h.Id, transcription = clip(h.Text), timestamp = h.Timestamp,
                        start_time = start, end_time = start, device_name = c?.Device ?? "", device_type = c?.IsInput == false ? "Output" : "Input",
                        file_path = c?.FilePath ?? "", speaker = (object?)null, tags = Array.Empty<string>(), score = h.Score,
                    },
                };
            }
            var f = db.FrameById(h.FirstId);
            return new
            {
                type = "OCR",
                content = new
                {
                    frame_id = h.FirstId, text = clip(h.Text), timestamp = h.Timestamp, app_name = h.AppName, window_name = h.WindowName,
                    focused = f?.Focused ?? true, file_path = f == null ? "" : f.VideoPath.Length > 0 ? f.VideoPath : f.SnapshotPath, text_source = f?.TextSource ?? "ocr", offset_index = 0,
                    tags = Array.Empty<string>(), score = h.Score,
                },
            };
        });
        return HttpResponse.Json(new { data = page, pagination = new { limit, offset, total = hits.Count } });
    }

    HttpResponse AudioFile(string idStr)
    {
        if (!long.TryParse(idStr, out var id) || db.ChunkById(id) is not AudioChunk c) return HttpResponse.Error("audio not found", 404);
        return Serve(c.FilePath, "audio/wav", "audio file missing");
    }

    HttpResponse FrameImage(string idStr)
    {
        if (!long.TryParse(idStr, out var id) || db.FrameById(id) is not Frame f) return HttpResponse.Error("frame not found", 404);
        // Only frames of our own data dir, whichever way the picture is stored.
        if (!InDataDir(f.VideoPath.Length > 0 ? f.VideoPath : f.SnapshotPath)) return HttpResponse.Error("snapshot missing", 404);
        if (frameJpeg == null) return Serve(f.SnapshotPath, "image/jpeg", "snapshot missing");
        return frameJpeg(f) is byte[] jpg ? new HttpResponse { ContentType = "image/jpeg", Body = jpg } : HttpResponse.Error("snapshot missing", 404);
    }

    /// Only serve files inside our own data dir.
    HttpResponse Serve(string path, string type, string missing)
    {
        if (!InDataDir(path) || !File.Exists(Path.GetFullPath(path))) return HttpResponse.Error(missing, 404);
        // Decrypted when Encrypt Recordings is on.
        if (RecordingFile.TryRead(Path.GetFullPath(path)) is not byte[] body) return HttpResponse.Error("cannot read the recording", 500);
        return new HttpResponse { ContentType = type, Body = body };
    }

    bool InDataDir(string path)
    {
        if (path.Length == 0) return false;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(config.DataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root, cmp);
    }
}

/// The search page, shared with the Mac app: it is read out of Sources/openrewind/WebUI.swift at build time.
public static class WebUI
{
    public static readonly string Html = Load();

    static string Load()
    {
        using var s = typeof(WebUI).Assembly.GetManifestResourceStream("WebUI.swift");
        if (s == null) return "<!doctype html><title>OpenRewind</title><p>Search page missing from this build.</p>";
        // Git on Windows may check the file out with CRLF line endings.
        var src = new StreamReader(s).ReadToEnd().Replace("\r\n", "\n");
        int a = src.IndexOf("#\"\"\"", StringComparison.Ordinal), b = src.LastIndexOf("\"\"\"#", StringComparison.Ordinal);
        if (a < 0 || b <= a) return "<!doctype html><title>OpenRewind</title><p>Search page missing from this build.</p>";
        // Swift drops the newline after the opening quotes, and the indentation is zero, so this is the page as served on Mac.
        var html = src[(a + 4)..b].TrimStart('\r', '\n');
        return html.EndsWith('\n') ? html[..^1] : html;
    }
}
