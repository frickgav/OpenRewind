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
    public void ClaudeThinkingIsSentBackUnchanged()
    {
        var reply = AiFormat.AnthropicReply(JsonNode.Parse("""
            {"content":[{"type":"thinking","thinking":"hmm","signature":"s1"},{"type":"tool_use","id":"t1","name":"search_screen","input":{"query":"x"}}],
             "usage":{"input_tokens":100,"cache_read_input_tokens":50,"output_tokens":20}}
            """)!.AsObject());
        Assert.Equal(new AiUsage(150, 20), reply.Usage);
        var msgs = new List<AiMessage>
        {
            new() { Role = AiRole.User, Text = "q" },
            new() { Role = AiRole.Assistant, ToolCalls = reply.ToolCalls, Raw = reply.Raw },
            new() { Role = AiRole.Tool, Text = "r", ToolCallId = "t1", ToolName = "search_screen", Image = new byte[] { 1, 2, 3 } },
        };
        var body = AiFormat.AnthropicBody("claude-x", "sys", msgs, AiTools.Specs);
        var content = body["messages"]![1]!["content"]!;
        Assert.Equal("thinking", (string?)content[0]!["type"]);
        Assert.Equal("s1", (string?)content[0]!["signature"]);
        var result = body["messages"]![2]!["content"]![0]!["content"]!;
        Assert.Equal("image", (string?)result[1]!["type"]);
        Assert.Equal("AQID", (string?)result[1]!["source"]!["data"]);
    }

    [Fact]
    public void OpenAIUsesResponsesAndReplaysItsOutput()
    {
        var reply = AiFormat.ResponsesReply(JsonNode.Parse("""
            {"output":[{"type":"reasoning","id":"rs_1","encrypted_content":"enc","summary":[]},
                       {"type":"function_call","id":"fc_1","call_id":"call_1","name":"search_screen","arguments":"{\"query\":\"x\"}"}],
             "usage":{"input_tokens":10,"output_tokens":5}}
            """)!.AsObject());
        Assert.Equal("x", (string?)reply.ToolCalls[0].Args["query"]);
        Assert.Equal("call_1", reply.ToolCalls[0].Id);
        Assert.Equal(new AiUsage(10, 5), reply.Usage);
        var msgs = new List<AiMessage>
        {
            new() { Role = AiRole.User, Text = "q" },
            new() { Role = AiRole.Assistant, ToolCalls = reply.ToolCalls, Raw = reply.Raw },
            new() { Role = AiRole.Tool, Text = "r", ToolCallId = "call_1", ToolName = "search_screen", Image = new byte[] { 1 } },
        };
        var body = AiFormat.ResponsesBody("gpt-6-luna", "sys", msgs, AiTools.Specs);
        var input = (JsonArray)body["input"]!;
        Assert.Equal(new[] { "user", "reasoning", "function_call", "function_call_output", "user" },
                     input.Select(i => (string?)i!["type"] ?? (string?)i!["role"]));
        Assert.Equal("enc", (string?)input[1]!["encrypted_content"]);
        Assert.Equal("input_image", (string?)input[4]!["content"]![1]!["type"]);
        Assert.False((bool)body["store"]!);
        Assert.False((bool)body["tools"]![0]!["strict"]!);
        Assert.Equal("sys", (string?)body["instructions"]);

        var text = AiFormat.ResponsesReply(JsonNode.Parse("""
            {"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Done"}]}]}
            """)!.AsObject());
        Assert.Equal("Done", text.Text);
    }

    [Fact]
    public void PricesAndOpenRouterModels()
    {
        Assert.Equal(new AiPrice(4, 20), AiPrice.Default("claude-opus-5-5"));
        Assert.Equal(new AiPrice(5, 25), AiPrice.Default("claude-opus-5-20260101"));
        Assert.Equal(new AiPrice(0.10, 0.50), AiPrice.Default("gpt-6-luna"));
        Assert.Null(AiPrice.Default("llama3"));
        Assert.Equal(0.03, new AiUsage(5_000, 1_000).Cost(new AiPrice(2, 20))!.Value, 6);
        Assert.Equal("5,000 tokens in, 1,000 out · $0.03", new AiUsage(5_000, 1_000).Describe(new AiPrice(2, 20)));
        Assert.Equal("10 tokens in, 2 out", new AiUsage(10, 2).Describe(null));

        var list = AiFormat.OpenRouterModels(JsonNode.Parse("""
            {"data":[{"id":"z/model","pricing":{"prompt":"-1","completion":"0"}},{"id":"openai/gpt-6-luna","pricing":{"prompt":"0.0000001","completion":"0.0000005"}}]}
            """)!.AsObject());
        Assert.Equal("openai/gpt-6-luna", list[0].Id);
        Assert.Equal(new AiPrice(0.1, 0.5), list[0].Price);
        Assert.Null(list[1].Price);
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
    public void ScreenshotsOnlyWhenAllowed()
    {
        var t = DateTime.UtcNow.AddMinutes(-20);
        AddFrame(t, "Chrome", "Chart", "");
        var tools = new AiTools(db);
        Assert.DoesNotContain(tools.Offered, s => s.Name == "view_screenshot");
        var call = new AiToolCall("1", "view_screenshot", new JsonObject { ["time"] = AiTime.Label(t.AddMinutes(1)) });
        Assert.Null(tools.Execute(call).Image);

        tools.Screenshots = f => new byte[] { 9 };
        Assert.Contains(tools.Offered, s => s.Name == "view_screenshot");
        var (text, image) = tools.Execute(call);
        Assert.Equal(new byte[] { 9 }, image);
        Assert.Contains("Chrome", text);
        Assert.Contains("\"time\"", AiTools.Arguments(call));
        // Nothing within 10 minutes.
        Assert.Null(tools.Execute(new AiToolCall("2", "view_screenshot", new JsonObject { ["time"] = AiTime.Label(t.AddHours(-2)) })).Image);
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

    [Fact]
    public void BlockedAppsAndSitesStayOutOfLookups()
    {
        // Saved before the user blocked them.
        var now = DateTime.UtcNow;
        AddFrame(now.AddMinutes(-30), "Signal", "Chat with Sam", "secret plan alpha");
        AddFrame(now.AddMinutes(-25), "Chrome", "Chase Online", "secret balance alpha", "https://www.chase.com/acct");
        AddFrame(now.AddMinutes(-20), "Chrome", "Statement", "secret statement alpha", "https://secure.chase.com/s");
        AddFrame(now.AddMinutes(-15), "Code", "notes.md", "secret notes alpha");
        var tools = new AiTools(db) { Blocked = new Blocklist(new[] { new BlockedApp("signal", "Signal") }, new[] { "Chase" }, true) };

        var hits = tools.Run(new AiToolCall("1", "search_screen", new JsonObject { ["query"] = "secret alpha" }));
        Assert.Contains("notes.md", hits);
        Assert.DoesNotContain("plan", hits);
        Assert.DoesNotContain("balance", hits);
        Assert.DoesNotContain("statement", hits);

        var text = tools.Run(new AiToolCall("2", "get_screen_text", new JsonObject
        {
            ["start"] = AiTime.Label(now.AddMinutes(-31)), ["end"] = AiTime.Label(now.AddMinutes(-14)),
        }));
        Assert.Contains("secret notes", text);
        Assert.DoesNotContain("Chat with Sam", text);
        Assert.DoesNotContain("chase.com", text);

        // Time in a blocked app still shows, without its window titles or addresses.
        var summary = tools.Run(new AiToolCall("3", "activity_summary", new JsonObject { ["start"] = "1h ago" }));
        Assert.DoesNotContain("Chat with Sam", summary);
        Assert.DoesNotContain("chase", summary, StringComparison.OrdinalIgnoreCase);

        tools.Screenshots = f => new byte[] { 9 };
        var (shot, image) = tools.Execute(new AiToolCall("4", "view_screenshot", new JsonObject { ["time"] = AiTime.Label(now.AddMinutes(-30)) }));
        Assert.Null(image);
        Assert.Contains("blocked", shot);
    }

    [Fact]
    public void BlockedSitesStayOutOfLookups()
    {
        var now = DateTime.UtcNow;
        AddFrame(now.AddMinutes(-20), "Chrome", "My Doc", "secret draft beta", "https://docs.google.com/document/d/abc/edit");
        AddFrame(now.AddMinutes(-15), "Chrome", "Other Doc", "public draft beta", "https://docs.google.com/document/d/xyz/edit");
        var tools = new AiTools(db) { Blocked = new Blocklist(Array.Empty<BlockedApp>(), Array.Empty<string>(), false, sites: new[] { "docs.google.com/document/d/abc" }) };
        var hits = tools.Run(new AiToolCall("1", "search_screen", new JsonObject { ["query"] = "draft beta" }));
        Assert.Contains("public draft", hits);
        Assert.DoesNotContain("secret draft", hits);
    }

    [Fact]
    public async Task OnlyLinksTheUserOrALookupSuppliedOpen()
    {
        AddFrame(DateTime.UtcNow.AddMinutes(-5), "Chrome", "Docs", "how to deploy", "https://docs.example.com/deploy");
        var client = new FakeClient();
        client.Replies.Enqueue(new AiReply("", new() { new("c1", "search_screen", new JsonObject { ["query"] = "deploy" }) }, new AiUsage(1, 1)));
        client.Replies.Enqueue(new AiReply("See the docs.", new(), new AiUsage(1, 1)));
        var convo = new AiConversation();
        await convo.Ask("what about https://news.example.org/a?", client, new AiTools(db), _ => { });

        Assert.True(convo.MayOpen("https://docs.example.com/deploy"));
        Assert.True(convo.MayOpen("https://news.example.org/a"));
        Assert.True(convo.MayOpen("openrewind://t/100"));
        Assert.False(convo.MayOpen("https://evil.example/?d=secret"));
        Assert.False(convo.MayOpen("file:///etc/passwd"));

        var (text, links) = AiMarkdown.Plain("[2:15 PM](https://evil.example/?d=x) and [docs](https://docs.example.com/deploy)", convo.MayOpen);
        Assert.Equal("2:15 PM (https://evil.example/?d=x) and docs", text);
        Assert.Equal("https://docs.example.com/deploy", Assert.Single(links).Url);

        convo.Clear();
        Assert.False(convo.MayOpen("https://docs.example.com/deploy"));
    }

    [Theory]
    [InlineData("http://203.0.113.5:11434/v1", true)]
    [InlineData("http://ollama.example.com/v1", true)]
    [InlineData("https://ollama.example.com/v1", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://192.168.1.20:11434/v1", false)]
    [InlineData("http://10.0.0.4/v1", false)]
    [InlineData("http://172.20.0.2/v1", false)]
    [InlineData("http://100.101.5.6/v1", false)]
    [InlineData("http://mac-studio.local:11434/v1", false)]
    [InlineData("http://gpu-box:11434/v1", false)]
    [InlineData("http://[::1]:11434/v1", false)]
    [InlineData("not a url", false)]
    public void WarnsAboutPlainHttpToOtherComputers(string address, bool risky) =>
        Assert.Equal(risky, AiFormat.IsPlainHttpRemote(address));

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
        client.Replies.Enqueue(new AiReply("", new() { new("c1", "search_screen", new JsonObject { ["query"] = "hello" }) }, new AiUsage(1000, 100)));
        client.Replies.Enqueue(new AiReply("You wrote hello world.", new(), new AiUsage(2000, 200)));
        var convo = new AiConversation();
        var items = new List<AiChatItem>();
        await convo.Ask("what did I write?", client, new AiTools(db), items.Add, new AiPrice(1, 10));
        Assert.Equal(new[] { AiChatKind.User, AiChatKind.Tool, AiChatKind.Assistant, AiChatKind.Usage }, items.Select(i => i.Kind));
        Assert.Contains("hello world", items[1].Detail);
        Assert.Contains("\"hello\"", items[1].Args);
        Assert.Equal(new[] { 1, 3 }, client.SeenHistory);
        Assert.Equal(new AiUsage(3000, 300), convo.Used);
        Assert.Equal(0.006, convo.Cost, 6);

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
