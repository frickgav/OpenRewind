import Foundation
import Security

/// The AI services a user can bring their own key for. Requests go straight from this Mac to the
/// provider; OpenRewind has no server in between. The Windows app has the same list in AiProviders.cs.
enum AIProviderKind: String, CaseIterable, Identifiable {
    case anthropic, openai, gemini, custom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .anthropic: return "Anthropic (Claude)"
        case .openai: return "OpenAI"
        case .gemini: return "Google Gemini"
        case .custom: return "Custom (OpenAI-compatible)"
        }
    }

    var defaultBaseURL: String {
        switch self {
        case .anthropic: return "https://api.anthropic.com"
        case .openai: return "https://api.openai.com/v1"
        case .gemini: return "https://generativelanguage.googleapis.com/v1beta"
        case .custom: return "http://localhost:11434/v1"
        }
    }

    /// Starting suggestions only: any model name can be typed, and "Refresh" asks the provider for its list.
    var suggestedModels: [String] {
        switch self {
        case .anthropic: return ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5"]
        case .openai: return ["gpt-5", "gpt-5-mini"]
        case .gemini: return ["gemini-2.5-flash", "gemini-2.5-pro"]
        case .custom: return []
        }
    }

    /// Local servers such as Ollama or LM Studio take no key.
    var needsKey: Bool { self != .custom }

    var keyPage: URL? {
        switch self {
        case .anthropic: return URL(string: "https://console.anthropic.com/settings/keys")
        case .openai: return URL(string: "https://platform.openai.com/api-keys")
        case .gemini: return URL(string: "https://aistudio.google.com/apikey")
        case .custom: return nil
        }
    }
}

/// What the user picked in AI settings. The key itself lives in the Keychain (see `KeyStore`).
struct AISettings {
    var kind: AIProviderKind
    var model: String
    var baseURL: String
    var key: String?

    private static let d = UserDefaults.standard

    static func load() -> AISettings {
        let kind = AIProviderKind(rawValue: d.string(forKey: "aiProvider") ?? "") ?? .anthropic
        return load(kind)
    }

    static func load(_ kind: AIProviderKind) -> AISettings {
        AISettings(kind: kind,
                   model: d.string(forKey: "aiModel.\(kind.rawValue)") ?? kind.suggestedModels.first ?? "",
                   baseURL: d.string(forKey: "aiBaseURL.\(kind.rawValue)") ?? kind.defaultBaseURL,
                   key: KeyStore.get(kind.rawValue))
    }

    func save() {
        let d = AISettings.d
        d.set(kind.rawValue, forKey: "aiProvider")
        d.set(model, forKey: "aiModel.\(kind.rawValue)")
        d.set(baseURL, forKey: "aiBaseURL.\(kind.rawValue)")
        KeyStore.set(kind.rawValue, key)
        if !model.isEmpty { AISettings.remember(models: [model], for: kind) }
    }

    var isReady: Bool { !model.isEmpty && (!kind.needsKey || !(key ?? "").isEmpty) }

    /// Models the user typed or fetched, so new ones stay in the list without an app update.
    static func savedModels(_ kind: AIProviderKind) -> [String] {
        let saved = d.stringArray(forKey: "aiModels.\(kind.rawValue)") ?? []
        var seen = Set<String>()
        return (kind.suggestedModels + saved).filter { seen.insert($0).inserted }
    }

    static func remember(models: [String], for kind: AIProviderKind) {
        let key = "aiModels.\(kind.rawValue)"
        var list = d.stringArray(forKey: key) ?? []
        for m in models where !m.isEmpty && !list.contains(m) { list.append(m) }
        d.set(list, forKey: key)
    }

    /// The user has read what gets sent to the provider.
    static var privacyAccepted: Bool {
        get { d.bool(forKey: "aiPrivacyAccepted") }
        set { d.set(newValue, forKey: "aiPrivacyAccepted") }
    }
}

/// API keys in the login Keychain, one item per provider. Never written to settings or the database.
enum KeyStore {
    static let service = "ai.agilityresources.openrewind.ai"

    static func get(_ account: String) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// nil or "" removes the key.
    static func set(_ account: String, _ value: String?) {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account]
        _ = SecItemDelete(q as CFDictionary)
        guard let value, !value.isEmpty else { return }
        var add = q
        add[kSecValueData as String] = Data(value.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { log("could not save AI key to the Keychain (\(status))") }
    }
}

