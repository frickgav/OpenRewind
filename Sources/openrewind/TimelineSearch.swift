import Foundation

/// One match from screen text or a transcript, placed on the timeline.
struct SearchHit: Identifiable {
    enum Kind { case screen, audio }
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
        guard !q.isEmpty else { hits = []; showResults = false; return }
        let work = DispatchWorkItem { [weak self] in self?.runSearch(q) }
        searchWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2, execute: work)
    }

    func runSearch(_ q: String) {
        var s = SearchQuery()
        s.q = q
        s.limit = 200
        let screen = db.search(s).rows.compactMap { f -> SearchHit? in
            guard let t = Time.iso.date(from: f.timestamp) else { return nil }
            return SearchHit(id: "f\(f.id)", kind: .screen, time: t,
                             title: f.appName.isEmpty ? "Screen" : f.appName,
                             snippet: Self.snippet(f.text, around: q), chunkId: nil)
        }
        let spoken = db.searchAudio(s, fetch: 200).rows.compactMap { a -> SearchHit? in
            guard let t = Time.iso.date(from: a.timestamp) else { return nil }
            return SearchHit(id: "a\(a.id)", kind: .audio, time: t,
                             title: a.isInput ? "Microphone" : "System audio",
                             snippet: a.text, chunkId: a.chunkId)
        }
        hits = Self.collapse((screen + spoken).sorted { $0.time > $1.time })
        selectedHit = 0
        showResults = true
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
            reload(hours: hours)
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
        let terms = Self.terms(query)
        guard !terms.isEmpty, let f = current, image != nil else { highlights = []; return }
        let key = "\(f.id) \(terms.joined(separator: " "))"
        if let cached = highlightCache[key] { highlights = cached; return }
        highlights = []
        let work = DispatchWorkItem { [weak self] in
            DispatchQueue.global(qos: .userInitiated).async {
                let found = FrameStore.image(f.path).map { OCR.find(terms, in: $0) } ?? []
                DispatchQueue.main.async {
                    guard let self else { return }
                    self.highlightCache[key] = found
                    self.highlightOrder.append(key)
                    if self.highlightOrder.count > 64 { self.highlightCache.removeValue(forKey: self.highlightOrder.removeFirst()) }
                    guard self.current?.id == f.id, Self.terms(self.query) == terms else { return }
                    self.highlights = found
                }
            }
        }
        highlightWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15, execute: work)
    }

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
