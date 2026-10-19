using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRewind;

/// The AI services a user can bring their own key for. Requests go straight from this PC to the
/// provider; OpenRewind has no server in between. Same list as AIProviders.swift on the Mac.
public enum AiProviderKind { Anthropic, OpenAI, Gemini, OpenRouter, Custom }

public static class AiProviders
{
    public static readonly AiProviderKind[] All = { AiProviderKind.Anthropic, AiProviderKind.OpenAI, AiProviderKind.Gemini, AiProviderKind.OpenRouter, AiProviderKind.Custom };

    /// Stable name used in settings and the credential store.
    public static string Id(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "anthropic",
        AiProviderKind.OpenAI => "openai",
        AiProviderKind.Gemini => "gemini",
        AiProviderKind.OpenRouter => "openrouter",
        _ => "custom",
    };

    public static AiProviderKind FromId(string? id) => All.FirstOrDefault(k => k.Id() == id, AiProviderKind.Anthropic);

    public static string Title(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "Anthropic (Claude)",
        AiProviderKind.OpenAI => "OpenAI",
        AiProviderKind.Gemini => "Google Gemini",
        AiProviderKind.OpenRouter => "OpenRouter (any model)",
        _ => "Custom (OpenAI-compatible)",
    };

    public static string DefaultBaseUrl(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "https://api.anthropic.com",
        AiProviderKind.OpenAI => "https://api.openai.com/v1",
        AiProviderKind.Gemini => "https://generativelanguage.googleapis.com/v1beta",
        AiProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        _ => "http://localhost:11434/v1",
    };

    /// Starting suggestions only: any model name can be typed, and Refresh asks the provider for its list.
    public static string[] SuggestedModels(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => new[] { "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-1" },
        AiProviderKind.OpenAI => new[] { "gpt-6.1-sol", "gpt-6-luna", "gpt-6-astra" },
        AiProviderKind.Gemini => new[] { "gemini-3.8-flash", "gemini-4-argon", "gemini-3.5-flash-lite" },
        _ => Array.Empty<string>(),
    };

    /// Local servers such as Ollama or LM Studio take no key.
    public static bool NeedsKey(this AiProviderKind k) => k != AiProviderKind.Custom;

    public static string? KeyPage(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "https://console.anthropic.com/settings/keys",
        AiProviderKind.OpenAI => "https://platform.openai.com/api-keys",
        AiProviderKind.Gemini => "https://aistudio.google.com/apikey",
        AiProviderKind.OpenRouter => "https://openrouter.ai/keys",
        _ => null,
    };
}

/// What the user picked. The key comes from the Windows Credential Manager, never settings.json.
public sealed record AiSettings(AiProviderKind Kind, string Model, string BaseUrl, string? Key)
{
    public bool IsReady => Model.Length > 0 && (!Kind.NeedsKey() || !string.IsNullOrEmpty(Key));
}

public sealed record AiToolCall(string Id, string Name, JsonObject Args, string? Signature = null);

public enum AiRole { User, Assistant, Tool }

/// One turn, in a shape that converts to each provider's format.
public sealed class AiMessage
{
    public AiRole Role;
    public string Text = "";
    public List<AiToolCall> ToolCalls = new();
    /// For Tool: which call this answers.
    public string ToolCallId = "";
    public string ToolName = "";
    /// For Tool: a JPEG screenshot that goes with the result (view_screenshot).
    public byte[]? Image;
    /// For Assistant from Anthropic: the content blocks exactly as they came. Claude's thinking blocks
    /// must be sent back unchanged during tool use, so they are replayed from here.
    public JsonArray? Raw;
}

public sealed record AiToolSpec(string Name, string Description, JsonObject Parameters);

/// Tokens one or more requests used. Input counts everything the provider read, cached or not.
public readonly record struct AiUsage(long Input, long Output)
{
    public static AiUsage operator +(AiUsage a, AiUsage b) => new(a.Input + b.Input, a.Output + b.Output);
    public bool IsZero => Input == 0 && Output == 0;

    public double? Cost(AiPrice? p) => p is { } x ? (Input * x.Input + Output * x.Output) / 1_000_000 : null;

    /// "12,345 tokens in, 678 out · $0.06"
    public string Describe(AiPrice? p) =>
        $"{Input:N0} tokens in, {Output:N0} out" + (Cost(p) is double c ? $" · {AiPrice.Money(c)}" : "");
}

