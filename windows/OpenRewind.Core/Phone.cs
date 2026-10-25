using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRewind;

/// A phone paired with Phone Access. `Secret` is its 256-bit key: first the one-time pairing key from
/// the QR code, then, from the phone's first request on, a new key the computer sends it sealed
/// (`NextSecret` until the phone has used it once). `LastSeq` is the highest request number accepted
/// from it and `SeenMask` which of the 64 numbers below it were used (bit 0 = LastSeq - 1), so requests
/// that cross on the way are all accepted once and a copy of any of them never is.
/// A phone that has not finished pairing within 15 minutes is forgotten, and its QR code stops working.
public sealed record PhoneDevice(string Id, string Name, DateTime Created, DateTime? LastSeen, long LastSeq, byte[] Secret,
                                 byte[]? NextSecret = null, long SeenMask = 0)
{
    /// The phone has used the key it got after pairing, so the QR code's key is gone.
    public bool Paired => LastSeen != null && NextSecret == null;
}

/// Why a request was refused. Malformed, UnknownDevice, Expired and BadSeal answer "not paired" (401) and
/// count as tries at guessing; Replayed and Stale come from a phone that has the key (the seal opened),
/// so they answer "try again" (409) and do not count.
public enum PhoneFailure { Malformed, UnknownDevice, Expired, BadSeal, Replayed, Stale }

public sealed class PhoneAuthException : Exception
{
    public PhoneFailure Failure { get; }
    public PhoneAuthException(PhoneFailure f) : base(f.ToString()) { Failure = f; }
}

/// A request that passed every check: sealed with the device's key, fresh, never seen before.
/// `Key` is the secret it was sealed with (its answer is sealed with the same); `NewKey` is true when
/// that was the key sent after pairing, used for the first time.
public sealed record PhoneRequest(PhoneDevice Device, long Seq, string Op, JsonObject Args, byte[] Key, bool NewKey = false);

