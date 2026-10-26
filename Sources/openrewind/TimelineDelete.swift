import AppKit

/// Delete on the timeline: the shown screenshot, the range marked with [ and ], or every screenshot that has the
/// searched words (a password or key that was on screen). Database.deleteFrames takes the screen text, the
/// search index rows, Search by Meaning moments, stars, meeting titles and the day's recap with the pictures, and
/// has videos written again without them; flushDeleted then empties the write-ahead log, so the deleted text is
/// not left in the database file either. Windows: TimelineDelete.cs.
extension TimelineModel {
    /// ⌫ / ⌦: the marked range when both ends are marked, otherwise the shown screenshot.
    func deletePressed() {
        if clipStart != nil && clipEnd != nil { deleteMarkedRange() } else { deleteShownScreenshot() }
    }

    func deleteShownScreenshot() {
        guard let f = current else { show(notice: "Nothing to delete here"); return }
        let when = f.time.formatted(date: .abbreviated, time: .standard)
        guard confirm("Delete this screenshot?",
                      "The screenshot from \(when) and its screen text are deleted, with what search and Ask AI learned from it, "
                      + "and the journal recap of that day. This cannot be undone.",
                      buttons: ["Delete Screenshot"]) == 0 else { return }
        let id = f.id
        runDelete(at: f.time) { db in
            let n = db.deleteFrame(id: id)
            return n > 0 ? "Screenshot deleted" : "That screenshot was already gone"
        }
    }

    /// The marked range, as the timeline shows it: with the app filter on, only that app's screenshots go, and
    /// apps hidden from the timeline are never touched. Without either, every screenshot in the range goes.
    func deleteMarkedRange() {
        guard let range = markedRange else { show(notice: "Mark where to start and end with [ and ] first"); return }
        let app = appFilter, hidden = hiddenKeys
        let filtered = app != nil || !hidden.isEmpty
        let ids = filtered ? Self.shownIds(db, from: range.from, to: range.to, app: app, hidden: hidden) : []
        let n = filtered ? ids.count
            : db.scalarInt("SELECT COUNT(*) FROM frames WHERE timestamp >= ? AND timestamp < ?",
                           [.text(Time.iso.string(from: range.from)), .text(Time.iso.string(from: range.to))])
        guard n > 0 else { show(notice: "No screenshots shown in that stretch"); return }
        let a = range.from.formatted(date: .abbreviated, time: .shortened), b = range.to.formatted(date: .omitted, time: .shortened)
        let what = "\(n) screenshot\(n == 1 ? "" : "s")" + (app.map { " of \($0)" } ?? "")
        let kept = app != nil ? " Other apps in that time are kept, and so is the audio."
            : !hidden.isEmpty ? " Apps hidden from the timeline are kept." : ""
        let text = "\(what) and their screen text are deleted, with what search and Ask AI learned from them, and the journal recap of "
            + "that day.\(kept)" + (app == nil ? " Audio is only deleted when you choose it (each audio clip that starts in the range)." : "")
            + " This cannot be undone."
        let choice = confirm("Delete \(what) from \(a) to \(b)?", text,
                             buttons: app == nil ? ["Delete Screenshots", "Delete Screenshots and Audio"] : ["Delete \(what)"])
        guard choice >= 0 else { return }
        let audio = choice == 1
        clearMarks()
        runDelete(at: range.from) { db in
            let f = filtered ? ids.reduce(0) { $0 + db.deleteFrame(id: $1) } : db.deleteFrames(from: range.from, to: range.to)
            let s = audio ? db.deleteAudio(from: range.from, to: range.to) : 0
            return "Deleted \(f) screenshot\(f == 1 ? "" : "s")" + (app.map { " of \($0)" } ?? "")
                + (audio ? " and \(s) audio clip\(s == 1 ? "" : "s")" : "")
        }
    }

    /// The screenshots the timeline shows from `from` up to `to` with this app filter and these hidden apps
    /// (lowercased names), oldest first.
    static func shownIds(_ db: Database, from: Date, to: Date, app: String?, hidden: Set<String>) -> [Int64] {
        // Compared as the database stores times (to the millisecond); timelineFrames also adds the first one after `to`.
        let end = Time.iso.string(from: to)
        return AppFilter.apply(db.timelineFrames(from: from, to: to), app: app, hidden: hidden)
            .filter { Time.iso.string(from: $0.time) < end }.map(\.id)
    }

    /// Every screenshot that has the words in the search box, in every app and at any time.
    func deleteMatching() {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { show(notice: "Search for the words first"); return }
        let ids = db.frameIds(matching: q)
        guard !ids.isEmpty else { show(notice: "No screenshots have “\(q)”"); return }
        guard confirm("Delete every screenshot with “\(q)”?",
                      "\(ids.count) screenshot\(ids.count == 1 ? "" : "s") in every app, from any day, have these words on screen or in the window title. "
                      + "They and their screen text are deleted, with what search and Ask AI learned from them, and the journal recaps of those days. "
                      + "This cannot be undone.",
                      buttons: ["Delete \(ids.count) Screenshot\(ids.count == 1 ? "" : "s")"]) == 0 else { return }
        runDelete(at: playhead) { db in
            let n = ids.reduce(0) { $0 + db.deleteFrame(id: $1) }
            return "Deleted \(n) screenshot\(n == 1 ? "" : "s") with “\(q)”"
        }
    }

    /// The trash button's Select a Stretch: the bar grows, and the next drag across it marks what to delete.
    func startSelecting() {
        selectingRange = true
        stopAudio()
        show(notice: "Drag across the bar at the bottom to select a stretch. ⇧-drag does it any time.")
    }

    /// A drag across the bar (Select a Stretch, or ⇧-drag): marks it like [ and ] do.
    func select(_ a: Date, _ b: Date) {
        clipStart = min(a, b)
        clipEnd = max(a, b)
    }

    var canDeleteMatching: Bool { !query.trimmingCharacters(in: .whitespaces).isEmpty && !searchingByMeaning && !starredOnly }

    /// The marked range from the screenshot shown at its start through the one at its end, or nil without both marks.
    var markedRange: (from: Date, to: Date)? {
        guard let s = clipStart, let e = clipEnd, !frames.isEmpty else { return nil }
        let (a, b) = s <= e ? (s, e) : (e, s)
        // The screenshot on screen at the start mark was taken a little before it.
        let shown = frames[frameIndex(at: a)].time
        return (from: min(a, shown), to: b.addingTimeInterval(0.01))
    }

    /// Runs the delete off the main thread, then shows the timeline again at `date` and tells what was done.
    private func runDelete(at date: Date, _ work: @escaping (Database) -> String) {
        guard !deleting else { show(notice: "Still deleting…"); return }
        deleting = true
        stopAudio()
        show(notice: "Deleting…")
        let store = db
        DispatchQueue.global(qos: .userInitiated).async {
            let done = work(store)
            store.flushDeleted()
            log("timeline delete: \(done)")
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.deleting = false
                self.highlightCache = [:]
                self.highlightOrder = []
                self.reloadAfterDelete(at: date)
                let q = self.query.trimmingCharacters(in: .whitespaces)
                if !q.isEmpty { self.runSearch(q) }
                self.show(notice: done)
            }
        }
    }

    /// The index of the button chosen (in `buttons` order), or -1 for Cancel.
    private func confirm(_ title: String, _ text: String, buttons: [String]) -> Int {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.alertStyle = .warning
        for b in buttons { a.addButton(withTitle: b).hasDestructiveAction = true }
        a.addButton(withTitle: "Cancel")
        let r = Dialogs.run(a).rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
        return r < buttons.count ? r : -1
    }
}
