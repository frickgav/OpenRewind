import AppKit
import SwiftUI

/// Shared look for the overlay, in a dark and a light version.
/// The window appearance picks one (menu bar → Settings → Timeline Look).
struct Theme {
    let dark: Bool
    init(_ scheme: ColorScheme) { dark = scheme == .dark }

    /// Black or white, the color that stands out on this background.
    private var ink: Color { dark ? .white : .black }
    var text: Color { dark ? .white : Color(white: 0.1) }
    var soft: Color { ink.opacity(0.8) }
    var dim: Color { ink.opacity(0.6) }
    var faint: Color { ink.opacity(dark ? 0.42 : 0.48) }
    var surface: Color { ink.opacity(dark ? 0.08 : 0.06) }
    var surfaceHi: Color { ink.opacity(dark ? 0.14 : 0.1) }
    var selected: Color { ink.opacity(dark ? 0.22 : 0.14) }
    var stroke: Color { ink.opacity(0.12) }
    var strokeHi: Color { ink.opacity(dark ? 0.35 : 0.3) }
    /// Tint over the blurred backdrop.
    var scrim: Color { dark ? .black.opacity(0.55) : .white.opacity(0.5) }
    /// Solid cards: results list, help, notices, hover preview.
    var card: Color { dark ? Color(white: 0.12) : Color(white: 0.97) }
    var shadow: Color { .black.opacity(dark ? 0.5 : 0.2) }
    /// Thin ring around the app color dot.
    var ring: Color { dark ? .black.opacity(0.6) : .white.opacity(0.9) }
    /// Search words inside snippets. Plain yellow is hard to read on white.
    var match: Color { dark ? .yellow : Color(red: 0.72, green: 0.42, blue: 0) }
    /// The playhead line and its time bubble.
    var playhead: Color { dark ? .white : .black }
    var playheadText: Color { dark ? .black : .white }
    /// Shrunk breaks and the area after now on the bar.
    var shade: Color { .black.opacity(dark ? 0.4 : 0.08) }
    /// Warning icon. Plain yellow is hard to see on a light background.
    var warn: Color { dark ? .yellow : .orange }
    let mic = Color.orange
    let system = Color.green
}

