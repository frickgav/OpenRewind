import Foundation

/// Hidden `openrewind connect-check <empty folder>`, run in CI: checks the Mac's one-click setup
/// editors (MCPConnect.swift) on the same cases as the Windows tests (McpConnectTests.cs), so both
/// apps read and change the same files the same way. Prints each failure; true when all pass.
enum MCPConnectCheck {
    static func run(in dir: URL) -> Bool {
        var failures = 0
        func check(_ ok: Bool, _ what: String) {
            if !ok { failures += 1; print("connect-check: FAILED: \(what)") }
        }
        func throwsConfigError(_ f: () throws -> Any?) -> Bool {
            do { _ = try f(); return false } catch is MCPConfigError { return true } catch { return false }
        }
        let name = "openrewind"
        let want = MCPCommandLine(command: "/Applications/OpenRewind.app/Contents/MacOS/OpenRewind", args: ["mcp"])
        let old = MCPCommandLine(command: "/Users/me/Downloads/OpenRewind.app/Contents/MacOS/OpenRewind", args: ["mcp"])
        func json(_ t: String?) -> MCPLink { MCPJSONConfig.status(t, name: name, want: want).0 }
        func toml(_ t: String?) -> MCPLink { MCPTOMLConfig.status(t, name: name, want: want).0 }
        func lines(_ t: String, _ f: (String) -> Bool) -> Int { t.components(separatedBy: "\n").filter(f).count }

        do {
            // JSON: the entry goes in and out; everything else stays as written.
            check(json(nil) == .notAdded, "json: no file is not added")
            let user = "{\r\n  \"b\": 1.50,\r\n  \"a\": {\"x\": [1, 2e3]},\r\n  \"mcpServers\": {\"other\": {\"command\": \"x\"}}\r\n}"
            let added = try MCPJSONConfig.add(user, name: name, want: want)
            check(json(added) == .added, "json: added")
            check(added.contains("\"b\": 1.50") && added.contains("2e3"), "json: numbers kept as written")
            check(added.range(of: "\"b\"")!.lowerBound < added.range(of: "\"a\"")!.lowerBound, "json: key order kept")
            check(added.contains("\"other\"") && added.contains("\r\n") && !added.replacingOccurrences(of: "\r\n", with: "").contains("\n"), "json: other servers and CRLF kept")
            let removed = try MCPJSONConfig.remove(added, name: name)
            check(removed.map(json) == .notAdded && removed?.contains("\"other\"") == true, "json: removed")
            check(try MCPJSONConfig.remove(user, name: name) == nil, "json: nothing to remove")
            check(json(try MCPJSONConfig.add(nil, name: name, want: old)) == .different, "json: another copy is different")

            // Turned off, and keys the button doesn't write.
            let on = try MCPJSONConfig.add(nil, name: name, want: want)
            let off = on.replacingOccurrences(of: "\"args\"", with: "\"disabled\": true,\n      \"args\"")
            check(json(off) == .turnedOff, "json: disabled is turned off")
            check(json(on.replacingOccurrences(of: "\"args\"", with: "\"disabled\": false,\n      \"args\"")) == .added, "json: disabled false is added")
            check(json(try MCPJSONConfig.add(off, name: name, want: want)) == .added, "json: add turns it back on")
            check(json(on.replacingOccurrences(of: "\"args\"", with: "\"env\": {\"TOKEN\": \"x\"},\n      \"args\"")) == .different, "json: env is different")
            check(json(on.replacingOccurrences(of: "\"args\"", with: "\"url\": \"http://x\",\n      \"args\"")) == .different, "json: url is different")
            check(json(on.replacingOccurrences(of: "\"command\"", with: "\"type\": \"sse\",\n      \"command\"")) == .different, "json: sse is different")
            check(json(try MCPJSONConfig.add(nil, name: name, want: want, stdioType: true)) == .added, "json: Claude Code's form is added")

            // Files OpenRewind can't change safely are refused, not rewritten.
            for bad in ["{\"a\": {\"x\": 1, \"x\": 2}}", "{\"mcpServers\": {}, \"mcpServers\": {}}", "{\"a\": [{\"k\": 1, \"k\": 1}]}",
                        "{\"a\": \"\\ud800x\"}", "{\"a\": \"\\udc00\"}", "{\"a\": 1,}", "// note\n{}", "[]", "{\"mcpServers\": []}"] {
                check(json(bad) == .unreadable, "json: refuses \(bad)")
                check(throwsConfigError { try MCPJSONConfig.add(bad, name: name, want: want) }, "json: add refuses \(bad)")
            }
            check(try MCPJSONConfig.add("{\"a\": \"\\ud83d\\ude00\"}", name: name, want: want).contains("😀"), "json: whole emoji kept")

            // TOML: only openrewind's table changes; Add then Remove gives back the same text.
            check(toml(nil) == .notAdded, "toml: no file is not added")
            let config = "# my settings\r\nmodel = \"gpt-5\"\r\n\r\n[profiles.work]\r\nmodel = 'o3'\r\n\r\n[mcp_servers.other]\r\ncommand = \"x\"\r\nargs = [\r\n  \"a\",\r\n]\r\n"
            let tAdded = try MCPTOMLConfig.add(config, name: name, want: want)
            check(toml(tAdded) == .added, "toml: added")
            check(tAdded.hasPrefix(config), "toml: the rest stays")
            check(try MCPTOMLConfig.remove(tAdded, name: name) == config, "toml: add then remove gives the same text")
            check(try MCPTOMLConfig.remove(config, name: name) == nil, "toml: nothing to remove")
            let tOld = try MCPTOMLConfig.add(config, name: name, want: old)
            check(toml(tOld) == .different, "toml: another copy is different")
            check(try MCPTOMLConfig.remove(try MCPTOMLConfig.add(tOld, name: name, want: want), name: name) == config, "toml: update in place")

            let tricky = "x = \"'''\"\ny = '\"\"\"'\n\n[mcp_servers.openrewind]\ncommand = 'old'\n"
            check(toml(tricky) == .different, "toml: triple quotes inside one-line strings")
            let tTricky = try MCPTOMLConfig.add(tricky, name: name, want: want)
            check(lines(tTricky) { $0.hasPrefix("[mcp_servers.openrewind]") } == 1 && toml(tTricky) == .added, "toml: one table after add")

            let inArray = "x = [\n  \"\"\"\n[mcp_servers.openrewind]\n\"\"\",\n]\n"
            check(toml(inArray) == .notAdded, "toml: multiline string inside an array")
            let tArray = try MCPTOMLConfig.add(inArray, name: name, want: want)
            check(tArray.hasPrefix(inArray) && (try MCPTOMLConfig.remove(tArray, name: name)) == inArray, "toml: array left alone")

            for header in ["[\"mcp_servers\".openrewind]", "['mcp_servers'.\"openrewind\"]"] {
                let text = "\(header)\ncommand = 'old'\n"
                check(toml(text) == .different, "toml: quoted key \(header)")
                let a = try MCPTOMLConfig.add(text, name: name, want: want)
                check(lines(a) { $0.hasPrefix("[") && $0.contains("openrewind") } == 1, "toml: one table for \(header)")
            }
            for foreign in ["[[mcp_servers.openrewind]]\ncommand = 'x'\n", "[other]\nnote = \"mcp_servers openrewind\"\n",
                            "[mcp_servers.other]\nenv = { mcp_servers = \"openrewind\" }\n", "mcp_servers.openrewind.command = 'x'\n",
                            "[mcp_servers]\nopenrewind = { command = 'x' }\n", "mcp_servers = { openrewind = { command = 'x' } }\n"] {
                check(toml(foreign) == .unreadable, "toml: leaves alone \(foreign)")
                check(throwsConfigError { try MCPTOMLConfig.add(foreign, name: name, want: want) }, "toml: add refuses \(foreign)")
            }
            let similar = "[mcp_servers.openrewind_old]\ncommand = 'x'\n"
            check(toml(similar) == .notAdded, "toml: similar names are someone else's")
            check(try MCPTOMLConfig.remove(try MCPTOMLConfig.add(similar, name: name, want: want), name: name) == similar, "toml: similar name kept")
            let tOff = try MCPTOMLConfig.add(nil, name: name, want: want) + "enabled = false\n"
            check(toml(tOff) == .turnedOff, "toml: enabled = false is turned off")
            check(toml(try MCPTOMLConfig.add(tOff, name: name, want: want)) == .added, "toml: add turns it back on")
            check(toml(try MCPTOMLConfig.add(nil, name: name, want: want) + "env = { A = 'b' }\n") == .different, "toml: env is different")
            check(MCPTOMLConfig.quote("C:\\it's") == "\"C:\\\\it's\"" && MCPTOMLConfig.quote("/a b") == "'/a b'", "toml: quoting")

            // Files: private, checked before they are replaced, one backup, all or nothing.
            let fm = FileManager.default
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
            func mode(_ u: URL) -> Int { ((try? fm.attributesOfItem(atPath: u.path))?[.posixPermissions] as? NSNumber)?.intValue ?? -1 }
            let file = dir.appendingPathComponent("sub/claude.json")
            try MCPConfigFile.change([file]) { try MCPJSONConfig.add($0, name: name, want: want) }
            check(mode(file) == 0o600, "files: a new file is private")
            check(!fm.fileExists(atPath: file.path + MCPConfigFile.backupSuffix), "files: no backup of nothing")
            try fm.setAttributes([.posixPermissions: 0o644], ofItemAtPath: file.path)
            try MCPConfigFile.change([file]) { _ in "{\"x\": 1}\n" }
            let backup = URL(fileURLWithPath: file.path + MCPConfigFile.backupSuffix)
            check(mode(file) == 0o644, "files: the file keeps its mode")
            check(mode(backup) == 0o600, "files: the backup is private")
            check(try MCPConfigFile.read(backup).map(json) == .added, "files: the backup holds the old text")
            try MCPConfigFile.change([file], removing: true) { _ in "{}\n" }
            check(!fm.fileExists(atPath: backup.path), "files: remove deletes the backup")

            var changed = false
            do { try MCPConfigFile.write(file, "{\"y\": 2}\n", expected: "{\"not\": \"this\"}\n") } catch is MCPConfigChanged { changed = true }
            check(changed && (try MCPConfigFile.read(file)) == "{}\n", "files: a file changed meanwhile is left alone")

            try Data([0x7B, 0x22, 0x70, 0x22, 0x3A, 0x22, 0xE9, 0x22, 0x7D]).write(to: file)
            check(throwsConfigError { try MCPConfigFile.read(file) }, "files: text that isn't UTF-8 is refused")
            try Data([0xEF, 0xBB, 0xBF] + Array("{}".utf8)).write(to: file)
            check(try MCPConfigFile.read(file) == "{}", "files: a byte order mark is dropped")

            let first = dir.appendingPathComponent("store.json")
            try Data("{\"a\":1}".utf8).write(to: first)
            let second = dir.appendingPathComponent("classic.json")
            try fm.createDirectory(at: second, withIntermediateDirectories: true)
            var message = ""
            do { try MCPConfigFile.change([first, second]) { try MCPJSONConfig.add($0, name: name, want: want) } } catch let e as MCPConfigError { message = e.message }
            check(message.contains("put back") && (try MCPConfigFile.read(first)) == "{\"a\":1}", "files: the first file is put back when the second fails")

            let real = dir.appendingPathComponent("dotfiles/mcp.json")
            try fm.createDirectory(at: real.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("{}".utf8).write(to: real)
            let link = dir.appendingPathComponent("link.json")
            try fm.createSymbolicLink(at: link, withDestinationURL: real)
            try MCPConfigFile.change([link]) { try MCPJSONConfig.add($0, name: name, want: want) }
            check((try? fm.destinationOfSymbolicLink(atPath: link.path)) != nil && (try MCPConfigFile.read(real)).map(json) == .added, "files: a link stays a link")

            let leftovers = (fm.enumerator(atPath: dir.path)?.allObjects as? [String] ?? []).filter { $0.hasSuffix(".tmp") }
            check(leftovers.isEmpty, "files: no temporary files left: \(leftovers)")
        } catch {
            failures += 1
            print("connect-check: FAILED: unexpected \(error)")
        }
        print(failures == 0 ? "connect-check: all passed" : "connect-check: \(failures) failed")
        return failures == 0
    }
}
