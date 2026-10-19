import SwiftUI

/// Pick websites and links to block: type one, or tick sites and pages from what was recorded lately.
/// Used by setup and Blocked Apps & Sites. The Windows app has the same in SitePicker.cs.
struct SitePickerView: View {
    let db: Database?
    /// Rules already blocked; left out of the lists.
    var already: Set<String>
    /// Called with the rules to block, or [] when cancelled.
    var done: ([String]) -> Void

    @State private var sites: [RecentSite]?
    @State private var chosen: [String] = []
    @State private var typed = ""
    @State private var note = "A site covers all its pages; a link covers that page and the ones under it."
    @State private var noteColor = SetupTheme.faint
    @State private var showPages = false
    @State private var query = ""

    private struct Row: Identifiable {
        var id: String { rule }
        var rule: String
        var title: String
        var detail: String
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Block Websites and Links").font(.system(size: 17, weight: .bold))
            HStack(spacing: 8) {
                TextField("Type a site (chase.com) or paste a link", text: $typed, onCommit: addTyped)
                    .textFieldStyle(.plain).font(.system(size: 14))
                    .padding(.horizontal, 12).padding(.vertical, 9)
                    .background(RoundedRectangle(cornerRadius: 10).fill(SetupTheme.card))
                Button("Add", action: addTyped).buttonStyle(PillButtonStyle(primary: true))
            }
            Text(note).font(.system(size: 12)).foregroundColor(noteColor).lineLimit(1)

            HStack {
                Text("Recently visited").font(.system(size: 15, weight: .semibold))
                Spacer()
                Picker("", selection: $showPages) {
                    Text("Sites").tag(false)
                    Text("Pages").tag(true)
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 160)
            }
            .padding(.top, 4)
            HStack(spacing: 8) {
                Image(systemName: "magnifyingglass").foregroundColor(SetupTheme.faint)
                TextField(showPages ? "Search recent pages" : "Search recent sites", text: $query).textFieldStyle(.plain).font(.system(size: 14))
            }
            .padding(.horizontal, 12).padding(.vertical, 9)
            .background(RoundedRectangle(cornerRadius: 10).fill(SetupTheme.card))

            if let sites {
                let shown = rows(sites)
                if shown.isEmpty {
                    Spacer()
                    Text(sites.isEmpty ? "No websites recorded yet. Sites you visit show up here, or type one above."
                         : query.isEmpty ? "Every recent site is already blocked" : "Nothing matches “\(query)”")
                        .foregroundColor(SetupTheme.dim).multilineTextAlignment(.center).frame(maxWidth: .infinity)
                    Spacer()
                } else {
                    ScrollView {
                        LazyVStack(spacing: 4) {
                            ForEach(shown) { row in
                                CheckRowView(emoji: showPages ? "🔗" : "🌐", title: row.title, detail: row.detail, on: Binding(
                                    get: { chosen.contains(row.rule) },
                                    set: { on in
                                        if on { if !chosen.contains(row.rule) { chosen.append(row.rule) } } else { chosen.removeAll { $0 == row.rule } }
                                    }))
                            }
                        }
                    }
                }
            } else {
                Spacer()
                ProgressView().controlSize(.small).frame(maxWidth: .infinity)
                Spacer()
            }

            HStack {
                Spacer()
                Button("Cancel") { done([]) }.buttonStyle(PillButtonStyle())
                Button("Block Selected") { done(chosen) }.buttonStyle(PillButtonStyle(primary: true)).disabled(chosen.isEmpty)
            }
        }
        .padding(20)
        .frame(width: 500, height: 600)
        .background(SetupTheme.bg)
        .foregroundColor(.white)
        .preferredColorScheme(.dark)
        .onAppear(perform: load)
    }

    /// Rows for the tab and search text. Sites or pages already blocked are left out.
    private func rows(_ sites: [RecentSite]) -> [Row] {
        let q = query.trimmingCharacters(in: .whitespaces)
        func hit(_ fields: String...) -> Bool { q.isEmpty || fields.contains { $0.localizedCaseInsensitiveContains(q) } }
        let lower = Set(already.map { $0.lowercased() })
        if !showPages {
            let now = Date()
            return sites.filter { !lower.contains($0.site) && hit($0.site) }.prefix(400).map { s in
                Row(rule: s.site, title: s.site, detail: "\(RecentSites.ago(s.lastSeen, now: now)) · \(s.pages.count) \(s.pages.count == 1 ? "page" : "pages")")
            }
        }
        var out: [Row] = []
        var seen = Set<String>()
        for p in sites.flatMap(\.pages).sorted(by: { $0.lastSeen > $1.lastSeen }) {
            guard let rule = SiteRules.normalize(p.url), !lower.contains(rule.lowercased()), hit(p.title, p.url),
                  seen.insert(rule).inserted else { continue }
            out.append(Row(rule: rule, title: p.title.isEmpty ? rule : p.title, detail: rule))
            if out.count >= 400 { break }
        }
        return out
    }

    private func addTyped() {
        let text = typed.trimmingCharacters(in: .whitespaces)
        guard let rule = SiteRules.normalize(text) else {
            if !text.isEmpty { note = "“\(text)” is not a website address."; noteColor = SetupTheme.warn }
            return
        }
        if !chosen.contains(rule) { chosen.append(rule) }
        typed = ""
        note = already.contains(rule) ? "\(rule) is already blocked." : "\(rule) will be blocked."
        noteColor = SetupTheme.good
    }

    private func load() {
        guard sites == nil else { return }
        DispatchQueue.global(qos: .userInitiated).async {
            let found = RecentSites.group(db?.recentPages() ?? [])
            DispatchQueue.main.async { sites = found }
        }
    }
}