/// Phone Access messages: end-to-end encrypted between the phone's browser and this computer, so the
/// Wi-Fi, the router and anyone else on the network see only noise. See docs/phone-access.md.
///
/// Keys: HKDF-SHA256 of the shared secret (salt "OpenRewind phone"), one key for requests and one for
/// answers ("openrewind/phone/v1/req/" or "…/res/" + device id). Every message is AES-256-GCM with a fresh
/// random 96-bit nonce. A request is {"v":1,"d":device,"n":nonce,"c":ciphertext+tag} sealing
/// {"seq","ts","op","args"} with the extra data "openrewind-phone-v1|req|"+device; seq must be higher than
/// any before it from that device, or one of the 64 below the highest not used yet, and ts (Unix ms) within
/// 5 minutes of now. An answer is {"n","c"} sealing the result with "openrewind-phone-v1|res|"+device+"|"+seq,
/// so it only fits the request it answers. The QR code's key is for pairing only: the answer to the
/// first request carries a new random key ("key"), and once the phone has used it the old one is gone.
/// The Mac app (PhoneAccess.swift) and the phone page (PhonePage.swift, using SJCL) do exactly the same.
public static class PhoneCrypto
{
    public const string Salt = "OpenRewind phone";
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);
    /// How long a QR code can be used to pair.
    public static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(15);
    /// How far below the highest request number a request may still arrive.
    public const int Window = 64;

    /// True when request `seq` was not used yet: above `last`, or in the window below it and not marked.
    public static bool Fresh(long last, long mask, long seq)
    {
        if (seq <= 0) return false;
        if (seq > last) return true;
        long back = last - seq;
        return back >= 1 && back <= Window && (mask & (1L << (int)(back - 1))) == 0;
    }

    /// `last` and `mask` with `seq` marked as used (call only when Fresh).
    public static (long Last, long Mask) Mark(long last, long mask, long seq)
    {
        if (seq > last)
        {
            long shift = seq - last;
            mask = shift > Window ? 0 : shift == Window ? long.MinValue : (mask << (int)shift) | (1L << (int)(shift - 1));
            return (seq, mask);
        }
        return (last, mask | (1L << (int)(last - seq - 1)));
    }

    public static (byte[] Req, byte[] Res) Keys(byte[] secret, string deviceId) =>
        (HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes("openrewind/phone/v1/req/" + deviceId)),
         HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes("openrewind/phone/v1/res/" + deviceId)));

    public static string RequestAad(string deviceId) => "openrewind-phone-v1|req|" + deviceId;
    public static string ResponseAad(string deviceId, long seq) => $"openrewind-phone-v1|res|{deviceId}|{seq}";

    /// A new secret and device id for pairing.
    public static (string Id, byte[] Secret) NewDevice() =>
        (Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), RandomNumberGenerator.GetBytes(32));

    /// What the QR code holds: the page address, with the device and secret after # so the browser
    /// never sends them anywhere (a URL fragment stays on the phone).
    public static string PairingUrl(string baseUrl, string deviceId, byte[] secret) =>
        $"{baseUrl.TrimEnd('/')}/#d={deviceId}&k={B64(secret)}";

    /// URL-safe base64 without padding.
    public static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// (nonce, ciphertext with the 16-byte tag at the end).
    public static (byte[] Nonce, byte[] Sealed) Seal(byte[] key, byte[] plain, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(aad));
        return (nonce, cipher.Concat(tag).ToArray());
    }

    /// The plain text, or null when the key, nonce, extra data or any byte is wrong.
    public static byte[]? Unseal(byte[] key, byte[] nonce, byte[] sealed_, string aad)
    {
        if (nonce.Length != 12 || sealed_.Length < 16) return null;
        var plain = new byte[sealed_.Length - 16];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, sealed_.AsSpan(0, plain.Length), sealed_.AsSpan(plain.Length), plain, Encoding.UTF8.GetBytes(aad));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// A request as the phone sends it (the phone page does this in JavaScript; tests use this one).
    public static string SealRequest(string deviceId, byte[] secret, long seq, long tsMillis, string op, JsonObject? args = null)
    {
        var plain = new JsonObject { ["seq"] = seq, ["ts"] = tsMillis, ["op"] = op, ["args"] = args ?? new JsonObject() };
        var (n, c) = Seal(Keys(secret, deviceId).Req, Encoding.UTF8.GetBytes(plain.ToJsonString()), RequestAad(deviceId));
        return new JsonObject { ["v"] = 1, ["d"] = deviceId, ["n"] = B64(n), ["c"] = B64(c) }.ToJsonString();
    }

    /// Checks and opens a request. `device` finds a paired (or pairing) device by id. The sequence is checked
    /// here against the device as read, and again atomically when it is recorded (Database.AcceptPhoneRequest).
    public static PhoneRequest Open(string body, Func<string, PhoneDevice?> device, DateTime nowUtc)
    {
        JsonObject env;
        try { env = JsonNode.Parse(body) as JsonObject ?? throw new PhoneAuthException(PhoneFailure.Malformed); }
        catch (JsonException) { throw new PhoneAuthException(PhoneFailure.Malformed); }
        string S(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        var id = S(env, "d");
        int version = env["v"] is JsonValue vv && vv.TryGetValue<int>(out var ver) ? ver : 0;
        if (id.Length is 0 or > 64 || version != 1) throw new PhoneAuthException(PhoneFailure.Malformed);
        var d = device(id) ?? throw new PhoneAuthException(PhoneFailure.UnknownDevice);
        byte[] nonce, sealed_;
        try { nonce = FromB64(S(env, "n")); sealed_ = FromB64(S(env, "c")); }
        catch (FormatException) { throw new PhoneAuthException(PhoneFailure.Malformed); }
        byte[] key = d.Secret;
        bool newKey = false;
        byte[]? plain = null;
        if (d.NextSecret != null && (plain = Unseal(Keys(d.NextSecret, d.Id).Req, nonce, sealed_, RequestAad(d.Id))) != null)
        {
            key = d.NextSecret;
            newKey = true;
        }
        plain ??= Unseal(Keys(d.Secret, d.Id).Req, nonce, sealed_, RequestAad(d.Id)) ?? throw new PhoneAuthException(PhoneFailure.BadSeal);
        // The QR code's key works only while pairing, for 15 minutes.
        if (!newKey && !d.Paired && nowUtc - d.Created > PairingWindow) throw new PhoneAuthException(PhoneFailure.Expired);
        JsonObject msg;
        try { msg = JsonNode.Parse(plain) as JsonObject ?? throw new PhoneAuthException(PhoneFailure.Malformed); }
        catch (JsonException) { throw new PhoneAuthException(PhoneFailure.Malformed); }
        long seq = msg["seq"] is JsonValue sv && sv.TryGetValue<long>(out var sq) ? sq : -1;
        long ts = msg["ts"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
        if (!Fresh(d.LastSeq, d.SeenMask, seq)) throw new PhoneAuthException(PhoneFailure.Replayed);
        var when = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ts, 0, 253402300799999)).UtcDateTime;
        if ((when - nowUtc).Duration() > MaxSkew) throw new PhoneAuthException(PhoneFailure.Stale);
        return new PhoneRequest(d, seq, S(msg, "op"), msg["args"] as JsonObject ?? new JsonObject(), key, newKey);
    }

    /// The sealed answer to request `seq` from `d`, with `key` (the secret the request was sealed with).
    public static string SealResponse(PhoneDevice d, long seq, JsonNode result, byte[]? key = null)
    {
        var (n, c) = Seal(Keys(key ?? d.Secret, d.Id).Res, Encoding.UTF8.GetBytes(result.ToJsonString()), ResponseAad(d.Id, seq));
        return new JsonObject { ["n"] = B64(n), ["c"] = B64(c) }.ToJsonString();
    }

    /// Opens an answer as the phone does. Null when it was changed or answers another request.
    public static JsonNode? OpenResponse(string body, string deviceId, byte[] secret, long seq)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject env) return null;
            var plain = Unseal(Keys(secret, deviceId).Res, FromB64(env["n"]!.GetValue<string>()), FromB64(env["c"]!.GetValue<string>()), ResponseAad(deviceId, seq));
            return plain == null ? null : JsonNode.Parse(plain);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException) { return null; }
    }
}