// MARK: - conversation

struct AIToolCall {
    var id: String
    var name: String
    var args: [String: Any]
    /// Gemini's thoughtSignature, which has to be sent back with the call.
    var signature: String?
}

/// One turn, in a shape that converts to each provider's format.
struct AIMessage {
    enum Role { case user, assistant, tool }
    var role: Role
    var text = ""
    var toolCalls: [AIToolCall] = []
    /// For `.tool`: which call this answers.
    var toolCallId = ""
    var toolName = ""
}

struct AIToolSpec {
    var name: String
    var description: String
    /// JSON schema of the arguments.
    var parameters: [String: Any]
}

struct AIReply {
    var text: String
    var toolCalls: [AIToolCall]
}

struct AIError: LocalizedError {
    var message: String
    var errorDescription: String? { message }
}

/// One request/response client for all providers (no streaming yet).
struct AIClient {
    let settings: AISettings

    private var base: String {
        var b = settings.baseURL.trimmingCharacters(in: .whitespaces)
        while b.hasSuffix("/") { b.removeLast() }
        return b.isEmpty ? settings.kind.defaultBaseURL : b
    }

    private var key: String { settings.key ?? "" }

    /// Gemini lists models as "models/gemini-…"; the request path adds "models/" itself.
    private var geminiModel: String {
        settings.model.hasPrefix("models/") ? String(settings.model.dropFirst(7)) : settings.model
    }

    func send(system: String, messages: [AIMessage], tools: [AIToolSpec]) async throws -> AIReply {
        switch settings.kind {
        case .anthropic:
            let body = AIFormat.anthropicBody(model: settings.model, system: system, messages: messages, tools: tools)
            let json = try await post("\(base)/v1/messages", body,
                                      headers: ["x-api-key": key, "anthropic-version": "2023-06-01"])
            return AIFormat.anthropicReply(json)
        case .openai, .custom:
            let body = AIFormat.openAIBody(model: settings.model, system: system, messages: messages, tools: tools)
            let json = try await post("\(base)/chat/completions", body, headers: bearer)
            return try AIFormat.openAIReply(json)
        case .gemini:
            let body = AIFormat.geminiBody(system: system, messages: messages, tools: tools)
            let json = try await post("\(base)/models/\(geminiModel):generateContent", body, headers: ["x-goog-api-key": key])
            return try AIFormat.geminiReply(json)
        }
    }

    /// The provider's current model names, for the model picker.
    func listModels() async throws -> [String] {
        switch settings.kind {
        case .anthropic:
            let json = try await get("\(base)/v1/models?limit=100", headers: ["x-api-key": key, "anthropic-version": "2023-06-01"])
            return ((json["data"] as? [[String: Any]]) ?? []).compactMap { $0["id"] as? String }
        case .openai, .custom:
            let json = try await get("\(base)/models", headers: bearer)
            let ids = ((json["data"] as? [[String: Any]]) ?? []).compactMap { $0["id"] as? String }
            return settings.kind == .openai ? ids.filter(AIFormat.isOpenAIChatModel).sorted() : ids.sorted()
        case .gemini:
            let json = try await get("\(base)/models?pageSize=1000", headers: ["x-goog-api-key": key])
            return ((json["models"] as? [[String: Any]]) ?? []).compactMap { m in
                guard let name = m["name"] as? String,
                      (m["supportedGenerationMethods"] as? [String])?.contains("generateContent") == true else { return nil }
                return name.hasPrefix("models/") ? String(name.dropFirst(7)) : name
            }
        }
    }

    private var bearer: [String: String] { key.isEmpty ? [:] : ["Authorization": "Bearer \(key)"] }

    private func post(_ url: String, _ body: [String: Any], headers: [String: String]) async throws -> [String: Any] {
        guard let u = URL(string: url) else { throw AIError(message: "Bad server address: \(url)") }
        var req = URLRequest(url: u, timeoutInterval: 180)
        req.httpMethod = "POST"
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
        req.httpBody = try JSONSerialization.data(withJSONObject: body)
        return try await run(req)
    }

