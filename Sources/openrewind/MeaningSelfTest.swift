import Foundation

/// Hidden `openrewind meaning-check`, run in CI: Search by Meaning end to end on a throwaway database,
/// with a made-up model so it runs on any Mac. Stores a few screenshots and a transcript, runs the
/// indexer, and checks that every moment was saved with its vector, that searches find the right one
/// (also after reopening the database), that a blocked window is never indexed, that a write that
/// fails leaves nothing behind and the cursor in place, that cursors from the builds that saved no
/// vectors are started over only once, that a value is never left out of a statement, and that deleting
/// screenshots takes every moment made from them along. Once as stored plainly, once encrypted.
/// Windows covers the same in MeaningTests.cs.
enum MeaningSelfTest {
    /// Words hashed into 256 numbers: close enough to a model that shared words make texts similar.
    final class StubEmbedder: Embedder {
        let modelId = "selftest-words-256"
        func embed(_ text: String) -> [Float]? {
            var v = [Float](repeating: 0, count: 256)
            for w in text.lowercased().split(whereSeparator: { !$0.isLetter && !$0.isNumber }) {
                var h: UInt32 = 2166136261  // FNV-1a: the same on every run, unlike hashValue
                for b in w.utf8 { h = (h ^ UInt32(b)) &* 16777619 }
                v[Int(h % 256)] += 1
            }
            return v.contains(where: { $0 != 0 }) ? Vectors.normalize(v) : nil
        }
    }

