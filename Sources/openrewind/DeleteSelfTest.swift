import Foundation

/// Hidden `openrewind delete-check`, run in CI: Delete on the timeline (TimelineDelete.swift) on a throwaway
/// database. Deletes one screenshot and checks the ones around it and the star on the next one stay, finds every
/// screenshot with some words in any app, and checks that deleted text is gone from the database file and its
/// write-ahead log, not only from search. Windows covers the same in TimelineDeleteTests.cs.
enum DeleteSelfTest {
    static func run() -> Bool {
        var failures = 0
        func check(_ ok: Bool, _ what: String) {
            if !ok { failures += 1; print("delete-check: FAILED: \(what)") }
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-delete-check-\(UUID().uuidString.prefix(8))")
        defer { try? FileManager.default.removeItem(at: dir) }
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let path = dir.appendingPathComponent("openrewind.db")
            let db = try Database(path: path)
            func add(_ t: Date, _ app: String, _ text: String) throws -> Int64 {
                try db.insert(Frame(id: 0, timestamp: Time.iso.string(from: t), appName: app, windowName: "Window", focused: true,
                                    snapshotPath: "", text: text, textSource: "ocr", hash: 0))
            }
            func count(_ words: String) -> Int {
                var s = SearchQuery()
                s.q = words
                return db.search(s).total
            }
            /// True when `s` is anywhere in the database file or its write-ahead log.
            func onDisk(_ s: String) -> Bool {
                let needle = Data(s.utf8)
                return [path.path, path.path + "-wal"].contains { (try? Data(contentsOf: URL(fileURLWithPath: $0)))?.range(of: needle) != nil }
            }

            // One screenshot, and the star that points at it; the ones around it and the next star stay.
            let t = Date().addingTimeInterval(-600)
            let before = try add(t, "Terminal", "build output")
            let shown = try add(t.addingTimeInterval(2), "Terminal", "signing secret whsec_abc")
            let after = try add(t.addingTimeInterval(4), "Terminal", "build done")
            db.addStar(at: t.addingTimeInterval(3))
            db.addStar(at: t.addingTimeInterval(5))
            check(db.deleteFrame(id: shown) == 1, "the screenshot was not deleted")
            check(db.frame(id: shown) == nil && db.frame(id: before) != nil && db.frame(id: after) != nil, "the wrong screenshots were deleted")
            check(count("whsec") == 0 && count("build") == 2, "search still finds the deleted screenshot, or lost the others")
            let stars = db.stars()
            check(stars.count == 1 && abs(stars[0].time.timeIntervalSince(t.addingTimeInterval(5))) < 0.01, "the wrong stars were deleted")
            check(db.deleteFrame(id: shown) == 0, "deleting a screenshot twice counted it twice")

            // Every screenshot with the words, in any app and on any day.
            let old = Date().addingTimeInterval(-30 * 3600)
            let a = try add(old, "Terminal", "export STRIPE_KEY=sk_live_51Hxyz")
            let b = try add(old.addingTimeInterval(20 * 3600), "Safari", "dashboard sk_live_51Hxyz copied")
            _ = try add(old.addingTimeInterval(21 * 3600), "Safari", "nothing here")
            check(db.frameIds(matching: "sk_live_51Hxyz") == [a, b], "the screenshots with the words were not all found")
            for id in db.frameIds(matching: "sk_live_51Hxyz") { db.deleteFrame(id: id) }
            check(db.frameIds(matching: "sk_live_51Hxyz").isEmpty && count("nothing") == 1, "deleting by words missed one or took another")
            check(db.frameIds(matching: "   ").isEmpty, "blank words matched screenshots")

            // The deleted text is gone from the file, also where it shared pages with screenshots that stay.
            let base = Date().addingTimeInterval(-300)
            for i in 0..<200 { _ = try add(base.addingTimeInterval(-2 * Double(i + 1)), "Editor", "ordinary line number \(i) of some code") }
            let secret = try add(base, "Editor", "zqxsecretvalue987 is the signing secret")
            db.flushDeleted()
            check(onDisk("zqxsecretvalue987"), "the check cannot see text in the file")
            check(db.deleteFrame(id: secret) == 1, "the secret screenshot was not deleted")
            db.flushDeleted()
            check(!onDisk("zqxsecretvalue987"), "deleted text is still in the database file")
            check(count("ordinary") == 200, "screenshots that were not deleted went missing")

            // A stretch while the timeline shows one app: only that app's screenshots go, and hidden apps stay.
            let m = Date().addingTimeInterval(-7200)
            var slack: [Int64] = [], other: [Int64] = []
            for i in 0..<6 {
                slack.append(try add(m.addingTimeInterval(Double(4 * i)), "Slack", "chat \(i)"))
                other.append(try add(m.addingTimeInterval(Double(4 * i + 2)), i % 2 == 0 ? "Safari" : "Game", "page \(i)"))
            }
            let shown = TimelineModel.shownIds(db, from: m, to: m.addingTimeInterval(24), app: "Slack", hidden: [])
            check(shown == slack, "the stretch with the Slack filter picked other apps' screenshots")
            for id in shown { db.deleteFrame(id: id) }
            check(slack.allSatisfy { db.frame(id: $0) == nil } && other.allSatisfy { db.frame(id: $0) != nil },
                  "deleting the filtered stretch took other apps' screenshots")
            let unhidden = TimelineModel.shownIds(db, from: m, to: m.addingTimeInterval(24), app: nil, hidden: ["game"])
            check(unhidden == other.enumerated().filter { $0.offset % 2 == 0 }.map(\.element), "a hidden app's screenshots were picked")
            check(TimelineModel.shownIds(db, from: m.addingTimeInterval(2), to: m.addingTimeInterval(6), app: nil, hidden: []) == [other[0]],
                  "the end of a stretch was not left out")

            // A range, the same.
            let r = Date().addingTimeInterval(-1800)
            _ = try add(r.addingTimeInterval(101), "Browser", "qqwvtoken555 on the page")
            _ = try add(r.addingTimeInterval(103), "Browser", "qqwvtoken555 still there")
            check(db.deleteFrames(from: r.addingTimeInterval(100), to: r.addingTimeInterval(105)) == 2, "the range was not deleted")
            db.flushDeleted()
            check(!onDisk("qqwvtoken555"), "text deleted with a range is still in the database file")
        } catch {
            check(false, "\(error)")
        }
        print(failures == 0 ? "delete-check passed" : "delete-check failed (\(failures))")
        return failures == 0
    }
}
