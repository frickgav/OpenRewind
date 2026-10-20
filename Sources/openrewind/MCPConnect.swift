import AppKit
import Foundation

/// Where an AI app's setup stands for this copy of OpenRewind.
enum MCPLink {
    /// No openrewind entry.
    case notAdded
    /// An entry that starts this copy, the same way the button would add it.
    case added
    /// An entry that starts another copy (an old path) or with other options; Add brings it up to date.
    case different
    /// This copy's entry, turned off in the app ("disabled": true, enabled = false). Add turns it back on.
    case turnedOff
    /// The file is there but OpenRewind can't read or safely change it (not valid JSON, an
    /// openrewind entry it did not write, …). Nothing is changed; the user sets it up by hand.
    case unreadable
}

/// How the AI app starts the server: the program and its arguments.
struct MCPCommandLine {
    var command: String
    var args: [String]

    func same(command c: String?, args a: [String]?) -> Bool { c == command && a == args }
}

/// The file can't be changed safely; `message` says why, in words for the user.
struct MCPConfigError: Error {
    let message: String
}

// MARK: - JSON

/// A JSON value that keeps objects' key order and numbers exactly as written, so a setup file
/// read and written back changes only where OpenRewind changed it. Foundation's JSONSerialization
/// sorts keys and rewrites numbers.
indirect enum OrderedJSON {
    case object([(key: String, value: OrderedJSON)])
    case array([OrderedJSON])
    case string(String)
    case number(String)
    case bool(Bool)
    case null

    subscript(key: String) -> OrderedJSON? {
        guard case .object(let members) = self else { return nil }
        return members.first(where: { $0.key == key })?.value
    }

    /// Sets a key, in place when it is there, at the end when it is not.
    mutating func set(_ key: String, _ value: OrderedJSON) {
        guard case .object(var members) = self else { return }
        if let i = members.firstIndex(where: { $0.key == key }) { members[i].value = value } else { members.append((key, value)) }
        self = .object(members)
    }

    mutating func remove(_ key: String) {
        guard case .object(var members) = self else { return }
        members.removeAll { $0.key == key }
        self = .object(members)
    }

    // Parsing: strict RFC 8259 (no comments, no trailing commas, no repeated keys), like the Windows app.

    struct ParseError: Error {}

    static func parse(_ text: String) throws -> OrderedJSON {
        var p = Parser(bytes: Array(text.utf8))
        p.space()
        let value = try p.value(depth: 0)
        p.space()
        guard p.i == p.bytes.count else { throw ParseError() }
        return value
    }

    private struct Parser {
        let bytes: [UInt8]
        var i = 0

        mutating func space() {
            while i < bytes.count, [0x20, 0x09, 0x0A, 0x0D].contains(bytes[i]) { i += 1 }
        }

        mutating func expect(_ s: String) throws {
            for b in s.utf8 {
                guard i < bytes.count, bytes[i] == b else { throw ParseError() }
                i += 1
            }
        }

        mutating func value(depth: Int) throws -> OrderedJSON {
            guard depth < 512, i < bytes.count else { throw ParseError() }
            switch bytes[i] {
            case UInt8(ascii: "{"):
                i += 1
                var members: [(key: String, value: OrderedJSON)] = []
                var seen = Set<String>()
                space()
                if i < bytes.count, bytes[i] == UInt8(ascii: "}") { i += 1; return .object(members) }
                while true {
                    space()
                    guard i < bytes.count, bytes[i] == UInt8(ascii: "\"") else { throw ParseError() }
                    let key = try string()
                    guard seen.insert(key).inserted else { throw ParseError() }
                    space()
                    try expect(":")
                    space()
                    members.append((key, try value(depth: depth + 1)))
                    space()
                    guard i < bytes.count else { throw ParseError() }
                    if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                    if bytes[i] == UInt8(ascii: "}") { i += 1; return .object(members) }
                    throw ParseError()
                }
            case UInt8(ascii: "["):
                i += 1
                var items: [OrderedJSON] = []
                space()
                if i < bytes.count, bytes[i] == UInt8(ascii: "]") { i += 1; return .array(items) }
                while true {
                    space()
                    items.append(try value(depth: depth + 1))
                    space()
                    guard i < bytes.count else { throw ParseError() }
                    if bytes[i] == UInt8(ascii: ",") { i += 1; continue }
                    if bytes[i] == UInt8(ascii: "]") { i += 1; return .array(items) }
                    throw ParseError()
                }
            case UInt8(ascii: "\""): return .string(try string())
            case UInt8(ascii: "t"): try expect("true"); return .bool(true)
            case UInt8(ascii: "f"): try expect("false"); return .bool(false)
            case UInt8(ascii: "n"): try expect("null"); return .null
            default: return .number(try number())
            }
        }

        /// How many digits were skipped.
        mutating func digits() -> Int {
            let s = i
            while i < bytes.count, bytes[i] >= 0x30, bytes[i] <= 0x39 { i += 1 }
            return i - s
        }

        mutating func number() throws -> String {
            let start = i
            if i < bytes.count, bytes[i] == UInt8(ascii: "-") { i += 1 }
            guard i < bytes.count else { throw ParseError() }
            if bytes[i] == UInt8(ascii: "0") { i += 1 } else if digits() == 0 { throw ParseError() }
            if i < bytes.count, bytes[i] == UInt8(ascii: ".") { i += 1; guard digits() > 0 else { throw ParseError() } }
            if i < bytes.count, bytes[i] == UInt8(ascii: "e") || bytes[i] == UInt8(ascii: "E") {
                i += 1
                if i < bytes.count, bytes[i] == UInt8(ascii: "+") || bytes[i] == UInt8(ascii: "-") { i += 1 }
                guard digits() > 0 else { throw ParseError() }
            }
            return String(decoding: bytes[start..<i], as: UTF8.self)
        }

        mutating func hex4() throws -> UInt32 {
            guard i + 4 <= bytes.count, let v = UInt32(String(decoding: bytes[i..<i + 4], as: UTF8.self), radix: 16) else { throw ParseError() }
            i += 4
            return v
        }

        mutating func string() throws -> String {
            i += 1  // the opening quote
            var out = [UInt8]()
            while i < bytes.count {
                let b = bytes[i]
                if b == UInt8(ascii: "\"") { i += 1; return String(decoding: out, as: UTF8.self) }
                if b < 0x20 { throw ParseError() }
                if b != UInt8(ascii: "\\") { out.append(b); i += 1; continue }
                i += 1
                guard i < bytes.count else { throw ParseError() }
                let e = bytes[i]
                i += 1
                switch e {
                case UInt8(ascii: "\""): out.append(0x22)
                case UInt8(ascii: "\\"): out.append(0x5C)
                case UInt8(ascii: "/"): out.append(0x2F)
                case UInt8(ascii: "b"): out.append(0x08)
                case UInt8(ascii: "f"): out.append(0x0C)
                case UInt8(ascii: "n"): out.append(0x0A)
                case UInt8(ascii: "r"): out.append(0x0D)
                case UInt8(ascii: "t"): out.append(0x09)
                case UInt8(ascii: "u"):
                    var code = try hex4()
                    if code >= 0xD800, code < 0xDC00, i + 6 <= bytes.count, bytes[i] == UInt8(ascii: "\\"), bytes[i + 1] == UInt8(ascii: "u") {
                        let save = i
                        i += 2
                        let low = try hex4()
                        if low >= 0xDC00, low < 0xE000 { code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00) } else { i = save }
                    }
                    // Half of a surrogate pair on its own isn't a character: the file is refused
                    // rather than written back changed, as the Windows app does.
                    guard let scalar = Unicode.Scalar(code) else { throw ParseError() }
                    out.append(contentsOf: Array(String(Character(scalar)).utf8))
                default: throw ParseError()
                }
            }
            throw ParseError()
        }
    }

    // Writing: two-space indents, like the Windows app (System.Text.Json) writes them.

    func formatted() -> String {
        var out = ""
        write(&out, indent: 0)
        return out
    }

    private func write(_ out: inout String, indent: Int) {
        let pad = String(repeating: "  ", count: indent + 1)
        let end = String(repeating: "  ", count: indent)
        switch self {
        case .object(let members):
            if members.isEmpty { out += "{}"; return }
            out += "{\n"
            for (n, m) in members.enumerated() {
                out += pad + Self.quote(m.key) + ": "
                m.value.write(&out, indent: indent + 1)
                out += n == members.count - 1 ? "\n" : ",\n"
            }
            out += end + "}"
        case .array(let items):
            if items.isEmpty { out += "[]"; return }
            out += "[\n"
            for (n, item) in items.enumerated() {
                out += pad
                item.write(&out, indent: indent + 1)
                out += n == items.count - 1 ? "\n" : ",\n"
            }
            out += end + "]"
        case .string(let s): out += Self.quote(s)
        case .number(let raw): out += raw
        case .bool(let b): out += b ? "true" : "false"
        case .null: out += "null"
        }
    }

    /// Quotes, backslashes and control characters escaped; everything else (é, 外) written as is.
    static func quote(_ s: String) -> String {
        var out = "\""
        for u in s.unicodeScalars {
            switch u {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{08}": out += "\\b"
            case "\u{0C}": out += "\\f"
            default:
                if u.value < 0x20 || u.value == 0x7F { out += String(format: "\\u%04X", u.value) } else { out.unicodeScalars.append(u) }
            }
        }
        return out + "\""
    }
}