/// US dollars per million tokens.
public readonly record struct AiPrice(double Input, double Output)
{
    /// List prices from the providers' pricing pages (October 2026). OpenRouter sends its prices with
    /// the model list; for anything else the user types the price in AI Settings.
    static readonly (string Prefix, AiPrice Price)[] Known =
    {
        ("gpt-6-astra", new(10, 50)), ("gpt-6.1-sol", new(2, 10)), ("gpt-6-luna", new(0.10, 0.50)),
        ("claude-opus-5-5", new(4, 20)), ("claude-sonnet-5-5", new(2, 10)), ("claude-haiku-5-5", new(0.10, 0.50)),
        ("claude-opus-5", new(5, 25)), ("claude-sonnet-5", new(2, 10)), ("claude-fable-5", new(10, 50)),
        ("claude-opus-4", new(5, 25)), ("claude-sonnet-4", new(3, 15)), ("claude-haiku-4-5", new(1, 5)),
    };

    public static AiPrice? Default(string model)
    {
        foreach (var (prefix, price) in Known.OrderByDescending(k => k.Prefix.Length))
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return price;
        return null;
    }

    public static string Money(double d) => d < 0.01 && d > 0 ? $"${d:0.0000}" : $"${d:N2}";
}

public sealed record AiReply(string Text, List<AiToolCall> ToolCalls, AiUsage Usage = default, JsonArray? Raw = null);

public sealed class AiException : Exception
{
    public AiException(string message) : base(message) { }
}

public interface IAiClient
{
    Task<AiReply> Send(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools, CancellationToken ct = default);
}

/// One request/response client for all providers (no streaming yet).
public sealed class AiClient : IAiClient
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(180) };
    readonly AiSettings settings;

    public AiClient(AiSettings settings) { this.settings = settings; }

    string Base
    {
        get
        {
            var b = settings.BaseUrl.Trim().TrimEnd('/');
            return b.Length == 0 ? settings.Kind.DefaultBaseUrl() : b;
        }
    }

    string Key => settings.Key ?? "";

    /// Gemini lists models as "models/gemini-…"; the request path adds "models/" itself.
    string GeminiModel => settings.Model.StartsWith("models/") ? settings.Model[7..] : settings.Model;

    public async Task<AiReply> Send(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools, CancellationToken ct = default)
    {
        switch (settings.Kind)
        {
            case AiProviderKind.Anthropic:
                return AiFormat.AnthropicReply(await Post($"{Base}/v1/messages",
                    AiFormat.AnthropicBody(settings.Model, system, messages, tools), AnthropicHeaders, ct));
            case AiProviderKind.Gemini:
                return AiFormat.GeminiReply(await Post($"{Base}/models/{GeminiModel}:generateContent",
                    AiFormat.GeminiBody(system, messages, tools), GoogleHeaders, ct));
            case AiProviderKind.OpenAI:
                // Newer OpenAI models only take tools on the Responses endpoint.
                return AiFormat.ResponsesReply(await Post($"{Base}/responses",
                    AiFormat.ResponsesBody(settings.Model, system, messages, tools), Bearer, ct));
            default:
                return AiFormat.OpenAIReply(await Post($"{Base}/chat/completions",
                    AiFormat.OpenAIBody(settings.Model, system, messages, tools), Bearer, ct));
        }
    }

    /// The provider's current model names, for the model picker.
    public async Task<List<string>> ListModels(CancellationToken ct = default) =>
        (await ListModelsWithPrices(ct)).Select(m => m.Id).ToList();

    /// Model names, with prices where the provider lists them (OpenRouter does).
    public async Task<List<(string Id, AiPrice? Price)>> ListModelsWithPrices(CancellationToken ct = default)
    {
        if (settings.Kind == AiProviderKind.OpenRouter)
            return AiFormat.OpenRouterModels(await Get($"{Base}/models", Bearer, ct));
        return (await ListModelNames(ct)).Select(id => (id, (AiPrice?)null)).ToList();
    }

    async Task<List<string>> ListModelNames(CancellationToken ct)
    {
        switch (settings.Kind)
        {
            case AiProviderKind.Anthropic:
            {
                var json = await Get($"{Base}/v1/models?limit=100", AnthropicHeaders, ct);
                return (json["data"] as JsonArray ?? new()).Select(m => (string?)m?["id"]).OfType<string>().ToList();
            }
            case AiProviderKind.Gemini:
            {
                var json = await Get($"{Base}/models?pageSize=1000", GoogleHeaders, ct);
                return (json["models"] as JsonArray ?? new())
                    .Where(m => (m?["supportedGenerationMethods"] as JsonArray)?.Any(x => (string?)x == "generateContent") == true)
                    .Select(m => (string?)m?["name"]).OfType<string>()
                    .Select(n => n.StartsWith("models/") ? n[7..] : n).ToList();
            }
            default:
            {
                var json = await Get($"{Base}/models", Bearer, ct);
                var ids = (json["data"] as JsonArray ?? new()).Select(m => (string?)m?["id"]).OfType<string>();
                if (settings.Kind == AiProviderKind.OpenAI) ids = ids.Where(AiFormat.IsOpenAIChatModel);
                return ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
            }
        }
    }

    void AnthropicHeaders(HttpRequestMessage r) { r.Headers.Add("x-api-key", Key); r.Headers.Add("anthropic-version", "2023-06-01"); }
    void GoogleHeaders(HttpRequestMessage r) => r.Headers.Add("x-goog-api-key", Key);
    void Bearer(HttpRequestMessage r) { if (Key.Length > 0) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key); }

    Task<JsonObject> Post(string url, JsonObject body, Action<HttpRequestMessage> headers, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Uri(url)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        headers(req);
        return Run(req, ct);
    }

    Task<JsonObject> Get(string url, Action<HttpRequestMessage> headers, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Uri(url));
        headers(req);
        return Run(req, ct);
    }

    static Uri Uri(string url) => System.Uri.TryCreate(url, UriKind.Absolute, out var u) ? u : throw new AiException($"Bad server address: {url}");

    static async Task<JsonObject> Run(HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        {
            HttpResponseMessage resp;
            try { resp = await http.SendAsync(req, ct); }
            catch (HttpRequestException e) { throw new AiException($"Could not reach {req.RequestUri?.Host}: {e.Message}"); }
            using (resp)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct);
                JsonObject? json = null;
                try { json = JsonNode.Parse(raw) as JsonObject; } catch (JsonException) { }
                if (!resp.IsSuccessStatusCode) throw new AiException(AiFormat.ErrorMessage((int)resp.StatusCode, json, raw));
                return json ?? throw new AiException("The provider sent an answer OpenRewind could not read.");
            }
        }
    }
}