struct TimelineView: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel
    var close: () -> Void
    /// Ask AI, shown beside the timeline when model.showAI is on.
    var chat: AIChatModel? = nil
    @FocusState private var searchFocused: Bool

    var body: some View {
        ZStack {
            Backdrop().ignoresSafeArea()
            theme.scrim.ignoresSafeArea()
            HStack(spacing: 0) {
            VStack(spacing: 14) {
                topBar
                if let gap = staleGap { staleBanner(gap) }
                UpdateBanner()
                ZStack(alignment: .topLeading) {
                    screenshot
                    if model.showResults { results.padding(.top, 2) }
                }
                controls
                TimelineBar(model: model).frame(height: 102)
                hints
            }
            .padding(.horizontal, 28)
            .padding(.top, 22)
            .padding(.bottom, 16)
            if model.showAI, let chat {
                AIChatView(model: chat) { model.showAI = false }
                    .frame(width: 420)
                    .clipShape(RoundedRectangle(cornerRadius: 14))
                    .overlay(RoundedRectangle(cornerRadius: 14).stroke(theme.stroke))
                    .shadow(color: theme.shadow, radius: 16)
                    .padding(.vertical, 16).padding(.trailing, 16)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
            }
            }
            if let n = model.notice {
                VStack {
                    Label(n, systemImage: "info.circle")
                        .font(.system(size: 13, weight: .medium))
                        .padding(.horizontal, 14).padding(.vertical, 8)
                        .background(Capsule().fill(theme.card))
                        .overlay(Capsule().stroke(theme.stroke))
                        .shadow(color: theme.shadow, radius: 10)
                        .padding(.top, 74)
                    Spacer()
                }
                .allowsHitTesting(false)
            }
            if model.showHelp { helpCard }
            if let clip = model.exportClip { ExportClipCard(model: model, range: clip).id(clip.id) }
            if model.tutorialStep != nil { TutorialCoach(model: model) { model.tutorialStep = nil } }
        }
        .foregroundColor(theme.text)
        .onChange(of: model.focusSearchToken) { _ in searchFocused = true }
    }

    // MARK: top bar

    /// Search on the left, the playhead time in the middle, help and close on the right.
    private var topBar: some View {
        ZStack {
            HStack(spacing: 10) {
                searchBar.frame(maxWidth: 460)
                if model.meaningAvailable { SearchModeSwitch(model: model) }
                StarredFilterButton(model: model)
                AppFilterMenu(model: model)
                Spacer(minLength: 220)
                if chat != nil { askAIButton }
                StarButton(model: model)
                FrameMenu(model: model)
                IconButton(symbol: "questionmark", help: "Keyboard shortcuts (?)") { model.showHelp.toggle() }
                IconButton(symbol: "xmark", help: "Close (esc)", action: close)
            }
            VStack(spacing: 1) {
                Text(model.playhead, format: .dateTime.hour().minute().second())
                    .font(.system(size: 28, weight: .semibold, design: .rounded)).monospacedDigit()
                Text(relative(model.playhead)).font(.system(size: 12)).foregroundColor(theme.dim)
            }
            .allowsHitTesting(false)
        }
    }

    private var askAIButton: some View {
        Button {
            if !model.showAI { chat?.reloadSettings() }
            withAnimation(.easeOut(duration: 0.18)) { model.showAI.toggle() }
        } label: {
            Label("Ask AI", systemImage: "sparkles")
                .font(.system(size: 12, weight: .semibold))
                .padding(.horizontal, 12).frame(height: 30)
                .background(Capsule().fill(model.showAI ? theme.selected : theme.surfaceHi))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help("Ask about what you did, saw or heard")
    }

    // MARK: stale warning

    /// Minutes since the newest screenshot, when that is long enough to look like a gap.
    private var staleGap: Int? {
        guard let last = model.newest else { return nil }
        let mins = Int(Date().timeIntervalSince(last.time) / 60)
        return mins >= 3 ? mins : nil
    }

    private func staleBanner(_ mins: Int) -> some View {
        let ago = mins >= 60 ? "\(mins / 60) h \(mins % 60) min" : "\(mins) min"
        let why = !Capture.hasScreenPermission
            ? "Screen recording is blocked by macOS. Menu bar → Screen permission needed."
            : "Screen recording may be off, or the screen has not changed."
        return HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundColor(theme.warn)
            Text("Newest screenshot is from \(ago) ago. \(why)").font(.system(size: 13))
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.yellow.opacity(0.15)))
    }

    // MARK: search

    private var searchPrompt: String { model.searchingByMeaning ? "Describe what you saw or heard" : "Search what you saw or heard" }

    private var searchBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass").foregroundColor(theme.dim)
            TextField(searchPrompt, text: $model.query)
                .textFieldStyle(.plain)
                .font(.system(size: 15))
                .focused($searchFocused)
                .onChange(of: model.query) { _ in model.queryChanged() }
                .onSubmit { model.openSelected(); searchFocused = false }
            if model.query.isEmpty {
                if !searchFocused { Keycap("/") }
            } else {
                if !model.hits.isEmpty && !model.showResults {
                    Text("\(model.selectedHit + 1) of \(model.hits.count)")
                        .font(.system(size: 12)).foregroundColor(theme.dim).monospacedDigit()
                    Button { model.nextHit(older: false) } label: { Image(systemName: "chevron.up") }
                        .buttonStyle(.plain).help("Newer match (⇧⌘G)")
                    Button { model.nextHit(older: true) } label: { Image(systemName: "chevron.down") }
                        .buttonStyle(.plain).help("Older match (⌘G)")
                    Button("List") { model.showResults = true; searchFocused = true }
                        .buttonStyle(.plain).font(.system(size: 12, weight: .medium))
                } else if model.hits.isEmpty && !model.showResults && !model.searching {
                    Text("No matches").font(.system(size: 12)).foregroundColor(theme.faint)
                }
                Button { model.clearSearch() } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundColor(theme.faint)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(Capsule().fill(searchFocused ? theme.surfaceHi : theme.surface))
        .overlay(Capsule().stroke(searchFocused ? theme.strokeHi : theme.stroke))
        .contentShape(Capsule())
        .onTapGesture { searchFocused = true }
    }

    private var results: some View {
        VStack(alignment: .leading, spacing: 0) {
            if model.hits.isEmpty {
                Text(model.searching ? "Searching by meaning…"
                     : model.starredOnly ? (model.query.isEmpty ? "No starred moments yet. Press S here, or \(StarShortcut.label) anywhere, to star one." : "No starred moments match “\(model.query)”")
                     : "No matches for “\(model.query)”").foregroundColor(theme.dim).padding(16)
            } else {
                HStack(spacing: 6) {
                    Text("\(model.hits.count) \(model.starredOnly ? "starred " : "")moment\(model.hits.count == 1 ? "" : "s")" + (model.searchingByMeaning && !model.starredOnly ? " · closest in meaning first" : ""))
                    Spacer()
                    Keycap("↑"); Keycap("↓"); Text("choose")
                    Keycap("return").padding(.leading, 6); Text("go")
                }
                .font(.system(size: 11)).foregroundColor(theme.faint)
                .padding(.horizontal, 14).padding(.top, 10).padding(.bottom, 4)
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(Array(model.hits.enumerated()), id: \.element.id) { i, h in
                                hitRow(h, selected: i == model.selectedHit)
                                    .id(i)
                                    .onTapGesture { model.selectedHit = i; model.openSelected(); searchFocused = false }
                            }
                        }
                        .padding(6)
                    }
                    .onChange(of: model.selectedHit) { i in withAnimation(.easeOut(duration: 0.1)) { proxy.scrollTo(i) } }
                }
            }
        }
        .frame(maxWidth: 640, maxHeight: 440, alignment: .top)
        .fixedSize(horizontal: false, vertical: model.hits.count < 6)
        .background(RoundedRectangle(cornerRadius: 12).fill(theme.card))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(theme.stroke))
        .shadow(color: theme.shadow, radius: 20)
    }

    private func hitRow(_ h: SearchHit, selected: Bool) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Group {
                if h.kind == .star {
                    Image(systemName: "star.fill").foregroundColor(.yellow)
                } else if h.kind == .screen, let icon = AppIcons.icon(for: h.title) {
                    Image(nsImage: icon).resizable().frame(width: 18, height: 18)
                } else {
                    Image(systemName: h.kind == .audio ? "waveform" : "display")
                        .foregroundColor(h.kind == .audio ? theme.system : theme.dim)
                }
            }
            .frame(width: 20).padding(.top, 1)
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(h.title).font(.system(size: 13, weight: .semibold))
                    Spacer()
                    Text(h.time, format: .dateTime.month(.abbreviated).day().hour().minute().second())
                        .font(.system(size: 11)).foregroundColor(theme.faint).monospacedDigit()
                }
                highlighted(h.snippet).font(.system(size: 12)).foregroundColor(theme.soft).lineLimit(2)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Color.accentColor.opacity(0.35) : Color.clear))
        .contentShape(Rectangle())
    }

    /// Bold + yellow the searched words inside a snippet.
    private func highlighted(_ text: String) -> Text {
        // A meaning match need not contain the typed words, so none are marked.
        let words = model.searchingByMeaning ? [] : model.query.lowercased().split(separator: " ").map(String.init).filter { !$0.isEmpty }
        var out = Text("")
        var rest = Substring(text)
        while !rest.isEmpty {
            let lower = rest.lowercased()
            var best: (Range<String.Index>, Int)? = nil
            for w in words {
                if let r = lower.range(of: w) {
                    let d = lower.distance(from: lower.startIndex, to: r.lowerBound)
                    if best == nil || d < best!.1 { best = (r, d) }
                }
            }
            guard let (r, d) = best else { out = out + Text(String(rest)); break }
            let len = lower.distance(from: r.lowerBound, to: r.upperBound)
            let a = rest.index(rest.startIndex, offsetBy: d)
            let b = rest.index(a, offsetBy: len)
            out = out + Text(String(rest[..<a])) + Text(String(rest[a..<b])).bold().foregroundColor(theme.match)
            rest = rest[b...]
        }
        return out
    }

    // MARK: screenshot

    /// The screenshot, with the spoken words at this moment shown like subtitles.
    private var screenshot: some View {
        GeometryReader { geo in
            ZStack(alignment: .bottom) {
                if let img = model.image {
                    Image(nsImage: img)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .overlay { liveText(img) }
                        .overlay(highlightBoxes)
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                        .overlay(RoundedRectangle(cornerRadius: 12).stroke(theme.stroke))
                        .shadow(color: theme.shadow, radius: 24)
                        .frame(width: geo.size.width, height: geo.size.height)
                } else {
                    emptyState.frame(width: geo.size.width, height: geo.size.height)
                }
                if let target = model.currentTarget, let f = model.current {
                    openButton(target, f)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                        .padding(.trailing, 18).padding(.bottom, 18)
                }
                if let c = model.caption {
                    Text(c)
                        .font(.system(size: 17, weight: .medium))
                        .multilineTextAlignment(.center)
                        .lineLimit(3)
                        .foregroundColor(.white)
                        .padding(.horizontal, 16).padding(.vertical, 9)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.black.opacity(0.78)))
                        .frame(maxWidth: min(900, geo.size.width - 80))
                        .padding(.bottom, 22)
                }
            }
        }
    }

    /// Select and copy text right on the screenshot (not on ones from apps or sites blocked now).
    @ViewBuilder private func liveText(_ img: NSImage) -> some View {
        if let f = model.current, LiveTextOverlay.isSupported, !model.currentHidden {
            LiveTextOverlay(model: model, frameId: f.id, image: img)
        }
    }

    /// A yellow box around each place the searched words appear on the screenshot.
    private var highlightBoxes: some View {
        GeometryReader { g in
            ForEach(Array(model.highlights.enumerated()), id: \.offset) { _, h in
                let r = CGRect(x: h.minX * g.size.width, y: h.minY * g.size.height,
                               width: h.width * g.size.width, height: h.height * g.size.height).insetBy(dx: -3, dy: -2)
                RoundedRectangle(cornerRadius: 4)
                    .fill(Color.yellow.opacity(0.28))
                    .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.yellow, lineWidth: 2))
                    .frame(width: r.width, height: r.height)
                    .position(x: r.midX, y: r.midY)
            }
        }
        .allowsHitTesting(false)
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: model.frames.isEmpty ? "record.circle" : "photo")
                .font(.system(size: 36, weight: .light)).foregroundColor(theme.faint)
            Text(model.frames.isEmpty ? "Nothing recorded yet" : "Screenshot not found")
                .font(.system(size: 16, weight: .semibold))
            Text(model.frames.isEmpty ? "Turn on screen recording in the menu bar." : "The file for this moment was deleted or moved.")
                .font(.system(size: 13)).foregroundColor(theme.dim)
        }
        .padding(28)
        .background(RoundedRectangle(cornerRadius: 14).fill(theme.surface))
    }

    // MARK: controls row

    /// App on the left, audio in the middle, live + zoom on the right.
    private var controls: some View {
        HStack(spacing: 12) {
            appBadge.frame(maxWidth: .infinity, alignment: .leading)
            audioButtons.layoutPriority(1)
            HStack(spacing: 10) {
                liveButton
                zoomPicker
            }
            .frame(maxWidth: .infinity, alignment: .trailing)
        }
        .frame(height: 36)
    }

    private var appBadge: some View {
        HStack(spacing: 10) {
            if let f = model.current {
                ZStack(alignment: .bottomTrailing) {
                    if let icon = AppIcons.icon(for: f.appName) {
                        Image(nsImage: icon).resizable().frame(width: 28, height: 28)
                    } else {
                        RoundedRectangle(cornerRadius: 7).fill(theme.surfaceHi).frame(width: 28, height: 28)
                            .overlay(Image(systemName: "app").foregroundColor(theme.dim))
                    }
                    // Same color as this app's blocks on the bar below.
                    Circle().fill(Color(TimelineModel.color(for: f.appName))).frame(width: 9, height: 9)
                        .overlay(Circle().stroke(theme.ring, lineWidth: 1.5))
                        .offset(x: 2, y: 2)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(f.appName.isEmpty ? "Unknown app" : f.appName).font(.system(size: 13, weight: .semibold))
                    if !f.windowName.isEmpty {
                        Text(f.windowName).font(.system(size: 12)).foregroundColor(theme.dim)
                    }
                }
                .lineLimit(1)
            }
        }
    }

    private var audioButtons: some View {
        HStack(spacing: 8) {
            if model.audioHere.isEmpty {
                Label("No audio at this moment", systemImage: "speaker.slash")
                    .font(.system(size: 12)).foregroundColor(theme.faint)
            }
            ForEach(model.audioHere, id: \.id) { a in
                let on = model.playingChunk == a.id
                Button { on ? model.stopAudio() : model.play(a) } label: {
                    HStack(spacing: 7) {
                        Image(systemName: on ? "pause.fill" : "play.fill").font(.system(size: 11))
                        Text(a.isInput ? "Microphone" : "System audio")
                        // Matches the audio lane color on the bar.
                        Circle().fill(a.isInput ? theme.mic : theme.system).frame(width: 7, height: 7)
                    }
                    .font(.system(size: 12, weight: .medium))
                    .padding(.horizontal, 13).padding(.vertical, 7)
                    .background(Capsule().fill(on ? Color.accentColor : theme.surfaceHi))
                    .foregroundColor(on ? .white : theme.text)
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .help(on ? "Pause (space)" : "Play from here (space)")
            }
        }
    }

    /// Goes back to what was on screen at this moment: the browser tab, the document, or the app window.
    private func openButton(_ target: TimelineModel.OpenTarget, _ f: TimelineFrame) -> some View {
        let (title, detail, help): (String, String, String) = {
            switch target {
            case .web(let url):
                return ("Open this tab in browser", url.host ?? url.lastPathComponent, "Open this tab again: \(url.absoluteString)")
            case .file(let url):
                return ("Open in \(f.appName)", url.lastPathComponent, "Open this file again: \(url.path)")
            case .window:
                return ("Go to \(f.appName)", f.windowName.isEmpty ? f.appName : f.windowName,
                        "Open \(f.appName) and bring this window to the front if it is still open")
            }
        }()
        return Button { model.openCurrent() } label: {
            HStack(spacing: 8) {
                Image(systemName: "arrow.up.forward.app").font(.system(size: 14, weight: .semibold))
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.system(size: 14, weight: .semibold)).lineLimit(1)
                    Text(detail).font(.system(size: 11))
                        .opacity(0.75).lineLimit(1).truncationMode(.middle)
                }
                Keycap("O")
            }
            .padding(.horizontal, 16).padding(.vertical, 9)
            .frame(maxWidth: 340)
            .foregroundColor(.white)
            .background(Capsule().fill(Color.accentColor))
            .shadow(color: theme.shadow, radius: 10)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .help("\(help) (O)")
    }

    private var liveButton: some View {
        Button { model.jumpToNow() } label: {
            HStack(spacing: 6) {
                if model.isLive {
                    Circle().fill(Color.red).frame(width: 7, height: 7)
                    Text("Live")
                } else {
                    Text("Now")
                    Image(systemName: "forward.end.fill").font(.system(size: 10))
                }
            }
            .font(.system(size: 12, weight: .semibold))
            .padding(.horizontal, 12).padding(.vertical, 6)
            .background(Capsule().fill(model.isLive ? theme.surface : Color.accentColor))
            .foregroundColor(model.isLive ? theme.dim : .white)
            .contentShape(Capsule())
        }
        .buttonStyle(.plain)
        .disabled(model.isLive)
        .help("Go to the newest moment (N)")
    }

    private static let zoomSteps: [(String, TimeInterval)] =
        [("5m", 300), ("15m", 900), ("1h", 3600), ("4h", 4 * 3600), ("12h", 12 * 3600)]

    private var zoomPicker: some View {
        HStack(spacing: 2) {
            ForEach(Self.zoomSteps, id: \.0) { label, span in
                let on = abs(model.span - span) < 1
                Button { withAnimation(.easeOut(duration: 0.15)) { model.span = span } } label: {
                    Text(label)
                        .font(.system(size: 12, weight: on ? .semibold : .regular))
                        .frame(minWidth: 34).padding(.vertical, 5)
                        .background(Capsule().fill(on ? theme.selected : Color.clear))
                        .foregroundColor(on ? theme.text : theme.dim)
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(Capsule().fill(theme.surface))
        .help("Time shown across the bar (+ and − also zoom)")
    }

    // MARK: hints and help

    private var hints: some View {
        HStack(spacing: 14) {
            hint(["←", "→"], "move")
            hint(["space"], "play audio")
            hint(["/"], "search")
            hint(["+", "−"], "zoom")
            hint(["O"], "open")
            hint(["S"], "star")
            hint(["[", "]"], "mark clip")
            hint(["?"], "all keys")
            hint(["esc"], "close")
        }
        .font(.system(size: 11)).foregroundColor(theme.faint)
    }

    private func hint(_ keys: [String], _ what: String) -> some View {
        HStack(spacing: 4) {
            ForEach(keys, id: \.self) { Keycap($0) }
            Text(what)
        }
    }

    private static let shortcuts: [(String, [[String]], String)] = [
        ("Move", [["←"], ["→"]], "Previous / next screenshot"),
        ("", [["⇧", "←"], ["⇧", "→"]], "Back / forward 1 minute"),
        ("", [["↑"], ["↓"]], "Back / forward 10 minutes"),
        ("", [["scroll"]], "Scroll the trackpad or wheel"),
        ("", [["N"]], "Go to now"),
        ("Open", [["O"]], "Open the tab, document or window from this moment"),
        ("Star", [["S"]], "Star this moment / remove its star"),
        ("Filter", [["A"]], "Only this app / all apps again"),
        ("Copy", [["⌘", "C"]], "Copy the selected text, or the screenshot"),
        ("", [["drag"]], "Select text on the screenshot"),
        ("", [["⌘", "S"]], "Save the screenshot as a PNG"),
        ("Clip", [["["], ["]"]], "Mark where a clip starts / ends"),
        ("", [["⌘", "E"]], "Export a clip as MP4 or GIF"),
        ("Audio", [["space"]], "Play or pause audio from here"),
        ("Search", [["/"], ["⌘", "F"]], "Search screen text and speech"),
        ("", [["⌘", "G"], ["⇧", "⌘", "G"]], "Older / newer match"),
        ("View", [["+"], ["−"]], "Zoom the bar in / out"),
        ("", [["esc"]], "Close the list, then the overlay"),
    ]

    private var helpCard: some View {
        ZStack {
            theme.scrim.ignoresSafeArea().onTapGesture { model.showHelp = false }
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text("Keyboard shortcuts").font(.system(size: 17, weight: .semibold))
                    Spacer()
                    IconButton(symbol: "xmark", help: "Close (esc)") { model.showHelp = false }
                }
                Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 9) {
                    ForEach(Array(Self.shortcuts.enumerated()), id: \.offset) { _, row in
                        GridRow {
                            Text(row.0).font(.system(size: 11, weight: .semibold)).foregroundColor(theme.faint)
                                .textCase(.uppercase)
                            HStack(spacing: 6) {
                                ForEach(Array(row.1.enumerated()), id: \.offset) { i, combo in
                                    if i > 0 { Text("/").foregroundColor(theme.faint) }
                                    HStack(spacing: 3) { ForEach(combo, id: \.self) { Keycap($0, large: true) } }
                                }
                            }
                            Text(row.2).font(.system(size: 13)).foregroundColor(theme.soft)
                        }
                    }
                }
                Text("Drag or click the bar to move. Hover it to preview.\nThe share button at the top has copy, save and export too.")
                    .font(.system(size: 12)).foregroundColor(theme.dim)
            }
            .padding(24)
            .frame(width: 560)
            .background(RoundedRectangle(cornerRadius: 16).fill(theme.card))
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(theme.stroke))
            .shadow(color: theme.shadow, radius: 30)
        }
    }

    private func relative(_ d: Date) -> String {
        let s = Date().timeIntervalSince(d)
        if s < 60 { return "just now" }
        let day = d.formatted(.dateTime.weekday(.wide).month().day())
        if s < 3600 { return "\(Int(s / 60)) min ago · \(day)" }
        return "\(Int(s / 3600)) h \(Int(s.truncatingRemainder(dividingBy: 3600) / 60)) min ago · \(day)"
    }
}

