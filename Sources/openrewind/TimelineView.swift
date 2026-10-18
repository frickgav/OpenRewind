import AppKit
import SwiftUI

/// Shared look for the overlay.
private enum Theme {
    static let surface = Color.white.opacity(0.08)
    static let surfaceHi = Color.white.opacity(0.14)
    static let stroke = Color.white.opacity(0.12)
    static let dim = Color.white.opacity(0.6)
    static let faint = Color.white.opacity(0.42)
    static let mic = Color.orange
    static let system = Color.green
}

struct TimelineView: View {
    @ObservedObject var model: TimelineModel
    var close: () -> Void
    @FocusState private var searchFocused: Bool

    var body: some View {
        ZStack {
            Backdrop().ignoresSafeArea()
            Color.black.opacity(0.55).ignoresSafeArea()
            VStack(spacing: 14) {
                topBar
                if let gap = staleGap { staleBanner(gap) }
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
            if model.showHelp { helpCard }
        }
        .foregroundColor(.white)
        .onChange(of: model.focusSearchToken) { _ in searchFocused = true }
    }

    // MARK: top bar

    /// Search on the left, the playhead time in the middle, help and close on the right.
    private var topBar: some View {
        ZStack {
            HStack(spacing: 10) {
                searchBar.frame(maxWidth: 460)
                Spacer(minLength: 220)
                IconButton(symbol: "questionmark", help: "Keyboard shortcuts (?)") { model.showHelp.toggle() }
                IconButton(symbol: "xmark", help: "Close (esc)", action: close)
            }
            VStack(spacing: 1) {
                Text(model.playhead, format: .dateTime.hour().minute().second())
                    .font(.system(size: 28, weight: .semibold, design: .rounded)).monospacedDigit()
                Text(relative(model.playhead)).font(.system(size: 12)).foregroundColor(Theme.dim)
            }
            .allowsHitTesting(false)
        }
    }

    // MARK: stale warning

    /// Minutes since the newest screenshot, when that is long enough to look like a gap.
    private var staleGap: Int? {
        guard let last = model.frames.last else { return nil }
        let mins = Int(Date().timeIntervalSince(last.time) / 60)
        return mins >= 3 ? mins : nil
    }

    private func staleBanner(_ mins: Int) -> some View {
        let ago = mins >= 60 ? "\(mins / 60) h \(mins % 60) min" : "\(mins) min"
        let why = !Capture.hasScreenPermission
            ? "Screen recording is blocked by macOS. Menu bar → Screen permission needed."
            : "Screen recording may be off, or the screen has not changed."
        return HStack(spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundColor(.yellow)
            Text("Newest screenshot is from \(ago) ago. \(why)").font(.system(size: 13))
        }
        .padding(.horizontal, 14).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.yellow.opacity(0.15)))
    }

    // MARK: search

    private var searchBar: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass").foregroundColor(Theme.dim)
            TextField("Search what you saw or heard", text: $model.query)
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
                        .font(.system(size: 12)).foregroundColor(Theme.dim).monospacedDigit()
                    Button { model.nextHit(older: false) } label: { Image(systemName: "chevron.up") }
                        .buttonStyle(.plain).help("Newer match (⇧⌘G)")
                    Button { model.nextHit(older: true) } label: { Image(systemName: "chevron.down") }
                        .buttonStyle(.plain).help("Older match (⌘G)")
                    Button("List") { model.showResults = true; searchFocused = true }
                        .buttonStyle(.plain).font(.system(size: 12, weight: .medium))
                } else if model.hits.isEmpty && !model.showResults {
                    Text("No matches").font(.system(size: 12)).foregroundColor(Theme.faint)
                }
                Button { model.clearSearch() } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundColor(Theme.faint)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(Capsule().fill(searchFocused ? Theme.surfaceHi : Theme.surface))
        .overlay(Capsule().stroke(Color.white.opacity(searchFocused ? 0.35 : 0.12)))
        .contentShape(Capsule())
        .onTapGesture { searchFocused = true }
    }

    private var results: some View {
        VStack(alignment: .leading, spacing: 0) {
            if model.hits.isEmpty {
                Text("No matches for “\(model.query)”").foregroundColor(Theme.dim).padding(16)
            } else {
                HStack(spacing: 6) {
                    Text("\(model.hits.count) moment\(model.hits.count == 1 ? "" : "s")")
                    Spacer()
                    Keycap("↑"); Keycap("↓"); Text("choose")
                    Keycap("return").padding(.leading, 6); Text("go")
                }
                .font(.system(size: 11)).foregroundColor(Theme.faint)
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
        .background(RoundedRectangle(cornerRadius: 12).fill(Color(white: 0.11)))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.stroke))
        .shadow(color: .black.opacity(0.5), radius: 20)
    }

    private func hitRow(_ h: SearchHit, selected: Bool) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Group {
                if h.kind == .screen, let icon = AppIcons.icon(for: h.title) {
                    Image(nsImage: icon).resizable().frame(width: 18, height: 18)
                } else {
                    Image(systemName: h.kind == .audio ? "waveform" : "display")
                        .foregroundColor(h.kind == .audio ? Theme.system : Theme.dim)
                }
            }
            .frame(width: 20).padding(.top, 1)
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(h.title).font(.system(size: 13, weight: .semibold))
                    Spacer()
                    Text(h.time, format: .dateTime.month(.abbreviated).day().hour().minute().second())
                        .font(.system(size: 11)).foregroundColor(Theme.faint).monospacedDigit()
                }
                highlighted(h.snippet).font(.system(size: 12)).foregroundColor(.white.opacity(0.8)).lineLimit(2)
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 8).fill(selected ? Color.accentColor.opacity(0.35) : Color.clear))
        .contentShape(Rectangle())
    }

    /// Bold + yellow the searched words inside a snippet.
    private func highlighted(_ text: String) -> Text {
        let words = model.query.lowercased().split(separator: " ").map(String.init).filter { !$0.isEmpty }
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
            out = out + Text(String(rest[..<a])) + Text(String(rest[a..<b])).bold().foregroundColor(.yellow)
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
                        .clipShape(RoundedRectangle(cornerRadius: 12))
                        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.stroke))
                        .shadow(color: .black.opacity(0.6), radius: 24)
                        .frame(width: geo.size.width, height: geo.size.height)
                } else {
                    emptyState.frame(width: geo.size.width, height: geo.size.height)
                }
                if let c = model.caption {
                    Text(c)
                        .font(.system(size: 17, weight: .medium))
                        .multilineTextAlignment(.center)
                        .lineLimit(3)
                        .padding(.horizontal, 16).padding(.vertical, 9)
                        .background(RoundedRectangle(cornerRadius: 10).fill(Color.black.opacity(0.78)))
                        .frame(maxWidth: min(900, geo.size.width - 80))
                        .padding(.bottom, 22)
                }
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: model.frames.isEmpty ? "record.circle" : "photo")
                .font(.system(size: 36, weight: .light)).foregroundColor(Theme.faint)
            Text(model.frames.isEmpty ? "Nothing recorded yet" : "Screenshot not found")
                .font(.system(size: 16, weight: .semibold))
            Text(model.frames.isEmpty ? "Turn on screen recording in the menu bar." : "The file for this moment was deleted or moved.")
                .font(.system(size: 13)).foregroundColor(Theme.dim)
        }
        .padding(28)
        .background(RoundedRectangle(cornerRadius: 14).fill(Theme.surface))
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
                        RoundedRectangle(cornerRadius: 7).fill(Theme.surfaceHi).frame(width: 28, height: 28)
                            .overlay(Image(systemName: "app").foregroundColor(Theme.dim))
                    }
                    // Same color as this app's blocks on the bar below.
                    Circle().fill(Color(TimelineModel.color(for: f.appName))).frame(width: 9, height: 9)
                        .overlay(Circle().stroke(Color.black.opacity(0.6), lineWidth: 1.5))
                        .offset(x: 2, y: 2)
                }
                VStack(alignment: .leading, spacing: 1) {
                    Text(f.appName.isEmpty ? "Unknown app" : f.appName).font(.system(size: 13, weight: .semibold))
                    if !f.windowName.isEmpty {
                        Text(f.windowName).font(.system(size: 12)).foregroundColor(Theme.dim)
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
                    .font(.system(size: 12)).foregroundColor(Theme.faint)
            }
            ForEach(model.audioHere, id: \.id) { a in
                let on = model.playingChunk == a.id
                Button { on ? model.stopAudio() : model.play(a) } label: {
                    HStack(spacing: 7) {
                        Image(systemName: on ? "pause.fill" : "play.fill").font(.system(size: 11))
                        Text(a.isInput ? "Microphone" : "System audio")
                        // Matches the audio lane color on the bar.
                        Circle().fill(a.isInput ? Theme.mic : Theme.system).frame(width: 7, height: 7)
                    }
                    .font(.system(size: 12, weight: .medium))
                    .padding(.horizontal, 13).padding(.vertical, 7)
                    .background(Capsule().fill(on ? Color.accentColor : Theme.surfaceHi))
                    .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .help(on ? "Pause (space)" : "Play from here (space)")
            }
        }
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
            .background(Capsule().fill(model.isLive ? Theme.surface : Color.accentColor))
            .foregroundColor(model.isLive ? Theme.dim : .white)
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
                        .background(Capsule().fill(on ? Color.white.opacity(0.22) : Color.clear))
                        .foregroundColor(on ? .white : Theme.dim)
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(Capsule().fill(Theme.surface))
        .help("Time shown across the bar (+ and − also zoom)")
    }

    // MARK: hints and help

    private var hints: some View {
        HStack(spacing: 14) {
            hint(["←", "→"], "move")
            hint(["space"], "play audio")
            hint(["/"], "search")
            hint(["+", "−"], "zoom")
            hint(["?"], "all keys")
            hint(["esc"], "close")
        }
        .font(.system(size: 11)).foregroundColor(Theme.faint)
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
        ("Audio", [["space"]], "Play or pause audio from here"),
        ("Search", [["/"], ["⌘", "F"]], "Search screen text and speech"),
        ("", [["⌘", "G"], ["⇧", "⌘", "G"]], "Older / newer match"),
        ("View", [["+"], ["−"]], "Zoom the bar in / out"),
        ("", [["esc"]], "Close the list, then the overlay"),
    ]

    private var helpCard: some View {
        ZStack {
            Color.black.opacity(0.45).ignoresSafeArea().onTapGesture { model.showHelp = false }
            VStack(alignment: .leading, spacing: 14) {
                HStack {
                    Text("Keyboard shortcuts").font(.system(size: 17, weight: .semibold))
                    Spacer()
                    IconButton(symbol: "xmark", help: "Close (esc)") { model.showHelp = false }
                }
                Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 9) {
                    ForEach(Array(Self.shortcuts.enumerated()), id: \.offset) { _, row in
                        GridRow {
                            Text(row.0).font(.system(size: 11, weight: .semibold)).foregroundColor(Theme.faint)
                                .textCase(.uppercase)
                            HStack(spacing: 6) {
                                ForEach(Array(row.1.enumerated()), id: \.offset) { i, combo in
                                    if i > 0 { Text("/").foregroundColor(Theme.faint) }
                                    HStack(spacing: 3) { ForEach(combo, id: \.self) { Keycap($0, large: true) } }
                                }
                            }
                            Text(row.2).font(.system(size: 13)).foregroundColor(.white.opacity(0.85))
                        }
                    }
                }
                Text("Drag or click the bar to move. Hover it to preview.")
                    .font(.system(size: 12)).foregroundColor(Theme.dim)
            }
            .padding(24)
            .frame(width: 560)
            .background(RoundedRectangle(cornerRadius: 16).fill(Color(white: 0.12)))
            .overlay(RoundedRectangle(cornerRadius: 16).stroke(Theme.stroke))
            .shadow(color: .black.opacity(0.6), radius: 30)
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
    let key: String
    var large = false
    init(_ key: String, large: Bool = false) { self.key = key; self.large = large }

    var body: some View {
        Text(key)
            .font(.system(size: large ? 12 : 10, weight: .medium, design: .rounded))
            .foregroundColor(.white.opacity(0.85))
            .padding(.horizontal, large ? 7 : 5).padding(.vertical, large ? 3 : 1.5)
            .frame(minWidth: large ? 24 : 18)
            .background(RoundedRectangle(cornerRadius: 4).fill(Color.white.opacity(0.12)))
            .overlay(RoundedRectangle(cornerRadius: 4).stroke(Color.white.opacity(0.18)))
    }
}

