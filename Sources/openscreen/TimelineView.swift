import AppKit
import SwiftUI

struct TimelineView: View {
    @ObservedObject var model: TimelineModel
    var close: () -> Void
    @FocusState private var searchFocused: Bool

    var body: some View {
        ZStack {
            Color.black.opacity(0.9).ignoresSafeArea()
            VStack(spacing: 14) {
                header
                if let gap = staleGap { staleBanner(gap) }
                searchBar
                ZStack(alignment: .top) {
                    screenshot
                    if model.showResults { results }
                }
                footer
            }
            .padding(.horizontal, 28)
            .padding(.vertical, 20)
        }
        .foregroundColor(.white)
        .onChange(of: model.focusSearchToken) { _ in searchFocused = true }
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
            Image(systemName: "magnifyingglass").foregroundColor(.white.opacity(0.6))
            TextField("Search what you saw or heard…", text: $model.query)
                .textFieldStyle(.plain)
                .font(.system(size: 16))
                .focused($searchFocused)
                .onChange(of: model.query) { _ in model.queryChanged() }
                .onSubmit { model.openSelected(); searchFocused = false }
            if !model.query.isEmpty {
                if !model.hits.isEmpty && !model.showResults {
                    Text("\(model.selectedHit + 1) of \(model.hits.count)")
                        .font(.system(size: 12)).foregroundColor(.white.opacity(0.6)).monospacedDigit()
                    Button { model.nextHit(older: false) } label: { Image(systemName: "chevron.up") }
                        .buttonStyle(.plain).help("Newer match (⇧⌘G)")
                    Button { model.nextHit(older: true) } label: { Image(systemName: "chevron.down") }
                        .buttonStyle(.plain).help("Older match (⌘G)")
                    Button("List") { model.showResults = true; searchFocused = true }
                        .buttonStyle(.plain).font(.system(size: 12))
                } else if model.hits.isEmpty && !model.showResults {
                    Text("No matches").font(.system(size: 12)).foregroundColor(.white.opacity(0.5))
                }
                Button { model.clearSearch() } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain).foregroundColor(.white.opacity(0.5))
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(searchFocused ? 0.14 : 0.08)))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(searchFocused ? 0.35 : 0.12)))
        .frame(maxWidth: 720)
    }

    private var results: some View {
        VStack(alignment: .leading, spacing: 0) {
            if model.hits.isEmpty {
                Text("No matches for “\(model.query)”").foregroundColor(.white.opacity(0.6)).padding(16)
            } else {
                Text("\(model.hits.count) moment\(model.hits.count == 1 ? "" : "s") · ↑ ↓ to choose · return to go")
                    .font(.system(size: 11)).foregroundColor(.white.opacity(0.45))
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
        .frame(maxWidth: 720, maxHeight: 420, alignment: .top)
        .fixedSize(horizontal: false, vertical: model.hits.count < 6)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color(white: 0.11)))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.white.opacity(0.12)))
        .shadow(color: .black.opacity(0.5), radius: 20)
    }

    private func hitRow(_ h: SearchHit, selected: Bool) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: h.kind == .audio ? "waveform" : "display")
                .frame(width: 18).foregroundColor(h.kind == .audio ? .green : .white.opacity(0.7))
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: 3) {
                HStack {
                    Text(h.title).font(.system(size: 13, weight: .semibold))
                    Spacer()
                    Text(h.time, format: .dateTime.month(.abbreviated).day().hour().minute().second())
                        .font(.system(size: 11)).foregroundColor(.white.opacity(0.5)).monospacedDigit()
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

    // MARK: header

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            if let f = model.current {
                Circle().fill(Color(TimelineModel.color(for: f.appName))).frame(width: 10, height: 10)
                VStack(alignment: .leading, spacing: 2) {
                    Text(f.appName.isEmpty ? "Unknown app" : f.appName).font(.system(size: 15, weight: .semibold))
                    Text(f.windowName).font(.system(size: 12)).foregroundColor(.white.opacity(0.6)).lineLimit(1)
                }
            }
            Spacer()
            VStack(spacing: 2) {
                Text(model.playhead, format: .dateTime.hour().minute().second())
                    .font(.system(size: 26, weight: .semibold, design: .rounded)).monospacedDigit()
                Text(relative(model.playhead)).font(.system(size: 12)).foregroundColor(.white.opacity(0.6))
            }
            Spacer()
            Button(action: close) {
                Text("esc").font(.system(size: 12, weight: .medium))
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(Capsule().stroke(Color.white.opacity(0.3)))
            }
            .buttonStyle(.plain)
        }
        .frame(maxWidth: .infinity)
    }

    // MARK: screenshot

    private var screenshot: some View {
        GeometryReader { geo in
            ZStack {
                if let img = model.image {
                    Image(nsImage: img)
                        .resizable()
                        .interpolation(.high)
                        .aspectRatio(contentMode: .fit)
                        .clipShape(RoundedRectangle(cornerRadius: 10))
                        .shadow(color: .black.opacity(0.6), radius: 24)
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "rectangle.dashed").font(.system(size: 40)).opacity(0.4)
                        Text(model.frames.isEmpty ? "Nothing recorded yet. Turn on screen recording in the menu bar."
                                                  : "Screenshot not found for this moment.")
                            .foregroundColor(.white.opacity(0.6))
                    }
                }
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
    }

    // MARK: footer

    private var footer: some View {
        VStack(spacing: 10) {
            if let c = model.caption {
                Text("“\(c)”")
                    .font(.system(size: 15))
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 14).padding(.vertical, 8)
                    .background(RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.08)))
                    .frame(maxWidth: 900)
            }
            HStack(spacing: 8) {
                ForEach(model.audioHere, id: \.id) { a in
                    let on = model.playingChunk == a.id
                    Button { on ? model.stopAudio() : model.play(a) } label: {
                        Label(a.isInput ? "Microphone" : "System audio", systemImage: on ? "pause.fill" : "play.fill")
                            .font(.system(size: 12, weight: .medium))
                            .padding(.horizontal, 12).padding(.vertical, 6)
                            .background(Capsule().fill(on ? Color.accentColor : Color.white.opacity(0.12)))
                    }
                    .buttonStyle(.plain)
                }
                if model.audioHere.isEmpty {
                    Text("No audio at this moment").font(.system(size: 12)).foregroundColor(.white.opacity(0.45))
                }
                Spacer()
                Button("Now") { model.jumpToNow() }.buttonStyle(.plain).font(.system(size: 12, weight: .medium))
                Text("Zoom \(spanLabel)").font(.system(size: 12)).foregroundColor(.white.opacity(0.6))
                Button { zoom(0.5) } label: { Image(systemName: "plus.magnifyingglass") }.buttonStyle(.plain)
                Button { zoom(2) } label: { Image(systemName: "minus.magnifyingglass") }.buttonStyle(.plain)
            }
            TimelineBar(model: model).frame(height: 62)
            Text("/ or ⌘F search  ·  ⌘G next match  ·  scroll or ← → to move  ·  ⇧ for 1 minute  ·  drag the bar  ·  space plays audio  ·  + − zoom  ·  N now  ·  esc close")
                .font(.system(size: 11)).foregroundColor(.white.opacity(0.4))
        }
    }

    private func zoom(_ k: Double) { model.span = min(max(model.span * k, 60), 12 * 3600) }

    private var spanLabel: String {
        model.span >= 3600 ? "\(Int(model.span / 3600))h" : "\(Int(model.span / 60))m"
    }

    private func relative(_ d: Date) -> String {
        let s = Date().timeIntervalSince(d)
        if s < 60 { return "just now" }
        let day = d.formatted(.dateTime.weekday(.wide).month().day())
        if s < 3600 { return "\(Int(s / 60)) min ago · \(day)" }
        return "\(Int(s / 3600)) h \(Int(s.truncatingRemainder(dividingBy: 3600) / 60)) min ago · \(day)"
    }
}