// MARK: - small parts

/// A key drawn like a keyboard key.
struct Keycap: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    let key: String
    var large = false
    init(_ key: String, large: Bool = false) { self.key = key; self.large = large }

    var body: some View {
        Text(key)
            .font(.system(size: large ? 12 : 10, weight: .medium, design: .rounded))
            .foregroundColor(theme.soft)
            .padding(.horizontal, large ? 7 : 5).padding(.vertical, large ? 3 : 1.5)
            .frame(minWidth: large ? 24 : 18)
            .background(RoundedRectangle(cornerRadius: 4).fill(theme.surfaceHi))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(theme.stroke))
    }
}

/// "Keyword | Meaning" next to search, shown while Search by Meaning is on.
struct SearchModeSwitch: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel

    var body: some View {
        HStack(spacing: 0) {
            segment("Keyword", on: !model.byMeaning) { model.setByMeaning(false) }
            segment("Meaning", on: model.byMeaning) { model.setByMeaning(true) }
        }
        .padding(3)
        .background(Capsule().fill(theme.surface))
        .overlay(Capsule().stroke(theme.stroke))
        .fixedSize()
        .help("Keyword finds the exact words. Meaning finds moments about the same thing, even in other words.")
    }

    private func segment(_ title: String, on: Bool, _ act: @escaping () -> Void) -> some View {
        Button(action: act) {
            Text(title).font(.system(size: 12, weight: .semibold))
                .foregroundColor(on ? theme.text : theme.dim)
                .padding(.horizontal, 12).frame(height: 28)
                .background(Capsule().fill(on ? Color.accentColor.opacity(0.35) : Color.clear))
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }
}

