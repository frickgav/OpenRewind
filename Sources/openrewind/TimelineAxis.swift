import Foundation

/// Maps real time to a position on the timeline bar where long breaks are shrunk.
/// Recorded stretches ("sessions") keep their real length; each break between them
/// takes a small fixed width, so a 2-hour gap does not eat the whole bar.
struct TimelineAxis {
    struct Session { var start: Date; var end: Date }

    /// A break longer than this splits sessions.
    static let gapThreshold: TimeInterval = 5 * 60

    let sessions: [Session]
    /// Width of one break, in "bar seconds".
    let gapV: TimeInterval
    /// Bar position where each session starts.
    private let vStart: [TimeInterval]

    init(sessions: [Session], gapV: TimeInterval) {
        self.sessions = sessions
        self.gapV = gapV
        var acc: TimeInterval = 0
        var starts: [TimeInterval] = []
        for (i, s) in sessions.enumerated() {
            if i > 0 { acc += gapV }
            starts.append(acc)
            acc += s.end.timeIntervalSince(s.start)
        }
        vStart = starts
    }

    /// Group frames and audio into sessions. A short session is added at "now" when
    /// recording stopped a while ago, so the break up to now is shrunk too.
    /// `gap` replaces gapThreshold (the app filter splits on shorter breaks).
    static func sessions(frames: [TimelineFrame], audio: [TimelineAudio], now: Date = Date(),
                         gap: TimeInterval = gapThreshold) -> [Session] {
        var spans: [(Date, Date)] = frames.map { ($0.time, $0.end == nil ? $0.time.addingTimeInterval(2) : AppFilter.until($0)) }
        spans += audio.map { ($0.start, $0.end) }
        spans.sort { $0.0 < $1.0 }
        var out: [Session] = []
        for (a, b) in spans {
            if let last = out.last, a.timeIntervalSince(last.end) <= gap {
                out[out.count - 1].end = max(last.end, b)
            } else {
                out.append(Session(start: a, end: b))
            }
        }
        if let last = out.last, now.timeIntervalSince(last.end) > gap {
            out.append(Session(start: now, end: now))
        } else if let last = out.last, now > last.end {
            out[out.count - 1].end = now
        }
        return out
    }

    /// Index of the last session that starts at or before `d` (or 0).
    private func sessionIndex(at d: Date) -> Int {
        var lo = 0, hi = sessions.count - 1, best = 0
        while lo <= hi {
            let mid = (lo + hi) / 2
            if sessions[mid].start <= d { best = mid; lo = mid + 1 } else { hi = mid - 1 }
        }
        return best
    }

    func v(_ d: Date) -> TimeInterval {
        guard !sessions.isEmpty else { return 0 }
        let i = sessionIndex(at: d)
        let s = sessions[i]
        if d < s.start { return d.timeIntervalSince(s.start) }  // before the first session
        if d <= s.end || i == sessions.count - 1 { return vStart[i] + d.timeIntervalSince(s.start) }
        // Inside the break after session i: spread it over the fixed break width.
        let next = sessions[i + 1]
        let frac = d.timeIntervalSince(s.end) / max(1, next.start.timeIntervalSince(s.end))
        return vStart[i] + s.end.timeIntervalSince(s.start) + frac * gapV
    }

    func date(_ v: TimeInterval) -> Date {
        guard !sessions.isEmpty else { return Date() }
        var lo = 0, hi = vStart.count - 1, i = 0
        while lo <= hi {
            let mid = (lo + hi) / 2
            if vStart[mid] <= v { i = mid; lo = mid + 1 } else { hi = mid - 1 }
        }
        let s = sessions[i]
        let dur = s.end.timeIntervalSince(s.start)
        let into = v - vStart[i]
        if into <= dur || i == sessions.count - 1 { return s.start.addingTimeInterval(into) }
        let next = sessions[i + 1]
        let frac = min(1, (into - dur) / gapV)
        return s.end.addingTimeInterval(frac * next.start.timeIntervalSince(s.end))
    }

    /// Breaks between sessions, as (end of one session, start of the next).
    var gaps: [(from: Date, to: Date)] {
        guard sessions.count > 1 else { return [] }
        return (1..<sessions.count).map { (sessions[$0 - 1].end, sessions[$0].start) }
    }
}
