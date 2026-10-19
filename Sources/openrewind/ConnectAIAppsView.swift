import AppKit
import SwiftUI

/// Settings → Connect AI Apps: the switch that lets AI apps read the history (MCP), and the setup
/// to paste into Claude Desktop, Claude Code and Cursor, with this copy's path filled in.
struct ConnectAIAppsView: View {
    /// Shared with the Settings menu item, so either one shows the other's change.
    @AppStorage(MCPSetup.enabledKey) private var allowed = false
    @State private var copied: String?
    let home: URL

    /// What every screen about MCP says about what AI apps get (the Windows app and README say the same).
    static let whatTheyGet = "Blocked apps and sites are left out of screen text and screenshots; in the activity summary a blocked app shows only as “(blocked app)” with its time. Audio transcripts are not filtered by app. What the AI app reads goes to its AI provider. Text on your screen could try to trick it, and many AI apps can open links or run commands, so check what it does with your history."

    private var exe: String { MCPSetup.executable }
    private var json: String { MCPSetup.jsonConfig(exe: exe, home: MCPSetup.homeArg(home)) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Connect AI Apps").font(.system(size: 20, weight: .semibold))
                Text("Let Claude, Cursor and other AI apps that support MCP search your OpenRewind history. They ask OpenRewind on this Mac.")
                    .font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
                Text(Self.whatTheyGet)
                    .font(.system(size: 12)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)

                VStack(alignment: .leading, spacing: 4) {
                    Toggle("Allow AI Apps to Read My History (MCP)", isOn: $allowed).font(.system(size: 13, weight: .semibold))
                    Text("Off by default. While off, AI apps that you connected get a message asking you to turn this on. Screenshots are shared only if “Let AI look at screenshots” is on in AI Settings.")
                        .font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
                }

                // Opened from Downloads, macOS runs the app from a random temporary folder that AI apps cannot find later.
                if exe.contains("/AppTranslocation/") {
                    Text("Move OpenRewind to your Applications folder and open it from there first, so the path below stays the same.")
                        .font(.system(size: 12)).foregroundColor(SetupTheme.warn).fixedSize(horizontal: false, vertical: true)
                }

                snippet("Claude Desktop",
                        hint: "Paste into \(MCPSetup.claudeDesktopFile) (Settings → Developer → Edit Config), then quit and reopen Claude. If the file already has “mcpServers”, add just the openrewind part.",
                        code: json)
                snippet("Claude Code", hint: "Run once in Terminal:", code: MCPSetup.claudeCode(exe: exe, home: MCPSetup.homeArg(home)))
                snippet("Cursor", hint: "Paste into \(MCPSetup.cursorFile) (Cursor Settings → MCP), then restart Cursor.", code: json)

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
    }

    private func snippet(_ title: String, hint: String, code: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 14, weight: .semibold))
            Text(hint).font(.system(size: 11)).foregroundColor(SetupTheme.faint).fixedSize(horizontal: false, vertical: true)
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
}