/// Slows down guessing: 10 refused requests from one address in 5 minutes block it for 15 minutes,
/// and 100 from everyone together block all phones for 5 minutes.
public sealed class PhoneGuard
{
    readonly Dictionary<string, List<DateTime>> fails = new();
    readonly Dictionary<string, DateTime> blockedUntil = new();
    readonly List<DateTime> all = new();
    DateTime allBlockedUntil = DateTime.MinValue;
    public const int PerAddress = 10, Total = 100;
    static readonly TimeSpan Window = TimeSpan.FromMinutes(5), Block = TimeSpan.FromMinutes(15);

    public bool Allowed(string ip, DateTime now)
    {
        lock (fails)
            return now >= allBlockedUntil && (!blockedUntil.TryGetValue(ip, out var u) || now >= u);
    }

    /// A refused try from `ip`. Only wrong-key tries (`global`) count toward blocking every phone, so
    /// stray requests from web pages on the network cannot shut phones out.
    public void Failed(string ip, DateTime now, bool global = true)
    {
        lock (fails)
        {
            if (!fails.TryGetValue(ip, out var l)) fails[ip] = l = new();
            l.Add(now);
            l.RemoveAll(t => now - t > Window);
            if (l.Count >= PerAddress) { blockedUntil[ip] = now + Block; l.Clear(); }
            if (!global) return;
            all.Add(now);
            all.RemoveAll(t => now - t > Window);
            if (all.Count >= Total) { allBlockedUntil = now + Window; all.Clear(); }
            if (fails.Count > 1000) fails.Clear();  // memory bound; blocks stay
        }
    }
}

