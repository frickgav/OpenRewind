using System.Text.Json.Nodes;
using OpenRewind;

public class AiTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-ai-test-" + Guid.NewGuid());
    readonly Database db;

    public AiTests()
    {
        var config = Config.Load(new[] { "--home", home });
        db = new Database(config.DbPath);
    }

    public void Dispose()
    {
        db.Dispose();
        try { Directory.Delete(home, true); } catch { }
    }

    void AddFrame(DateTime t, string app, string window, string text, string url = "") =>
        db.Insert(new Frame(0, Time.Iso(t), app, window, true, "", text, "ocr", 0, url));

    static List<AiMessage> ToolRound() => new()
    {
        new() { Role = AiRole.User, Text = "what did I do today?" },
        new() { Role = AiRole.Assistant, Text = "Let me look.", ToolCalls = new()
        {
            new("c1", "activity_summary", new JsonObject { ["start"] = "today" }, "sig"),
            new("c2", "search_screen", new JsonObject { ["query"] = "invoice" }),
        } },
        new() { Role = AiRole.Tool, Text = "summary", ToolCallId = "c1", ToolName = "activity_summary" },
        new() { Role = AiRole.Tool, Text = "hits", ToolCallId = "c2", ToolName = "search_screen" },
    };

    [Fact]
    public void AnthropicPutsAllToolResultsInOneUserMessage()
    {
        var body = AiFormat.AnthropicBody("claude-x", "sys", ToolRound(), AiTools.Specs);
        var msgs = (JsonArray)body["messages"]!;
        Assert.Equal(3, msgs.Count);
        Assert.Equal("tool_use", (string?)msgs[1]!["content"]![1]!["type"]);
        Assert.Equal("today", (string?)msgs[1]!["content"]![1]!["input"]!["start"]);
        var results = (JsonArray)msgs[2]!["content"]!;
        Assert.Equal(2, results.Count);
        Assert.Equal("c2", (string?)results[1]!["tool_use_id"]);
        Assert.Equal(4, ((JsonArray)body["tools"]!).Count);
        Assert.Equal("sys", (string?)body["system"]);
    }

    [Fact]
    public void OpenAISendsArgumentsAsTextAndOneMessagePerResult()
    {
        var body = AiFormat.OpenAIBody("gpt-x", "sys", ToolRound(), AiTools.Specs);
        var msgs = (JsonArray)body["messages"]!;
        Assert.Equal(5, msgs.Count);
        Assert.Equal("system", (string?)msgs[0]!["role"]);
        var args = (string?)msgs[2]!["tool_calls"]![0]!["function"]!["arguments"];
        Assert.Equal("today", (string?)JsonNode.Parse(args!)!["start"]);
        Assert.Equal("c1", (string?)msgs[3]!["tool_call_id"]);
        // No tools, no "tools" key: OpenAI refuses an empty list.
        Assert.Null(AiFormat.OpenAIBody("gpt-x", "sys", ToolRound(), Array.Empty<AiToolSpec>())["tools"]);
    }

    [Fact]
    public void GeminiEchoesTheThoughtSignature()
    {
        var body = AiFormat.GeminiBody("sys", ToolRound(), AiTools.Specs);
        var contents = (JsonArray)body["contents"]!;
        Assert.Equal(3, contents.Count);
        Assert.Equal("model", (string?)contents[1]!["role"]);
        Assert.Equal("sig", (string?)contents[1]!["parts"]![1]!["thoughtSignature"]);
        Assert.Equal(2, ((JsonArray)contents[2]!["parts"]!).Count);
        Assert.Equal("search_screen", (string?)contents[2]!["parts"]![1]!["functionResponse"]!["name"]);
    }

    [Fact]
    public void RepliesAreReadFromEachProvider()
    {
        var a = AiFormat.AnthropicReply(JsonNode.Parse("""
            {"content":[{"type":"text","text":"Hi"},{"type":"tool_use","id":"t1","name":"search_screen","input":{"query":"x"}}]}
            """)!.AsObject());
        Assert.Equal("Hi", a.Text);
        Assert.Equal("x", (string?)a.ToolCalls[0].Args["query"]);

        var o = AiFormat.OpenAIReply(JsonNode.Parse("""
            {"choices":[{"message":{"content":null,"tool_calls":[{"id":"t2","type":"function","function":{"name":"search_audio","arguments":"{\"query\":\"y\"}"}}]}}]}
            """)!.AsObject());
        Assert.Equal("", o.Text);
        Assert.Equal("search_audio", o.ToolCalls[0].Name);
        Assert.Equal("y", (string?)o.ToolCalls[0].Args["query"]);

        var g = AiFormat.GeminiReply(JsonNode.Parse("""
            {"candidates":[{"content":{"parts":[{"text":"thinking","thought":true},{"text":"Done"},{"functionCall":{"name":"activity_summary","args":{}},"thoughtSignature":"abc"}]}}]}
            """)!.AsObject());
        Assert.Equal("Done", g.Text);
        Assert.Equal("abc", g.ToolCalls[0].Signature);
    }

    [Fact]
    public void ErrorsSayWhatToFix()
    {
        var json = JsonNode.Parse("""{"error":{"message":"invalid x-api-key"}}""")!.AsObject();
        Assert.Contains("did not accept the API key", AiFormat.ErrorMessage(401, json, ""));
        Assert.Contains("invalid x-api-key", AiFormat.ErrorMessage(401, json, ""));
        Assert.True(AiFormat.IsOpenAIChatModel("gpt-5-mini"));
        Assert.False(AiFormat.IsOpenAIChatModel("text-embedding-3-large"));
        Assert.False(AiFormat.IsOpenAIChatModel("gpt-4o-mini-tts"));
    }

    [Fact]
    public void ActivitySummaryCountsTimeAndCapsGaps()
    {
        var t0 = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
        var rows = new List<ActivityRow>();
        // 10 minutes in the editor, a 3-hour gap (counts 2 minutes at most), then 5 minutes on GitHub.
        for (int i = 0; i < 10; i++) rows.Add(new(t0.AddMinutes(i), "Code", "Database.cs", ""));
        var t1 = t0.AddHours(3);
        for (int i = 0; i < 5; i++) rows.Add(new(t1.AddMinutes(i), "Chrome", "Pull request", "https://www.github.com/x"));
        var s = AiActivity.Summary(rows, t0, t1.AddMinutes(5));
        Assert.Contains("- Code: 11m", s);   // 9 one-minute steps + the 2-minute cap on the gap
        Assert.Contains("“Database.cs”", s);
        Assert.Contains("- github.com: 4m", s);
        Assert.Contains($"openrewind://t/{new DateTimeOffset(t0).ToUnixTimeSeconds()}", s);
        Assert.Equal("Nothing was recorded between", AiActivity.Summary(new List<ActivityRow>(), t0, t1)[..28]);
    }

    [Fact]
    public void ToolsReadTheDatabase()
    {
        var now = DateTime.UtcNow;
        AddFrame(now.AddMinutes(-30), "Chrome", "Invoices", "Invoice 1042 from Acme for $3,200 due Friday", "https://mail.example.com/1");
        AddFrame(now.AddMinutes(-29), "Chrome", "Invoices", "Invoice 1042 from Acme for $3,200 due Friday");
        AddFrame(now.AddMinutes(-10), "Code", "AiTools.cs", "public sealed class AiTools");
        var tools = new AiTools(db);

        var hits = tools.Run(new AiToolCall("1", "search_screen", new JsonObject { ["query"] = "acme invoice" }));
        Assert.Contains("Invoice 1042", hits);
        Assert.Contains("near-duplicates skipped", hits);
        Assert.Single(hits.Split('\n').Where(l => l.StartsWith("- ")));

        var summary = tools.Run(new AiToolCall("2", "activity_summary", new JsonObject { ["start"] = "1h ago" }));
        Assert.Contains("Chrome", summary);

        var text = tools.Run(new AiToolCall("3", "get_screen_text", new JsonObject
        {
            ["start"] = AiTime.Label(now.AddMinutes(-31)), ["end"] = AiTime.Label(now.AddMinutes(-9)),
        }));
        Assert.Contains("2 different screens", text);

        Assert.StartsWith("Error", tools.Run(new AiToolCall("4", "get_screen_text", new JsonObject())));
    }

    [Fact]
    public void TimeArgumentsAreLocal()
    {
        var local = new DateTime(2026, 10, 18, 14, 30, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), AiTime.Parse(JsonValue.Create("2026-10-18 14:30")));
        Assert.Equal(local.ToUniversalTime(), AiTime.Parse(JsonValue.Create("2026-10-18T14:30:00")));
        Assert.NotNull(AiTime.Parse(JsonValue.Create("yesterday")));
        Assert.Null(AiTime.Parse(null));
        var link = AiTime.Link(local.ToUniversalTime());
        Assert.Equal(local.ToUniversalTime(), AiTime.FromLink(link));
    }

    sealed class FakeClient : IAiClient
    {
        public readonly Queue<AiReply> Replies = new();
        public readonly List<int> SeenHistory = new();
        public Task<AiReply> Send(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools, CancellationToken ct)
        {
            SeenHistory.Add(messages.Count);
            return Replies.Count > 0 ? Task.FromResult(Replies.Dequeue()) : throw new AiException("boom");
        }
    }

    [Fact]
    public async Task ConversationRunsToolsUntilTheModelAnswers()
    {
        AddFrame(DateTime.UtcNow.AddMinutes(-5), "Code", "x", "hello world");
        var client = new FakeClient();
        client.Replies.Enqueue(new AiReply("", new() { new("c1", "search_screen", new JsonObject { ["query"] = "hello" }) }));
        client.Replies.Enqueue(new AiReply("You wrote hello world.", new()));
        var convo = new AiConversation();
        var items = new List<AiChatItem>();
        await convo.Ask("what did I write?", client, new AiTools(db), items.Add);
        Assert.Equal(new[] { AiChatKind.User, AiChatKind.Tool, AiChatKind.Assistant }, items.Select(i => i.Kind));
        Assert.Contains("hello world", items[1].Detail);
        Assert.Equal(new[] { 1, 3 }, client.SeenHistory);

        // A failed request leaves no half question behind.
        await convo.Ask("again?", client, new AiTools(db), items.Add);
        Assert.Equal(AiChatKind.Error, items[^1].Kind);
        Assert.Equal(4, convo.History.Count);
    }
}

public class AiMarkdownTests
{
    [Fact]
    public void LinksBecomeRangesAndMarkupIsDropped()
    {
        var (text, links) = AiMarkdown.Plain("## Today\n- **Coding** at [9:02 AM](openrewind://t/100) and `x`");
        Assert.Equal("Today\n• Coding at 9:02 AM and x", text);
        var l = Assert.Single(links);
        Assert.Equal("9:02 AM", text.Substring(l.Start, l.Length));
        Assert.Equal("openrewind://t/100", l.Url);
    }
}