/// Request bodies and response parsing for each provider. Free of networking so it can be tested,
/// and it matches AIFormat in the Mac app.
public static class AiFormat
{
    public const int MaxTokens = 4096;

    // MARK: Anthropic

    public static JsonObject AnthropicBody(string model, string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools)
    {
        var out_ = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case AiRole.User:
                    out_.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case AiRole.Assistant when m.Raw != null:
                    out_.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Raw.DeepClone() });
                    break;
                case AiRole.Assistant:
                    var content = new JsonArray();
                    if (m.Text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = c.Args.DeepClone() });
                    out_.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                case AiRole.Tool:
                    // All results for one assistant turn go back in a single user message.
                    JsonNode result = m.Image == null ? m.Text : new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = m.Text },
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(m.Image) },
                        });
                    var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = result };
                    if (out_.Count > 0 && out_[^1] is JsonObject last && (string?)last["role"] == "user" && last["content"] is JsonArray blocks)
                        blocks.Add(block);
                    else
                        out_.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
                    break;
            }
        }
        var body = new JsonObject { ["model"] = model, ["max_tokens"] = MaxTokens, ["system"] = system, ["messages"] = out_ };
        if (tools.Count > 0)
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters.DeepClone(),
            }).ToArray());
        return body;
    }

    public static AiReply AnthropicReply(JsonObject json)
    {
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var b in json["content"] as JsonArray ?? new())
        {
            switch ((string?)b?["type"])
            {
                case "text": text.Append((string?)b!["text"]); break;
                case "tool_use":
                    calls.Add(new AiToolCall((string?)b!["id"] ?? Guid.NewGuid().ToString(), (string?)b["name"] ?? "",
                                             b["input"]?.DeepClone() as JsonObject ?? new()));
                    break;
            }
        }
        var u = json["usage"];
        var usage = new AiUsage(L(u?["input_tokens"]) + L(u?["cache_creation_input_tokens"]) + L(u?["cache_read_input_tokens"]), L(u?["output_tokens"]));
        return new AiReply(text.ToString(), calls, usage, json["content"]?.DeepClone() as JsonArray);
    }

    static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long x) ? x : n is JsonValue d && d.TryGetValue(out double y) ? (long)y : 0;

    // MARK: OpenAI and OpenAI-compatible servers

    public static JsonObject OpenAIBody(string model, string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools)
    {
        var out_ = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        // Tool messages can only hold text, so screenshots follow the tool answers as a user message.
        var images = new List<byte[]>();
        void FlushImages()
        {
            if (images.Count == 0) return;
            var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "The screenshot(s) view_screenshot returned, in order:" });
            foreach (var img in images)
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(img) } });
            out_.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
            images.Clear();
        }
        foreach (var m in messages)
        {
            if (m.Role != AiRole.Tool) FlushImages();
            switch (m.Role)
            {
                case AiRole.User:
                    out_.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case AiRole.Assistant:
                    var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
                    if (m.ToolCalls.Count > 0)
                        msg["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c.Id, ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToJsonString() },
                        }).ToArray());
                    out_.Add(msg);
                    break;
                case AiRole.Tool:
                    out_.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Text });
                    if (m.Image != null) images.Add(m.Image);
                    break;
            }
        }
        FlushImages();
        var body = new JsonObject { ["model"] = model, ["messages"] = out_ };
        // OpenAI refuses an empty tools list.
        if (tools.Count > 0)
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
            }).ToArray());
        return body;
    }

    public static AiReply OpenAIReply(JsonObject json)
    {
        var msg = (json["choices"] as JsonArray)?.FirstOrDefault()?["message"] as JsonObject
                  ?? throw new AiException("The provider sent no answer.");
        var calls = new List<AiToolCall>();
        foreach (var c in msg["tool_calls"] as JsonArray ?? new())
        {
            var f = c?["function"];
            JsonObject args;
            try { args = JsonNode.Parse((string?)f?["arguments"] ?? "{}") as JsonObject ?? new(); }
            catch (JsonException) { args = new(); }
            calls.Add(new AiToolCall((string?)c?["id"] ?? Guid.NewGuid().ToString(), (string?)f?["name"] ?? "", args));
        }
        var u = json["usage"];
        return new AiReply(msg["content"] is JsonValue v && v.TryGetValue(out string? s) ? s : "", calls,
                           new AiUsage(L(u?["prompt_tokens"]), L(u?["completion_tokens"])));
    }

    /// The OpenAI model list also has embeddings, speech and image models; keep the chat ones.
    public static bool IsOpenAIChatModel(string id)
    {
        string[] skip = { "embedding", "whisper", "tts", "dall-e", "image", "audio", "realtime", "moderation", "transcribe", "search", "davinci", "babbage" };
        id = id.ToLowerInvariant();
        return (id.StartsWith("gpt") || id.StartsWith('o') || id.StartsWith("chatgpt")) && !skip.Any(id.Contains);
    }

    /// OpenRouter's list: {"data":[{"id":"openai/gpt-6-luna","pricing":{"prompt":"0.0000001","completion":"0.0000005"}}]}
    /// with prices in dollars per token.
    public static List<(string Id, AiPrice? Price)> OpenRouterModels(JsonObject json)
    {
        static double? PerMillion(JsonNode? n) =>
            double.TryParse(n?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d >= 0
                ? Math.Round(d * 1_000_000, 6) : null;
        return (json["data"] as JsonArray ?? new())
            .Select(m => ((string?)m?["id"], PerMillion(m?["pricing"]?["prompt"]), PerMillion(m?["pricing"]?["completion"])))
            .Where(m => m.Item1 != null)
            .Select(m => (m.Item1!, m.Item2 is double i && m.Item3 is double o ? new AiPrice(i, o) : (AiPrice?)null))
            .OrderBy(m => m.Item1, StringComparer.Ordinal).ToList();
    }

    // MARK: OpenAI Responses

    /// OpenAI's /v1/responses. `store: false` keeps the conversation off OpenAI's servers, so the
    /// whole history goes in every request; the model's own output items (reasoning included) are
    /// replayed exactly as they came.
    public static JsonObject ResponsesBody(string model, string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools)
    {
        var input = new JsonArray();
        var images = new List<byte[]>();
        void FlushImages()
        {
            if (images.Count == 0) return;
            var parts = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "The screenshot(s) view_screenshot returned, in order:" });
            foreach (var img in images)
                parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(img) });
            input.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
            images.Clear();
        }
        foreach (var m in messages)
        {
            if (m.Role != AiRole.Tool) FlushImages();
            switch (m.Role)
            {
                case AiRole.User:
                    input.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
                    break;
                case AiRole.Assistant when m.Raw != null:
                    foreach (var item in m.Raw) input.Add(item?.DeepClone());
                    break;
                case AiRole.Assistant:
                    if (m.Text.Length > 0) input.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Text });
                    foreach (var c in m.ToolCalls)
                        input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = c.Id, ["name"] = c.Name, ["arguments"] = c.Args.ToJsonString() });
                    break;
                case AiRole.Tool:
                    input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId, ["output"] = m.Text });
                    if (m.Image != null) images.Add(m.Image);
                    break;
            }
        }
        FlushImages();
        var body = new JsonObject { ["model"] = model, ["instructions"] = system, ["input"] = input, ["store"] = false };
        if (tools.Count > 0)
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                // Strict mode wants every argument required; ours are mostly optional.
                ["type"] = "function", ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(), ["strict"] = false,
            }).ToArray());
        return body;
    }

    public static AiReply ResponsesReply(JsonObject json)
    {
        var output = json["output"] as JsonArray ?? throw new AiException("The provider sent no answer.");
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var item in output)
        {
            switch ((string?)item?["type"])
            {
                case "message":
                    foreach (var part in item!["content"] as JsonArray ?? new())
                        if ((string?)part?["type"] == "output_text") text.Append((string?)part!["text"]);
                    break;
                case "function_call":
                    JsonObject args;
                    try { args = JsonNode.Parse((string?)item!["arguments"] ?? "{}") as JsonObject ?? new(); }
                    catch (JsonException) { args = new(); }
                    calls.Add(new AiToolCall((string?)item!["call_id"] ?? Guid.NewGuid().ToString(), (string?)item["name"] ?? "", args));
                    break;
            }
        }
        var u = json["usage"];
        return new AiReply(text.ToString(), calls, new AiUsage(L(u?["input_tokens"]), L(u?["output_tokens"])), (JsonArray)output.DeepClone());
    }

    // MARK: Gemini

    public static JsonObject GeminiBody(string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools)
    {
        var out_ = new JsonArray();
        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case AiRole.User:
                    out_.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = m.Text }) });
                    break;
                case AiRole.Assistant:
                    var parts = new JsonArray();
                    if (m.Text.Length > 0) parts.Add(new JsonObject { ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                    {
                        var p = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = c.Args.DeepClone() } };
                        if (c.Signature != null) p["thoughtSignature"] = c.Signature;
                        parts.Add(p);
                    }
                    out_.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                case AiRole.Tool:
                    var part = new JsonObject
                    {
                        ["functionResponse"] = new JsonObject { ["name"] = m.ToolName, ["response"] = new JsonObject { ["result"] = m.Text } },
                    };
                    JsonArray target;
                    if (out_.Count > 0 && out_[^1] is JsonObject last && (string?)last["role"] == "user"
                        && last["parts"] is JsonArray lp && lp.FirstOrDefault()?["functionResponse"] != null)
                        target = lp;
                    else
                        out_.Add(new JsonObject { ["role"] = "user", ["parts"] = target = new JsonArray() });
                    target.Add(part);
                    if (m.Image != null)
                        target.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(m.Image) } });
                    break;
            }
        }
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = out_,
        };
        if (tools.Count > 0)
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone(),
                }).ToArray()),
            });
        return body;
    }

    public static AiReply GeminiReply(JsonObject json)
    {
        var cand = (json["candidates"] as JsonArray)?.FirstOrDefault()
                   ?? throw new AiException($"Gemini sent no answer ({(string?)json["promptFeedback"]?["blockReason"] ?? "no answer"}).");
        var text = new StringBuilder();
        var calls = new List<AiToolCall>();
        foreach (var p in cand["content"]?["parts"] as JsonArray ?? new())
        {
            if (p == null || (p["thought"] is JsonValue t && t.TryGetValue(out bool isThought) && isThought)) continue;
            if (p["text"] is JsonValue tv && tv.TryGetValue(out string? s)) text.Append(s);
            if (p["functionCall"] is JsonObject f)
                calls.Add(new AiToolCall((string?)f["id"] ?? $"call_{calls.Count + 1}", (string?)f["name"] ?? "",
                                         f["args"]?.DeepClone() as JsonObject ?? new(), (string?)p["thoughtSignature"]));
        }
        var u = json["usageMetadata"];
        return new AiReply(text.ToString(), calls,
                           new AiUsage(L(u?["promptTokenCount"]), L(u?["candidatesTokenCount"]) + L(u?["thoughtsTokenCount"])));
    }

    // MARK: errors

    public static string ErrorMessage(int status, JsonObject? json, string raw)
    {
        var err = json?["error"];
        var detail = (err as JsonObject)?["message"]?.ToString() ?? (err is JsonValue ? err.ToString() : null)
                     ?? (raw.Length > 300 ? raw[..300] : raw);
        return status switch
        {
            401 or 403 => $"The provider did not accept the API key ({status}). {detail}",
            404 => $"Model or address not found (404). Check the model name. {detail}",
            429 => $"The provider says too many requests or no credit left (429). {detail}",
            _ => $"The provider returned an error ({status}). {detail}",
        };
    }
}