/// Private network addresses: the only ones Phone Access listens on and answers.
public static class Lan
{
    /// 10/8, 172.16/12, 192.168/16, and 100.64/10 (carrier-grade NAT, used by Tailscale).
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    /// This computer's private IPv4 addresses that a phone can reach: adapters that are up and have a
    /// default gateway (the Wi-Fi or Ethernet the PC is on), plus Tailscale's 100.64/10 addresses.
    /// Virtual adapters of WSL, Hyper-V, Docker, VirtualBox and VMware are left out, so the pairing code
    /// never points at them and nothing in a container or VM can reach the server. Wi-Fi and Ethernet first.
    public static List<IPAddress> Addresses()
    {
        var out_ = new List<(IPAddress Ip, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (IsVirtual(nic.Name + " " + nic.Description)) continue;
                var props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var a in props.UnicastAddresses)
                {
                    if (!IsPrivate(a.Address)) continue;
                    bool tailscale = a.Address.GetAddressBytes() is [100, >= 64 and <= 127, _, _];
                    if (!gateway && !tailscale) continue;
                    int rank = gateway && nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet ? 0 : gateway ? 1 : 2;
                    out_.Add((a.Address, rank));
                }
            }
        }
        catch (NetworkInformationException) { }
        return out_.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }

    /// Adapters that belong to virtual machines and containers, by their name or description.
    public static bool IsVirtual(string nameAndDescription) =>
        new[] { "vEthernet", "Hyper-V", "WSL", "Docker", "VirtualBox", "VMware", "VMnet", "Npcap Loopback" }
            .Any(v => nameAndDescription.Contains(v, StringComparison.OrdinalIgnoreCase));
}

public sealed partial class Database
{
    /// The Mac app creates the same table. With Encrypt Recordings on, the whole database (secrets too) is encrypted.
    void MigratePhone()
    {
        Exec("""
        CREATE TABLE IF NOT EXISTS phone_devices (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL DEFAULT '',
            secret TEXT NOT NULL,
            created_at TEXT NOT NULL,
            last_seen TEXT,
            last_seq INTEGER NOT NULL DEFAULT 0,
            next_secret TEXT,
            seen_mask INTEGER NOT NULL DEFAULT 0
        );
        """);
        // Tables made by earlier test builds lack the last two columns.
        var cols = Strings("SELECT name FROM pragma_table_info('phone_devices')");
        if (!cols.Contains("next_secret")) Exec("ALTER TABLE phone_devices ADD COLUMN next_secret TEXT");
        if (!cols.Contains("seen_mask")) Exec("ALTER TABLE phone_devices ADD COLUMN seen_mask INTEGER NOT NULL DEFAULT 0");
    }

    const string PhoneCols = "id, name, created_at, last_seen, last_seq, secret, next_secret, seen_mask";

    static PhoneDevice ReadPhone(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Time.ParseIso(r.GetString(2)) ?? DateTime.UnixEpoch,
            r.IsDBNull(3) ? null : Time.ParseIso(r.GetString(3)), r.GetInt64(4), System.Convert.FromBase64String(r.GetString(5)),
            r.IsDBNull(6) ? null : System.Convert.FromBase64String(r.GetString(6)), r.GetInt64(7));

    /// Paired phones, and ones still pairing from the last 15 minutes; older unfinished pairings are removed.
    public List<PhoneDevice> PhoneDevices()
    {
        lock (gate)
        {
            if (!HasTable("phone_devices")) return new();
            Run("DELETE FROM phone_devices WHERE (last_seen IS NULL OR next_secret IS NOT NULL) AND created_at < ?",
                Time.Iso(DateTime.UtcNow - PhoneCrypto.PairingWindow));
            return Rows($"SELECT {PhoneCols} FROM phone_devices ORDER BY created_at", ReadPhone);
        }
    }

