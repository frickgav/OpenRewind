import AppKit
import SwiftUI

/// One row in the chat window.
struct ChatItem: Identifiable {
    enum Kind { case user, assistant, tool, error }
    let id = UUID()
    var kind: Kind
    var text: String
    /// For `.tool`: exactly what was sent to the provider.
    var detail = ""
}

/// The conversation behind the chat window. Kept by the menu bar app, so closing the window keeps it.
final class AIChatModel: ObservableObject {
    @Published var items: [ChatItem] = []
    @Published var input = ""
    @Published var busy = false
    @Published var settings = AISettings.load()
    @Published var privacyAccepted = AISettings.privacyAccepted
    private var history: [AIMessage] = []
    private var task: Task<Void, Never>?
    private let tools: AITools
    var openMoment: (Date) -> Void = { _ in }
    var openSettings: () -> Void = {}

    /// Lookups per question before giving up.
    static let maxRounds = 10
    /// Characters of earlier tool answers kept in the conversation; older ones are shortened.
    static let historyBudget = 60_000

    init(db: Database) { tools = AITools(db: db) }

    func reloadSettings() { settings = AISettings.load() }

    func acceptPrivacy() {
        AISettings.privacyAccepted = true
        privacyAccepted = true
    }

    func send(_ text: String? = nil) {
        let q = (text ?? input).trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, !busy, settings.isReady else { return }
        input = ""
        items.append(ChatItem(kind: .user, text: q))
        history.append(AIMessage(role: .user, text: q))
        busy = true
        let client = AIClient(settings: settings)
        task = Task { @MainActor [weak self] in
            await self?.run(client)
            self?.busy = false
        }
    }

    func stop() { task?.cancel() }

    func clear() {
        stop()
        items = []
        history = []
    }

    @MainActor private func run(_ client: AIClient) async {
        do {
            for _ in 0..<Self.maxRounds {
                trimHistory()
                let reply = try await client.send(system: Self.systemPrompt(), messages: history, tools: AITools.specs)
                try Task.checkCancellation()
                history.append(AIMessage(role: .assistant, text: reply.text, toolCalls: reply.toolCalls))
                if !reply.text.isEmpty { items.append(ChatItem(kind: .assistant, text: reply.text)) }
                if reply.toolCalls.isEmpty { return }
                for call in reply.toolCalls {
                    let tools = self.tools
                    let result = await Task.detached { tools.run(call) }.value
                    try Task.checkCancellation()
                    history.append(AIMessage(role: .tool, text: result, toolCallId: call.id, toolName: call.name))
                    items.append(ChatItem(kind: .tool, text: AITools.describe(call), detail: result))
                }
            }
            items.append(ChatItem(kind: .error, text: "Stopped after \(Self.maxRounds) lookups. Try asking something narrower."))
        } catch {
            let cancelled = error is CancellationError || (error as? URLError)?.code == .cancelled
            items.append(ChatItem(kind: .error, text: cancelled ? "Stopped." : error.localizedDescription))
            // Leave the history in a state every provider accepts: no unanswered tool calls.
            while history.last?.role == .tool { history.removeLast() }
            if let last = history.last, last.role == .assistant, !last.toolCalls.isEmpty { history.removeLast() }
            if history.last?.role == .user { history.removeLast() }
        }
    }

    /// Long chats would send every earlier lookup again; shorten the oldest ones first.
    private func trimHistory() {
        var total = history.filter { $0.role == .tool }.reduce(0) { $0 + $1.text.count }
        for i in history.indices where total > Self.historyBudget && history[i].role == .tool && history[i].text.count > 1500 {
            total -= history[i].text.count - 1500
            history[i].text = String(history[i].text.prefix(1500)) + "\n[shortened]"
        }
    }

    static func systemPrompt() -> String {
        let now = Date()
        let day = now.formatted(.dateTime.weekday(.wide).day().month(.wide).year())
        return """
        You are the assistant inside OpenRewind, an app that records this user's computer: text read from \
        screenshots, app names, window titles, browser addresses, and transcripts of the microphone and system audio. \
        Everything stays on their computer; you can read it only through the tools.

        Now: \(AITime.label(now)) local time (\(TimeZone.current.identifier)), \(day).

        - Always look things up with the tools instead of guessing. For "what did I do / work on" questions, call \
        activity_summary first, then search_screen or get_screen_text for details.
        - Times in tool answers are local. Write times the way people say them (2:15 PM).
        - When you mention a moment, link it with the link from the tool answer, e.g. [2:15 PM](openrewind://t/1760000000). \
        The link opens that moment in the user's timeline.
        - Group work into tasks or projects instead of listing every window. Be concise.
        - If nothing is found, say so plainly. Screen text comes from OCR and can have errors.
        """
    }
}

// MARK: - views