/// Horizontal strip: a tick per frame (colored by app), audio below, playhead in the middle.
struct TimelineBar: View {
    @ObservedObject var model: TimelineModel
    @State private var dragStart: Date?

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let start = model.playhead.addingTimeInterval(-model.span / 2)
            let end = model.playhead.addingTimeInterval(model.span / 2)
            let x = { (d: Date) -> CGFloat in CGFloat(d.timeIntervalSince(start) / model.span) * w }
            ZStack(alignment: .topLeading) {
                RoundedRectangle(cornerRadius: 8).fill(Color.white.opacity(0.06))
                Canvas { ctx, size in
                    // Future (after now) is shaded.
                    let nowX = x(Date())
                    if nowX < size.width {
                        ctx.fill(Path(CGRect(x: max(0, nowX), y: 0, width: size.width - max(0, nowX), height: size.height)),
                                 with: .color(.black.opacity(0.35)))
                    }
                    let lo = model.frameIndex(at: start)
                    let hi = model.frameIndex(at: end)
                    if !model.frames.isEmpty {
                        for i in lo...hi {
                            let f = model.frames[i]
                            let fx = x(f.time)
                            guard fx >= 0, fx <= size.width else { continue }
                            ctx.fill(Path(CGRect(x: fx - 1, y: 8, width: 2, height: 26)),
                                     with: .color(Color(TimelineModel.color(for: f.appName)).opacity(0.85)))
                        }
                    }
                    for a in model.audio where a.end > start && a.start < end {
                        let ax = max(0, x(a.start)), bx = min(size.width, x(a.end))
                        let y: CGFloat = a.isInput ? 40 : 48
                        ctx.fill(Path(roundedRect: CGRect(x: ax, y: y, width: max(2, bx - ax), height: 6), cornerRadius: 3),
                                 with: .color(a.isInput ? Color.orange.opacity(0.8) : Color.green.opacity(0.8)))
                    }
                    // Search matches.
                    for h in model.hits where h.time >= start && h.time <= end {
                        let hx = x(h.time)
                        ctx.fill(Path(CGRect(x: hx - 1.5, y: 0, width: 3, height: 8)), with: .color(.yellow))
                    }
                    // Playhead.
                    ctx.fill(Path(CGRect(x: size.width / 2 - 1, y: 0, width: 2, height: size.height)), with: .color(.white))
                }
                HStack {
                    Text(start, format: .dateTime.hour().minute())
                    Spacer()
                    Text(end, format: .dateTime.hour().minute())
                }
                .font(.system(size: 10)).foregroundColor(.white.opacity(0.45))
                .padding(.horizontal, 6).offset(y: 46)
            }
            .contentShape(Rectangle())
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { g in
                        if dragStart == nil {
                            dragStart = model.playhead
                            // A click (no movement yet) jumps to where you clicked.
                            let t = start.addingTimeInterval(Double(g.startLocation.x / w) * model.span)
                            if abs(g.translation.width) < 2 { dragStart = t; model.jump(to: t); return }
                        }
                        if let d = dragStart {
                            model.jump(to: d.addingTimeInterval(-Double(g.translation.width / w) * model.span))
                        }
                    }
                    .onEnded { _ in dragStart = nil }
            )
        }
    }
}
