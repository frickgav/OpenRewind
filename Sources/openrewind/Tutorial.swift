import SwiftUI

/// A small animated demo of the app for the setup window: open the timeline with the hotkey,
/// scroll back in time, search. Loops every 12 seconds. Drawn by hand, no video file needed.
/// The Windows app draws the same demo in DemoView.cs.
struct DemoView: View {
    var hotkey: [String]
    private static let scene = 4.0
    private static let steps = ["Open your timeline", "Scroll back in time", "Search anything"]
    private static let colors: [Color] = [.blue, .purple, .green, .orange, .red, .cyan]
    private static let blocks: [(from: Double, to: Double, app: Int)] =
        [(0, 0.14, 0), (0.14, 0.30, 1), (0.30, 0.38, 2), (0.38, 0.55, 3), (0.55, 0.62, 0), (0.62, 0.80, 4), (0.80, 1, 5)]
    @State private var start = Date()

    var body: some View {
        SwiftUI.TimelineView(.animation) { ctx in
            let t = ctx.date.timeIntervalSince(start).truncatingRemainder(dividingBy: Self.scene * 3)
            let scene = Int(t / Self.scene)
            let st = t - Double(scene) * Self.scene
            VStack(spacing: 12) {
                HStack(spacing: 8) {
                    ForEach(0..<3) { i in
                        ZStack(alignment: .leading) {
                            Capsule().fill(i == scene ? SetupTheme.accent : SetupTheme.card)
                            if i == scene {
                                GeometryReader { g in
                                    Capsule().fill(Color.white.opacity(0.22)).frame(width: g.size.width * st / Self.scene)
                                }
                            }
                            Text("\(i + 1)  \(Self.steps[i])").font(.system(size: 13, weight: i == scene ? .semibold : .regular))
                                .foregroundColor(i == scene ? .white : SetupTheme.dim).frame(maxWidth: .infinity)
                        }
                        .frame(height: 32)
                        .clipShape(Capsule())
                    }
                }
                Canvas { g, size in draw(&g, size, scene: scene, t: st) }
                    .background(Color(red: 0.09, green: 0.1, blue: 0.125))
                    .clipShape(RoundedRectangle(cornerRadius: 14))
                    .overlay(RoundedRectangle(cornerRadius: 14).stroke(SetupTheme.stroke))
            }
        }
    }

    private func draw(_ g: inout GraphicsContext, _ s: CGSize, scene: Int, t: Double) {
        let r = CGRect(origin: .zero, size: s)
        switch scene {
        case 0:
            // A normal desktop, the hotkey is pressed, the timeline fades in.
            desktop(&g, r)
            let fade = min(max((t - 1.6) / 0.5, 0), 1)
            if fade > 0 { overlay(&g, r, playhead: 0.96, alpha: fade) }
            keycaps(&g, r, down: t > 1.0 && t < 1.6, alpha: 1 - fade, lift: fade * r.height * 0.3)
        case 1:
            // The playhead slides back along the bar and the screenshot changes with it.
            let x = min(max((t - 0.3) / 3.2, 0), 1)
            let eased = x < 0.5 ? 2 * x * x : 1 - pow(-2 * x + 2, 2) / 2
            overlay(&g, r, playhead: 0.96 - 0.8 * eased, alpha: 1)
        default:
            // A word is typed, results drop down, then the word lights up on the screenshot.
            let word = "invoice"
            let n = Int(min(max((t - 0.3) / 0.12, 0), Double(word.count)))
            overlay(&g, r, playhead: t >= 2.6 ? 0.42 : 0.96, alpha: 1, query: String(word.prefix(n)),
                    results: t > 1.4 && t < 2.6, highlight: t >= 2.6)
        }
    }