struct AIChatView: View {
    @ObservedObject var model: AIChatModel

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider().overlay(SetupTheme.stroke)
            if !model.settings.isReady {
                notice(icon: "key", title: "Add your AI key",
                       text: "OpenRewind can answer questions about what you did and saw, using your own Anthropic, OpenAI or Gemini key, or a local model (Ollama, LM Studio).",
                       button: "Open AI Settings…") { model.openSettings() }
            } else if !model.privacyAccepted {
                notice(icon: "lock.shield", title: "Before you start",
                       text: "To answer, the AI reads parts of your history: screen text, app and window names, web addresses and transcripts. Those parts are sent to \(model.settings.kind.title) using your key, and their privacy terms apply. Screenshots are never sent. Blocked apps and sites are never recorded, so the AI never sees them. Each lookup is listed in the chat so you can see what was sent.",
                       button: "Continue") { model.acceptPrivacy() }
            } else {
                conversation
                Divider().overlay(SetupTheme.stroke)
                inputBar
            }
        }
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
        .environment(\.openURL, OpenURLAction { url in
            if let d = AITime.date(fromLink: url) { model.openMoment(d); return .handled }
            return .systemAction
        })
    }

    private var header: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text("Ask OpenRewind").font(.system(size: 15, weight: .semibold))
                if model.settings.isReady {
                    Text("\(model.settings.kind.title) · \(model.settings.model)").font(.system(size: 11)).foregroundColor(SetupTheme.faint)
                }
            }
            Spacer()
            Button { model.clear() } label: { Image(systemName: "square.and.pencil") }
                .buttonStyle(.plain).help("New chat").disabled(model.items.isEmpty)
            Button { model.openSettings() } label: { Image(systemName: "gearshape") }
                .buttonStyle(.plain).help("AI settings")
        }
        .padding(.horizontal, 16).padding(.top, 34).padding(.bottom, 10)
    }

    private func notice(icon: String, title: String, text: String, button: String, action: @escaping () -> Void) -> some View {
        VStack(spacing: 14) {
            Spacer()
            Image(systemName: icon).font(.system(size: 30)).foregroundColor(SetupTheme.accent)
            Text(title).font(.system(size: 18, weight: .semibold))
            Text(text).font(.system(size: 13)).foregroundColor(SetupTheme.dim).multilineTextAlignment(.center)
                .frame(maxWidth: 420).fixedSize(horizontal: false, vertical: true)
            Button(button, action: action).buttonStyle(PillButtonStyle(primary: true))
            Spacer()
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var conversation: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    if model.items.isEmpty { suggestions }
                    ForEach(model.items) { row($0) }
                    if model.busy {
                        HStack(spacing: 8) { ProgressView().controlSize(.small); Text("Thinking…").foregroundColor(SetupTheme.faint) }
                            .font(.system(size: 12))
                    }
                    Color.clear.frame(height: 1).id("end")
                }
                .padding(16)
            }
            .onChange(of: model.items.count) { withAnimation { proxy.scrollTo("end") } }
            .onChange(of: model.busy) { withAnimation { proxy.scrollTo("end") } }
        }
    }

    private var suggestions: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Ask about anything you did, saw or heard on this Mac.").font(.system(size: 13)).foregroundColor(SetupTheme.dim)
            ForEach(["What did I work on today?", "What was I doing yesterday afternoon?", "Summarize my meetings this week",
                     "Find the article I was reading about"], id: \.self) { s in
                Button { if s.hasSuffix("about") { model.input = s + " " } else { model.send(s) } } label: {
                    Text(s).font(.system(size: 13)).padding(.horizontal, 12).padding(.vertical, 7)
                        .background(RoundedRectangle(cornerRadius: 8).fill(SetupTheme.card))
                }
                .buttonStyle(.plain)
            }
        }
    }

    @ViewBuilder private func row(_ item: ChatItem) -> some View {
        switch item.kind {
        case .user:
            HStack {
                Spacer(minLength: 60)
                Text(item.text).font(.system(size: 13)).textSelection(.enabled)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(RoundedRectangle(cornerRadius: 12).fill(SetupTheme.accent))
            }
        case .assistant:
            Text(Self.markdown(item.text)).font(.system(size: 13)).textSelection(.enabled)
                .tint(Color(red: 0.45, green: 0.65, blue: 1))
                .padding(.horizontal, 12).padding(.vertical, 8)
                .background(RoundedRectangle(cornerRadius: 12).fill(SetupTheme.card))
                .frame(maxWidth: .infinity, alignment: .leading)
        case .tool:
            DisclosureGroup {
                ScrollView {
                    Text(item.detail).font(.system(size: 11, design: .monospaced)).foregroundColor(SetupTheme.dim)
                        .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 220)
            } label: {
                Label("\(item.text) · \(item.detail.count.formatted()) characters sent", systemImage: "magnifyingglass")
                    .font(.system(size: 11)).foregroundColor(SetupTheme.faint)
            }
        case .error:
            Label(item.text, systemImage: "exclamationmark.triangle").font(.system(size: 12)).foregroundColor(SetupTheme.warn)
                .textSelection(.enabled)
        }
    }

    private var inputBar: some View {
        HStack(spacing: 8) {
            TextField("Ask about what you did or saw…", text: $model.input)
                .textFieldStyle(.plain).font(.system(size: 14))
                .onSubmit { model.send() }
                .disabled(model.busy)
            if model.busy {
                Button("Stop") { model.stop() }.buttonStyle(PillButtonStyle())
            } else {
                Button("Send") { model.send() }.buttonStyle(PillButtonStyle(primary: true))
                    .disabled(model.input.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(12)
    }

    /// Bold, italics, code and links; line breaks kept.
    static func markdown(_ s: String) -> AttributedString {
        (try? AttributedString(markdown: s, options: .init(interpretedSyntax: .inlineOnlyPreservingWhitespace))) ?? AttributedString(s)
    }
}

/// Provider, key and model. Saved per provider, so switching back keeps what was typed.
struct AISettingsView: View {
    var onSave: () -> Void
    @State private var kind: AIProviderKind
    @State private var key: String
    @State private var model: String
    @State private var baseURL: String
    @State private var models: [String]
    @State private var status = ""
    @State private var working = false

    init(onSave: @escaping () -> Void) {
        self.onSave = onSave
        let s = AISettings.load()
        _kind = State(initialValue: s.kind)
        _key = State(initialValue: s.key ?? "")
        _model = State(initialValue: s.model)
        _baseURL = State(initialValue: s.baseURL)
        _models = State(initialValue: AISettings.savedModels(s.kind))
    }

    private var current: AISettings {
        AISettings(kind: kind, model: model.trimmingCharacters(in: .whitespaces),
                   baseURL: baseURL.trimmingCharacters(in: .whitespaces), key: key.trimmingCharacters(in: .whitespacesAndNewlines))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("AI Settings").font(.system(size: 20, weight: .semibold))
            Text("Use your own key. Questions and the history the AI looks up go straight from this Mac to the provider you pick.")
                .font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)

            field("Provider") {
                Picker("", selection: $kind) {
                    ForEach(AIProviderKind.allCases) { Text($0.title).tag($0) }
                }
                .labelsHidden()
                .onChange(of: kind) { select(kind) }
            }

            if kind == .custom {
                field("Server address") {
                    TextField("http://localhost:11434/v1", text: $baseURL).textFieldStyle(.roundedBorder)
                    Text("Ollama: http://localhost:11434/v1 · LM Studio: http://localhost:1234/v1 · OpenRouter: https://openrouter.ai/api/v1")
                        .font(.system(size: 11)).foregroundColor(SetupTheme.faint)
                }
            }

            field(kind.needsKey ? "API key" : "API key (if the server needs one)") {
                SecureField("Paste your key", text: $key).textFieldStyle(.roundedBorder)
                HStack {
                    Text("Saved in your Keychain.").font(.system(size: 11)).foregroundColor(SetupTheme.faint)
                    if let page = kind.keyPage { Link("Get a key", destination: page).font(.system(size: 11)) }
                }
            }

            field("Model") {
                HStack {
                    TextField("Model name", text: $model).textFieldStyle(.roundedBorder)
                    Menu("Choose") {
                        ForEach(models, id: \.self) { m in Button(m) { model = m } }
                    }
                    .fixedSize()
                    .disabled(models.isEmpty)
                    Button("Refresh List") { refresh() }.disabled(working)
                }
                Text("Type any model name, so new models work as soon as they come out. Refresh List asks the provider for its models.")
                    .font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
            }

            Spacer()
            if !status.isEmpty {
                Text(status).font(.system(size: 12)).foregroundColor(SetupTheme.dim).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                if working { ProgressView().controlSize(.small) }
                Spacer()
                Button("Test") { test() }.buttonStyle(PillButtonStyle()).disabled(working || !current.isReady)
                Button("Save") { save() }.buttonStyle(PillButtonStyle(primary: true))
            }
        }
        .padding(24).padding(.top, 14)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
    }

    private func field<V: View>(_ title: String, @ViewBuilder _ content: () -> V) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 12, weight: .semibold)).foregroundColor(SetupTheme.dim)
            content()
        }
    }

    private func select(_ k: AIProviderKind) {
        let s = AISettings.load(k)
        key = s.key ?? ""
        model = s.model
        baseURL = s.baseURL
        models = AISettings.savedModels(k)
        status = ""
    }

    private func save() {
        current.save()
        models = AISettings.savedModels(kind)
        status = "Saved."
        onSave()
    }

    private func refresh() {
        let s = current
        working = true
        status = "Asking \(s.kind.title) for its models…"
        Task { @MainActor in
            do {
                let list = try await AIClient(settings: s).listModels()
                AISettings.remember(models: list, for: s.kind)
                models = AISettings.savedModels(s.kind)
                status = list.isEmpty ? "The provider listed no models." : "Found \(list.count) models. Pick one from Choose."
            } catch {
                status = error.localizedDescription
            }
            working = false
        }
    }

    private func test() {
        let s = current
        working = true
        status = "Testing…"
        Task { @MainActor in
            do {
                let r = try await AIClient(settings: s).send(system: "Reply with the single word OK.",
                                                             messages: [AIMessage(role: .user, text: "Test")], tools: [])
                status = "It works. \(s.model) replied: \(r.text.prefix(80))"
            } catch {
                status = error.localizedDescription
            }
            working = false
        }
    }
}