/// Adds and removes the `mcpServers.openrewind` entry in JSON setup files: Claude Desktop's
/// claude_desktop_config.json, Cursor's mcp.json and Claude Code's ~/.claude.json. Everything else
/// in the file stays as it was. A file that is not valid JSON is never rewritten.
/// The Windows app does the same in McpConnect.cs (McpJsonConfig), with tests.
enum MCPJSONConfig {
    static let serversKey = "mcpServers"
    static let notJSON = "it isn't valid JSON (or has comments)"

    private static func parse(_ text: String?) throws -> OrderedJSON {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .object([]) }
        let root: OrderedJSON
        do { root = try OrderedJSON.parse(text) } catch { throw MCPConfigError(message: notJSON) }
        guard case .object = root else { throw MCPConfigError(message: "it doesn't hold a JSON object") }
        if let servers = root[serversKey] {
            guard case .object = servers else { throw MCPConfigError(message: "its “\(serversKey)” is not a JSON object") }
        }
        return root
    }

    static func status(_ text: String?, name: String, want: MCPCommandLine) -> (MCPLink, String?) {
        let root: OrderedJSON
        do { root = try parse(text) } catch let e as MCPConfigError { return (.unreadable, e.message) } catch { return (.unreadable, notJSON) }
        guard let entry = root[serversKey]?[name] else { return (.notAdded, nil) }
        guard case .object(let members) = entry else { return (.different, nil) }
        // Any key the button doesn't write means the user set it up their own way.
        if members.contains(where: { !ownKeys.contains($0.key) }) { return (.different, nil) }
        if let env = entry["env"] {
            guard case .object(let vars) = env, vars.isEmpty else { return (.different, nil) }
        }
        var command: String?
        if case .string(let c)? = entry["command"] { command = c }
        var args: [String]? = []
        if let a = entry["args"] {
            if case .array(let items) = a {
                args = []
                for item in items {
                    if case .string(let s) = item { args?.append(s) } else { args = nil; break }
                }
            } else { args = nil }
        }
        // A server that isn't stdio is not what the button adds.
        if let t = entry["type"] { if case .string("stdio") = t {} else { return (.different, nil) } }
        guard want.same(command: command, args: args) else { return (.different, nil) }
        if case .bool(true)? = entry["disabled"] { return (.turnedOff, nil) }
        return (.added, nil)
    }

    /// The keys the button writes (and "disabled", which the app sets to turn it off).
    private static let ownKeys: Set<String> = ["type", "command", "args", "env", "disabled"]

    /// The file with the openrewind entry added, or replaced if one is there. `stdioType` writes
    /// "type": "stdio" too, as Claude Code does.
    static func add(_ text: String?, name: String, want: MCPCommandLine, stdioType: Bool = false) throws -> String {
        var root = try parse(text)
        var servers = root[serversKey] ?? .object([])
        var entry: [(key: String, value: OrderedJSON)] = []
        if stdioType { entry.append(("type", .string("stdio"))) }
        entry.append(("command", .string(want.command)))
        entry.append(("args", .array(want.args.map { OrderedJSON.string($0) })))
        if stdioType { entry.append(("env", .object([]))) }
        servers.set(name, .object(entry))
        root.set(serversKey, servers)
        return format(root, original: text)
    }

    /// The file without the openrewind entry, or nil when there is none (nothing to change).
    static func remove(_ text: String?, name: String) throws -> String? {
        guard let text, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        var root = try parse(text)
        guard var servers = root[serversKey], servers[name] != nil else { return nil }
        servers.remove(name)
        root.set(serversKey, servers)
        return format(root, original: text)
    }

    /// The line endings the file had, and a newline at the end.
    private static func format(_ root: OrderedJSON, original: String?) -> String {
        let crlf = original?.contains("\r\n") == true
        let json = root.formatted()
        return crlf ? json.replacingOccurrences(of: "\n", with: "\r\n") + "\r\n" : json + "\n"
    }
}