/// Copy, save and export, as a round button in the top bar.
struct FrameMenu: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel

    var body: some View {
        Menu {
            Button("Copy Screenshot (or selected text)") { model.copy() }
            Button("Copy All Text") { model.copyAllText() }
            Button("Save Screenshot…") { model.saveFrame() }
            Divider()
            Button("Mark Clip Start Here") { model.markStart() }
            Button("Mark Clip End Here") { model.markEnd() }
            if model.hasMarks { Button("Clear Clip Marks") { model.clearMarks() } }
            Button("Export Clip…") { model.openExport() }
        } label: {
            Image(systemName: "square.and.arrow.up")
                .font(.system(size: 12, weight: .semibold))
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .frame(width: 30, height: 30)
        .background(Circle().fill(theme.surfaceHi))
        .help("Copy, save or export (⌘C, ⌘S, ⌘E)")
    }
}

/// Pill next to search: "All apps", or the app the timeline is filtered to. Click to choose.
struct AppFilterMenu: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel

    var body: some View {
        HStack(spacing: 6) {
            Menu {
                Button { model.setAppFilter(nil) } label: {
                    Label("All apps", systemImage: model.appFilter == nil ? "checkmark" : "square.grid.2x2")
                }
                Divider()
                ForEach(model.apps, id: \.self) { app in
                    Button { model.setAppFilter(app) } label: {
                        if let icon = AppIcons.small(for: app) {
                            Label { Text(app) } icon: { Image(nsImage: icon) }
                        } else {
                            Text(app)
                        }
                    }
                }
            } label: {
                HStack(spacing: 6) {
                    if let app = model.appFilter, let icon = AppIcons.icon(for: app) {
                        Image(nsImage: icon).resizable().frame(width: 18, height: 18)
                    } else if model.appFilter == nil {
                        Image(systemName: "square.grid.2x2").foregroundColor(theme.dim)
                    }
                    Text(model.appFilter ?? "All apps").font(.system(size: 13, weight: .medium)).lineLimit(1)
                }
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Only show moments when one app was in front (A)")
            if model.appFilter != nil {
                Button { model.setAppFilter(nil) } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundColor(theme.faint).help("Show all apps")
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(Capsule().fill(model.appFilter != nil ? Color.accentColor.opacity(0.3) : theme.surface))
        .overlay(Capsule().stroke(theme.stroke))
        .frame(maxWidth: 240)
    }
}

struct IconButton: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    let symbol: String
    let help: String
    let action: () -> Void
    @State private var hover = false

    init(symbol: String, help: String, action: @escaping () -> Void) {
        self.symbol = symbol; self.help = help; self.action = action
    }

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 12, weight: .semibold))
                .frame(width: 30, height: 30)
                .background(Circle().fill(hover ? theme.selected : theme.surfaceHi))
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(help)
    }
}

