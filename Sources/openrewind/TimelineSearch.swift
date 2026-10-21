import Foundation

/// One match from screen text or a transcript, placed on the timeline.
struct SearchHit: Identifiable {
    enum Kind { case screen, audio, star }
    var id: String
    var kind: Kind
    var time: Date
    var title: String
    var snippet: String
    var chunkId: Int64?
}

extension TimelineModel {
    /// Debounced search as you type.
    func queryChanged() {
        searchWork?.cancel()
        let q = query.trimmingCharacters(in: .whitespaces)
        refreshHighlights()
        if starredOnly { showStarred(); return }
        guard !q.isEmpty else { searchSeq += 1; searching = false; hits = []; showResults = false; return }
        let work = DispatchWorkItem { [weak self] in self?.runSearch(q) }
        searchWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2, execute: work)
    }

    /// Search by Meaning is on and its model is loaded.
    var meaningAvailable: Bool { meaning?.embedder != nil }
    /// The search box finds moments by meaning right now.
    var searchingByMeaning: Bool { byMeaning && meaningAvailable }

    /// Keyword (the exact words) or Meaning (moments about the same thing, best first).
    func setByMeaning(_ on: Bool) {
        guard byMeaning != on else { return }
        byMeaning = on
        refreshHighlights()
        let q = query.trimmingCharacters(in: .whitespaces)
        if !q.isEmpty { runSearch(q) }
    }

    func runSearch(_ q: String) {
        if starredOnly { showStarred(); return }
        searchSeq += 1
        searching = false
        if searchingByMeaning, let m = meaning {
            // Turning the question into a vector and reading every stored one takes a moment: do it off the main thread.
            let seq = searchSeq
            searching = true
            hits = []
            selectedHit = 0
            showResults = true
            var mq = MeaningQuery()
            mq.app = appFilter
            mq.limit = 50
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                let found = m.search(q, mq).compactMap(TimelineModel.searchHit)
                DispatchQueue.main.async {
                    guard let self, seq == self.searchSeq else { return }
                    self.searching = false
                    // While filtering, speech only counts when it was heard with that app in front.
                    self.hits = found.filter { self.shows($0) && ($0.kind == .screen || self.appFilter == nil || AppFilter.covers(self.frames, $0.time)) }
                    self.selectedHit = 0
                    self.showResults = true
                }
            }
            return
        }
        let hidden = hiddenKeys
        var s = SearchQuery()
        s.q = q
        s.limit = 200
        s.appName = appFilter
        let screen = db.search(s).rows.compactMap { f -> SearchHit? in
            guard let t = Time.iso.date(from: f.timestamp), appFilter == nil || f.appName == appFilter,
                  !hidden.contains(f.appName.lowercased()) else { return nil }
            return SearchHit(id: "f\(f.id)", kind: .screen, time: t,
                             title: f.appName.isEmpty ? "Screen" : f.appName,
                             snippet: Self.snippet(f.text, around: q), chunkId: nil)
        }
        let spoken = db.searchAudio(s, fetch: 200).rows.compactMap { a -> SearchHit? in
            guard let t = Time.iso.date(from: a.timestamp) else { return nil }
            // While filtering, speech only counts when it was heard with that app in front.
            if appFilter != nil && !AppFilter.covers(frames, t) { return nil }
            if AppFilter.hiddenAt(allTimelineFrames, t, hidden: hidden) { return nil }
            return SearchHit(id: "a\(a.id)", kind: .audio, time: t,
                             title: a.isInput ? "Microphone" : "System audio",
                             snippet: a.text, chunkId: a.chunkId)
        }
        hits = Self.collapse((screen + spoken).sorted { $0.time > $1.time })
        selectedHit = 0
        showResults = true
    }

    /// False for a result from an app hidden from the timeline, or speech heard while one was in front.
    private func shows(_ h: SearchHit) -> Bool {
        let hidden = hiddenKeys
        if hidden.isEmpty { return true }
        if h.kind == .screen { return !hidden.contains(h.title.lowercased()) }
        return !AppFilter.hiddenAt(allTimelineFrames, h.time, hidden: hidden)
    }

    /// A Search by Meaning result as a row in the list.
    static func searchHit(_ h: MeaningHit) -> SearchHit? {
        guard let t = Time.iso.date(from: h.timestamp) else { return nil }
        let flat = h.text.split(whereSeparator: { $0.isWhitespace }).joined(separator: " ")
        return SearchHit(id: "m\(h.id)", kind: h.isAudio ? .audio : .screen, time: t,
                         title: h.appName.isEmpty ? "Screen" : h.appName,
                         snippet: flat.count > 160 ? String(flat.prefix(160)) + "…" : flat,
                         chunkId: h.isAudio ? h.firstId : nil)
    }

    /// The same text stays on screen for many frames. Keep the first frame of each run
    /// so results are distinct moments, not 40 copies of one window.
    static func collapse(_ hits: [SearchHit]) -> [SearchHit] {
        var out: [SearchHit] = []
        for h in hits {
            if let last = out.last, last.kind == .screen, h.kind == .screen,
               last.title == h.title, last.snippet == h.snippet, last.time.timeIntervalSince(h.time) < 300 {
                out[out.count - 1] = h  // newest-first list: keep the earlier frame of the run
                continue
            }
            out.append(h)
        }
        return out
    }

    /// Go to a result: load enough history, move the playhead, play speech from that point.
    func open(_ hit: SearchHit) {
        if hit.time < loadedSince {
            let hours = Date().timeIntervalSince(hit.time) / 3600 + 1
            reload(hours: hours, keepApp: true)
        }
        jump(to: hit.time)
        showResults = false
        // Done typing: arrows and space should drive the timeline now, not the text box.
        DispatchQueue.main.async { [weak self] in self?.leaveSearchBox?() }
        if hit.kind == .audio, let a = audio.first(where: { $0.id == hit.chunkId }) { play(a) }
    }

    func openSelected() {
        guard hits.indices.contains(selectedHit) else { return }
        open(hits[selectedHit])
    }

    func moveSelection(_ d: Int) {
        guard !hits.isEmpty else { return }
        selectedHit = (selectedHit + d + hits.count) % hits.count
    }

    /// Next / previous match in time order, after the result list is closed (⌘G / ⇧⌘G).
    func nextHit(older: Bool) {
        guard !hits.isEmpty else { return }
        moveSelection(older ? 1 : -1)
        open(hits[selectedHit])
    }

    func clearSearch() {
        query = ""; hits = []; showResults = false
        searchSeq += 1; searching = false
        starredOnly = false
        refreshHighlights()
    }

    /// The words of a search, lowercased, for marking them on a screenshot.
    static func terms(_ q: String) -> [String] {
        var out: [String] = []
        for w in q.split(whereSeparator: { $0.isWhitespace }) {
            let t = w.replacingOccurrences(of: "\"", with: "").lowercased()
            if !t.isEmpty && !out.contains(t) { out.append(t) }
        }
        return out
    }

    /// Mark the searched words on the shown screenshot. Uses what is cached, otherwise reads it
    /// once scrolling settles, since reading the words off a screenshot takes a moment.
    func refreshHighlights() {
        highlightWork?.cancel()
        let terms = highlightTerms
        guard !terms.isEmpty, let f = current, image != nil else { highlights = []; return }
        let key = "\(f.id) \(terms.joined(separator: " "))"
        if let cached = highlightCache[key] { highlights = cached; return }
        highlights = []
        let db = self.db
        let work = DispatchWorkItem { [weak self] in
            DispatchQueue.global(qos: .userInitiated).async {
                // Looked up again when the picture moved since the timeline was read (see currentSource).
                let picture = FrameImages.shared.image(f.source) ?? db.frame(id: f.id).flatMap { FrameImages.shared.image($0.source) }
                let found = picture.map { OCR.find(terms, in: $0) } ?? []
                DispatchQueue.main.async {
                    guard let self else { return }
                    self.highlightCache[key] = found
                    self.highlightOrder.append(key)
                    if self.highlightOrder.count > 64 { self.highlightCache.removeValue(forKey: self.highlightOrder.removeFirst()) }
                    guard self.current?.id == f.id, self.highlightTerms == terms else { return }
                    self.highlights = found
                }
            }
        }
        highlightWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: work)
    }

    /// Words to mark on the screenshot. None for a meaning search: its matches need not share a word with the question.
    var highlightTerms: [String] { searchingByMeaning ? [] : Self.terms(query) }

    static func snippet(_ text: String, around q: String) -> String {
        let flat = text.replacingOccurrences(of: "\n", with: " ")
        let words = q.lowercased().split(separator: " ").map(String.init)
        let lower = flat.lowercased()
        let at = words.compactMap { lower.range(of: $0)?.lowerBound }.min()
        guard let at else { return String(flat.prefix(140)) }
        let startOffset = max(0, flat.distance(from: flat.startIndex, to: at) - 50)
        let start = flat.index(flat.startIndex, offsetBy: startOffset)
        let end = flat.index(start, offsetBy: min(160, flat.distance(from: start, to: flat.endIndex)))
        return (startOffset > 0 ? "…" : "") + flat[start..<end] + (end < flat.endIndex ? "…" : "")
    }
}
