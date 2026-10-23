using System.Text.Json.Nodes;
using OpenRewind;

/// The phone page's JavaScript (SJCL) and this side must agree byte for byte; the vectors below were made
/// by running the page's own SJCL in Node with the key 00 01 … 1f and the nonce 01 02 … 0c.
public class PhoneInteropTests
{
    const string Device = "0123456789abcdef";
    const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    const string JsRequest = "{\"v\":1,\"d\":\"0123456789abcdef\",\"n\":\"AQIDBAUGBwgJCgsM\",\"c\":\"_kuJvJDzdJ10FVU9EghPfdGAQIMUZr3mEDbGrFDNFyg-P-bwTzzYatp3Tw2W9tdLbGHFN-8gl2dF6KlKFY7MmwtidN6-PvNKrerAgToEIxYcCdL4metp1-H4eB_rNcXZ9pvWgt7AaaSu\"}";
    const string JsResponse = "{\"n\":\"AQIDBAUGBwgJCgsM\",\"c\":\"3wnAXb0k9br9GnhIOkhkMgYniAhcpnaj7bGi\"}";
    static readonly DateTime Then = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime;

    static PhoneDevice Dev(long lastSeq = 0) => new(Device, "", Then, null, lastSeq, PhoneCrypto.FromB64(Key));

    [Fact]
    public void OpensARequestSealedByThePage()
    {
        Assert.Equal(Enumerable.Range(0, 32).Select(i => (byte)i), PhoneCrypto.FromB64(Key));
        var r = PhoneCrypto.Open(JsRequest, id => id == Device ? Dev() : null, Then);
        Assert.Equal("search", r.Op);
        Assert.Equal(1700000000000, r.Seq);
        Assert.Equal("héllo", r.Args["q"]!.GetValue<string>());
        Assert.Equal("all", r.Args["kind"]!.GetValue<string>());
    }

    [Fact]
    public void ReadsAnAnswerTheWayThePageDoes()
    {
        var answer = PhoneCrypto.OpenResponse(JsResponse, Device, PhoneCrypto.FromB64(Key), 1700000000000);
        Assert.True(answer!["ok"]!.GetValue<bool>());
        Assert.Null(PhoneCrypto.OpenResponse(JsResponse, Device, PhoneCrypto.FromB64(Key), 1700000000001));
    }

    [Fact]
    public void PageVectorStillChecksReplayAndAge()
    {
        Assert.Equal(PhoneFailure.Replayed, Assert.Throws<PhoneAuthException>(() => PhoneCrypto.Open(JsRequest, _ => Dev(1700000000000), Then)).Failure);
        Assert.Equal(PhoneFailure.Stale, Assert.Throws<PhoneAuthException>(() => PhoneCrypto.Open(JsRequest, _ => Dev(), Then.AddMinutes(6))).Failure);
    }

    [Fact]
    public void ThePageLoadsNothingFromOutside()
    {
        var html = PhonePage.Html;
        Assert.StartsWith("<!doctype html>", html);
        Assert.EndsWith("</html>", html);
        Assert.Contains("sjcl.mode.gcm", html);
        Assert.Contains("connect-src 'self'", html);
        Assert.DoesNotContain("src=\"http", html);
        Assert.DoesNotContain("innerHTML", html);  // results are shown as text only
    }
}

public class PhoneOpsTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-phoneops-test-" + Guid.NewGuid());
    readonly Database db;
    readonly PhoneOps ops;
    readonly PhoneDevice device;
    Blocklist list = new(Array.Empty<BlockedApp>(), Array.Empty<string>(), false);

    public PhoneOpsTests()
    {
        var config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
        var api = new Api(db, config, blocked: () => list);
        ops = new PhoneOps(db, api.Handle, "Work PC", () => list);
        var (id, secret) = PhoneCrypto.NewDevice();
        db.AddPhoneDevice(id, secret);
        device = db.PhoneDevice(id)!;
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    Task<JsonNode> Run(string op, JsonObject? args = null) => ops.Run(new PhoneRequest(device, 1, op, args ?? new JsonObject()), default);

    void AddFrame(string app, string text) =>
        db.Insert(new Frame(0, Time.Iso(DateTime.UtcNow.AddMinutes(-3)), app, "Window", true, Path.Combine(home, "secret-path.jpg"), text, "ocr", 0));

    [Fact]
    public async Task HelloNamesThePhone()
    {
        var r = await Run("hello", new JsonObject { ["name"] = "iPhone" });
        Assert.Equal("Work PC", r["computer"]!.GetValue<string>());
        Assert.False(r["ai"]!.GetValue<bool>());
        Assert.Equal("iPhone", db.PhoneDevice(device.Id)!.Name);
    }

    [Fact]
    public async Task SearchGivesTextOnlyAndLeavesOutBlockedApps()
    {
        AddFrame("Chrome", "invoice 1042 from Acme");
        AddFrame("Signal", "invoice talk in a blocked app");
        list = new Blocklist(new[] { new BlockedApp("signal", "Signal") }, Array.Empty<string>(), false);
        var r = await Run("search", new JsonObject { ["q"] = "invoice", ["kind"] = "screen" });
        var results = r["results"]!.AsArray();
        Assert.Single(results);
        Assert.Equal("Chrome", results[0]!["app"]!.GetValue<string>());
        Assert.Equal("screen", results[0]!["kind"]!.GetValue<string>());
        Assert.DoesNotContain("secret-path", r.ToJsonString());
        Assert.False((await Run("search", new JsonObject { ["q"] = "" }))["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AskNeedsAiAndAnswersWithTheTools()
    {
        Assert.Equal("no-ai", (await Run("ask", new JsonObject { ["question"] = "what?" }))["error"]!.GetValue<string>());
        var client = new OneReply();
        AiUsage? spent = null;
        ops.Ai = () => new AiSettings(AiProviderKind.Anthropic, "test-model", "", "key");
        ops.Client = _ => client;
        ops.Spent = (_, u, _) => spent = u;
        var r = await Run("ask", new JsonObject { ["question"] = "what did I do?" });
        Assert.Equal("You worked on invoices.", r["answer"]!.GetValue<string>());
        Assert.Equal(new AiUsage(10, 5), spent);
        Assert.True((await Run("hello"))["ai"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnknownOpsAreRefused() =>
        Assert.Equal("unknown", (await Run("delete_everything"))["error"]!.GetValue<string>());

    sealed class OneReply : IAiClient
    {
        public Task<AiReply> Send(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools, CancellationToken ct) =>
            Task.FromResult(new AiReply("You worked on invoices.", new(), new AiUsage(10, 5)));
    }
}
