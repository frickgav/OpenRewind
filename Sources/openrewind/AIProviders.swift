import AppKit
import Security

/// The AI services a user can bring their own key for. Requests go straight from this Mac to the
/// provider; OpenRewind has no server in between. The Windows app has the same list in AiProviders.cs.
enum AIProviderKind: String, CaseIterable, Identifiable {
    case anthropic, openai, gemini, openrouter, custom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .anthropic: return "Anthropic (Claude)"
        case .openai: return "OpenAI"
        case .gemini: return "Google Gemini"
        case .openrouter: return "OpenRouter (any model)"
        case .custom: return "Custom (OpenAI-compatible)"
        }
    }

    var defaultBaseURL: String {
        switch self {
        case .anthropic: return "https://api.anthropic.com"
        case .openai: return "https://api.openai.com/v1"
        case .gemini: return "https://generativelanguage.googleapis.com/v1beta"
        case .openrouter: return "https://openrouter.ai/api/v1"
        case .custom: return "http://localhost:11434/v1"
        }
    }

    /// Starting suggestions only: any model name can be typed, and "Refresh" asks the provider for its list.
    var suggestedModels: [String] {
        switch self {
        case .anthropic: return ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-1"]
        case .openai: return ["gpt-6.1-sol", "gpt-6-luna", "gpt-6-astra"]
        case .gemini: return ["gemini-3.8-flash", "gemini-4-argon", "gemini-3.5-flash-lite"]
        case .openrouter, .custom: return []
        }
    }

    /// Local servers such as Ollama or LM Studio take no key.
    var needsKey: Bool { self != .custom }

    var keyPage: URL? {
        switch self {
        case .anthropic: return URL(string: "https://console.anthropic.com/settings/keys")
        case .openai: return URL(string: "https://platform.openai.com/api-keys")
        case .gemini: return URL(string: "https://aistudio.google.com/apikey")
        case .openrouter: return URL(string: "https://openrouter.ai/keys")
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

    /// `interactive: false` for work nobody asked for just now (timers, meeting checks): it never
    /// brings up the Keychain prompt, and the key reads as missing until the user opens the AI.
    static func load(interactive: Bool = true) -> AISettings {
        let kind = AIProviderKind(rawValue: d.string(forKey: "aiProvider") ?? "") ?? .anthropic
        return load(kind, interactive: interactive)
    }

    static func load(_ kind: AIProviderKind, interactive: Bool = true) -> AISettings {
        AISettings(kind: kind,
                   model: d.string(forKey: "aiModel.\(kind.rawValue)") ?? kind.suggestedModels.first ?? "",
                   baseURL: d.string(forKey: "aiBaseURL.\(kind.rawValue)") ?? kind.defaultBaseURL,
                   key: KeyStore.get(kind.rawValue, interactive: interactive))
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

    /// Let the AI ask for screenshots (view_screenshot). Off unless the user turns it on.
    static var screenshotsAllowed: Bool {
        get { d.bool(forKey: "aiScreenshots") }
        set { d.set(newValue, forKey: "aiScreenshots") }
    }

    /// The price the user typed for a model, else the one from OpenRouter's list, else the built-in one.
    static func price(for model: String) -> AIPrice? {
        for key in ["aiPrices", "aiListedPrices"] {
            if let p = (d.dictionary(forKey: key)?[model] as? [Double]), p.count == 2 { return AIPrice(input: p[0], output: p[1]) }
        }
        return AIPrice.builtIn(model)
    }

    /// nil removes the typed price, so the listed or built-in one applies again.
    static func setPrice(_ p: AIPrice?, for model: String) {
        var all = d.dictionary(forKey: "aiPrices") ?? [:]
        if let p { all[model] = [p.input, p.output] } else { all.removeValue(forKey: model) }
        d.set(all, forKey: "aiPrices")
    }

    static func rememberListedPrices(_ list: [(id: String, price: AIPrice?)]) {
        var all = d.dictionary(forKey: "aiListedPrices") ?? [:]
        for m in list { if let p = m.price { all[m.id] = [p.input, p.output] } }
        d.set(all, forKey: "aiListedPrices")
    }

    /// Everything used with one provider since the user last reset it.
    static func total(_ kind: AIProviderKind) -> AITotal? {
        guard let t = d.dictionary(forKey: "aiTotal.\(kind.rawValue)") else { return nil }
        return AITotal(input: (t["input"] as? NSNumber)?.intValue ?? 0, output: (t["output"] as? NSNumber)?.intValue ?? 0,
                       cost: (t["cost"] as? NSNumber)?.doubleValue ?? 0, unpriced: t["unpriced"] as? Bool ?? false,
                       since: t["since"] as? Date ?? Date())
    }

    static func add(_ used: AIUsage, cost: Double?, to kind: AIProviderKind) {
        guard !used.isZero else { return }
        var t = total(kind) ?? AITotal(input: 0, output: 0, cost: 0, unpriced: false, since: Date())
        t.input += used.input
        t.output += used.output
        t.cost += cost ?? 0
        if cost == nil { t.unpriced = true }
        d.set(["input": t.input, "output": t.output, "cost": t.cost, "unpriced": t.unpriced, "since": t.since] as [String: Any],
              forKey: "aiTotal.\(kind.rawValue)")
    }

    static func resetTotal(_ kind: AIProviderKind) { d.removeObject(forKey: "aiTotal.\(kind.rawValue)") }
}

struct AITotal {
    var input: Int, output: Int
    var cost: Double
    /// Some requests went to a model with no price, so `cost` is too low.
    var unpriced: Bool
    var since: Date
}

/// Tokens one or more requests used. `input` counts everything the provider read, cached or not.
struct AIUsage: Equatable {
    var input = 0, output = 0
    static func + (a: AIUsage, b: AIUsage) -> AIUsage { AIUsage(input: a.input + b.input, output: a.output + b.output) }
    var isZero: Bool { input == 0 && output == 0 }

    func cost(_ p: AIPrice?) -> Double? {
        guard let p else { return nil }
        return (Double(input) * p.input + Double(output) * p.output) / 1_000_000
    }

    /// "12,345 tokens in, 678 out · $0.06"
    func describe(_ p: AIPrice?) -> String {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        let s = "\(f.string(from: input as NSNumber) ?? "\(input)") tokens in, \(f.string(from: output as NSNumber) ?? "\(output)") out"
        return cost(p).map { s + " · " + AIPrice.money($0) } ?? s
    }
}

/// US dollars per million tokens.
struct AIPrice: Equatable {
    var input: Double, output: Double

    /// List prices from the providers' pricing pages (October 2026). OpenRouter sends its prices with
    /// the model list; for anything else the user types the price in AI Settings.
    private static let known: [(String, AIPrice)] = [
        ("gpt-6-astra", AIPrice(input: 10, output: 50)), ("gpt-6.1-sol", AIPrice(input: 2, output: 10)), ("gpt-6-luna", AIPrice(input: 0.10, output: 0.50)),
        ("claude-opus-5-5", AIPrice(input: 4, output: 20)), ("claude-sonnet-5-5", AIPrice(input: 2, output: 10)), ("claude-haiku-5-5", AIPrice(input: 0.10, output: 0.50)),
        ("claude-opus-5", AIPrice(input: 5, output: 25)), ("claude-sonnet-5", AIPrice(input: 2, output: 10)), ("claude-fable-5", AIPrice(input: 10, output: 50)),
        ("claude-opus-4", AIPrice(input: 5, output: 25)), ("claude-sonnet-4", AIPrice(input: 3, output: 15)), ("claude-haiku-4-5", AIPrice(input: 1, output: 5)),
    ]

    static func builtIn(_ model: String) -> AIPrice? {
        let m = model.lowercased()
        return known.sorted { $0.0.count > $1.0.count }.first { m.hasPrefix($0.0) }?.1
    }

    static func money(_ d: Double) -> String {
        d > 0 && d < 0.01 ? String(format: "$%.4f", d) : String(format: "$%.2f", d)
    }
}

/// API keys in the login Keychain, one item per provider. Never written to settings or the database.
enum KeyStore {
    static let service = "ai.agilityresources.openrewind.ai"

    /// Keys already read this launch. Every Keychain read can bring up the macOS password prompt,
    /// so each key is read at most once per launch.
    private static var cache: [String: String?] = [:]
    private static let lock = NSLock()

    /// A read by a build that doesn't own the item yet brings up the macOS password prompt. With
    /// `interactive` false that read is skipped (nil, not cached), so a timer never puts up a prompt
    /// while the app is in the background, where it can't take typing. Otherwise the app comes to
    /// the front first, so the prompt gets the keyboard.
    static func get(_ account: String, interactive: Bool = true) -> String? {
        lock.lock(); defer { lock.unlock() }
        if let hit = cache[account] { return hit }
        if !owned(account) {
            guard exists(account) else { cache.updateValue(nil, forKey: account); return nil }
            guard interactive else { return nil }
            comeForward()
        }
        let value = read(account)
        cache.updateValue(value, forKey: account)
        if let value { takeOwnership(account, value) }
        return value
    }

    /// nil or "" removes the key.
    static func set(_ account: String, _ value: String?) {
        lock.lock(); defer { lock.unlock() }
        if write(account, value) { UserDefaults.standard.set(buildID, forKey: ownerKey(account)) }
        cache.updateValue((value?.isEmpty ?? true) ? nil : value, forKey: account)
    }

    private static func ownerKey(_ account: String) -> String { "aiKeychainOwner.\(account)" }

    /// This build saved the item, so reading it doesn't ask.
    private static func owned(_ account: String) -> Bool {
        UserDefaults.standard.string(forKey: ownerKey(account)) == buildID
    }

    /// Asks only for the item's attributes, which never brings up the prompt.
    private static func exists(_ account: String) -> Bool {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecMatchLimit as String: kSecMatchLimitOne,
                                kSecReturnAttributes as String: true]
        var out: CFTypeRef?
        return SecItemCopyMatching(q as CFDictionary, &out) != errSecItemNotFound
    }

    /// The menu bar app is usually in the background; a prompt it puts up there shows but the
    /// keyboard stays with the app in front.
    private static func comeForward() {
        let go: () -> Void = { NSApp?.activate(ignoringOtherApps: true) }
        // Not sync: the main thread may be waiting on `lock`. The prompt stays up until answered.
        if Thread.isMainThread { go() } else { DispatchQueue.main.async(execute: go) }
    }

    private static func read(_ account: String) -> String? {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account, kSecReturnData as String: true,
                                kSecMatchLimit as String: kSecMatchLimitOne]
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    @discardableResult
    private static func write(_ account: String, _ value: String?) -> Bool {
        let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                kSecAttrAccount as String: account]
        _ = SecItemDelete(q as CFDictionary)
        guard let value, !value.isEmpty else { return true }
        var add = q
        add[kSecValueData as String] = Data(value.utf8)
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { log("could not save AI key to the Keychain (\(status))") }
        return status == errSecSuccess
    }

    /// macOS lets only the app build that saved a Keychain item read it without asking; a new build
    /// (every update, as the app is not signed by Apple) gets the password prompt. After the first
    /// read in a new build, save the key again so this build owns it and later launches stay quiet.
    private static func takeOwnership(_ account: String, _ value: String) {
        guard !owned(account) else { return }
        if write(account, value) { UserDefaults.standard.set(buildID, forKey: ownerKey(account)) }
    }

    /// Changes whenever the app binary does.
    static var buildID: String {
        let exe = Bundle.main.executableURL
        let date = exe.flatMap { try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate }
        let version = Bundle.main.infoDictionary?["CFBundleVersion"] as? String ?? ""
        return "\(version)-\(Int(date?.timeIntervalSince1970 ?? 0))"
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
    /// For `.tool`: a JPEG screenshot that goes with the result (view_screenshot).
    var image: Data?
    /// For `.assistant` from Anthropic or OpenAI: the output exactly as it came. Thinking and
    /// reasoning items must be sent back unchanged during tool use, so they are replayed from here.
    var raw: [[String: Any]]?
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
    var usage = AIUsage()
    var raw: [[String: Any]]?
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
        case .openai:
            // Newer OpenAI models only take tools on the Responses endpoint.
            let body = AIFormat.responsesBody(model: settings.model, system: system, messages: messages, tools: tools)
            let json = try await post("\(base)/responses", body, headers: bearer)
            return try AIFormat.responsesReply(json)
        case .openrouter, .custom:
            let body = AIFormat.openAIBody(model: settings.model, system: system, messages: messages, tools: tools)
            let json = try await post("\(base)/chat/completions", body, headers: bearer)
            return try AIFormat.openAIReply(json)
        case .gemini:
            let body = AIFormat.geminiBody(system: system, messages: messages, tools: tools)
            let json = try await post("\(base)/models/\(geminiModel):generateContent", body, headers: ["x-goog-api-key": key])
            return try AIFormat.geminiReply(json)
        }
    }

    /// Model names, with prices where the provider lists them (OpenRouter does).
    func listModelsWithPrices() async throws -> [(id: String, price: AIPrice?)] {
        if settings.kind == .openrouter { return AIFormat.openRouterModels(try await get("\(base)/models", headers: bearer)) }
        return try await listModels().map { (id: $0, price: nil) }
    }

    /// The provider's current model names, for the model picker.
    func listModels() async throws -> [String] {
        switch settings.kind {
        case .openrouter:
            return try await listModelsWithPrices().map { $0.id }
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
                if let raw = m.raw {
                    out.append(["role": "assistant", "content": raw])
                    continue
                }
                var content: [[String: Any]] = []
                if !m.text.isEmpty { content.append(["type": "text", "text": m.text]) }
                for c in m.toolCalls { content.append(["type": "tool_use", "id": c.id, "name": c.name, "input": c.args]) }
                out.append(["role": "assistant", "content": content])
            case .tool:
                // All results for one assistant turn go back in a single user message.
                var block: [String: Any] = ["type": "tool_result", "tool_use_id": m.toolCallId, "content": m.text]
                if let img = m.image {
                    block["content"] = [["type": "text", "text": m.text],
                                        ["type": "image", "source": ["type": "base64", "media_type": "image/jpeg", "data": img.base64EncodedString()]]]
                }
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
        let u = (json["usage"] as? [String: Any]) ?? [:]
        let usage = AIUsage(input: num(u["input_tokens"]) + num(u["cache_creation_input_tokens"]) + num(u["cache_read_input_tokens"]),
                            output: num(u["output_tokens"]))
        return AIReply(text: text, toolCalls: calls, usage: usage, raw: json["content"] as? [[String: Any]])
    }

    static func num(_ v: Any?) -> Int { (v as? NSNumber)?.intValue ?? 0 }

    static func jsonText(_ v: Any) -> String {
        (try? JSONSerialization.data(withJSONObject: v)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
    }

    // MARK: OpenAI Responses

    /// OpenAI's /v1/responses. `store: false` keeps the conversation off OpenAI's servers, so the
    /// whole history goes in every request; the model's own output items (reasoning included) are
    /// replayed exactly as they came.
    static func responsesBody(model: String, system: String, messages: [AIMessage], tools: [AIToolSpec]) -> [String: Any] {
        var input: [[String: Any]] = []
        var images: [Data] = []
        func flushImages() {
            guard !images.isEmpty else { return }
            var parts: [[String: Any]] = [["type": "input_text", "text": "The screenshot(s) view_screenshot returned, in order:"]]
            for img in images { parts.append(["type": "input_image", "image_url": "data:image/jpeg;base64," + img.base64EncodedString()]) }
            input.append(["role": "user", "content": parts])
            images = []
        }
        for m in messages {
            if m.role != .tool { flushImages() }
            switch m.role {
            case .user:
                input.append(["role": "user", "content": m.text])
            case .assistant:
                if let raw = m.raw {
                    input += raw
                } else {
                    if !m.text.isEmpty { input.append(["role": "assistant", "content": m.text]) }
                    for c in m.toolCalls {
                        input.append(["type": "function_call", "call_id": c.id, "name": c.name, "arguments": jsonText(c.args)])
                    }
                }
            case .tool:
                input.append(["type": "function_call_output", "call_id": m.toolCallId, "output": m.text])
                if let img = m.image { images.append(img) }
            }
        }
        flushImages()
        var body: [String: Any] = ["model": model, "instructions": system, "input": input, "store": false]
        if !tools.isEmpty {
            // Strict mode wants every argument required; ours are mostly optional.
            body["tools"] = tools.map { ["type": "function", "name": $0.name, "description": $0.description,
                                         "parameters": $0.parameters, "strict": false] as [String: Any] }
        }
        return body
    }

    static func responsesReply(_ json: [String: Any]) throws -> AIReply {
        guard let output = json["output"] as? [[String: Any]] else { throw AIError(message: "The provider sent no answer.") }
        var text = "", calls: [AIToolCall] = []
        for item in output {
            switch (item["type"] as? String) ?? "" {
            case "message":
                for part in (item["content"] as? [[String: Any]]) ?? [] where part["type"] as? String == "output_text" {
                    text += (part["text"] as? String) ?? ""
                }
            case "function_call":
                let raw = (item["arguments"] as? String) ?? "{}"
                let args = (try? JSONSerialization.jsonObject(with: Data(raw.utf8))) as? [String: Any] ?? [:]
                calls.append(AIToolCall(id: (item["call_id"] as? String) ?? UUID().uuidString, name: (item["name"] as? String) ?? "", args: args))
            default: break
            }
        }
        let u = (json["usage"] as? [String: Any]) ?? [:]
        return AIReply(text: text, toolCalls: calls, usage: AIUsage(input: num(u["input_tokens"]), output: num(u["output_tokens"])), raw: output)
    }

    /// OpenRouter's list, with prices in dollars per token as text:
    /// {"data":[{"id":"openai/gpt-6-luna","pricing":{"prompt":"0.0000001","completion":"0.0000005"}}]}
    static func openRouterModels(_ json: [String: Any]) -> [(id: String, price: AIPrice?)] {
        func perMillion(_ v: Any?) -> Double? {
            let d = (v as? String).flatMap { Double($0) } ?? (v as? NSNumber)?.doubleValue
            guard let d, d >= 0 else { return nil }
            return (d * 1_000_000 * 1_000_000).rounded() / 1_000_000
        }
        return ((json["data"] as? [[String: Any]]) ?? []).compactMap { m -> (id: String, price: AIPrice?)? in
            guard let id = m["id"] as? String else { return nil }
            let p = (m["pricing"] as? [String: Any]) ?? [:]
            if let i = perMillion(p["prompt"]), let o = perMillion(p["completion"]) { return (id: id, price: AIPrice(input: i, output: o)) }
            return (id: id, price: nil)
        }.sorted { $0.id < $1.id }
    }

    // MARK: OpenAI and OpenAI-compatible servers

    static func openAIBody(model: String, system: String, messages: [AIMessage], tools: [AIToolSpec]) -> [String: Any] {
        var out: [[String: Any]] = [["role": "system", "content": system]]
        // Tool messages can only hold text, so screenshots follow the tool answers as a user message.
        var images: [Data] = []
        func flushImages() {
            guard !images.isEmpty else { return }
            var parts: [[String: Any]] = [["type": "text", "text": "The screenshot(s) view_screenshot returned, in order:"]]
            for img in images { parts.append(["type": "image_url", "image_url": ["url": "data:image/jpeg;base64," + img.base64EncodedString()]]) }
            out.append(["role": "user", "content": parts])
            images = []
        }
        for m in messages {
            if m.role != .tool { flushImages() }
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
                if let img = m.image { images.append(img) }
            }
        }
        flushImages()
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
        let u = (json["usage"] as? [String: Any]) ?? [:]
        return AIReply(text: (msg["content"] as? String) ?? "", toolCalls: calls,
                       usage: AIUsage(input: num(u["prompt_tokens"]), output: num(u["completion_tokens"])))
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
                var add: [[String: Any]] = [["functionResponse": ["name": m.toolName, "response": ["result": m.text]]]]
                if let img = m.image { add.append(["inlineData": ["mimeType": "image/jpeg", "data": img.base64EncodedString()]]) }
                if var last = out.last, last["role"] as? String == "user", var parts = last["parts"] as? [[String: Any]],
                   parts.first?["functionResponse"] != nil {
                    parts += add
                    last["parts"] = parts
                    out[out.count - 1] = last
                } else {
                    out.append(["role": "user", "parts": add])
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
        let u = (json["usageMetadata"] as? [String: Any]) ?? [:]
        return AIReply(text: text, toolCalls: calls,
                       usage: AIUsage(input: num(u["promptTokenCount"]), output: num(u["candidatesTokenCount"]) + num(u["thoughtsTokenCount"])))
    }

    // MARK: custom servers

    /// An http:// address that leaves this computer and its network, where the key and history would
    /// travel unencrypted. Loopback, .local names and private or link-local addresses are fine.
    static func isPlainHTTPRemote(_ address: String) -> Bool {
        guard let u = URL(string: address.trimmingCharacters(in: .whitespaces)), u.scheme?.lowercased() == "http",
              let raw = u.host?.lowercased(), !raw.isEmpty else { return false }
        let host = raw.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        if host.contains(":") { return !(host == "::1" || host.hasPrefix("fe80:") || host.hasPrefix("fc") || host.hasPrefix("fd")) }
        if host == "localhost" || host.hasSuffix(".localhost") || host.hasSuffix(".local") || !host.contains(".") { return false }
        let p = host.split(separator: ".").compactMap { Int($0) }
        guard p.count == 4 else { return true }
        switch (p[0], p[1]) {
        case (127, _), (10, _), (192, 168), (169, 254): return false
        case (172, 16...31): return false
        case (100, 64...127): return false  // carrier-grade NAT range, used by Tailscale
        default: return true
        }
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