    public PhoneDevice? PhoneDevice(string id) => PhoneDevices().FirstOrDefault(d => d.Id == id);

    public void AddPhoneDevice(string id, byte[] secret, string name = "") =>
        Run("INSERT INTO phone_devices (id, name, secret, created_at) VALUES (?, ?, ?, ?)", id, name, System.Convert.ToBase64String(secret), Time.Now());

    /// Revoke: the phone's key is deleted, so nothing it sends is accepted again.
    public void RemovePhoneDevice(string id) => Run("DELETE FROM phone_devices WHERE id = ?", id);

    public void RenamePhoneDevice(string id, string name) => Run("UPDATE phone_devices SET name = ? WHERE id = ?", name, id);

    /// Records request `seq` as used, if it was not used before (checked and written under the lock, so two
    /// copies sent at once cannot both pass). `newKey`: the request was sealed with the key sent after
    /// pairing, which now replaces the QR code's key. While pairing, returns the key to send to the phone
    /// (`candidate` the first time, the same one after). Accepted is false for a replay or a revoked phone.
    public (bool Accepted, byte[]? SendKey) AcceptPhoneRequest(string id, long seq, bool newKey = false, byte[]? candidate = null)
    {
        lock (gate)
        {
            var row = Rows($"SELECT {PhoneCols} FROM phone_devices WHERE id = ?", ReadPhone, id).FirstOrDefault();
            if (row == null || !PhoneCrypto.Fresh(row.LastSeq, row.SeenMask, seq)) return (false, null);
            var (last, mask) = PhoneCrypto.Mark(row.LastSeq, row.SeenMask, seq);
            if (newKey && row.NextSecret != null)
            {
                Run("UPDATE phone_devices SET secret = next_secret, next_secret = NULL, last_seq = ?, seen_mask = ?, last_seen = ? WHERE id = ?",
                    last, mask, Time.Now(), id);
                return (true, null);
            }
            if (!row.Paired)
            {
                var next = row.NextSecret ?? candidate ?? RandomNumberGenerator.GetBytes(32);
                Run("UPDATE phone_devices SET next_secret = ?, last_seq = ?, seen_mask = ?, last_seen = ? WHERE id = ?",
                    System.Convert.ToBase64String(next), last, mask, Time.Now(), id);
                return (true, next);
            }
            Run("UPDATE phone_devices SET last_seq = ?, seen_mask = ?, last_seen = ? WHERE id = ?", last, mask, Time.Now(), id);
            return (true, null);
        }
    }
}

/// Phone Access: a small web server on this computer's private network address (port 3032), apart from
/// the localhost-only API on 3031. It serves the phone page, and answers only sealed requests from
/// paired phones (see PhoneCrypto). Nothing is reachable from outside the local network.
public sealed class PhoneServer : IDisposable
{
    public const int DefaultPort = 3032;
    readonly Database db;
    readonly Func<PhoneRequest, CancellationToken, Task<JsonNode>> ops;
    readonly Func<bool> locked;
    readonly Func<string> page;
    readonly PhoneGuard guard = new();
    readonly List<TcpListener> listeners = new();
    readonly CancellationTokenSource stop = new();
    readonly Func<DateTime> now;

    /// When a paired phone last asked something (UTC); for turning Phone Access off after hours unused.
    public DateTime LastUsed { get; private set; }
    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();
    /// The Host headers it answers ("192.168.1.5:3032"): its own addresses only, so a web page that renames
    /// itself to this address (DNS rebinding) is refused. Filled by Start; tests add their own.
    public HashSet<string> Hosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// `ops` answers a checked request ("search", "ask", "promises"…); `locked` is the app lock;
    /// `page` is the phone page's HTML.
    public PhoneServer(Database db, Func<PhoneRequest, CancellationToken, Task<JsonNode>> ops, Func<bool> locked, Func<string> page,
                       Func<DateTime>? now = null)
    {
        this.db = db; this.ops = ops; this.locked = locked; this.page = page;
        this.now = now ?? (() => DateTime.UtcNow);
        LastUsed = this.now();
    }