    static func run(encrypted: Bool) -> Bool {
        let label = encrypted ? "meaning-check (encrypted)" : "meaning-check"
        var failures = 0
        func check(_ ok: Bool, _ what: String) {
            if !ok { failures += 1; print("\(label): FAILED: \(what)") }
        }
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("openrewind-meaning-check-\(UUID().uuidString.prefix(8))")
        defer { try? FileManager.default.removeItem(at: dir) }
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let path = dir.appendingPathComponent("openrewind.db")
            let key: Data? = encrypted ? RecordingFile.newKey() : nil
            var db = try Database(path: path, key: key, createEncrypted: encrypted)
            check(db.isEncrypted == encrypted, "the database is \(db.isEncrypted ? "" : "not ")encrypted")

            // A database from the builds that saved no vectors: cursors moved on, moments without a vector,
            // and no marker yet. Opening it starts them over, once.
            check(db.meaningState(Database.resetMarker) == "1", "a new database has no reset marker")
            try db.exec("""
                DELETE FROM meaning_state WHERE key = '\(Database.resetMarker)';
                INSERT INTO meaning_moments (kind, timestamp, end_time, first_id, last_id, text) VALUES ('screen', 'a', 'b', 1, 1, 'left over');
                """)
            db.setMeaningState(Database.frameCursor, "999")
            db.setMeaningState(Database.chunkCursor, "999")
            db = try Database(path: path, key: key)
            check(db.meaningState(Database.frameCursor) == nil && db.meaningState(Database.chunkCursor) == nil,
                  "cursors left from a database with no vectors were not started over")
            check(db.scalarInt("SELECT COUNT(*) FROM meaning_moments", []) == 0, "moments left from a database with no vectors were kept")
            check(db.meaningState(Database.resetMarker) == "1", "the reset left no marker")
            // Still no vectors (nothing stored yet), but the reset was done: the next open keeps the cursors.
            db.setMeaningState(Database.frameCursor, "0")
            db = try Database(path: path, key: key)
            check(db.meaningState(Database.frameCursor) == "0", "the cursors were started over again on the next open")

            // A statement never runs with a value left out, one too many, or an empty blob.
            let insertState = "INSERT INTO meaning_state (key, value) VALUES (?, ?)"
            db.lock.lock()
            let tooFew = db.runHeld(insertState, [.text("selftest")])
            let tooMany = db.runHeld(insertState, [.text("selftest"), .text("1")], blob: Data([1]))
            let emptyBlob = db.runHeld(insertState, [.text("selftest")], blob: Data())
            let right = db.runHeld(insertState, [.text("selftest"), .text("2")])
            db.lock.unlock()
            check(!tooFew, "a statement ran with a value left out")
            check(!tooMany, "a statement ran with a value too many")
            check(!emptyBlob, "an empty blob was stored")
            check(right && db.meaningState("selftest") == "2", "a statement with the right values did not run")
            try db.exec("DELETE FROM meaning_state WHERE key = 'selftest'")

            let t0 = Date().addingTimeInterval(-3600)
            @discardableResult
            func frame(_ s: TimeInterval, _ app: String, _ window: String, _ text: String, blocked: Bool = false) throws -> Int64 {
                try db.insert(Frame(id: 0, timestamp: Time.iso.string(from: t0.addingTimeInterval(s)), appName: app, windowName: window,
                                    focused: true, snapshotPath: "", text: text, textSource: "ocr", hash: 0, blocked: blocked))
            }
            let grocery = try frame(0, "Notes", "Grocery list", "buy oat milk\nbananas and bread")
            try frame(2, "Notes", "Grocery list", "buy oat milk\nbananas and bread\nfresh basil")
            try frame(4, "Mail", "Invoice 1043", "invoice 1043 from Acme is due friday")
            try frame(6, "Bank", "Accounts", "account balance secret", blocked: true)
            let lastFrame = try frame(8, "Terminal", "deploy", "git push origin main\ndeploy the website")
            let chunkTime = Time.iso.string(from: t0.addingTimeInterval(10))
            let chunkId = try db.insertChunk(filePath: "", timestamp: chunkTime, device: "test", isInput: true)
            try db.saveTranscripts(chunk: AudioChunk(id: chunkId, filePath: "", timestamp: chunkTime, device: "test", isInput: true, status: "pending"),
                                   segments: [(start: 0, end: 5, text: "we agreed to ship the release on tuesday")], engine: "test")

            let service = MeaningService(db: db)
            service.embedder = StubEmbedder()
            service.blocked = { Blocklist(apps: [], keywords: [], privateWindows: false) }
            service.pace = { .idle }
            func index() throws {
                var rounds = 0
                while try service.step(), rounds < 20 { rounds += 1 }
            }
            do { try index() } catch { check(false, "indexing threw \(error)") }

            func counts(_ db: Database) -> (moments: Int, vectors: Int, bad: Int) {
                (db.scalarInt("SELECT COUNT(*) FROM meaning_moments", []), db.scalarInt("SELECT COUNT(*) FROM meaning_vectors", []),
                 db.scalarInt("SELECT COUNT(*) FROM meaning_vectors WHERE vector IS NULL OR length(vector) != 256", []))
            }
            var c = counts(db)
            // Grocery list (2 frames), invoice, deploy, and the clip; the blocked window is left out.
            check(c.moments == 4, "\(c.moments) moments saved, expected 4")
            check(c.vectors == c.moments, "\(c.vectors) vectors for \(c.moments) moments")
            check(c.bad == 0, "\(c.bad) vectors missing or the wrong size")
            check(db.meaningProgress().moments == c.vectors, "progress counts \(db.meaningProgress().moments) moments")
            check(db.meaningState(Database.frameCursor) == String(lastFrame), "frame cursor at \(db.meaningState(Database.frameCursor) ?? "nil"), expected \(lastFrame)")
            check(db.meaningState(Database.chunkCursor) == String(chunkId), "clip cursor at \(db.meaningState(Database.chunkCursor) ?? "nil"), expected \(chunkId)")
            check(db.scalarInt("SELECT COUNT(*) FROM meaning_moments WHERE text LIKE '%secret%' OR app_name = 'Bank'", []) == 0,
                  "the blocked window was indexed")
            check((try? service.step()) == false, "the indexer found more to do after finishing")
            check(db.meaningState(Database.resetMarker) == "1", "starting the index (a new model) dropped the reset marker")

            // An empty vector is refused, not stored.
            let emptyVector = Moment(isAudio: false, start: chunkTime, end: chunkTime, firstId: lastFrame, lastId: lastFrame,
                                 appName: "Terminal", windowName: "deploy", url: "", text: "empty")
            check(!db.saveMoment(emptyVector, model: StubEmbedder().modelId, vector: Data(), cursorKey: Database.frameCursor, cursor: lastFrame),
                  "a moment with an empty vector was saved")
            check(counts(db).moments == 4, "a moment with an empty vector was kept")

            func searches(_ service: MeaningService, _ when: String) {
                let groceries = service.search("oat milk and bananas", MeaningQuery())
                check(groceries.first?.appName == "Notes" && groceries.first?.text.contains("fresh basil") == true,
                      "\(when): groceries found \(groceries.first.map { "\($0.appName): \($0.text)" } ?? "nothing")")
                check(service.search("acme invoice due", MeaningQuery()).first?.appName == "Mail", "\(when): the invoice was not found first")
                check(service.search("ship the release tuesday", MeaningQuery()).first?.isAudio == true, "\(when): the transcript was not found first")
                check(service.search("ship the release tuesday", MeaningQuery(audio: false)).first?.isAudio != true, "\(when): screen-only search returned audio")
                check(service.search("account balance", MeaningQuery()).allSatisfy { $0.appName != "Bank" }, "\(when): a search returned the blocked window")
            }
            searches(service, "search")

            // A write that fails (here a trigger refuses vectors): nothing is kept, the cursor stays, and the indexer
            // stops the round with an error (it then waits longer each time) instead of skipping the moment.
            try db.exec("CREATE TRIGGER selftest_refuse BEFORE INSERT ON meaning_vectors BEGIN SELECT RAISE(ABORT, 'refused by the self-test'); END;")
            let extra = try frame(20, "Safari", "Recipes", "lemon pasta with garlic")
            var threw = false
            do { try index() } catch is MeaningService.SaveFailed { threw = true } catch { check(false, "a failed save threw \(error)") }
            check(threw, "a failed save did not stop the round")
            c = counts(db)
            check(c.moments == 4 && c.vectors == 4, "a failed save left \(c.moments) moments and \(c.vectors) vectors")
            check(db.meaningState(Database.frameCursor) == String(lastFrame), "a failed save moved the cursor")
            check(MeaningService.backoff(failures: 1) == 60 && MeaningService.backoff(failures: 3) == 240
                  && MeaningService.backoff(failures: 50) == 3600, "the retry wait is wrong")
            // The same moment failing again and again is skipped after a few tries; another one starts the count over.
            var retry = MeaningService.Retry()
            let stuck = MeaningService.SaveFailed(cursorKey: Database.frameCursor, cursor: extra)
            var skips: [Bool] = []
            for _ in 0..<MeaningService.triesPerMoment { skips.append(retry.failed(stuck).skip != nil) }
            check(skips == Array(repeating: false, count: MeaningService.triesPerMoment - 1) + [true], "a stuck moment was skipped after \(skips)")
            check(retry.failed(stuck).skip == nil, "the count did not start over after a skip")
            check(retry.failed(MeaningService.SaveFailed(cursorKey: Database.chunkCursor, cursor: 1)).skip == nil
                  && retry.failed(stuck).skip == nil, "a different moment did not start the count over")
            check(retry.failed(stuck).pause == 3600, "the wait did not keep growing across moments")
            retry.succeeded()
            check(retry.failed(stuck).pause == 60, "a good round did not reset the wait")
            try db.exec("DROP TRIGGER selftest_refuse")
            do { try index() } catch { check(false, "indexing after the fix threw \(error)") }
            c = counts(db)
            check(c.moments == 5 && c.vectors == 5 && c.bad == 0, "after the fix: \(c.moments) moments, \(c.vectors) vectors, \(c.bad) bad")
            check(db.meaningState(Database.frameCursor) == String(extra), "after the fix the cursor is not past the new frame")

            // Read back from a fresh open of the same file (with the key when encrypted).
            db = try Database(path: path, key: key)
            check(db.isEncrypted == encrypted, "reopened: the database is \(db.isEncrypted ? "" : "not ")encrypted")
            c = counts(db)
            check(c.moments == 5 && c.vectors == 5 && c.bad == 0, "reopened: \(c.moments) moments, \(c.vectors) vectors, \(c.bad) bad")
            let reopened = MeaningService(db: db)
            reopened.embedder = StubEmbedder()
            reopened.blocked = { Blocklist(apps: [], keywords: [], privateWindows: false) }
            searches(reopened, "reopened")
            check(reopened.search("lemon pasta garlic", MeaningQuery()).first?.appName == "Safari", "reopened: the new moment was not found")
            check(db.meaningState(Database.frameCursor) == String(extra) && db.meaningState(Database.chunkCursor) == String(chunkId),
                  "reopened: the cursors were started over although vectors exist")

            // Deleting screenshots takes their moments along, in the same transaction. Here also one whose
            // times don't match its frames (as one stored in a race would be): it goes because its frame is gone.
            let slipped = Moment(isAudio: false, start: "2999-01-01T00:00:00.000Z", end: "2999-01-01T00:00:01.000Z", firstId: grocery,
                                 lastId: grocery, appName: "Notes", windowName: "Grocery list", url: "", text: "buy oat milk")
            check(db.saveMoment(slipped, model: StubEmbedder().modelId, vector: Vectors.quantize(StubEmbedder().embed("buy oat milk") ?? [1]),
                                cursorKey: Database.frameCursor, cursor: extra), "could not store the extra moment")
            check(db.deleteFrames(from: nil, to: t0.addingTimeInterval(3)) == 2, "the grocery screenshots were not deleted")
            c = counts(db)
            check(db.scalarInt("SELECT COUNT(*) FROM meaning_moments WHERE app_name = 'Notes' OR text LIKE '%oat milk%'", []) == 0,
                  "moments of deleted screenshots are still there")
            check(c.moments == 4 && c.vectors == 4, "after the delete: \(c.moments) moments, \(c.vectors) vectors")
            check(reopened.search("oat milk and bananas", MeaningQuery()).allSatisfy { $0.appName != "Notes" }, "a search found deleted screenshots")
        } catch {
            check(false, "\(error)")
        }
        print(failures == 0 ? "\(label): all passed" : "\(label): \(failures) failed")
        return failures == 0
    }
}
