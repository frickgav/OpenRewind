import AppKit
import Combine
import SwiftUI

/// Settings → Connect AI Apps: the switch that lets AI apps read the history (MCP), one button per
/// AI app that adds OpenRewind to it (and takes it out again), and the setup to paste by hand into others.
struct ConnectAIAppsView: View {
    /// Shared with the Settings menu item, so either one shows the other's change.
    @AppStorage(MCPSetup.enabledKey) private var allowed = false
    @State private var copied: String?
    @State private var rows: [String: RowState] = [:]
    let home: URL

    /// What every screen about MCP says about what AI apps get (the Windows app and README say the same).
    static let whatTheyGet = "Blocked apps and sites are left out of screen text and screenshots; in the activity summary a blocked app shows only as “(blocked app)” with its time. Audio transcripts are not filtered by app. What the AI app reads goes to its AI provider. Text on your screen could try to trick it, and many AI apps can open links or run commands, so check what it does with your history."

    /// What one app's row shows: its setup now, and what the last click did.
    struct RowState {
        var link: MCPLink = .notAdded
        var problem: String?
        var running = false
        var message: String?
        var failed = false
        var busy = false
    }

    private var exe: String { MCPSetup.executable }
    private var homeArg: String? { MCPSetup.homeArg(home) }
    private var json: String { MCPSetup.jsonConfig(exe: exe, home: homeArg) }
    private var want: MCPCommandLine { MCPCommandLine(command: exe, args: MCPSetup.args(home: homeArg)) }
    private var links: [AIAppLink] {
        [JSONAppLink.claudeDesktop(want), ClaudeCodeLink(want: want), ChatGPTLink(want: want), JSONAppLink.cursor(want)]
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Connect AI Apps").font(.system(size: 20, weight: .semibold))
                Text("Let Claude, ChatGPT, Cursor and other AI apps that support MCP search your OpenRewind history. They ask OpenRewind on this Mac.")
                    .font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
                Text(Self.whatTheyGet)
                    .font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)

                VStack(alignment: .leading, spacing: 4) {
                    Toggle("Allow AI Apps to Read My History (MCP)", isOn: $allowed).font(.system(size: 13, weight: .semibold))
                    Text("Off by default; adding an app below turns it on. While off, AI apps that you connected get a message asking you to turn this on. Screenshots are shared only if “Let AI look at screenshots” is on in AI Settings.")
                        .font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
                }

                // Opened from Downloads, macOS runs the app from a random temporary folder that AI apps cannot find later.
                if exe.contains("/AppTranslocation/") {
                    Text("Move OpenRewind to your Applications folder and open it from there first, so AI apps can keep starting it.")
                        .font(.system(size: 12)).foregroundColor(SetupTheme.warn).fixedSize(horizontal: false, vertical: true)
                }

                ForEach(links) { link in row(link) }

                VStack(alignment: .leading, spacing: 6) {
                    Text("Other AI apps").font(.system(size: 14, weight: .semibold))
                    Text("Most MCP apps take these lines in their MCP settings, with the path of this OpenRewind:")
                        .font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
                    snippet("JSON", code: json)
                    Text("Claude Code by hand, in Terminal:")
                        .font(.system(size: 11)).foregroundColor(SetupTheme.faint)
                    snippet("Command", code: MCPSetup.claudeCode(exe: exe, home: homeArg))
                }

