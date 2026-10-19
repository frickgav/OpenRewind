using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRewind;

/// The AI services a user can bring their own key for. Requests go straight from this PC to the
/// provider; OpenRewind has no server in between. Same list as AIProviders.swift on the Mac.
public enum AiProviderKind { Anthropic, OpenAI, Gemini, Custom }

public static class AiProviders
{
    public static readonly AiProviderKind[] All = { AiProviderKind.Anthropic, AiProviderKind.OpenAI, AiProviderKind.Gemini, AiProviderKind.Custom };

    /// Stable name used in settings and the credential store.
    public static string Id(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "anthropic",
        AiProviderKind.OpenAI => "openai",
        AiProviderKind.Gemini => "gemini",
        _ => "custom",
    };

    public static AiProviderKind FromId(string? id) => All.FirstOrDefault(k => k.Id() == id, AiProviderKind.Anthropic);

    public static string Title(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "Anthropic (Claude)",
        AiProviderKind.OpenAI => "OpenAI",
        AiProviderKind.Gemini => "Google Gemini",
        _ => "Custom (OpenAI-compatible)",
    };

    public static string DefaultBaseUrl(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "https://api.anthropic.com",
        AiProviderKind.OpenAI => "https://api.openai.com/v1",
        AiProviderKind.Gemini => "https://generativelanguage.googleapis.com/v1beta",
        _ => "http://localhost:11434/v1",
    };

    /// Starting suggestions only: any model name can be typed, and Refresh asks the provider for its list.
    public static string[] SuggestedModels(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => new[] { "claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5" },
        AiProviderKind.OpenAI => new[] { "gpt-5", "gpt-5-mini" },
        AiProviderKind.Gemini => new[] { "gemini-2.5-flash", "gemini-2.5-pro" },
        _ => Array.Empty<string>(),
    };

    /// Local servers such as Ollama or LM Studio take no key.
    public static bool NeedsKey(this AiProviderKind k) => k != AiProviderKind.Custom;

    public static string? KeyPage(this AiProviderKind k) => k switch
    {
        AiProviderKind.Anthropic => "https://console.anthropic.com/settings/keys",
        AiProviderKind.OpenAI => "https://platform.openai.com/api-keys",
        AiProviderKind.Gemini => "https://aistudio.google.com/apikey",
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
}

public sealed record AiToolSpec(string Name, string Description, JsonObject Parameters);

public sealed record AiReply(string Text, List<AiToolCall> ToolCalls);

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
            default:
                return AiFormat.OpenAIReply(await Post($"{Base}/chat/completions",
                    AiFormat.OpenAIBody(settings.Model, system, messages, tools), Bearer, ct));
        }
    }

    /// The provider's current model names, for the model picker.
    public async Task<List<string>> ListModels(CancellationToken ct = default)
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
                case AiRole.Assistant:
                    var content = new JsonArray();
                    if (m.Text.Length > 0) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
                    foreach (var c in m.ToolCalls)
                        content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = c.Args.DeepClone() });
                    out_.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                case AiRole.Tool:
                    // All results for one assistant turn go back in a single user message.
                    var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = m.ToolCallId, ["content"] = m.Text };
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
        return new AiReply(text.ToString(), calls);
    }

    // MARK: OpenAI and OpenAI-compatible servers

    public static JsonObject OpenAIBody(string model, string system, IReadOnlyList<AiMessage> messages, IReadOnlyList<AiToolSpec> tools)
    {
        var out_ = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages)
        {
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
                    break;
            }
        }
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
        return new AiReply(msg["content"] is JsonValue v && v.TryGetValue(out string? s) ? s : "", calls);
    }

    /// The OpenAI model list also has embeddings, speech and image models; keep the chat ones.
    public static bool IsOpenAIChatModel(string id)
    {
        string[] skip = { "embedding", "whisper", "tts", "dall-e", "image", "audio", "realtime", "moderation", "transcribe", "search", "davinci", "babbage" };
        id = id.ToLowerInvariant();
        return (id.StartsWith("gpt") || id.StartsWith('o') || id.StartsWith("chatgpt")) && !skip.Any(id.Contains);
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
                    if (out_.Count > 0 && out_[^1] is JsonObject last && (string?)last["role"] == "user"
                        && last["parts"] is JsonArray lp && lp.FirstOrDefault()?["functionResponse"] != null)
                        lp.Add(part);
                    else
                        out_.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(part) });
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
        return new AiReply(text.ToString(), calls);
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
