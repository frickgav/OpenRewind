import AppKit
import SwiftUI

/// Turning on Screen Recording or Accessibility, made easy: opens the right pane of System Settings
/// and floats a small card beside it with OpenRewind's icon to drag into the list. The card follows
/// the Settings window and closes by itself once the permission is on.
final class PermissionHelper {
    static let shared = PermissionHelper()

    private var panel: NSPanel?
    private var timer: Timer?
    private let model = PermissionHelperModel()
    private var permission: Permission = .screen
    private var lastSeen = Date()
    private var doneAt: Date?

    /// Ask for `p` and guide the user to it. Microphone has no list to drop into, so it only opens Settings.
    func show(_ p: Permission) {
        p.request()
        NSWorkspace.shared.open(p.settingsURL)
        // A drag only helps from the real .app (not `swift run`), and only for lists that take one.
        guard p != .microphone, !p.granted, Bundle.main.bundleURL.pathExtension == "app" else { return }
        permission = p
        model.title = p.title
        model.done = false
        doneAt = nil
        lastSeen = Date()
        if panel == nil { panel = makePanel() }
        panel?.alphaValue = 0  // shown once the Settings window is found
        panel?.orderFrontRegardless()
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            self?.tick()
        }
    }

    func close() {
        timer?.invalidate(); timer = nil
        panel?.orderOut(nil)
    }

    private func tick() {
        guard let panel else { return }
        if permission.granted, doneAt == nil {
            doneAt = Date()
            withAnimation { model.done = true }
        }
        if let doneAt, Date().timeIntervalSince(doneAt) > 1.5 { close(); return }

        guard let settings = Self.settingsWindowFrame() else {
            // Settings is still opening, or the user closed it.
            if Date().timeIntervalSince(lastSeen) > 15 { close() }
            return
        }
        lastSeen = Date()
        let size = panel.frame.size
        let screen = NSScreen.screens.first { $0.frame.intersects(settings) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? settings
        // Below the window, under the list on the right side of Settings. Beside it when there's no room.
        var origin = NSPoint(x: settings.midX - size.width / 2 + settings.width * 0.15, y: settings.minY - size.height + 6)
        var edge = Edge.top
        if origin.y < visible.minY {
            edge = .leading
            origin = NSPoint(x: settings.maxX - 6, y: settings.midY - size.height / 2)
            if origin.x + size.width > visible.maxX { origin.x = settings.maxX - size.width - 24 }
        }
        origin.x = min(max(origin.x, visible.minX), visible.maxX - size.width)
        if model.edge != edge { model.edge = edge }
        if panel.frame.origin != origin { panel.setFrameOrigin(origin) }
        if panel.alphaValue < 1 { panel.animator().alphaValue = 1 }
    }

    /// System Settings' main window in AppKit coordinates (bottom-left origin).
    private static func settingsWindowFrame() -> NSRect? {
        guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return nil }
        for w in info {
            let owner = w[kCGWindowOwnerName as String] as? String ?? ""
            guard owner == "System Settings" || owner == "System Preferences",
                  (w[kCGWindowLayer as String] as? Int ?? 1) == 0,
                  let b = w[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: b as CFDictionary), r.width > 300, r.height > 200 else { continue }
            let primary = NSScreen.screens.first?.frame.height ?? r.maxY
            return NSRect(x: r.minX, y: primary - r.maxY, width: r.width, height: r.height)
        }
        return nil
    }

    private func makePanel() -> NSPanel {
        let p = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 150),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        p.level = .floating
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = false
        p.hidesOnDeactivate = false
        p.isMovableByWindowBackground = false
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.contentView = NSHostingView(rootView: PermissionHelperView(model: model) { [weak self] in self?.close() })
        return p
    }
}

final class PermissionHelperModel: ObservableObject {
    @Published var title = ""
    @Published var done = false
    /// Which side of the card points at System Settings.
    @Published var edge: Edge = .top
}

private struct PermissionHelperView: View {
    @ObservedObject var model: PermissionHelperModel
    var close: () -> Void
    @State private var bounce = false

    var body: some View {
        Group {
            if model.edge == .top {
                VStack(spacing: 0) { arrow.rotationEffect(.degrees(-90)).offset(y: bounce ? -6 : 0); card }
            } else {
                HStack(spacing: 0) { arrow.rotationEffect(.degrees(180)).offset(x: bounce ? -6 : 0); card }
            }
        }
        .frame(width: 360, height: 150)
        .onAppear { withAnimation(.easeInOut(duration: 0.6).repeatForever()) { bounce = true } }
    }

    private var arrow: some View {
        Image(systemName: "arrow.right")
            .font(.system(size: 22, weight: .bold))
            .foregroundColor(SetupTheme.accent)
            .frame(width: 30, height: 30)
            .opacity(model.done ? 0 : 1)
    }

    private var card: some View {
        HStack(spacing: 14) {
            if model.done {
                Image(systemName: "checkmark.circle.fill").font(.system(size: 40)).foregroundColor(SetupTheme.good)
                Text("\(model.title) is on").font(.system(size: 15, weight: .semibold))
                Spacer()
            } else {
                VStack(spacing: 4) {
                    AppDragIcon().frame(width: 56, height: 56)
                    Text("OpenRewind").font(.system(size: 10)).foregroundColor(SetupTheme.faint)
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text("Drag OpenRewind into the list").font(.system(size: 14, weight: .semibold))
                    Text("Then turn its switch on for \(model.title). This closes by itself when it's done.")
                        .font(.system(size: 12)).foregroundColor(SetupTheme.dim)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 0)
            }
        }
        .foregroundColor(.white)
        .padding(.horizontal, 16).padding(.vertical, 12)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(RoundedRectangle(cornerRadius: 14).fill(SetupTheme.bg))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(SetupTheme.accent.opacity(0.7), lineWidth: 1.5))
        .overlay(alignment: .topTrailing) {
            Button(action: close) { Image(systemName: "xmark").font(.system(size: 10, weight: .bold)) }
                .buttonStyle(.plain).foregroundColor(SetupTheme.faint).padding(8)
                .help("Close")
        }
        .shadow(color: .black.opacity(0.35), radius: 10, y: 4)
        .padding(8)
    }
}

/// OpenRewind's own icon, dragged as the app file so System Settings adds it to the list.
private struct AppDragIcon: NSViewRepresentable {
    func makeNSView(context: Context) -> DragIconView { DragIconView() }
    func updateNSView(_ v: DragIconView, context: Context) {}
}

final class DragIconView: NSView, NSDraggingSource {
    private let icon = NSWorkspace.shared.icon(forFile: Bundle.main.bundlePath)

    override func draw(_ dirtyRect: NSRect) { icon.draw(in: bounds) }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .openHand) }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func mouseDragged(with event: NSEvent) {
        let item = NSDraggingItem(pasteboardWriter: Bundle.main.bundleURL as NSURL)
        item.setDraggingFrame(bounds, contents: icon)
        beginDraggingSession(with: [item], event: event, source: self)
    }

    func draggingSession(_ session: NSDraggingSession, sourceOperationMaskFor context: NSDraggingContext) -> NSDragOperation {
        context == .outsideApplication ? [.copy, .generic, .link] : []
    }
}