/// Blurs whatever is behind the overlay window.
struct Backdrop: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let v = NSVisualEffectView()
        v.material = .hudWindow
        v.blendingMode = .behindWindow
        v.state = .active
        return v
    }
    func updateNSView(_ v: NSVisualEffectView, context: Context) {}
}

/// App icons looked up by the app name stored with each screenshot.
enum AppIcons {
    private static var cache: [String: NSImage?] = [:]

    static func icon(for name: String) -> NSImage? {
        guard !name.isEmpty else { return nil }
        if let hit = cache[name] { return hit }
        var img = NSWorkspace.shared.runningApplications.first { $0.localizedName == name }?.icon
        if img == nil {
            let dirs = ["/Applications", "/System/Applications", "/System/Applications/Utilities",
                        "/Applications/Utilities", NSHomeDirectory() + "/Applications"]
            if let path = dirs.map({ "\($0)/\(name).app" }).first(where: { FileManager.default.fileExists(atPath: $0) }) {
                img = NSWorkspace.shared.icon(forFile: path)
            }
        }
        cache[name] = img
        return img
    }

    /// A 16 pt copy for menus (app icons are huge otherwise).
    static func small(for name: String) -> NSImage? {
        guard let img = icon(for: name)?.copy() as? NSImage else { return nil }
        img.size = NSSize(width: 16, height: 16)
        return img
    }
}