// MARK: - TOML

/// Adds and removes the `[mcp_servers.openrewind]` table in config.toml, where the ChatGPT desktop
/// app (and Codex) keep their MCP servers. TOML is edited as lines: only the openrewind table is
/// written or taken out, so the rest of the file stays exactly as it was, comments included.
/// When the file defines openrewind some other way (an inline table, dotted keys), it is left alone.
/// The Windows app does the same in McpConnect.cs (McpTomlConfig), with tests.
enum MCPTOMLConfig {
    static let marker = "# Added by OpenRewind (Connect AI Apps). Remove it there or delete this table."

    private static func regex(_ pattern: String) -> NSRegularExpression { try! NSRegularExpression(pattern: pattern) }
    private static func matches(_ r: NSRegularExpression, _ s: String) -> NSTextCheckingResult? {
        r.firstMatch(in: s, range: NSRange(location: 0, length: (s as NSString).length))
    }

    /// mcp_servers as a TOML key: bare or quoted.
    private static let servers = #"(?:mcp_servers|"mcp_servers"|'mcp_servers')"#
    private static let anyHeader = regex(#"^\s*\["#)
    private static let serversHeader = regex(#"^\s*\[\s*"# + servers + #"\s*\]\s*(#.*)?$"#)
    private static let inlineServers = regex(#"^\s*"# + servers + #"\s*="#)

    private static func names(_ name: String) -> String {
        let n = NSRegularExpression.escapedPattern(for: name)
        return "(?:\(n)|\"\(n)\"|'\(n)')"
    }

    private struct Line {
        var text: String
        var own: Bool
        var ownMain: Bool
        var header: Bool
    }

    static func foreign(_ name: String) -> String { "it already sets up “\(name)” in a way OpenRewind doesn't change" }

    /// The file as lines, which of them belong to openrewind's tables, and whether openrewind is
    /// also defined in a way only the user should change.
    private static func scan(_ text: String, name: String) -> (lines: [Line], foreign: Bool) {
        let own = regex(#"^\s*\[\s*"# + servers + #"\s*\.\s*"# + names(name) + #"\s*(\.[^\]]*)?\]\s*(#.*)?$"#)
        let dotted = regex(#"^\s*"# + servers + #"\s*\.\s*"# + names(name) + #"\s*[.=]"#)
        let keyInServers = regex(#"^\s*"# + names(name) + #"\s*[.=]"#)
        // A line that names both mcp_servers and openrewind (as a whole word) outside a comment.
        let mentions = regex("mcp_servers.*(?<![\\w-])" + NSRegularExpression.escapedPattern(for: name) + "(?![\\w-])")
        var lines: [Line] = []
        var inOwn = false, inServers = false, foreign = false, atRoot = true
        var multiline: String?
        var depth = 0
        for raw in text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            if multiline != nil || depth > 0 {
                // Inside a """ or ''' string, or the rest of an array or table value that spans
                // lines: not a header or a key, whatever it looks like.
                let inString = multiline != nil
                let (open, delta) = walk(raw, multiline)
                multiline = open
                depth = max(0, depth + delta)
                if !inString && !inOwn && matches(mentions, stripComment(raw)) != nil { foreign = true }
                lines.append(Line(text: raw, own: inOwn, ownMain: false, header: false))
                continue
            }
            let header = matches(anyHeader, raw) != nil
            var ownMain = false
            if header {
                atRoot = false
                let m = matches(own, raw)
                inOwn = m != nil
                ownMain = m.map { $0.range(at: 1).location == NSNotFound } ?? false
                inServers = matches(serversHeader, raw) != nil
                // [[mcp_servers.openrewind]], or any other header naming it that isn't openrewind's table.
                if !inOwn && matches(mentions, stripComment(raw)) != nil { foreign = true }
            } else if !inOwn {
                if matches(dotted, raw) != nil || matches(mentions, stripComment(raw)) != nil { foreign = true }
                if inServers && matches(keyInServers, raw) != nil { foreign = true }
                if atRoot && matches(inlineServers, raw) != nil { foreign = true }
            }
            lines.append(Line(text: raw, own: inOwn, ownMain: ownMain, header: header))
            let (opened, d) = walk(raw, nil)
            multiline = opened
            depth = header ? 0 : max(0, d)
        }
        return (lines, foreign)
    }

    /// Reads one line as TOML does, starting inside the multi-line string `open` (""" or ''') if
    /// there is one. Returns the multi-line string still open at its end, and brackets and braces
    /// opened minus closed outside strings and comments.
    private static func walk(_ line: String, _ startOpen: String?) -> (open: String?, delta: Int) {
        let u = Array(line.unicodeScalars)
        var open = startOpen
        var i = 0, d = 0
        func at(_ k: Int, _ s: String) -> Bool {
            let p = Array(s.unicodeScalars)
            return k + p.count <= u.count && Array(u[k..<k + p.count]) == p
        }
        while i < u.count {
            if let o = open {
                guard let end = closeOf(u, i, o) else { return (o, d) }
                i = end + 3
                open = nil
                continue
            }
            let c = u[i]
            if c == "#" { break }
            if at(i, "\"\"\"") { open = "\"\"\""; i += 3; continue }
            if at(i, "'''") { open = "'''"; i += 3; continue }
            if c == "\"" {
                i += 1
                while i < u.count && u[i] != "\"" { if u[i] == "\\" { i += 1 }; i += 1 }
                i += 1
                continue
            }
            if c == "'" {
                i += 1
                while i < u.count && u[i] != "'" { i += 1 }
                i += 1
                continue
            }
            if c == "[" || c == "{" { d += 1 } else if c == "]" || c == "}" { d -= 1 }
            i += 1
        }
        return (open, d)
    }

    /// Where the multi-line string `open` closes on the line, from `from`; nil if it doesn't.
    private static func closeOf(_ u: [Unicode.Scalar], _ from: Int, _ open: String) -> Int? {
        let q: Unicode.Scalar = open == "'''" ? "'" : "\""
        var i = from
        while i + 2 < u.count {
            if q == "\"" && u[i] == "\\" { i += 2; continue }
            if u[i] == q && u[i + 1] == q && u[i + 2] == q { return i }
            i += 1
        }
        return nil
    }

    /// The line up to a # that is not inside a quoted string.
    private static func stripComment(_ line: String) -> String {
        let u = Array(line.unicodeScalars)
        var quote: Unicode.Scalar?
        var i = 0
        while i < u.count {
            let c = u[i]
            if let q = quote {
                if c == "\\" && q == "\"" { i += 1 } else if c == q { quote = nil }
            } else if c == "#" {
                var s = String.UnicodeScalarView()
                s.append(contentsOf: u[0..<i])
                return String(s)
            } else if c == "\"" || c == "'" { quote = c }
            i += 1
        }
        return line
    }

    static func status(_ text: String?, name: String, want: MCPCommandLine) -> (MCPLink, String?) {
        guard let text, !text.isEmpty else { return (.notAdded, nil) }
        let (lines, isForeign) = scan(text, name: name)
        if isForeign { return (.unreadable, foreign(name)) }
        guard lines.contains(where: { $0.own }) else { return (.notAdded, nil) }
        var command: String?
        var args: [String]? = []
        var sawArgs = false, extra = false, inMain = false, off = false
        for l in lines {
            if l.header { inMain = l.ownMain; if l.own && !l.ownMain { extra = true }; continue }
            guard inMain else { continue }
            let body = stripComment(l.text).trimmingCharacters(in: .whitespaces)
            if body.isEmpty { continue }
            guard let eq = body.firstIndex(of: "=") else { extra = true; continue }
            let key = body[..<eq].trimmingCharacters(in: .whitespaces)
            let value = body[body.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            if key == "command" { command = parseString(value) }
            else if key == "args" { sawArgs = true; args = parseStringArray(value) }
            else if key == "enabled" && (value == "false" || value == "true") { off = value == "false" }
            // Anything else (env, a url, …) is not what the button writes.
            else { extra = true }
        }
        if !sawArgs { args = [] }
        if extra || !want.same(command: command, args: args) { return (.different, nil) }
        return (off ? .turnedOff : .added, nil)
    }

    /// The file with openrewind's table written, in place of any it had.
    static func add(_ text: String?, name: String, want: MCPCommandLine) throws -> String {
        let text = text ?? ""
        let crlf = text.contains("\r\n")
        let (lines, isForeign) = scan(text, name: name)
        if isForeign { throw MCPConfigError(message: foreign(name)) }
        let table = self.table(name, want)
        var output: [String] = []
        if lines.contains(where: { $0.own }) {
            var placed = false
            for item in withoutOwn(lines) {
                if let l = item { output.append(l.text) }
                // The blank lines after the old table went with it; one goes back after the new one.
                else if !placed { output += table; output.append(""); placed = true }
            }
            // Never two blank lines at the very end.
            while output.count > 1 && output[output.count - 1].isEmpty && output[output.count - 2].isEmpty { output.removeLast() }
        } else {
            output = lines.map(\.text)
            // The file's last line ends with "\n", which leaves an empty last line.
            while let last = output.last, last.trimmingCharacters(in: .whitespaces).isEmpty { output.removeLast() }
            if !output.isEmpty { output.append("") }
            output += table
            output.append("")
        }
        let result = output.joined(separator: crlf ? "\r\n" : "\n")
        // A file read wrongly could end up with two openrewind tables, which ChatGPT would refuse
        // to load at all. Check the result the same way and write nothing if it isn't exactly one.
        let check = scan(result, name: name)
        if check.foreign || check.lines.filter(\.ownMain).count != 1 || status(result, name: name, want: want).0 != .added {
            throw MCPConfigError(message: "OpenRewind couldn't work out where “\(name)” goes in it")
        }
        return result
    }

    /// The file without openrewind's table, or nil when it has none (nothing to change).
    static func remove(_ text: String?, name: String) throws -> String? {
        guard let text, !text.isEmpty else { return nil }
        let (lines, isForeign) = scan(text, name: name)
        guard lines.contains(where: { $0.own }) else {
            if isForeign { throw MCPConfigError(message: foreign(name)) }
            return nil
        }
        // Where a table was taken out, no run of blank lines is left behind.
        var output: [String] = []
        var seam = false
        for item in withoutOwn(lines) {
            guard let l = item else { seam = true; continue }
            let blank = l.text.trimmingCharacters(in: .whitespaces).isEmpty
            if seam && blank && (output.last.map { $0.trimmingCharacters(in: .whitespaces).isEmpty } ?? true) { continue }
            seam = seam && blank
            output.append(l.text)
        }
        if seam {
            // The table was last: the file ends where it did before it, with a newline if it had one.
            while let last = output.last, last.trimmingCharacters(in: .whitespaces).isEmpty { output.removeLast() }
            // The last byte: "\r\n" is one Character in Swift, so hasSuffix("\n") would miss it.
            if !output.isEmpty && text.utf8.last == 0x0A { output.append("") }
        }
        return output.joined(separator: text.contains("\r\n") ? "\r\n" : "\n")
    }

    /// The lines that stay, with nil once for each run of openrewind's lines. The marker comment
    /// just above a table goes with it, and so do blank lines at the table's end.
    private static func withoutOwn(_ lines: [Line]) -> [Line?] {
        var drop = [Bool](repeating: false, count: lines.count)
        for i in lines.indices where lines[i].own {
            drop[i] = true
            if lines[i].header && i > 0 && lines[i - 1].text.trimmingCharacters(in: .whitespaces) == marker { drop[i - 1] = true }
        }
        // Comments at the end of a table are usually about the table after it: they stay.
        for i in lines.indices where lines[i].own && !(i + 1 < lines.count && lines[i + 1].own) {
            var k = i
            while k >= 0 && lines[k].own && !lines[k].header {
                let t = lines[k].text.trimmingCharacters(in: .whitespaces)
                if !t.isEmpty {
                    if !t.hasPrefix("#") { break }
                    if t != marker { drop[k] = false }
                }
                k -= 1
            }
        }
        var out: [Line?] = []
        for i in lines.indices {
            if drop[i] { if i == 0 || !drop[i - 1] { out.append(nil) }; continue }
            out.append(lines[i])
        }
        return out
    }

    private static func table(_ name: String, _ want: MCPCommandLine) -> [String] {
        [marker, "[mcp_servers.\(name)]", "command = \(quote(want.command))", "args = [\(want.args.map(quote).joined(separator: ", "))]"]
    }

    /// A TOML string: 'literal' when it can be, "basic" with escapes otherwise.
    static func quote(_ s: String) -> String {
        let control = s.unicodeScalars.contains { $0.properties.generalCategory == .control }
        if !s.contains("'") && !control { return "'" + s + "'" }
        var out = "\""
        for u in s.unicodeScalars {
            switch u {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if u.properties.generalCategory == .control { out += String(format: "\\u%04X", u.value) } else { out.unicodeScalars.append(u) }
            }
        }
        return out + "\""
    }

    /// One TOML string value ("basic" or 'literal'), or nil if `value` is not just one string.
    static func parseString(_ value: String) -> String? {
        let u = Array(value.unicodeScalars)
        var i = 0
        guard let s = readString(u, &i), i == u.count else { return nil }
        return s
    }

    /// A one-line array of strings, or nil if it is anything else.
    static func parseStringArray(_ value: String) -> [String]? {
        let u = Array(value.unicodeScalars)
        guard u.first == "[", u.last == "]" else { return nil }
        var list: [String] = []
        var i = 1
        func skip() { while i < u.count, u[i].properties.isWhitespace { i += 1 } }
        while true {
            skip()
            if i < u.count, u[i] == "]" { return i == u.count - 1 ? list : nil }
            guard let s = readString(u, &i) else { return nil }
            list.append(s)
            skip()
            if i < u.count, u[i] == "," { i += 1; continue }
            if i < u.count, u[i] == "]" { return i == u.count - 1 ? list : nil }
            return nil
        }
    }

    private static func readString(_ v: [Unicode.Scalar], _ i: inout Int) -> String? {
        guard i < v.count else { return nil }
        var out = String.UnicodeScalarView()
        if v[i] == "'" {
            var j = i + 1
            while j < v.count, v[j] != "'" { out.append(v[j]); j += 1 }
            guard j < v.count else { return nil }
            i = j + 1
            return String(out)
        }
        guard v[i] == "\"" else { return nil }
        i += 1
        while i < v.count {
            let c = v[i]
            if c == "\"" { i += 1; return String(out) }
            if c != "\\" { out.append(c); i += 1; continue }
            i += 1
            guard i < v.count else { return nil }
            switch v[i] {
            case "\"": out.append("\"")
            case "\\": out.append("\\")
            case "n": out.append("\n")
            case "t": out.append("\t")
            case "r": out.append("\r")
            case "b": out.append("\u{08}")
            case "f": out.append("\u{0C}")
            case "e": out.append("\u{1B}")
            case "u", "U":
                let n = v[i] == "u" ? 4 : 8
                guard i + n < v.count else { return nil }
                var hex = String.UnicodeScalarView()
                hex.append(contentsOf: v[(i + 1)...(i + n)])
                guard let code = UInt32(String(hex), radix: 16), let scalar = Unicode.Scalar(code) else { return nil }
                out.append(scalar)
                i += n
            default: return nil
            }
            i += 1
        }
        return nil
    }
}

// MARK: - Files

/// The file changed between reading it and replacing it (the app wrote it meanwhile).
struct MCPConfigChanged: Error {
    let path: String
}

/// Reads and writes AI apps' setup files without ever leaving one half-written: the new text goes
/// to a new private temporary file in the same folder, which replaces the file in one step, and only
/// if nothing else changed the file meanwhile. The previous version is kept as one
/// "<name>.openrewind-backup" next to it (readable only by you), which Remove deletes again.
/// The Windows app does the same in McpConnect.cs (McpConfigFile), with tests.
enum MCPConfigFile {
    static let backupSuffix = ".openrewind-backup"
    static let notUTF8 = "it isn't saved as UTF-8 text"

    /// The file's text, or nil when it doesn't exist. Bytes that aren't UTF-8 throw MCPConfigError
    /// rather than being read as something else and written back.
    static func read(_ url: URL) throws -> String? {
        let target = url.resolvingSymlinksInPath()
        guard isFile(target.path) else { return nil }
        return try decode(Data(contentsOf: target))
    }

    /// A file, not a folder (as .NET's File.Exists, so both apps treat a folder in the way alike).
    private static func isFile(_ path: String) -> Bool {
        var folder: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &folder) && !folder.boolValue
    }

    static func decode(_ data: Data) throws -> String {
        // A byte order mark some editors add is not part of the JSON or TOML (.NET drops it too).
        let bytes = data.starts(with: [0xEF, 0xBB, 0xBF]) ? Data(data.dropFirst(3)) : data
        let text = String(decoding: bytes, as: UTF8.self)
        // Decoding replaces bytes that aren't UTF-8; if anything was replaced, the text isn't the file.
        guard Data(text.utf8) == bytes else { throw MCPConfigError(message: notUTF8) }
        return text
    }

    /// Writes `text` over the file (or creates it and its folder), if the file still holds
    /// `expected` (nil: doesn't exist); otherwise throws MCPConfigChanged and changes nothing.
    /// A file that is a link to another (dotfiles kept elsewhere) is written where the link points,
    /// so the link stays. Other failures throw MCPConfigError with the system's reason.
    static func write(_ url: URL, _ text: String, expected: String?, backup: Bool = true) throws {
        let fm = FileManager.default
        let target = url.resolvingSymlinksInPath()
        try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        let exists = isFile(target.path)
        // Keep a private file private (~/.claude.json is readable only by its owner); new files are private.
        var mode: mode_t = 0o600
        if exists, let m = (try? fm.attributesOfItem(atPath: target.path))?[.posixPermissions] as? NSNumber {
            mode = mode_t(truncatingIfNeeded: m.intValue & 0o777)
        }
        let temp = target.path + ".openrewind-\(UUID().uuidString).tmp"
        // Gone after the rename; taken away here if anything went wrong before it.
        defer { unlink(temp) }
        try createNew(temp, Data(text.utf8), mode)
        let now: String?
        if isFile(target.path) {
            do { now = try decode(Data(contentsOf: target)) } catch { now = "\0changed" }
        } else {
            now = nil
        }
        guard now == expected else { throw MCPConfigChanged(path: target.path) }
        if backup && exists {
            let copy = target.path + backupSuffix
            unlink(copy)
            try createNew(copy, Data(contentsOf: target), 0o600)
        }
        // rename(2) replaces the file in one step.
        guard Darwin.rename(temp, target.path) == 0 else { throw MCPConfigError(message: String(cString: strerror(errno))) }
    }

    /// Takes away the backup `write` keeps, if there is one.
    static func deleteBackup(_ url: URL) {
        let copy = url.resolvingSymlinksInPath().path + backupSuffix
        if unlink(copy) != 0 && errno != ENOENT { log("connect: cannot delete a backup: \(String(cString: strerror(errno)))") }
    }

    /// A new file with `data`, made with `mode` from the start so it is never readable by others.
    private static func createNew(_ path: String, _ data: Data, _ mode: mode_t) throws {
        let fd = Darwin.open(path, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, mode)
        guard fd >= 0 else { throw MCPConfigError(message: String(cString: strerror(errno))) }
        defer { Darwin.close(fd) }
        // open() leaves out what the umask takes away; the exact mode is set before anything is written.
        guard fchmod(fd, mode) == 0 else { throw MCPConfigError(message: String(cString: strerror(errno))) }
        try data.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
            var done = 0
            while done < buffer.count {
                let n = Darwin.write(fd, buffer.baseAddress! + done, buffer.count - done)
                if n < 0 {
                    if errno == EINTR { continue }
                    throw MCPConfigError(message: String(cString: strerror(errno)))
                }
                done += n
            }
        }
        guard fsync(fd) == 0 else { throw MCPConfigError(message: String(cString: strerror(errno))) }
    }

    /// Changes every file with `change` (nil result: leave it), or none of them. Reads them all
    /// first, writes them one by one, and puts back the ones already written if a later one fails.
    /// When an app writes a file meanwhile, starts over with what it wrote, up to three times.
    /// Removing (`removing`) keeps no backup and deletes the old one.
    static func change(_ files: [URL], removing: Bool = false, _ change: (String?) throws -> String?) throws {
        var attempt = 1
        while true {
            do {
                try changeOnce(files, removing, change)
                return
            } catch let e as MCPConfigChanged {
                guard attempt < 3 else { throw MCPConfigError(message: "\(e.path) changed while OpenRewind was saving it. Quit the app and try again.") }
                log("connect: \(e.path) changed while OpenRewind was saving it; trying again")
                attempt += 1
            }
        }
    }

    private static func changeOnce(_ files: [URL], _ removing: Bool, _ change: (String?) throws -> String?) throws {
        var writes: [(url: URL, before: String?, after: String)] = []
        for f in files {
            let before: String?
            let after: String?
            do {
                before = try read(f)
                after = try change(before)
            } catch let e as MCPConfigError {
                throw MCPConfigError(message: "OpenRewind didn't change \(f.path) because \(e.message). Fix that file, or set it up by hand below.")
            } catch {
                throw MCPConfigError(message: "OpenRewind can't read \(f.path) (\(error.localizedDescription)).")
            }
            if let after, after != before { writes.append((f, before, after)) }
        }
        var done: [(url: URL, before: String?, after: String)] = []
        for w in writes {
            do {
                try write(w.url, w.after, expected: w.before, backup: !removing)
            } catch {
                for d in done { putBack(d.url, d.before, d.after) }
                if error is MCPConfigChanged { throw error }
                let also = done.isEmpty ? "" : done.count > 1 ? " The other files were put back as they were." : " The other file was put back as it was."
                let reason = (error as? MCPConfigError)?.message ?? error.localizedDescription
                throw MCPConfigError(message: "Couldn't save \(w.url.path) (\(reason)).\(also)")
            }
            log("connect: changed \(w.url.path)")
            done.append(w)
        }
        if removing { for f in files { deleteBackup(f) } }
    }

    /// Undoes one write: the old text back, or no file if there was none.
    private static func putBack(_ url: URL, _ before: String?, _ after: String) {
        do {
            if let before {
                try write(url, before, expected: after, backup: false)
            } else if try read(url) == after {
                try FileManager.default.removeItem(at: url.resolvingSymlinksInPath())
            }
            log("connect: put \(url.path) back")
        } catch {
            log("connect: cannot put \(url.path) back: \((error as? MCPConfigError)?.message ?? error.localizedDescription)")
        }
    }
}

// MARK: - AI apps

/// One AI app that Connect AI Apps sets up with one click: where it keeps its MCP servers, and how
/// to add, update and remove the openrewind entry there. Only that entry in those files changes.
/// The Windows app has the same in AiAppLinks.cs.
class AIAppLink: Identifiable {
    var id: String { title }
    let want: MCPCommandLine
    init(want: MCPCommandLine) { self.want = want }

    var title: String { "" }
    /// The short name in "Restart …" and in messages.
    var appName: String { title }
    /// What the button does, for the line under the title.
    var about: String { "" }
    /// The app's bundle identifiers, for Restart; empty when there is nothing to restart.
    var bundleIDs: [String] { [] }

    func status() -> (MCPLink, String?) { (.notAdded, nil) }
    /// Adds or updates the entry; returns what to tell the user. Throws MCPConfigError with a message for the user.
    func add() throws -> String { "" }
    func remove() throws -> String { "" }

    static var home: URL { FileManager.default.homeDirectoryForCurrentUser }

    var running: [NSRunningApplication] {
        bundleIDs.flatMap { NSRunningApplication.runningApplications(withBundleIdentifier: $0) }
    }

    /// Quits the app the normal way (it may ask to save something) and opens it again once it is gone.
    func restart() {
        let apps = running
        // Every app that was quit opens again, each once.
        var urls: [URL] = []
        for a in apps { if let u = a.bundleURL, !urls.contains(u) { urls.append(u) } }
        guard !urls.isEmpty else { return }
        log("connect: restarting \(appName)")
        for a in apps { a.terminate() }
        DispatchQueue.global().async {
            // Wait up to 10 seconds for it to quit.
            for _ in 0..<50 where apps.contains(where: { !$0.isTerminated }) { Thread.sleep(forTimeInterval: 0.2) }
            DispatchQueue.main.async {
                for url in urls {
                    NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration()) { _, error in
                        if let error { log("connect: cannot reopen \(url.path): \(error.localizedDescription)") }
                    }
                }
            }
        }
    }

    /// The status of several files read as one: added only when every one has it.
    func statusOf(_ files: [URL], _ one: (String?) -> (MCPLink, String?)) -> (MCPLink, String?) {
        var all: [MCPLink] = []
        for f in files {
            let text: String?
            do { text = try MCPConfigFile.read(f) } catch let e as MCPConfigError {
                return (.unreadable, "OpenRewind can't change \(f.path) because \(e.message). Set it up by hand below.")
            } catch {
                return (.unreadable, "OpenRewind can't read \(f.path) (\(error.localizedDescription)).")
            }
            let (link, problem) = one(text)
            if link == .unreadable { return (.unreadable, "OpenRewind can't change \(f.path) because \(problem ?? MCPJSONConfig.notJSON). Set it up by hand below.") }
            all.append(link)
        }
        if all.allSatisfy({ $0 == .notAdded }) { return (.notAdded, nil) }
        if all.allSatisfy({ $0 == .added }) { return (.added, nil) }
        if all.allSatisfy({ $0 == .added || $0 == .turnedOff }) { return (.turnedOff, nil) }
        return (.different, nil)
    }

    /// Changes all the files or none of them (see MCPConfigFile.change).
    func change(_ files: [URL], removing: Bool = false, _ change: (String?) throws -> String?) throws {
        try MCPConfigFile.change(files, removing: removing, change)
    }
}

/// Claude Desktop and Cursor: an "mcpServers" entry in a JSON file.
final class JSONAppLink: AIAppLink {
    private let t: String, app: String, a: String, file: URL, ids: [String]

    init(want: MCPCommandLine, title: String, app: String, about: String, file: URL, bundleIDs: [String]) {
        t = title; self.app = app; a = about; self.file = file; ids = bundleIDs
        super.init(want: want)
    }

    override var title: String { t }
    override var appName: String { app }
    override var about: String { a }
    override var bundleIDs: [String] { ids }

    override func status() -> (MCPLink, String?) {
        statusOf([file]) { MCPJSONConfig.status($0, name: MCPSetup.name, want: want) }
    }

    override func add() throws -> String {
        try change([file]) { try MCPJSONConfig.add($0, name: MCPSetup.name, want: want) }
        return "Added. Quit and reopen \(app) to use it."
    }

    override func remove() throws -> String {
        try change([file], removing: true) { try MCPJSONConfig.remove($0, name: MCPSetup.name) }
        return "Removed. Quit and reopen \(app) to finish."
    }

    static func claudeDesktop(_ want: MCPCommandLine) -> JSONAppLink {
        JSONAppLink(want: want, title: "Claude Desktop", app: "Claude",
                    about: "Adds OpenRewind to Claude's claude_desktop_config.json.",
                    file: home.appendingPathComponent("Library/Application Support/Claude/claude_desktop_config.json"),
                    bundleIDs: ["com.anthropic.claudefordesktop"])
    }

    static func cursor(_ want: MCPCommandLine) -> JSONAppLink {
        JSONAppLink(want: want, title: "Cursor", app: "Cursor",
                    about: "Adds OpenRewind to Cursor's mcp.json, for every project.",
                    file: home.appendingPathComponent(".cursor/mcp.json"),
                    bundleIDs: ["com.todesktop.230313mzl4w4u92"])
    }
}

/// The ChatGPT desktop app (Work chats) and Codex: [mcp_servers.openrewind] in ~/.codex/config.toml.
final class ChatGPTLink: AIAppLink {
    private var file: URL {
        let codexHome = ProcessInfo.processInfo.environment["CODEX_HOME"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
        return (codexHome ?? Self.home.appendingPathComponent(".codex")).appendingPathComponent("config.toml")
    }

    override var title: String { "ChatGPT" }
    override var about: String {
        "For Work chats in the ChatGPT desktop app, and Codex. What OpenRewind returns there goes to OpenAI like anything else in the chat. Regular chats and chatgpt.com can't reach it."
    }
    /// The ChatGPT app only; Codex sessions are never quit by OpenRewind.
    override var bundleIDs: [String] { ["com.openai.chat"] }

    override func status() -> (MCPLink, String?) {
        statusOf([file]) { MCPTOMLConfig.status($0, name: MCPSetup.name, want: want) }
    }

    override func add() throws -> String {
        try change([file]) { try MCPTOMLConfig.add($0, name: MCPSetup.name, want: want) }
        return "Added. Quit and reopen ChatGPT, then ask in a Work chat."
    }

    override func remove() throws -> String {
        try change([file], removing: true) { try MCPTOMLConfig.remove($0, name: MCPSetup.name) }
        return "Removed. Quit and reopen ChatGPT to finish."
    }
}

/// Claude Code: `claude mcp add --scope user` when its command is found, which keeps Claude Code in
/// charge of its own ~/.claude.json; otherwise the same entry is written there directly.
final class ClaudeCodeLink: AIAppLink {
    private var file: URL {
        let dir = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"].flatMap { $0.isEmpty ? nil : URL(fileURLWithPath: $0) }
        return (dir ?? Self.home).appendingPathComponent(".claude.json")
    }

    override var title: String { "Claude Code" }
    override var about: String { "Adds OpenRewind to Claude Code for every project (user scope)." }

    override func status() -> (MCPLink, String?) {
        statusOf([file]) { MCPJSONConfig.status($0, name: MCPSetup.name, want: want) }
    }

    override func add() throws -> String {
        if let cli = Self.findCLI() {
            // `add` refuses a name that is already there, so an old entry goes first.
            if status().0 != .notAdded { _ = Self.run(cli, ["mcp", "remove", "--scope", "user", MCPSetup.name]) }
            if Self.run(cli, ["mcp", "add", "--scope", "user", MCPSetup.name, "--", want.command] + want.args), status().0 == .added {
                return "Added. New Claude Code sessions can use it."
            }
            log("connect: claude mcp add did not add it; writing the file instead")
        }
        try change([file]) { try MCPJSONConfig.add($0, name: MCPSetup.name, want: want, stdioType: true) }
        return "Added. New Claude Code sessions can use it."
    }

    override func remove() throws -> String {
        if let cli = Self.findCLI(), Self.run(cli, ["mcp", "remove", "--scope", "user", MCPSetup.name]), status().0 == .notAdded {
            MCPConfigFile.deleteBackup(file)
            return "Removed from Claude Code."
        }
        try change([file], removing: true) { try MCPJSONConfig.remove($0, name: MCPSetup.name) }
        return "Removed from Claude Code."
    }

    /// Where the installers and Node version managers put `claude`. Apps opened from the Dock don't
    /// get the shell's PATH, so these are looked in first.
    static var usualDirs: [String] {
        let home = Self.home.path as NSString
        var dirs = [".local/bin", ".claude/local", ".npm-global/bin", ".volta/bin", ".asdf/shims", ".bun/bin",
                    ".local/share/fnm/aliases/default/bin", "Library/Application Support/fnm/aliases/default/bin"]
            .map { home.appendingPathComponent($0) }
        // nvm keeps one folder per Node version; the newest first.
        let nvm = home.appendingPathComponent(".nvm/versions/node") as NSString
        let versions = ((try? FileManager.default.contentsOfDirectory(atPath: nvm as String)) ?? [])
            .sorted { $0.compare($1, options: .numeric) == .orderedDescending }
        dirs += versions.map { nvm.appendingPathComponent("\($0)/bin") }
        return dirs + ["/opt/homebrew/bin", "/usr/local/bin"]
    }

    static func findCLI() -> String? {
        // Full paths only: an empty or relative PATH entry would mean the current folder.
        let path = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init).filter { $0.hasPrefix("/") }
        return (usualDirs + path).map { ($0 as NSString).appendingPathComponent("claude") }
            .first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// Runs the command for at most 30 seconds. Its messages go to the log, not on screen.
    static func run(_ exe: String, _ args: [String]) -> Bool {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        // An npm install is a node script; node is usually next to it or in Homebrew.
        let dir = (exe as NSString).deletingLastPathComponent
        env["PATH"] = ([dir] + usualDirs + [env["PATH"] ?? "/usr/bin:/bin"]).joined(separator: ":")
        p.environment = env
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        let errors = Pipe()
        p.standardError = errors
        let done = DispatchSemaphore(value: 0)
        p.terminationHandler = { _ in done.signal() }
        do { try p.run() } catch {
            log("connect: cannot run claude: \(error.localizedDescription)")
            return false
        }
        if done.wait(timeout: .now() + 30) == .timedOut {
            p.terminate()
            log("connect: claude \(args[1]) timed out")
            return false
        }
        if p.terminationStatus != 0 {
            let first = String(decoding: errors.fileHandleForReading.availableData, as: UTF8.self).split(separator: "\n").first ?? ""
            log("connect: claude \(args[1]) exited \(p.terminationStatus): \(first)")
        }
        return p.terminationStatus == 0
    }
}
