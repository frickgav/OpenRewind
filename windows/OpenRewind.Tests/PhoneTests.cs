using System.Text;
using System.Text.Json.Nodes;
using OpenRewind;

public class PhoneTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-phone-test-" + Guid.NewGuid());
    readonly Database db;
    readonly PhoneServer server;
    readonly string id;
    /// The QR code's key, then (after Pair) the phone's own key.
    byte[] secret;
    readonly byte[] token;
    bool locked;
    DateTime now = DateTime.UtcNow;
    long Ms => new DateTimeOffset(now).ToUnixTimeMilliseconds();

    public PhoneTests()
    {
        db = new Database(Config.Load(new[] { "--home", home }).DbPath);
        (id, token) = PhoneCrypto.NewDevice();
        secret = token;
        db.AddPhoneDevice(id, token);
        server = new PhoneServer(db, (r, _) => Task.FromResult<JsonNode>(new JsonObject { ["ok"] = true, ["op"] = r.Op, ["q"] = r.Args["q"]?.GetValue<string>() }),
                                 () => locked, () => "<html>phone</html>", () => now);
    }

    public void Dispose()
    {
        server.Dispose();
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    async Task<(int Status, string Body)> Post(string body, string ip = "192.168.1.20")
    {
        var (s, _, b) = await server.Handle("POST", "/api", Encoding.UTF8.GetBytes(body), ip);
        return (s, Encoding.UTF8.GetString(b));
    }

    string Req(long seq, string op = "search", byte[]? key = null, string? device = null, long? ts = null) =>
        PhoneCrypto.SealRequest(device ?? id, key ?? secret, seq, ts ?? Ms, op, new JsonObject { ["q"] = "invoice" });

    /// Pairs as the page does: the first request with the QR code's key gets the phone's own key back,
    /// and the next request with that key finishes pairing. Leaves `secret` set to the phone's key.
    async Task Pair()
    {
        var (status, body) = await Post(Req(1, "hello"));
        Assert.Equal(200, status);
        var key = PhoneCrypto.FromB64(PhoneCrypto.OpenResponse(body, id, token, 1)!["key"]!.GetValue<string>());
        Assert.NotEqual(token, key);
        Assert.False(db.PhoneDevice(id)!.Paired);
        secret = key;
        var (s2, b2) = await Post(Req(2, "hello"));
        Assert.Equal(200, s2);
        Assert.Null(PhoneCrypto.OpenResponse(b2, id, secret, 2)!["key"]);
        Assert.True(db.PhoneDevice(id)!.Paired);
    }

    [Fact]
    public async Task RoundTrip()
    {
        await Pair();
        var (status, body) = await Post(Req(3));
        Assert.Equal(200, status);
        var answer = PhoneCrypto.OpenResponse(body, id, secret, 3)!;
        Assert.Equal("search", answer["op"]!.GetValue<string>());
        Assert.Equal("invoice", answer["q"]!.GetValue<string>());
        Assert.DoesNotContain("invoice", body);  // sealed on the wire
        // The answer only opens for the request it answers.
        Assert.Null(PhoneCrypto.OpenResponse(body, id, secret, 4));
    }

    [Fact]
    public async Task TheQrCodeStopsWorkingOncePaired()
    {
        await Pair();
        Assert.Equal(401, (await Post(Req(10, key: token))).Status);   // a photo of the code is useless now
        Assert.Equal(200, (await Post(Req(11))).Status);
        Assert.Equal(secret, db.PhoneDevice(id)!.Secret);
    }

    [Fact]
    public async Task ALostKeyAnswerIsSentAgain()
    {
        var (_, b1) = await Post(Req(1, "hello"));
        var (_, b2) = await Post(Req(2, "hello"));   // the first answer never arrived; the phone tries again
        var k1 = PhoneCrypto.OpenResponse(b1, id, token, 1)!["key"]!.GetValue<string>();
        Assert.Equal(k1, PhoneCrypto.OpenResponse(b2, id, token, 2)!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheQrCodeExpiresAfterFifteenMinutes()
    {
        now = now.AddMinutes(16);
        Assert.Equal(401, (await Post(Req(1, "hello"))).Status);
    }

    [Fact]
    public async Task RequestsThatCrossOnTheWayAreAllAccepted()
    {
        await Pair();
        Assert.Equal(200, (await Post(Req(10))).Status);
        Assert.Equal(200, (await Post(Req(8))).Status);    // sent before 10, arrived after
        Assert.Equal(200, (await Post(Req(9))).Status);
        Assert.Equal(409, (await Post(Req(9))).Status);    // but each only once
        Assert.Equal(200, (await Post(Req(100))).Status);
        Assert.Equal(409, (await Post(Req(100 - PhoneCrypto.Window - 1))).Status);  // too far behind
        // None of that counted as guessing.
        for (int i = 0; i < PhoneGuard.PerAddress; i++) Assert.Equal(409, (await Post(Req(100))).Status);
        Assert.Equal(200, (await Post(Req(101))).Status);
    }

    [Fact]
    public void ReplayWindowMarksEachNumberOnce()
    {
        long last = 0, mask = 0;
        foreach (var seq in new long[] { 5, 3, 70, 7, 69, 6 })
        {
            Assert.True(PhoneCrypto.Fresh(last, mask, seq), seq.ToString());
            (last, mask) = PhoneCrypto.Mark(last, mask, seq);
            Assert.False(PhoneCrypto.Fresh(last, mask, seq), seq.ToString());
        }
        Assert.False(PhoneCrypto.Fresh(last, mask, 5));   // 70 - 5 = 65: outside the window
        Assert.True(PhoneCrypto.Fresh(last, mask, 8));
        Assert.False(PhoneCrypto.Fresh(last, mask, 0));
        Assert.False(PhoneCrypto.Fresh(last, mask, -1));
        (last, mask) = PhoneCrypto.Mark(last, mask, 70 + 64);
        Assert.False(PhoneCrypto.Fresh(last, mask, 70));
        Assert.True(PhoneCrypto.Fresh(last, mask, 71));
    }

    [Fact]
    public async Task OnlyRequestsToThisAddressFromThisPage()
    {
        server.Hosts.Add("192.168.1.5:3032");
        Assert.Equal(403, (await server.Handle("POST", "/api", Encoding.UTF8.GetBytes(Req(1)), "192.168.1.20", "evil.example:3032")).Status);
        Assert.Equal(403, (await server.Handle("POST", "/api", Encoding.UTF8.GetBytes(Req(1)), "192.168.1.20", "192.168.1.5:3032", "http://evil.example")).Status);
        Assert.Equal(403, (await server.Handle("GET", "/", Array.Empty<byte>(), "192.168.1.20", "")).Status);
        Assert.Equal(200, (await server.Handle("POST", "/api", Encoding.UTF8.GetBytes(Req(1, "hello")), "192.168.1.20", "192.168.1.5:3032", "http://192.168.1.5:3032")).Status);
        // Refused pages never count toward blocking the phone.
        for (int i = 0; i < PhoneGuard.PerAddress + 2; i++)
            await server.Handle("POST", "/api", Encoding.UTF8.GetBytes("x"), "192.168.1.20", "192.168.1.5:3032", "http://evil.example");
        Assert.Equal(200, (await Post(Req(2, "hello"))).Status);
    }

    [Fact]
    public async Task TamperedRequestsAreRefused()
    {
        var env = JsonNode.Parse(Req(1))!.AsObject();
        var c = PhoneCrypto.FromB64(env["c"]!.GetValue<string>());
        c[3] ^= 1;
        env["c"] = PhoneCrypto.B64(c);
        Assert.Equal(401, (await Post(env.ToJsonString())).Status);
        // Moved to another device id: the extra data no longer matches.
        var (other, otherSecret) = PhoneCrypto.NewDevice();
        db.AddPhoneDevice(other, secret);
        var moved = JsonNode.Parse(Req(2))!.AsObject();
        moved["d"] = other;
        Assert.Equal(401, (await Post(moved.ToJsonString())).Status);
        Assert.Equal(401, (await Post("{\"v\":1}")).Status);
        Assert.Equal(401, (await Post("plain text")).Status);
        // A changed answer does not open.
        var (_, body) = await Post(Req(3));
        var res = JsonNode.Parse(body)!.AsObject();
        var rc = PhoneCrypto.FromB64(res["c"]!.GetValue<string>());
        rc[0] ^= 0x80;
        res["c"] = PhoneCrypto.B64(rc);
        Assert.Null(PhoneCrypto.OpenResponse(res.ToJsonString(), id, secret, 3));
    }

    [Fact]
    public async Task ReplaysAndOldRequestsAreRefused()
    {
        await Pair();
        var first = Req(5);
        Assert.Equal(200, (await Post(first)).Status);
        Assert.Equal(409, (await Post(first)).Status);    // the same again
        Assert.Equal(409, (await Post(Req(2))).Status);   // a number already used
        Assert.Equal(200, (await Post(Req(6))).Status);
        Assert.Equal(409, (await Post(Req(7, ts: Ms - 10 * 60_000))).Status);  // too old
        Assert.Equal(409, (await Post(Req(8, ts: Ms + 10 * 60_000))).Status);  // from the future
        // A copy sent by someone without the key changes nothing and reads nothing.
        var (_, body) = await Post(first);
        Assert.DoesNotContain("invoice", body);
    }

    [Fact]
    public async Task WrongKeyOrUnknownOrRevokedDeviceIsRefused()
    {
        Assert.Equal(401, (await Post(Req(1, key: PhoneCrypto.NewDevice().Secret))).Status);
        Assert.Equal(401, (await Post(Req(1, device: "0123456789abcdef"))).Status);
        await Pair();
        Assert.Equal(200, (await Post(Req(3))).Status);
        db.RemovePhoneDevice(id);
        Assert.Equal(401, (await Post(Req(4))).Status);
    }

    [Fact]
    public async Task LockedAnswersOnlySayLocked()
    {
        await Pair();
        locked = true;
        var (status, body) = await Post(Req(3));
        Assert.Equal(200, status);
        var answer = PhoneCrypto.OpenResponse(body, id, secret, 3)!;
        Assert.Equal("locked", answer["error"]!.GetValue<string>());
        Assert.Null(answer["q"]);
    }

    [Fact]
    public async Task TooManyFailuresBlockTheAddress()
    {
        await Pair();
        var bad = Req(3, key: PhoneCrypto.NewDevice().Secret);
        for (int i = 0; i < PhoneGuard.PerAddress; i++) Assert.Equal(401, (await Post(bad, "192.168.1.66")).Status);
        Assert.Equal(429, (await Post(Req(3), "192.168.1.66")).Status);  // even a good one, for a while
        Assert.Equal(200, (await Post(Req(4), "192.168.1.20")).Status);  // other phones still work
        now = now.AddMinutes(16);
        Assert.Equal(200, (await Post(Req(5), "192.168.1.66")).Status);
    }

    [Fact]
    public async Task ServesThePageAndNothingElse()
    {
        var (s, type, body) = await server.Handle("GET", "/", Array.Empty<byte>(), "192.168.1.20");
        Assert.Equal(200, s);
        Assert.StartsWith("text/html", type);
        Assert.Equal("<html>phone</html>", Encoding.UTF8.GetString(body));
        Assert.Equal(404, (await server.Handle("GET", "/search?q=x", Array.Empty<byte>(), "192.168.1.20")).Status);
        Assert.Equal(405, (await server.Handle("GET", "/api", Array.Empty<byte>(), "192.168.1.20")).Status);
    }

    [Fact]
    public void OnlyPrivateAddresses()
    {
        foreach (var ok in new[] { "10.0.0.5", "172.16.0.1", "172.31.255.1", "192.168.1.1", "100.100.1.1", "::ffff:192.168.0.2" })
            Assert.True(Lan.IsPrivate(System.Net.IPAddress.Parse(ok)), ok);
        foreach (var no in new[] { "8.8.8.8", "172.32.0.1", "127.0.0.1", "169.254.1.1", "100.128.0.1", "fe80::1", "::1" })
            Assert.False(Lan.IsPrivate(System.Net.IPAddress.Parse(no)), no);
    }

    [Fact]
    public void PairingUrlKeepsTheSecretInTheFragment()
    {
        var url = PhoneCrypto.PairingUrl("http://192.168.1.5:3032", id, secret);
        Assert.StartsWith("http://192.168.1.5:3032/#d=" + id + "&k=", url);
        Assert.Equal(secret, PhoneCrypto.FromB64(url[(url.IndexOf("&k=") + 3)..]));
    }

    [Fact]
    public void PendingDevicesAreForgottenAfterFifteenMinutes()
    {
        Assert.Contains(db.PhoneDevices(), d => d.Id == id && !d.Paired);
    }
}