    /// Listens on each private address. False when there is none (no Wi-Fi or Ethernet) or none could be used.
    public bool Start(int port = DefaultPort)
    {
        var urls = new List<string>();
        foreach (var ip in Lan.Addresses())
        {
            try
            {
                var l = new TcpListener(ip, port);
                l.Start();
                listeners.Add(l);
                urls.Add($"http://{ip}:{port}");
                Hosts.Add($"{ip}:{port}");
                _ = Task.Run(() => Accept(l));
            }
            catch (SocketException e) { Log.Write($"phone access: cannot listen on {ip}:{port}: {e.Message}"); }
        }
        Urls = urls;
        if (urls.Count > 0) Log.Write($"phone access on {string.Join(", ", urls)}");
        return urls.Count > 0;
    }

    async Task Accept(TcpListener l)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => Serve(c));
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        try
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (!Lan.IsPrivate(remote)) return;  // only the local network; drop anything else without a word
            client.ReceiveTimeout = 10_000;
            var stream = client.GetStream();
            var buf = new byte[16384];
            var data = new MemoryStream();
            int end = -1;
            using var readFor = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            readFor.CancelAfter(TimeSpan.FromSeconds(15));
            while (end < 0)
            {
                int n = await stream.ReadAsync(buf, readFor.Token).ConfigureAwait(false);
                if (n <= 0 || data.Length > 16384) return;
                data.Write(buf, 0, n);
                end = data.GetBuffer().AsSpan(0, (int)data.Length).IndexOf("\r\n\r\n"u8);
            }
            var head = Encoding.ASCII.GetString(data.GetBuffer(), 0, end);
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return;
            int length = 0;
            string host = "";
            string? origin = null;
            foreach (var h in lines.Skip(1))
            {
                if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) && !int.TryParse(h[15..].Trim(), out length)) return;
                if (h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) host = h[5..].Trim();
                if (h.StartsWith("Origin:", StringComparison.OrdinalIgnoreCase)) origin = h[7..].Trim();
            }
            if (length is < 0 or > 65536) return;
            var body = new MemoryStream();
            body.Write(data.GetBuffer(), end + 4, (int)data.Length - end - 4);
            while (body.Length < length)
            {
                int n = await stream.ReadAsync(buf, readFor.Token).ConfigureAwait(false);
                if (n <= 0) return;
                body.Write(buf, 0, n);
            }
            var (status, type, bytes) = await Handle(first[0], first[1], body.ToArray().AsSpan(0, length).ToArray(), remote.ToString(), host, origin).ConfigureAwait(false);
            var reason = status switch
            {
                200 => "OK", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict",
                429 => "Too Many Requests", _ => "Error",
            };
            var hdr = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\n" +
                      "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), stop.Token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, stop.Token).ConfigureAwait(false);
        }
        catch (Exception) { }
    }

    static (int, string, byte[]) Plain(int status, string msg) =>
        (status, "application/json", Encoding.UTF8.GetBytes(new JsonObject { ["error"] = msg }.ToJsonString()));

    /// One request, without the socket (tests call this directly). `host` and `origin` are the request's
    /// headers; null skips that check (tests).
    public async Task<(int Status, string ContentType, byte[] Body)> Handle(string method, string target, byte[] body, string remote,
                                                                          string? host = null, string? origin = null)
    {
        // Only requests addressed to this computer, and only from the phone page it served (or none).
        if (host != null && !Hosts.Contains(host)) return Plain(403, "wrong address");
        if (origin != null && (host == null || !origin.Equals("http://" + host, StringComparison.OrdinalIgnoreCase))) return Plain(403, "wrong page");
        var path = target.Split('?')[0];
        if (method == "GET" && (path == "/" || path == "/index.html"))
            return (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(page()));
        if (path != "/api") return Plain(404, "not found");
        if (method != "POST") return Plain(405, "method not allowed");
        var t = now();
        if (!guard.Allowed(remote, t)) return Plain(429, "too many tries; wait a few minutes");
        PhoneRequest req;
        byte[]? sendKey;
        try
        {
            req = PhoneCrypto.Open(Encoding.UTF8.GetString(body), id => db.PhoneDevice(id), t);
            bool ok;
            (ok, sendKey) = db.AcceptPhoneRequest(req.Device.Id, req.Seq, req.NewKey, RandomNumberGenerator.GetBytes(32));
            if (!ok) throw new PhoneAuthException(PhoneFailure.Replayed);
        }
        catch (PhoneAuthException e) when (e.Failure is PhoneFailure.Replayed or PhoneFailure.Stale)
        {
            // Sealed with the right key, so it is the phone itself: requests that crossed, or a wrong clock.
            Log.Write($"phone access: {e.Failure} request from {remote}");
            return Plain(409, e.Failure == PhoneFailure.Stale ? "clock" : "retry");
        }
        catch (PhoneAuthException e)
        {
            guard.Failed(remote, t, global: e.Failure == PhoneFailure.BadSeal);
            Log.Write($"phone access: refused a request from {remote} ({e.Failure})");
            return Plain(401, "not paired");
        }
        LastUsed = t;
        JsonNode result;
        if (locked()) result = new JsonObject { ["ok"] = false, ["error"] = "locked", ["message"] = "OpenRewind is locked. Unlock it on the computer, then try again." };
        else
        {
            try { result = await ops(req, stop.Token).ConfigureAwait(false); }
            catch (Exception e) { result = new JsonObject { ["ok"] = false, ["error"] = "failed", ["message"] = e.Message }; }
        }
        // Pairing: the phone's own key, replacing the QR code's (sealed, so only that phone reads it).
        if (sendKey != null && result is JsonObject o) o["key"] = PhoneCrypto.B64(sendKey);
        return (200, "application/json", Encoding.UTF8.GetBytes(PhoneCrypto.SealResponse(req.Device, req.Seq, result, req.Key)));
    }

    public void Dispose()
    {
        stop.Cancel();
        foreach (var l in listeners) { try { l.Stop(); } catch { } }
        listeners.Clear();
    }
}