    private func get(_ url: String, headers: [String: String]) async throws -> [String: Any] {
        guard let u = URL(string: url) else { throw AIError(message: "Bad server address: \(url)") }
        var req = URLRequest(url: u, timeoutInterval: 30)
        for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
        return try await run(req)
    }

    private func run(_ req: URLRequest) async throws -> [String: Any] {
        let (data, resp) = try await URLSession.shared.data(for: req)
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard (200..<300).contains(status) else {
            throw AIError(message: AIFormat.errorMessage(status: status, json: json, raw: data))
        }
        guard let json else { throw AIError(message: "The provider sent an answer OpenRewind could not read.") }
        return json
    }
}

/// Request bodies and response parsing for each provider. Kept free of networking so it matches
/// AiFormat in the Windows app line for line.
enum AIFormat {
    static let maxTokens = 4096

    // MARK: Anthropic

    static func anthropicBody(model: String, system: String, messages: [AIMessage], tools: [AIToolSpec]) -> [String: Any] {
        var out: [[String: Any]] = []
        for m in messages {
            switch m.role {
            case .user:
                out.append(["role": "user", "content": m.text])
            case .assistant:
                var content: [[String: Any]] = []
                if !m.text.isEmpty { content.append(["type": "text", "text": m.text]) }
                for c in m.toolCalls { content.append(["type": "tool_use", "id": c.id, "name": c.name, "input": c.args]) }
                out.append(["role": "assistant", "content": content])
            case .tool:
                // All results for one assistant turn go back in a single user message.
                let block: [String: Any] = ["type": "tool_result", "tool_use_id": m.toolCallId, "content": m.text]
                if var last = out.last, last["role"] as? String == "user", var blocks = last["content"] as? [[String: Any]] {
                    blocks.append(block)
                    last["content"] = blocks
                    out[out.count - 1] = last
                } else {
                    out.append(["role": "user", "content": [block]])
                }
            }
        }
        var body: [String: Any] = ["model": model, "max_tokens": maxTokens, "system": system, "messages": out]
        if !tools.isEmpty {
            body["tools"] = tools.map { ["name": $0.name, "description": $0.description, "input_schema": $0.parameters] }
        }
        return body
    }

    static func anthropicReply(_ json: [String: Any]) -> AIReply {
        var text = "", calls: [AIToolCall] = []
        for b in (json["content"] as? [[String: Any]]) ?? [] {
            switch (b["type"] as? String) ?? "" {
            case "text": text += (b["text"] as? String) ?? ""
            case "tool_use":
                calls.append(AIToolCall(id: (b["id"] as? String) ?? UUID().uuidString, name: (b["name"] as? String) ?? "",
                                        args: (b["input"] as? [String: Any]) ?? [:]))
            default: break
            }
        }
        return AIReply(text: text, toolCalls: calls)
    }

    // MARK: OpenAI and OpenAI-compatible servers

