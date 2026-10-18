import Foundation

/// The timeline's app filter: only the moments when one app was in front.
enum AppFilter {
    /// A break longer than this splits the filtered timeline, so time spent in other apps shrinks to a notch.
    static let gapThreshold: TimeInterval = 60

    /// Apps in these (time-sorted) frames, the one in front longest first.
    /// A frame counts until the next screenshot, at most 60 s.
    static func apps(_ frames: [TimelineFrame]) -> [String] {
        var time: [String: Double] = [:]
        for (i, f) in frames.enumerated() where !f.appName.isEmpty {
            let secs = i + 1 < frames.count ? min(60, frames[i + 1].time.timeIntervalSince(f.time)) : 2
            time[f.appName, default: 0] += secs
        }
        return time.sorted {
            $0.value != $1.value ? $0.value > $1.value : $0.key.localizedCaseInsensitiveCompare($1.key) == .orderedAscending
        }.map(\.key)
    }

    /// The frames where `app` was in front, each with `end` set to when the next screenshot (of any app)
    /// was taken, so it does not stretch over time another app was in front. No app: all frames, unchanged.
    static func apply(_ all: [TimelineFrame], app: String?) -> [TimelineFrame] {
        guard let app else { return all }
        var out: [TimelineFrame] = []
        for (i, f) in all.enumerated() where f.appName == app {
            var g = f
            g.end = i + 1 < all.count ? all[i + 1].time : nil
            out.append(g)
        }
        return out
    }

    /// How long a filtered frame stays on screen: until the next screenshot, at most 60 s (like the bar's blocks).
    static func until(_ f: TimelineFrame) -> Date {
        let cap = f.time.addingTimeInterval(60)
        guard let e = f.end else { return cap }
        return min(max(e, f.time.addingTimeInterval(2)), cap)
    }

    /// True when `t` falls inside one of these (filtered, time-sorted) frames.
    static func covers(_ frames: [TimelineFrame], _ t: Date) -> Bool {
        var lo = 0, hi = frames.count - 1, best = -1
        while lo <= hi {
            let mid = (lo + hi) / 2
            if frames[mid].time <= t { best = mid; lo = mid + 1 } else { hi = mid - 1 }
        }
        return best >= 0 && t < until(frames[best])
    }
}