/// What a paired phone may ask for, answered with the same rules as the computer: the search goes through
/// the local API's /search (so the block list and App Lock apply exactly as there), Ask AI uses the
/// user's provider with the Ask AI lookups (no screenshots), and promises leave out blocked sources.
/// Answers carry text only: no screenshots, audio or file paths. Mac: PhoneOps in PhoneAccess.swift.
public sealed class PhoneOps
{
    readonly Database db;
    readonly Func<HttpRequest, HttpResponse> api;
    readonly Func<Blocklist> blocked;
    readonly string computer;

    /// Settings for Ask AI when the user set up a provider and accepted what Ask AI sends; else null.
    public Func<AiSettings?> Ai { get; set; } = () => null;
    /// Makes the client for a request (tests pass a fake).
    public Func<AiSettings, IAiClient> Client { get; set; } = s => new AiClient(s);
    /// Called with what each question cost, to add to the totals in AI Settings.
    public Action<AiSettings, AiUsage, double?>? Spent { get; set; }
    public Func<string, AiPrice?> Price { get; set; } = _ => null;

    public PhoneOps(Database db, Func<HttpRequest, HttpResponse> api, string computer, Func<Blocklist>? blocked = null)
    {
        this.db = db; this.api = api; this.computer = computer;
        this.blocked = blocked ?? (() => Blocklist.Current);
    }