    static func openAIBody(model: String, system: String, messages: [AIMessage], tools: [AIToolSpec]) -> [String: Any] {
        var out: [[String: Any]] = [["role": "system", "content": system]]
        for m in messages {
            switch m.role {
            case .user:
                out.append(["role": "user", "content": m.text])
            case .assistant:
                var msg: [String: Any] = ["role": "assistant", "content": m.text]
                if !m.toolCalls.isEmpty {
                    msg["tool_calls"] = m.toolCalls.map { c -> [String: Any] in
                        let args = (try? JSONSerialization.data(withJSONObject: c.args)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
                        return ["id": c.id, "type": "function", "function": ["name": c.name, "arguments": args]]
                    }
                }
                out.append(msg)
            case .tool:
                out.append(["role": "tool", "tool_call_id": m.toolCallId, "content": m.text])
            }
        }
        var body: [String: Any] = ["model": model, "messages": out]
        // OpenAI refuses an empty tools list.
        if !tools.isEmpty {
            body["tools"] = tools.map { ["type": "function",
                                         "function": ["name": $0.name, "description": $0.description, "parameters": $0.parameters]] }
        }
        return body
    }

    static func openAIReply(_ json: [String: Any]) throws -> AIReply {
        guard let msg = ((json["choices"] as? [[String: Any]])?.first)?["message"] as? [String: Any] else {
            throw AIError(message: "The provider sent no answer.")
        }
        let calls = ((msg["tool_calls"] as? [[String: Any]]) ?? []).map { c -> AIToolCall in
            let f = (c["function"] as? [String: Any]) ?? [:]
            let raw = (f["arguments"] as? String) ?? "{}"
            let args = (try? JSONSerialization.jsonObject(with: Data(raw.utf8))) as? [String: Any] ?? [:]
            return AIToolCall(id: (c["id"] as? String) ?? UUID().uuidString, name: (f["name"] as? String) ?? "", args: args)
        }
        return AIReply(text: (msg["content"] as? String) ?? "", toolCalls: calls)
    }

    /// The OpenAI model list also has embeddings, speech and image models; keep the chat ones.
    static func isOpenAIChatModel(_ id: String) -> Bool {
        let skip = ["embedding", "whisper", "tts", "dall-e", "image", "audio", "realtime", "moderation", "transcribe", "search", "davinci", "babbage"]
        let id = id.lowercased()
        return (id.hasPrefix("gpt") || id.hasPrefix("o") || id.hasPrefix("chatgpt")) && !skip.contains { id.contains($0) }
    }

    // MARK: Gemini

    static func geminiBody(system: String, messages: [AIMessage], tools: [AIToolSpec]) -> [String: Any] {
        var out: [[String: Any]] = []
        for m in messages {
            switch m.role {
            case .user:
                out.append(["role": "user", "parts": [["text": m.text]]])
            case .assistant:
                var parts: [[String: Any]] = []
                if !m.text.isEmpty { parts.append(["text": m.text]) }
                for c in m.toolCalls {
                    var p: [String: Any] = ["functionCall": ["name": c.name, "args": c.args]]
                    if let s = c.signature { p["thoughtSignature"] = s }
                    parts.append(p)
                }
                out.append(["role": "model", "parts": parts])
            case .tool:
                let part: [String: Any] = ["functionResponse": ["name": m.toolName, "response": ["result": m.text]]]
                if var last = out.last, last["role"] as? String == "user", var parts = last["parts"] as? [[String: Any]],
                   parts.first?["functionResponse"] != nil {
                    parts.append(part)
                    last["parts"] = parts
                    out[out.count - 1] = last
                } else {
                    out.append(["role": "user", "parts": [part]])
                }
            }
        }
        var body: [String: Any] = ["systemInstruction": ["parts": [["text": system]]], "contents": out]
        if !tools.isEmpty {
            body["tools"] = [["functionDeclarations": tools.map { ["name": $0.name, "description": $0.description, "parameters": $0.parameters] }]]
        }
        return body
    }

    static func geminiReply(_ json: [String: Any]) throws -> AIReply {
        guard let cand = (json["candidates"] as? [[String: Any]])?.first else {
            let reason = ((json["promptFeedback"] as? [String: Any])?["blockReason"] as? String) ?? "no answer"
            throw AIError(message: "Gemini sent no answer (\(reason)).")
        }
        var text = "", calls: [AIToolCall] = []
        for p in ((cand["content"] as? [String: Any])?["parts"] as? [[String: Any]]) ?? [] {
            if p["thought"] as? Bool == true { continue }
            if let t = p["text"] as? String { text += t }
            if let f = p["functionCall"] as? [String: Any] {
                calls.append(AIToolCall(id: (f["id"] as? String) ?? "call_\(calls.count + 1)", name: (f["name"] as? String) ?? "",
                                        args: (f["args"] as? [String: Any]) ?? [:], signature: p["thoughtSignature"] as? String))
            }
        }
        return AIReply(text: text, toolCalls: calls)
    }

    // MARK: errors

    static func errorMessage(status: Int, json: [String: Any]?, raw: Data) -> String {
        let fromJSON = ((json?["error"] as? [String: Any])?["message"] as? String) ?? (json?["error"] as? String)
        let detail = fromJSON ?? String(data: raw.prefix(300), encoding: .utf8) ?? ""
        switch status {
        case 401, 403: return "The provider did not accept the API key (\(status)). \(detail)"
        case 404: return "Model or address not found (404). Check the model name. \(detail)"
        case 429: return "The provider says too many requests or no credit left (429). \(detail)"
        default: return "The provider returned an error (\(status)). \(detail)"
        }
    }
}