    private func overlay(_ g: inout GraphicsContext, _ s: CGRect, playhead p: Double, alpha: Double,
                         query: String? = nil, results: Bool = false, highlight: Bool = false) {
        g.opacity = alpha
        g.fill(Path(s), with: .color(Color(red: 0.055, green: 0.055, blue: 0.07).opacity(0.93)))
        let pill = CGRect(x: 18, y: 14, width: min(260, s.width * 0.45), height: 28)
        g.fill(Path(roundedRect: pill, cornerRadius: 14), with: .color(.white.opacity(0.15)))
        if query != nil { g.stroke(Path(roundedRect: pill, cornerRadius: 14), with: .color(SetupTheme.accent), lineWidth: 1.5) }
        let q = query ?? ""
        let caret = Int(Date().timeIntervalSince1970 * 2) % 2 == 0 ? "|" : ""
        g.draw(Text(q.isEmpty ? "Search what you saw or heard" : q + caret).font(.system(size: 12.5))
                .foregroundColor(q.isEmpty ? SetupTheme.dim : .white),
               at: CGPoint(x: pill.minX + 12, y: pill.midY), anchor: .leading)

        let shot = CGRect(x: 18, y: pill.maxY + 12, width: s.width - 36, height: s.height - 150)
        fakeWindow(&g, shot, app: appAt(p), highlight: highlight)

        if results {
            let lines = ["Invoice #1042 · Mail · 2:14 PM", "Invoices 2026 · Numbers · 11:03 AM", "“send the invoice” · Meeting · Mon"]
            let list = CGRect(x: pill.minX, y: pill.maxY + 6, width: pill.width + 60, height: CGFloat(lines.count) * 30 + 8)
            g.fill(Path(roundedRect: list, cornerRadius: 10), with: .color(Color(red: 0.15, green: 0.15, blue: 0.18)))
            for (i, line) in lines.enumerated() {
                let row = CGRect(x: list.minX + 4, y: list.minY + 4 + CGFloat(i) * 30, width: list.width - 8, height: 28)
                if i == 0 { g.fill(Path(roundedRect: row, cornerRadius: 7), with: .color(SetupTheme.accent)) }
                g.draw(Text(line).font(.system(size: 12)).foregroundColor(.white), at: CGPoint(x: row.minX + 10, y: row.midY), anchor: .leading)
            }
        }

        let bar = CGRect(x: 18, y: s.height - 70, width: s.width - 36, height: 22)
        for b in Self.blocks {
            let x0 = bar.minX + b.from * bar.width, x1 = bar.minX + b.to * bar.width
            g.fill(Path(CGRect(x: x0, y: bar.minY, width: max(1, x1 - x0 - 2), height: bar.height)), with: .color(Self.colors[b.app]))
        }
        for i in 0...6 {
            let x = bar.minX + bar.width * CGFloat(i) / 6
            g.fill(Path(CGRect(x: x, y: bar.maxY + 8, width: 1, height: 6)), with: .color(.white.opacity(0.55)))
            g.draw(Text("\(9 + i):00").font(.system(size: 11)).foregroundColor(.white.opacity(0.6)), at: CGPoint(x: x, y: bar.maxY + 24))
        }
        let px = bar.minX + p * bar.width
        g.stroke(Path { $0.move(to: CGPoint(x: px, y: bar.minY - 10)); $0.addLine(to: CGPoint(x: px, y: bar.maxY + 4)) }, with: .color(.white), lineWidth: 2)
        g.fill(Path(ellipseIn: CGRect(x: px - 6, y: bar.minY - 16, width: 12, height: 12)), with: .color(.white))
        g.opacity = 1
    }

    /// A fake app window whose look depends on the app, so scrubbing visibly changes the picture.
    private func fakeWindow(_ g: inout GraphicsContext, _ r: CGRect, app: Int, highlight: Bool) {
        g.fill(Path(roundedRect: r, cornerRadius: 10), with: .color(Color(white: 0.93)))
        var head = Path(roundedRect: CGRect(x: r.minX, y: r.minY, width: r.width, height: 26), cornerRadius: 10)
        head.addRect(CGRect(x: r.minX, y: r.minY + 16, width: r.width, height: 10))
        g.fill(head, with: .color(Self.colors[app]))
        var rng = SeededRandom(seed: UInt64(app * 7919 + 1))
        var y = r.minY + 40, line = 0
        while y < r.maxY - 16 {
            let w = (0.35 + rng.next() * 0.55) * (r.width - 40)
            let lr = CGRect(x: r.minX + 20, y: y, width: w, height: 9)
            g.fill(Path(roundedRect: lr, cornerRadius: 4), with: .color(Color(white: line == 0 ? 0.5 : 0.8)))
            if highlight && line == 2 {
                let hl = CGRect(x: lr.minX - 4, y: lr.minY - 5, width: min(90, lr.width) + 8, height: lr.height + 10)
                g.fill(Path(roundedRect: hl, cornerRadius: 4), with: .color(.yellow.opacity(0.45)))
                g.stroke(Path(roundedRect: hl, cornerRadius: 4), with: .color(.yellow), lineWidth: 1.5)
            }
            y += line == 0 ? 26 : 18
            line += 1
        }
    }

    private func desktop(_ g: inout GraphicsContext, _ s: CGRect) {
        g.fill(Path(s), with: .linearGradient(Gradient(colors: [Color(red: 0.11, green: 0.17, blue: 0.36), Color(red: 0.07, green: 0.08, blue: 0.12)]),
                                             startPoint: .zero, endPoint: CGPoint(x: s.width, y: s.height)))
        fakeWindow(&g, CGRect(x: 26, y: 24, width: s.width * 0.52, height: s.height * 0.62), app: 5, highlight: false)
        fakeWindow(&g, CGRect(x: s.width * 0.44, y: 54, width: s.width * 0.5, height: s.height * 0.58), app: 0, highlight: false)
    }