/// The timeline bar. Long breaks are shrunk to a small marker (TimelineAxis), each stretch of
/// one app is a solid colored block, audio sits on two thin lanes, and clock times run along the bottom.
/// Near "now" the view stops at now instead of showing an empty future half.
struct TimelineBar: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    @ObservedObject var model: TimelineModel
    @State private var dragStartV: TimeInterval?
    @State private var hoverX: CGFloat?

    // The top 20 pt hold the playhead time bubble.
    private let appY: CGFloat = 38, appH: CGFloat = 22
    private let micY: CGFloat = 64, sysY: CGFloat = 71, laneH: CGFloat = 4
    private let tickY: CGFloat = 80

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let axis = model.axis
            let now = model.now
            // Keep the playhead in the middle, but never show more than a little space after now.
            let v0 = min(axis.v(model.playhead) - model.span / 2, axis.v(now) + model.span * 0.03 - model.span)
            let v1 = v0 + model.span
            let x = { (d: Date) -> CGFloat in CGFloat((axis.v(d) - v0) / model.span) * w }
            let px = x(model.playhead)
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 10).fill(theme.surface).padding(.top, 20)
                Canvas { ctx, size in
                    let startDate = axis.date(v0), endDate = axis.date(v1)
                    drawApps(ctx, size: size, from: startDate, to: endDate, x: x)
                    drawAudio(ctx, size: size, from: startDate, to: endDate, x: x)
                    drawBreaks(ctx, axis: axis, size: size, x: x)
                    drawTicks(ctx, axis: axis, size: size, from: startDate, to: endDate, x: x)
                    drawLaneLabels(ctx)
                    for h in model.hits where h.time >= startDate && h.time <= endDate {
                        ctx.fill(Path(CGRect(x: x(h.time) - 1.5, y: 24, width: 3, height: 10)), with: .color(theme.match))
                    }
                    drawMarks(ctx, size: size, x: x)
                    TimelineBar.drawStars(ctx, stars: model.stars, from: startDate, to: endDate, x: x)
                    // "Now" edge.
                    let nx = x(now)
                    if nx < size.width - 1 {
                        ctx.fill(Path(CGRect(x: nx, y: 22, width: size.width - nx, height: size.height - 22)),
                                 with: .color(theme.shade))
                        ctx.fill(Path(CGRect(x: nx - 0.5, y: 22, width: 1, height: size.height - 24)),
                                 with: .color(.red.opacity(0.7)))
                    }
                    // Hover line.
                    if let hx = hoverX, dragStartV == nil, abs(hx - px) > 3 {
                        ctx.fill(Path(CGRect(x: hx - 0.5, y: 20, width: 1, height: size.height - 22)),
                                 with: .color(theme.dim))
                    }
                    // Playhead with its time in a small bubble that stays inside the bar.
                    ctx.fill(Path(CGRect(x: px - 1, y: 16, width: 2, height: size.height - 18)), with: .color(theme.playhead))
                    let label = ctx.resolve(Text(model.playhead, format: .dateTime.hour().minute().second())
                        .font(.system(size: 10, weight: .semibold)).foregroundColor(theme.playheadText))
                    let ls = label.measure(in: size)
                    let bw = ls.width + 12
                    let bx = min(max(0, px - bw / 2), size.width - bw)
                    let bubble = CGRect(x: bx, y: 1, width: bw, height: 15)
                    ctx.fill(Path(roundedRect: bubble, cornerRadius: 7.5), with: .color(theme.playhead))
                    ctx.draw(label, at: CGPoint(x: bubble.midX, y: bubble.midY))
                }
                if let hx = hoverX, dragStartV == nil, abs(hx - px) > 3 {
                    let d = axis.date(v0 + Double(hx / w) * model.span)
                    if d <= now { HoverPreview(model: model, date: d).position(previewPosition(hx, width: w)) }
                }
            }
            .contentShape(Rectangle())
            .onContinuousHover { phase in
                switch phase {
                case .active(let p): hoverX = p.x
                case .ended: hoverX = nil
                }
            }
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { g in
                        if dragStartV == nil {
                            // A click jumps to where you clicked; a drag then moves from there.
                            let v = v0 + Double(g.startLocation.x / w) * model.span
                            dragStartV = v
                            model.jump(to: axis.date(v))
                            return
                        }
                        if let sv = dragStartV {
                            model.jump(to: axis.date(sv - Double(g.translation.width / w) * model.span))
                        }
                    }
                    .onEnded { _ in dragStartV = nil }
            )
        }
    }

    /// The preview card floats above the bar, centered on the pointer but kept on screen.
    private func previewPosition(_ hx: CGFloat, width: CGFloat) -> CGPoint {
        let half = HoverPreview.width / 2 + 4
        return CGPoint(x: min(max(half, hx), width - half), y: -HoverPreview.height / 2 - 10)
    }

    /// The clip marked with [ and ]: a tinted band between two flags.
    private func drawMarks(_ ctx: GraphicsContext, size: CGSize, x: (Date) -> CGFloat) {
        let a = model.clipStart.map(x), b = model.clipEnd.map(x)
        if let a, let b {
            ctx.fill(Path(CGRect(x: a, y: 22, width: b - a, height: size.height - 24)), with: .color(Color.accentColor.opacity(0.22)))
        }
        for (mx, isStart) in [(a, true), (b, false)] {
            guard let mx else { continue }
            ctx.fill(Path(CGRect(x: mx - 1, y: 20, width: 2, height: size.height - 22)), with: .color(.accentColor))
            var flag = Path()
            flag.move(to: CGPoint(x: mx, y: 20))
            flag.addLine(to: CGPoint(x: mx + (isStart ? 7 : -7), y: 24))
            flag.addLine(to: CGPoint(x: mx, y: 28))
            flag.closeSubpath()
            ctx.fill(flag, with: .color(.accentColor))
        }
    }

    /// Tiny "mic" / "sys" keys at the left end of the audio lanes.
    private func drawLaneLabels(_ ctx: GraphicsContext) {
        for (text, y, color) in [("mic", micY, theme.mic), ("sys", sysY, theme.system)] {
            let t = ctx.resolve(Text(text).font(.system(size: 7, weight: .bold)).foregroundColor(color.opacity(0.9)))
            ctx.draw(t, at: CGPoint(x: 4, y: y + laneH / 2), anchor: .leading)
        }
    }

    /// Solid blocks: one per run of frames from the same app (a frame lasts until the next, max 60 s).
    /// While filtering by app, a frame lasts until the next screenshot of any app (its `end`).
    private func drawApps(_ ctx: GraphicsContext, size: CGSize, from: Date, to: Date, x: (Date) -> CGFloat) {
        let frames = model.frames
        guard !frames.isEmpty else { return }
        var i = max(0, model.frameIndex(at: from))
        let last = model.frameIndex(at: to)
        while i <= last {
            let app = frames[i].appName
            let runStart = frames[i].time
            var runEnd = runStart.addingTimeInterval(2)
            var j = i
            while j <= last {
                let f = frames[j]
                guard f.appName == app else { break }
                let next = f.end ?? (j + 1 < frames.count ? frames[j + 1].time : f.time.addingTimeInterval(2))
                runEnd = min(next, f.time.addingTimeInterval(60))
                j += 1
                if next.timeIntervalSince(f.time) > 60 { break }  // idle: end the block here
                if j < frames.count && frames[j].time > next { break }  // another app was in front in between
            }
            let a = x(runStart), b = x(runEnd)
            if b > 0 && a < size.width {
                let r = CGRect(x: a, y: appY, width: max(2, b - a - 1), height: appH)
                ctx.fill(Path(roundedRect: r, cornerRadius: 3), with: .color(Color(TimelineModel.color(for: app))))
            }
            i = max(j, i + 1)
        }
    }

    private func drawAudio(_ ctx: GraphicsContext, size: CGSize, from: Date, to: Date, x: (Date) -> CGFloat) {
        for a in model.audio where a.end > from && a.start < to {
            let ax = max(0, x(a.start)), bx = min(size.width, x(a.end))
            let r = CGRect(x: ax, y: a.isInput ? micY : sysY, width: max(2, bx - ax), height: laneH)
            ctx.fill(Path(roundedRect: r, cornerRadius: 2), with: .color(a.isInput ? theme.mic : theme.system))
        }
    }

    /// Each shrunk break: a dark notch with how long it was.
    private func drawBreaks(_ ctx: GraphicsContext, axis: TimelineAxis, size: CGSize, x: (Date) -> CGFloat) {
        for g in axis.gaps {
            let a = x(g.from), b = x(g.to)
            guard b > 0, a < size.width else { continue }
            let r = CGRect(x: a + 2, y: 26, width: max(0, b - a - 4), height: size.height - 30)
            ctx.fill(Path(roundedRect: r, cornerRadius: 4), with: .color(theme.shade))
            var dash = Path(); dash.move(to: CGPoint(x: r.midX, y: r.minY + 4)); dash.addLine(to: CGPoint(x: r.midX, y: r.maxY - 16))
            ctx.stroke(dash, with: .color(theme.faint), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
            let label = ctx.resolve(Text(Self.duration(g.to.timeIntervalSince(g.from)))
                .font(.system(size: 9, weight: .medium)).foregroundColor(theme.dim))
            if label.measure(in: size).width < r.width + 30 {
                ctx.draw(label, at: CGPoint(x: r.midX, y: r.maxY - 7))
            }
        }
    }

    /// Clock labels at round times inside recorded stretches (never inside a break).
    private func drawTicks(_ ctx: GraphicsContext, axis: TimelineAxis, size: CGSize, from: Date, to: Date, x: (Date) -> CGFloat) {
        let target = model.span / Double(max(4, Int(size.width / 110)))
        // Whole minutes at least, so two labels never read the same.
        let steps: [TimeInterval] = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200]
        let step = steps.first(where: { $0 >= target }) ?? 43200
        var lastLabelX = -CGFloat.infinity
        for s in axis.sessions where s.end >= from && s.start <= to {
            let lo = max(s.start, from), hi = min(s.end, to)
            var t = Date(timeIntervalSince1970: (lo.timeIntervalSince1970 / step).rounded(.up) * step)
            while t <= hi {
                let tx = x(t)
                ctx.fill(Path(CGRect(x: tx - 0.5, y: tickY, width: 1, height: 4)), with: .color(theme.faint))
                if tx - lastLabelX > 60 {
                    let fmt: Date.FormatStyle = step >= 3600 ? .dateTime.hour() : .dateTime.hour().minute()
                    let label = ctx.resolve(Text(t, format: fmt).font(.system(size: 10)).foregroundColor(theme.dim))
                    ctx.draw(label, at: CGPoint(x: tx, y: tickY + 11))
                    lastLabelX = tx
                }
                t = t.addingTimeInterval(step)
            }
        }
    }

    static func duration(_ s: TimeInterval) -> String {
        let m = Int(s / 60)
        if m < 60 { return "\(m)m break" }
        if m < 24 * 60 { return "\(m / 60)h \(m % 60)m break" }
        return "\(m / 1440)d break"
    }
}