/// Round icon button for the top bar.
struct IconButton: View {
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
                .background(Circle().fill(Color.white.opacity(hover ? 0.2 : 0.1)))
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
        v.appearance = NSAppearance(named: .darkAqua)
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
}

/// The timeline bar. Long breaks are shrunk to a small marker (TimelineAxis), each stretch of
/// one app is a solid colored block, audio sits on two thin lanes, and clock times run along the bottom.
/// Near "now" the view stops at now instead of showing an empty future half.
struct TimelineBar: View {
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
            let now = Date()
            // Keep the playhead in the middle, but never show more than a little space after now.
            let v0 = min(axis.v(model.playhead) - model.span / 2, axis.v(now) + model.span * 0.03 - model.span)
            let v1 = v0 + model.span
            let x = { (d: Date) -> CGFloat in CGFloat((axis.v(d) - v0) / model.span) * w }
            let px = x(model.playhead)
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.06)).padding(.top, 20)
                Canvas { ctx, size in
                    let startDate = axis.date(v0), endDate = axis.date(v1)
                    drawApps(ctx, size: size, from: startDate, to: endDate, x: x)
                    drawAudio(ctx, size: size, from: startDate, to: endDate, x: x)
                    drawBreaks(ctx, axis: axis, size: size, x: x)
                    drawTicks(ctx, axis: axis, size: size, from: startDate, to: endDate, x: x)
                    drawLaneLabels(ctx)
                    for h in model.hits where h.time >= startDate && h.time <= endDate {
                        ctx.fill(Path(CGRect(x: x(h.time) - 1.5, y: 24, width: 3, height: 10)), with: .color(.yellow))
                    }
                    // "Now" edge.
                    let nx = x(now)
                    if nx < size.width - 1 {
                        ctx.fill(Path(CGRect(x: nx, y: 22, width: size.width - nx, height: size.height - 22)),
                                 with: .color(.black.opacity(0.3)))
                        ctx.fill(Path(CGRect(x: nx - 0.5, y: 22, width: 1, height: size.height - 24)),
                                 with: .color(.red.opacity(0.7)))
                    }
                    // Hover line.
                    if let hx = hoverX, dragStartV == nil, abs(hx - px) > 3 {
                        ctx.fill(Path(CGRect(x: hx - 0.5, y: 20, width: 1, height: size.height - 22)),
                                 with: .color(.white.opacity(0.45)))
                    }
                    // Playhead with its time in a small bubble that stays inside the bar.
                    ctx.fill(Path(CGRect(x: px - 1, y: 16, width: 2, height: size.height - 18)), with: .color(.white))
                    let label = ctx.resolve(Text(model.playhead, format: .dateTime.hour().minute().second())
                        .font(.system(size: 10, weight: .semibold)).foregroundColor(.black))
                    let ls = label.measure(in: size)
                    let bw = ls.width + 12
                    let bx = min(max(0, px - bw / 2), size.width - bw)
                    let bubble = CGRect(x: bx, y: 1, width: bw, height: 15)
                    ctx.fill(Path(roundedRect: bubble, cornerRadius: 7.5), with: .color(.white))
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

    /// Tiny "mic" / "sys" keys at the left end of the audio lanes.
    private func drawLaneLabels(_ ctx: GraphicsContext) {
        for (text, y, color) in [("mic", micY, Color.orange), ("sys", sysY, Color.green)] {
            let t = ctx.resolve(Text(text).font(.system(size: 7, weight: .bold)).foregroundColor(color.opacity(0.9)))
            ctx.draw(t, at: CGPoint(x: 4, y: y + laneH / 2), anchor: .leading)
        }
    }

    /// Solid blocks: one per run of frames from the same app (a frame lasts until the next, max 60 s).
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
                let next = j + 1 < frames.count ? frames[j + 1].time : f.time.addingTimeInterval(2)
                runEnd = min(next, f.time.addingTimeInterval(60))
                j += 1
                if next.timeIntervalSince(f.time) > 60 { break }  // idle: end the block here
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
            ctx.fill(Path(roundedRect: r, cornerRadius: 2), with: .color(a.isInput ? .orange : .green))
        }
    }

    /// Each shrunk break: a dark notch with how long it was.
    private func drawBreaks(_ ctx: GraphicsContext, axis: TimelineAxis, size: CGSize, x: (Date) -> CGFloat) {
        for g in axis.gaps {
            let a = x(g.from), b = x(g.to)
            guard b > 0, a < size.width else { continue }
            let r = CGRect(x: a + 2, y: 26, width: max(0, b - a - 4), height: size.height - 30)
            ctx.fill(Path(roundedRect: r, cornerRadius: 4), with: .color(.black.opacity(0.45)))
            var dash = Path(); dash.move(to: CGPoint(x: r.midX, y: r.minY + 4)); dash.addLine(to: CGPoint(x: r.midX, y: r.maxY - 16))
            ctx.stroke(dash, with: .color(.white.opacity(0.25)), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
            let label = ctx.resolve(Text(Self.duration(g.to.timeIntervalSince(g.from)))
                .font(.system(size: 9, weight: .medium)).foregroundColor(.white.opacity(0.6)))
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
                ctx.fill(Path(CGRect(x: tx - 0.5, y: tickY, width: 1, height: 4)), with: .color(.white.opacity(0.35)))
                if tx - lastLabelX > 60 {
                    let fmt: Date.FormatStyle = step >= 3600 ? .dateTime.hour() : .dateTime.hour().minute()
                    let label = ctx.resolve(Text(t, format: fmt).font(.system(size: 10)).foregroundColor(.white.opacity(0.6)))
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
    static let width: CGFloat = 240, height: CGFloat = 178
    @ObservedObject var model: TimelineModel
    let date: Date

    var body: some View {
        let p = model.preview(at: date)
        VStack(alignment: .leading, spacing: 6) {
            ZStack {
                RoundedRectangle(cornerRadius: 6).fill(Color.white.opacity(0.06))
                if let img = p?.image {
                    Image(nsImage: img).resizable().aspectRatio(contentMode: .fit)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                } else {
                    Text(p == nil ? "Nothing recorded" : "No picture")
                        .font(.system(size: 11)).foregroundColor(.white.opacity(0.5))
                }
            }
            .frame(height: 128)
            HStack(spacing: 6) {
                if let f = p?.frame {
                    Circle().fill(Color(TimelineModel.color(for: f.appName))).frame(width: 7, height: 7)
                    Text(f.appName.isEmpty ? "Unknown app" : f.appName).lineLimit(1)
                }
                Spacer(minLength: 4)
                Text(date, format: .dateTime.hour().minute().second()).monospacedDigit().foregroundColor(.white.opacity(0.6))
            }
            .font(.system(size: 11, weight: .medium))
        }
        .padding(8)
        .frame(width: Self.width, height: Self.height)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color(white: 0.13)))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(0.15)))
        .shadow(color: .black.opacity(0.5), radius: 14)
        .allowsHitTesting(false)
    }
}