                HStack {
                    Spacer()
                    Button("Done") { SetupWindows.shared.close("mcp") }.buttonStyle(PillButtonStyle(primary: true))
                }
            }
            .padding(24).padding(.top, 14)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
        .onAppear { refresh(clearMessages: true) }
        // Back from Claude or a text editor: show what is set up now.
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didBecomeKeyNotification)) { _ in refresh(clearMessages: false) }
    }

    private func row(_ link: AIAppLink) -> some View {
        let s = rows[link.id] ?? RowState()
        let statusText: String = s.message ?? {
            switch s.link {
            case .added: return s.running ? "Added. Restart \(link.appName) if it doesn't list OpenRewind yet." : "Added."
            case .different: return "Set up for another copy of OpenRewind or with other options. Update points it at this one."
            case .unreadable: return s.problem ?? "OpenRewind can't change this app's setup. Set it up by hand below."
            case .notAdded: return "Not added."
            }
        }()
        let color: Color = s.message != nil && s.failed ? SetupTheme.warn
            : s.link == .added ? SetupTheme.good : s.link == .unreadable ? SetupTheme.warn : SetupTheme.dim
        let addTitle: String = s.link == .added ? "Added ✓" : s.link == .different ? "Update" : "Add to \(link.appName)"
        return VStack(alignment: .leading, spacing: 6) {
            Text(link.title).font(.system(size: 14, weight: .semibold))
            Text(link.about).font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
            Text(s.busy ? "Working…" : statusText)
                .font(.system(size: 12)).foregroundColor(s.busy ? SetupTheme.dim : color)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Button(addTitle) { act(link, add: true) }
                    .buttonStyle(PillButtonStyle(primary: s.link != .added))
                    .disabled(s.busy || s.link == .added || s.link == .unreadable)
                    .opacity(s.busy || s.link == .unreadable ? 0.5 : 1)
                if s.link == .added || s.link == .different {
                    Button("Remove") { act(link, add: false) }.buttonStyle(PillButtonStyle()).disabled(s.busy)
                }
                if s.link == .added && s.running {
                    Button("Restart \(link.appName)") { restart(link) }.buttonStyle(PillButtonStyle()).disabled(s.busy)
                }
            }
        }
    }

    /// Reads every app's setup again and shows it.
    private func refresh(clearMessages: Bool) {
        for link in links {
            var s = rows[link.id] ?? RowState()
            if s.busy { continue }
            let (l, problem) = link.status()
            if clearMessages || l != s.link { s.message = nil }
            s.link = l
            s.problem = problem
            s.running = l == .added && !link.running.isEmpty
            rows[link.id] = s
        }
    }

    /// Adds or removes in the background (Claude Code's command can take a few seconds), then shows how it went.
    private func act(_ link: AIAppLink, add: Bool) {
        guard rows[link.id]?.busy != true else { return }
        rows[link.id, default: RowState()].busy = true
        if add && !allowed {
            // Adding an app is asking for it to work: turn on the switch it needs.
            allowed = true
            log("AI apps (MCP) allowed")
        }
        DispatchQueue.global(qos: .userInitiated).async {
            let (message, failed) = Self.run(link, add: add)
            Task { @MainActor in done(link, message: message, failed: failed) }
        }
    }

    /// What to show after adding or removing, and whether it failed.
    nonisolated private static func run(_ link: AIAppLink, add: Bool) -> (String, Bool) {
        do { return (try add ? link.add() : link.remove(), false) } catch let e as MCPConfigError {
            return (e.message, true)
        } catch {
            log("connect: \(link.title): \(error.localizedDescription)")
            return ("Couldn't save the change: \(error.localizedDescription)", true)
        }
    }

    private func restart(_ link: AIAppLink) {
        link.restart()
        done(link, message: "Restarting \(link.appName)…", failed: false)
    }

    /// Shows the setup as it is now, with what just happened under the app's name.
    private func done(_ link: AIAppLink, message: String, failed: Bool) {
        rows[link.id, default: RowState()].busy = false
        refresh(clearMessages: false)
        rows[link.id, default: RowState()].message = message
        rows[link.id, default: RowState()].failed = failed
    }

    private func snippet(_ title: String, code: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text(code)
                .font(.system(size: 11, design: .monospaced))
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
                .background(RoundedRectangle(cornerRadius: 8).fill(SetupTheme.card))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(SetupTheme.stroke))
            Button(copied == title ? "Copied" : "Copy") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(code, forType: .string)
                copied = title
            }
            .buttonStyle(PillButtonStyle())
        }
    }
}