    private func keycaps(_ g: inout GraphicsContext, _ s: CGRect, down: Bool, alpha: Double, lift: CGFloat) {
        guard alpha > 0 else { return }
        let size: CGFloat = 46, gap: CGFloat = 8
        let total = CGFloat(hotkey.count) * size + CGFloat(hotkey.count - 1) * gap
        var x = (s.width - total) / 2
        let y = s.midY - size / 2 + lift + (down ? 3 : 0)
        g.opacity = alpha
        for key in hotkey {
            let r = CGRect(x: x, y: y, width: size, height: size)
            if !down { g.fill(Path(roundedRect: r.offsetBy(dx: 0, dy: 4), cornerRadius: 9), with: .color(.black.opacity(0.6))) }
            g.fill(Path(roundedRect: r, cornerRadius: 9), with: .color(down ? SetupTheme.accent : Color(white: 0.25)))
            g.draw(Text(key).font(.system(size: 18, weight: .semibold)).foregroundColor(.white), at: CGPoint(x: r.midX, y: r.midY))
            x += size + gap
        }
        g.opacity = 1
    }

    private func appAt(_ p: Double) -> Int { (Self.blocks.first { p >= $0.from && p < $0.to } ?? Self.blocks[Self.blocks.count - 1]).app }
}

/// Same pseudo-random lines every time for a given app.
private struct SeededRandom {
    var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> Double {
        state = state &* 6364136223846793005 &+ 1442695040888963407
        return Double(state >> 11) / Double(1 << 53)
    }
}

/// Guided tour on top of the real timeline: a card that points at the bar, then the search box.
/// Steps move on by themselves when the user does the thing (scrolls, searches), or with Next.
struct TutorialCoach: View {
    @ObservedObject var model: TimelineModel
    var close: () -> Void

    private enum Anchor { case bar, search, center }
    private static let steps: [(title: String, text: String, at: Anchor)] = [
        ("This is your timeline", "Every colored block is an app you used. The newest moment is on the right, and the big picture shows what was on screen.", .bar),
        ("Go back in time", "Scroll with the trackpad or mouse wheel, drag along the bar, or press ← and →. Try it now.", .bar),
        ("Search anything", "Press / and type a word you saw or heard, like a name or a file. Pick a result to jump to that moment.", .search),
        ("You're all set", "Press \(Onboarding.hotkey) from anywhere to open this. Esc closes it, and ? shows every shortcut.", .center),
    ]
    @State private var startIndex = -1
    @State private var pulse = false

    var body: some View {
        let step = model.tutorialStep ?? 0
        let s = Self.steps[min(step, Self.steps.count - 1)]
        VStack {
            if s.at != .search { Spacer() }
            HStack {
                if s.at == .center || s.at == .bar { Spacer() }
                card(step, s)
                Spacer()
            }
            if s.at == .center { Spacer() }
        }
        .padding(.top, s.at == .search ? 70 : 0)
        .padding(.bottom, s.at == .bar ? 150 : 0)
        .padding(.horizontal, 28)
        .onAppear { startIndex = model.index; pulse = true }
        .onChange(of: model.tutorialStep) { _ in startIndex = model.index }
        .onChange(of: model.index) { i in if model.tutorialStep == 1 && i != startIndex { advance() } }
        .onChange(of: model.query) { q in if model.tutorialStep == 2 && q.trimmingCharacters(in: .whitespaces).count >= 2 { advance() } }
    }

    private func card(_ step: Int, _ s: (title: String, text: String, at: Anchor)) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            if s.at == .search {
                Image(systemName: "arrow.up").font(.system(size: 20, weight: .bold)).foregroundColor(SetupTheme.accent)
                    .offset(y: pulse ? -4 : 2).animation(.easeInOut(duration: 0.7).repeatForever(), value: pulse)
            }
            Text("STEP \(step + 1) OF \(Self.steps.count)").font(.system(size: 11, weight: .bold)).foregroundColor(SetupTheme.accent)
            Text(s.title).font(.system(size: 19, weight: .bold))
            Text(s.text).font(.system(size: 13.5)).foregroundColor(SetupTheme.dim).fixedSize(horizontal: false, vertical: true)
            HStack {
                if s.at == .bar {
                    Image(systemName: "arrow.down").font(.system(size: 20, weight: .bold)).foregroundColor(SetupTheme.accent)
                        .offset(y: pulse ? 4 : -2).animation(.easeInOut(duration: 0.7).repeatForever(), value: pulse)
                }
                Spacer()
                if step < Self.steps.count - 1 { Button("Skip tour", action: close).buttonStyle(PillButtonStyle()) }
                Button(step < Self.steps.count - 1 ? "Next" : "Done", action: advance).buttonStyle(PillButtonStyle(primary: true))
            }
            .padding(.top, 4)
        }
        .padding(20)
        .frame(width: 400)
        .background(RoundedRectangle(cornerRadius: 16).fill(Color(red: 0.12, green: 0.12, blue: 0.15)))
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(SetupTheme.accent, lineWidth: 1.5))
        .shadow(color: .black.opacity(0.5), radius: 24, y: 8)
        .foregroundColor(.white)
    }

    private func advance() {
        guard let step = model.tutorialStep else { return }
        if step >= Self.steps.count - 1 { close() } else { model.tutorialStep = step + 1 }
    }
}