/// Small card above the bar: the screenshot, app and time under the pointer.
struct HoverPreview: View {
    @Environment(\.colorScheme) private var scheme
    private var theme: Theme { Theme(scheme) }
    static let width: CGFloat = 240, height: CGFloat = 178
    @ObservedObject var model: TimelineModel
    let date: Date

    var body: some View {
        let p = model.preview(at: date)
        VStack(alignment: .leading, spacing: 6) {
            ZStack {
                RoundedRectangle(cornerRadius: 6).fill(theme.surface)
                if let img = p?.image {
                    Image(nsImage: img).resizable().aspectRatio(contentMode: .fit)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                } else {
                    Text(p == nil ? "Nothing recorded" : "No picture")
                        .font(.system(size: 11)).foregroundColor(theme.faint)
                }
            }
            .frame(height: 128)
            HStack(spacing: 6) {
                if let f = p?.frame {
                    Circle().fill(Color(TimelineModel.color(for: f.appName))).frame(width: 7, height: 7)
                    Text(f.appName.isEmpty ? "Unknown app" : f.appName).lineLimit(1)
                }
                Spacer(minLength: 4)
                Text(date, format: .dateTime.hour().minute().second()).monospacedDigit().foregroundColor(theme.dim)
            }
            .font(.system(size: 11, weight: .medium))
        }
        .foregroundColor(theme.text)
        .padding(8)
        .frame(width: Self.width, height: Self.height)
        .background(RoundedRectangle(cornerRadius: 10).fill(theme.card))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(theme.stroke))
        .shadow(color: theme.shadow, radius: 14)
        .allowsHitTesting(false)
    }
}
