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
                    // A lone surrogate can't be UTF-8; it becomes U+FFFD, as .NET does.
                    let scalar = Unicode.Scalar(code) ?? "\u{FFFD}"
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
        guard case .object = entry else { return (.different, nil) }
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
        // A server that also sets a URL, or isn't stdio, is not what the button adds.
        if entry["url"] != nil { return (.different, nil) }
        if let t = entry["type"] { if case .string("stdio") = t {} else { return (.different, nil) } }
        return (want.same(command: command, args: args) ? .added : .different, nil)
    }

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

    private static let anyHeader = regex(#"^\s*\["#)
    private static let serversHeader = regex(#"^\s*\[\s*mcp_servers\s*\]\s*(#.*)?$"#)
    private static let inlineServers = regex(#"^\s*mcp_servers\s*="#)

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
        let own = regex(#"^\s*\[\s*mcp_servers\s*\.\s*"# + names(name) + #"\s*(\.[^\]]*)?\]\s*(#.*)?$"#)
        let dotted = regex(#"^\s*mcp_servers\s*\.\s*"# + names(name) + #"\s*[.=]"#)
        let keyInServers = regex(#"^\s*"# + names(name) + #"\s*[.=]"#)
        var lines: [Line] = []
        var inOwn = false, inServers = false, foreign = false, atRoot = true
        var multiline: String?
        var depth = 0
        for raw in text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            if let m = multiline {
                // Inside a """ or ''' string: not a header or a key, whatever it looks like.
                if count(raw, m) % 2 == 1 { multiline = nil }
                lines.append(Line(text: raw, own: inOwn, ownMain: false, header: false))
                continue
            }
            if depth > 0 {
                // The rest of an array (or table) value that spans lines.
                depth = max(0, depth + bracketDelta(raw))
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
            } else {
                if !inOwn {
                    if matches(dotted, raw) != nil { foreign = true }
                    if inServers && matches(keyInServers, raw) != nil { foreign = true }
                    if atRoot && matches(inlineServers, raw) != nil { foreign = true }
                }
                depth = max(0, bracketDelta(raw))
            }
            lines.append(Line(text: raw, own: inOwn, ownMain: ownMain, header: header))
            for q in ["\"\"\"", "'''"] where count(stripComment(raw), q) % 2 == 1 {
                multiline = q
                depth = 0
                break
            }
        }
        return (lines, foreign)
    }

    private static func count(_ s: String, _ what: String) -> Int { s.components(separatedBy: what).count - 1 }

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

    /// Brackets and braces opened minus closed on a line, outside strings and comments.
    private static func bracketDelta(_ line: String) -> Int {
        let u = Array(line.unicodeScalars)
        var d = 0
        var quote: Unicode.Scalar?
        var i = 0
        while i < u.count {
            let c = u[i]
            if let q = quote {
                if c == "\\" && q == "\"" { i += 1 } else if c == q { quote = nil }
            } else if c == "#" { break }
            else if c == "\"" || c == "'" { quote = c }
            else if c == "[" || c == "{" { d += 1 }
            else if c == "]" || c == "}" { d -= 1 }
            i += 1
        }
        return d
    }

    static func status(_ text: String?, name: String, want: MCPCommandLine) -> (MCPLink, String?) {
        guard let text, !text.isEmpty else { return (.notAdded, nil) }
        let (lines, isForeign) = scan(text, name: name)
        if isForeign { return (.unreadable, foreign(name)) }
        guard lines.contains(where: { $0.own }) else { return (.notAdded, nil) }
        var command: String?
        var args: [String]? = []
        var sawArgs = false, extra = false, inMain = false
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
            // Anything else (env, enabled = false, a url, …) is not what the button writes.
            else { extra = true }
        }
        if !sawArgs { args = [] }
        return (!extra && want.same(command: command, args: args) ? .added : .different, nil)
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
        return output.joined(separator: crlf ? "\r\n" : "\n")
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
            if !output.isEmpty && text.hasSuffix("\n") { output.append("") }
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

/// Reads and writes AI apps' setup files without ever leaving one half-written: the old file is
/// copied to "<name>.openrewind-backup" next to it, the new text goes to a temporary file in the
/// same folder, and that replaces the file in one step. Nothing else is touched.
enum MCPConfigFile {
    static let backupSuffix = ".openrewind-backup"

    /// The file's text, or nil when it doesn't exist.
    static func read(_ url: URL) throws -> String? {
        let target = url.resolvingSymlinksInPath()
        guard FileManager.default.fileExists(atPath: target.path) else { return nil }
        let text = try String(contentsOf: target, encoding: .utf8)
        // A byte order mark some editors add is not part of the JSON or TOML (.NET drops it too).
        return text.hasPrefix("\u{FEFF}") ? String(text.dropFirst()) : text
    }

    /// Writes `text` over the file (or creates it and its folder). A file that is a link to another
    /// (dotfiles kept elsewhere) is written where the link points, so the link stays.
    static func write(_ url: URL, _ text: String) throws {
        let fm = FileManager.default
        let target = url.resolvingSymlinksInPath()
        try fm.createDirectory(at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
        let temp = URL(fileURLWithPath: target.path + ".openrewind-tmp")
        let backup = URL(fileURLWithPath: target.path + backupSuffix)
        let exists = fm.fileExists(atPath: target.path)
        if exists {
            try? fm.removeItem(at: backup)
            try fm.copyItem(at: target, to: backup)
        }
        try? fm.removeItem(at: temp)
        try Data(text.utf8).write(to: temp)
        // Keep a private file private (~/.claude.json is readable only by its owner).
        if exists, let mode = try fm.attributesOfItem(atPath: target.path)[.posixPermissions] {
            try fm.setAttributes([.posixPermissions: mode], ofItemAtPath: temp.path)
        }
        // rename(2) replaces the file in one step.
        guard rename(temp.path, target.path) == 0 else {
            let err = String(cString: strerror(errno))
            try? fm.removeItem(at: temp)
            throw MCPConfigError(message: "Couldn't save \(target.path) (\(err)).")
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

    /// Quits the app the normal way and opens it again once it is gone.
    func restart() {
        guard let app = running.first, let url = app.bundleURL else { return }
        log("connect: restarting \(appName)")
        for a in running { a.terminate() }
        DispatchQueue.global().async {
            // Wait up to 10 seconds for it to quit (it may ask to save something).
            for _ in 0..<50 where !app.isTerminated { Thread.sleep(forTimeInterval: 0.2) }
            DispatchQueue.main.async {
                NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration()) { _, error in
                    if let error { log("connect: cannot reopen \(url.path): \(error.localizedDescription)") }
                }
            }
        }
    }

    /// The status of several files read as one: added only when every one has it.
    func statusOf(_ files: [URL], _ one: (String?) -> (MCPLink, String?)) -> (MCPLink, String?) {
        var all: [MCPLink] = []
        for f in files {
            let text: String?
            do { text = try MCPConfigFile.read(f) } catch {
                return (.unreadable, "OpenRewind can't read \(f.path) (\(error.localizedDescription)).")
            }
            let (link, problem) = one(text)
            if link == .unreadable { return (.unreadable, "OpenRewind can't change \(f.path) because \(problem ?? MCPJSONConfig.notJSON). Set it up by hand below.") }
            all.append(link)
        }
        if all.allSatisfy({ $0 == .notAdded }) { return (.notAdded, nil) }
        return (all.allSatisfy({ $0 == .added }) ? .added : .different, nil)
    }

    /// Works out every file's new text first and writes only when all of them can be changed.
    func change(_ files: [URL], _ change: (String?) throws -> String?) throws {
        var writes: [(URL, String)] = []
        for f in files {
            let text: String?
            do { text = try MCPConfigFile.read(f) } catch {
                throw MCPConfigError(message: "OpenRewind can't read \(f.path) (\(error.localizedDescription)).")
            }
            let after: String?
            do { after = try change(text) } catch let e as MCPConfigError {
                throw MCPConfigError(message: "OpenRewind didn't change \(f.path) because \(e.message). Fix that file, or set it up by hand below.")
            }
            if let after, after != text { writes.append((f, after)) }
        }
        for (url, text) in writes {
            try MCPConfigFile.write(url, text)
            log("connect: changed \(url.path)")
        }
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
        try change([file]) { try MCPJSONConfig.remove($0, name: MCPSetup.name) }
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
        "For Work chats in the ChatGPT desktop app, and Codex. Regular chats and chatgpt.com only reach servers on the internet, and OpenRewind never puts your history there."
    }
    override var bundleIDs: [String] { ["com.openai.chat", "com.openai.codex"] }

    override func status() -> (MCPLink, String?) {
        statusOf([file]) { MCPTOMLConfig.status($0, name: MCPSetup.name, want: want) }
    }

    override func add() throws -> String {
        try change([file]) { try MCPTOMLConfig.add($0, name: MCPSetup.name, want: want) }
        return "Added. Quit and reopen ChatGPT, then ask in a Work chat."
    }

    override func remove() throws -> String {
        try change([file]) { try MCPTOMLConfig.remove($0, name: MCPSetup.name) }
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
            return "Removed from Claude Code."
        }
        try change([file]) { try MCPJSONConfig.remove($0, name: MCPSetup.name) }
        return "Removed from Claude Code."
    }

    /// Where the installers put `claude`. Apps opened from the Dock don't get the shell's PATH.
    private static let usualDirs = ["~/.local/bin", "~/.claude/local", "/opt/homebrew/bin", "/usr/local/bin"]
        .map { NSString(string: $0).expandingTildeInPath }

    static func findCLI() -> String? {
        let path = (ProcessInfo.processInfo.environment["PATH"] ?? "").split(separator: ":").map(String.init)
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
