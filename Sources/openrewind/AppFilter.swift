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

    /// The frames where `app` was in front (any app when nil) and no app in `hidden` (lower-case names, see
    /// HiddenApps) was, each with `end` set to when the next screenshot (of any app) was taken, so it does not
    /// stretch over time another app was in front. No app and nothing hidden: all frames, unchanged.
    static func apply(_ all: [TimelineFrame], app: String?, hidden: Set<String> = []) -> [TimelineFrame] {
        guard app != nil || !hidden.isEmpty else { return all }
        var out: [TimelineFrame] = []
        for (i, f) in all.enumerated() where (app == nil || f.appName == app) && !hidden.contains(f.appName.lowercased()) {
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

    /// True when, at `t`, an app in `hidden` was in front: `t` falls inside a screenshot of it in `all`
    /// (every app, time-sorted), which lasts until the next screenshot, at most 60 s.
    static func hiddenAt(_ all: [TimelineFrame], _ t: Date, hidden: Set<String>) -> Bool {
        guard !hidden.isEmpty else { return false }
        var lo = 0, hi = all.count - 1, best = -1
        while lo <= hi {
            let mid = (lo + hi) / 2
            if all[mid].time <= t { best = mid; lo = mid + 1 } else { hi = mid - 1 }
        }
        guard best >= 0, hidden.contains(all[best].appName.lowercased()) else { return false }
        let next = best + 1 < all.count ? all[best + 1].time : all[best].time.addingTimeInterval(60)
        return t < min(next, all[best].time.addingTimeInterval(60))
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

/// Apps hidden from the timeline (H, or the apps menu), saved in UserDefaults so they stay hidden on every open.
/// They are still recorded; Settings → Blocked Apps & Sites stops that. The Windows app keeps the same list in
/// settings.json (TimelineHiddenApps).
enum HiddenApps {
    static let key = "timelineHiddenApps"
    static let changed = Notification.Name("OpenRewindHiddenAppsChanged")

    /// As the timeline names them, sorted.
    static var names: [String] { UserDefaults.standard.stringArray(forKey: key) ?? [] }

    /// Lower case, for AppFilter.apply.
    static var keys: Set<String> { Set(names.map { $0.lowercased() }) }

    static func hide(_ app: String) {
        let app = app.trimmingCharacters(in: .whitespaces)
        guard !app.isEmpty, !keys.contains(app.lowercased()) else { return }
        save((names + [app]).sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending })
    }

    static func show(_ app: String) {
        save(names.filter { $0.caseInsensitiveCompare(app) != .orderedSame })
    }

    private static func save(_ list: [String]) {
        UserDefaults.standard.set(list, forKey: key)
        log("timeline: \(list.count) hidden apps")
        NotificationCenter.default.post(name: changed, object: nil)
    }
}