    static string Arg(JsonObject a, string k, int max) =>
        a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Length > max ? s[..max] : s).Trim() : "";

    static JsonObject Fail(string error, string message) => new() { ["ok"] = false, ["error"] = error, ["message"] = message };

    public async Task<JsonNode> Run(PhoneRequest r, CancellationToken ct)
    {
        switch (r.Op)
        {
            case "hello":
                var name = Arg(r.Args, "name", 60);
                if (name.Length > 0 && name != r.Device.Name) db.RenamePhoneDevice(r.Device.Id, name);
                return new JsonObject { ["ok"] = true, ["computer"] = computer, ["ai"] = Ai() != null };
            case "search":
                return Search(Arg(r.Args, "q", 200), Arg(r.Args, "kind", 10));
            case "promises":
                return Promises();
            case "ask":
                return await Ask(Arg(r.Args, "question", 2000), ct);
            default:
                return Fail("unknown", "This phone page is newer or older than OpenRewind. Reload it.");
        }
    }

    JsonNode Search(string q, string kind)
    {
        if (q.Length == 0) return Fail("empty", "Type something to search for.");
        var query = new Dictionary<string, string>
        {
            ["q"] = q, ["limit"] = "30", ["max_content_length"] = "400",
            ["content_type"] = kind is "screen" ? "ocr" : kind is "audio" ? "audio" : "all",
        };
        var res = api(new HttpRequest("GET", "/search", query));
        if (res.Status == 423) return Fail("locked", "OpenRewind is locked. Unlock it on the computer, then try again.");
        if (res.Status != 200) return Fail("failed", "The search did not work.");
        var results = new JsonArray();
        if (JsonNode.Parse(res.Body)?["data"] is JsonArray data)
            foreach (var item in data)
            {
                var c = item?["content"];
                if (c == null) continue;
                var audio = item!["type"]?.GetValue<string>() == "Audio";
                results.Add(new JsonObject
                {
                    ["kind"] = audio ? "audio" : "screen",
                    ["time"] = c["timestamp"]?.GetValue<string>() ?? "",
                    ["app"] = audio ? (c["device_type"]?.GetValue<string>() == "Input" ? "Microphone" : "Computer audio") : c["app_name"]?.GetValue<string>() ?? "",
                    ["title"] = audio ? "" : c["window_name"]?.GetValue<string>() ?? "",
                    ["text"] = (audio ? c["transcription"] : c["text"])?.GetValue<string>() ?? "",
                });
            }
        return new JsonObject { ["ok"] = true, ["results"] = results };
    }

    JsonNode Promises()
    {
        var list = blocked();
        var arr = new JsonArray();
        foreach (var p in db.PromiseList(OpenRewind.Promises.Open, 100))
        {
            if (OpenRewind.Promises.Hidden(p, list)) continue;
            arr.Add(new JsonObject
            {
                ["what"] = p.What, ["who"] = p.Who, ["due"] = p.RemindAt is DateTime d ? Time.Iso(d) : null,
                ["time"] = Time.Iso(p.Time), ["source"] = p.Source,
            });
        }
        return new JsonObject { ["ok"] = true, ["promises"] = arr };
    }

    async Task<JsonNode> Ask(string question, CancellationToken ct)
    {
        if (question.Length == 0) return Fail("empty", "Type a question.");
        if (Ai() is not AiSettings ai) return Fail("no-ai", "Set up Ask AI on the computer first (Settings → AI).");
        var convo = new AiConversation();
        var tools = new AiTools(db) { Blocked = blocked() };
        var answer = new List<string>();
        string? error = null;
        await convo.Ask(question, Client(ai), tools, item =>
        {
            if (item.Kind == AiChatKind.Assistant) answer.Add(item.Text);
            if (item.Kind == AiChatKind.Error) error = item.Text;
        }, Price(ai.Model), ct);
        Spent?.Invoke(ai, convo.LastUsed, convo.LastCost);
        if (answer.Count == 0) return Fail("failed", error ?? "No answer came back.");
        return new JsonObject { ["ok"] = true, ["answer"] = string.Join("\n\n", answer), ["model"] = ai.Model };
    }
}
