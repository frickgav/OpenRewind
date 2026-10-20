using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenRewind;

/// Where an AI app's setup stands for this copy of OpenRewind.
public enum McpLink
{
    /// No openrewind entry.
    NotAdded,
    /// An entry that starts this copy, the same way the button would add it.
    Added,
    /// An entry that starts another copy (an old path) or with other options; Add brings it up to date.
    Different,
    /// This copy's entry, turned off in the app ("disabled": true, enabled = false). Add turns it back on.
    TurnedOff,
    /// The file is there but OpenRewind can't read or safely change it (not valid JSON, an
    /// openrewind entry it did not write, …). Nothing is changed; the user sets it up by hand.
    Unreadable,
}

/// How the AI app starts the server: the program and its arguments.
public sealed record McpCommandLine(string Command, IReadOnlyList<string> Args)
{
    /// Paths on Windows ignore letter case, so the Windows app compares them that way; the Mac
    /// app compares them exactly.
    public bool SameAs(string? command, IReadOnlyList<string>? args, bool ignoreCase) =>
        command != null && args != null
        && string.Equals(command, Command, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && args.SequenceEqual(Args);
}

/// The file can't be changed safely; Message says why, in words for the user.
public sealed class McpConfigException : Exception
{
    public McpConfigException(string message) : base(message) { }
}

/// Adds and removes the `mcpServers.openrewind` entry in JSON setup files: Claude Desktop's
/// claude_desktop_config.json, Cursor's mcp.json and Claude Code's ~/.claude.json. Everything else
/// in the file stays as it was (keys, their order, numbers as written). A file that is not valid
/// JSON is never rewritten. The Mac app does the same in MCPConnect.swift.
public static class McpJsonConfig
{
    public const string ServersKey = "mcpServers";

    static readonly JsonDocumentOptions Strict = new() { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public const string NotJson = "it isn't valid JSON (or has comments)";

    /// The parsed file and its servers object, or an exception saying why it can't be changed.
    static (JsonObject Root, JsonObject? Servers) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (new JsonObject(), null);
        JsonNode? node;
        try { node = JsonNode.Parse(text, documentOptions: Strict); }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { throw new McpConfigException(NotJson); }
        if (node is not JsonObject root) throw new McpConfigException("it doesn't hold a JSON object");
        // Repeated keys anywhere, and text that isn't whole Unicode characters, are refused, as the Mac app does.
        try { if (!Valid(root)) throw new McpConfigException(NotJson); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { throw new McpConfigException(NotJson); }
        var servers = root[ServersKey];
        if (servers == null) return (root, null);
        if (servers is not JsonObject so) throw new McpConfigException($"its “{ServersKey}” is not a JSON object");
        return (root, so);
    }

    /// Reads every key and string once. JsonObject reports a repeated key only on first use (ArgumentException).
    static bool Valid(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o) if (!WholeText(key) || !Valid(value)) return false;
                return true;
            case JsonArray a:
                foreach (var item in a) if (!Valid(item)) return false;
                return true;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return WholeText(v.GetValue<string>());
            default:
                return true;
        }
    }

    /// No half of a surrogate pair on its own (JSON can spell one as "\ud800").
    static bool WholeText(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    /// The keys the button writes; anything else in the entry means the user set it up their own way.
    static readonly HashSet<string> OwnKeys = new() { "type", "command", "args", "env", "disabled" };

    public static McpLink Status(string? text, string name, McpCommandLine want, bool ignoreCase, out string? problem)
    {
        problem = null;
        JsonObject? servers;
        try { servers = Parse(text).Servers; }
        catch (McpConfigException e) { problem = e.Message; return McpLink.Unreadable; }
        if (servers?[name] is not JsonNode entry) return McpLink.NotAdded;
        if (entry is not JsonObject o) return McpLink.Different;
        if (o.Any(p => !OwnKeys.Contains(p.Key))) return McpLink.Different;
        if (o["env"] is JsonNode env && (env is not JsonObject eo || eo.Count > 0)) return McpLink.Different;
        var command = o["command"] is JsonValue c && c.TryGetValue(out string? cs) ? cs : null;
        List<string>? args = null;
        if (o["args"] is JsonArray a)
        {
            args = new List<string>();
            foreach (var item in a)
                if (item is JsonValue v && v.TryGetValue(out string? s)) args.Add(s); else { args = null; break; }
        }
        else if (o["args"] == null) args = new List<string>();
        // A server that isn't stdio is not what the button adds.
        if (o["type"] is JsonNode t && !(t is JsonValue tv && tv.TryGetValue(out string? ts) && ts == "stdio")) return McpLink.Different;
        if (!want.SameAs(command, args, ignoreCase)) return McpLink.Different;
        return o["disabled"] is JsonValue d && d.GetValueKind() == JsonValueKind.True ? McpLink.TurnedOff : McpLink.Added;
    }

    /// The file with the openrewind entry added, or replaced if one is there. `stdioType` writes
    /// "type": "stdio" too, as Claude Code does. Throws McpConfigException when it can't be changed.
    public static string Add(string? text, string name, McpCommandLine want, bool stdioType = false)
    {
        var (root, servers) = Parse(text);
        if (servers == null) { servers = new JsonObject(); root[ServersKey] = servers; }
        var entry = new JsonObject();
        if (stdioType) entry["type"] = "stdio";
        entry["command"] = want.Command;
        entry["args"] = new JsonArray(want.Args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray());
        if (stdioType) entry["env"] = new JsonObject();
        // Replacing in place keeps the entry where it was among the user's other servers.
        servers[name] = entry;
        return Format(root, text);
    }

    /// The file without the openrewind entry, or null when there is none (nothing to change).
    public static string? Remove(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var (root, servers) = Parse(text);
        if (servers == null || !servers.ContainsKey(name)) return null;
        servers.Remove(name);
        return Format(root, text);
    }

    /// Two-space indents, the line endings the file had, and a newline at the end.
    static string Format(JsonObject root, string? original)
    {
        var json = root.ToJsonString(Pretty);
        var crlf = original?.Contains("\r\n") == true;
        json = json.Replace("\r\n", "\n");
        if (crlf) json = json.Replace("\n", "\r\n");
        return json + (crlf ? "\r\n" : "\n");
    }
}

/// Adds and removes the `[mcp_servers.openrewind]` table in config.toml, where the ChatGPT desktop
/// app (and Codex) keep their MCP servers. TOML is edited as lines: only the openrewind table is
/// written or taken out, so the rest of the file stays exactly as it was, comments included.
/// When the file defines openrewind some other way (an inline table, dotted keys), it is left alone.
public static class McpTomlConfig
{
    /// The comment written above the table, so people reading the file know where it came from.
    public const string Marker = "# Added by OpenRewind (Connect AI Apps). Remove it there or delete this table.";

    /// mcp_servers as a TOML key: bare or quoted.
    const string Servers = @"(?:mcp_servers|""mcp_servers""|'mcp_servers')";

    static readonly Regex AnyHeader = new(@"^\s*\[", RegexOptions.Compiled);
    static readonly Regex ServersHeader = new($@"^\s*\[\s*{Servers}\s*\]\s*(#.*)?$", RegexOptions.Compiled);
    static readonly Regex InlineServers = new($@"^\s*{Servers}\s*=", RegexOptions.Compiled);

    static string Names(string name) => $@"(?:{Regex.Escape(name)}|""{Regex.Escape(name)}""|'{Regex.Escape(name)}')";

    static Regex OwnHeader(string name) =>
        new($@"^\s*\[\s*{Servers}\s*\.\s*{Names(name)}\s*(\.[^\]]*)?\]\s*(#.*)?$");

    static Regex DottedKey(string name) => new($@"^\s*{Servers}\s*\.\s*{Names(name)}\s*[.=]");

    static Regex KeyInServers(string name) => new($@"^\s*{Names(name)}\s*[.=]");

    /// A line that names both mcp_servers and openrewind (as a whole word) anywhere outside a comment.
    static Regex Mentions(string name) => new($@"mcp_servers.*(?<![\w-]){Regex.Escape(name)}(?![\w-])");

    /// One line of the file and what it is.
    sealed record Line(string Text, bool Own, bool OwnMain, bool Header);

    /// The file as lines, which of them belong to openrewind's tables, and whether openrewind is
    /// also defined in a way only the user should change.
    static (List<Line> Lines, bool Foreign) Scan(string text, string name)
    {
        var own = OwnHeader(name);
        var dotted = DottedKey(name);
        var keyInServers = KeyInServers(name);
        var mentions = Mentions(name);
        var lines = new List<Line>();
        bool inOwn = false, inServers = false, foreign = false, atRoot = true;
        string? multiline = null;
        var depth = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (multiline != null || depth > 0)
            {
                // Inside a """ or ''' string, or the rest of an array or table value that spans
                // lines: not a header or a key, whatever it looks like.
                var inString = multiline != null;
                var (open, delta) = Walk(raw, multiline);
                multiline = open;
                depth = Math.Max(0, depth + delta);
                if (!inString && !inOwn && mentions.IsMatch(StripComment(raw))) foreign = true;
                lines.Add(new Line(raw, inOwn, false, false));
                continue;
            }
            var header = AnyHeader.IsMatch(raw);
            var ownMatch = header ? own.Match(raw) : null;
            if (header)
            {
                atRoot = false;
                inOwn = ownMatch!.Success;
                inServers = ServersHeader.IsMatch(raw);
                // [[mcp_servers.openrewind]], or any other header naming it that isn't openrewind's table.
                if (!inOwn && mentions.IsMatch(StripComment(raw))) foreign = true;
            }
            else if (!inOwn)
            {
                var body = StripComment(raw);
                if (dotted.IsMatch(raw) || mentions.IsMatch(body)) foreign = true;
                if (inServers && keyInServers.IsMatch(raw)) foreign = true;
                if (atRoot && InlineServers.IsMatch(raw)) foreign = true;
            }
            lines.Add(new Line(raw, inOwn, header && inOwn && !ownMatch!.Groups[1].Success, header));
            var (opened, d) = Walk(raw, null);
            multiline = opened;
            depth = header ? 0 : Math.Max(0, d);
        }
        return (lines, foreign);
    }

    /// Reads one line as TOML does, starting inside the multi-line string `open` (""" or ''') if
    /// there is one. Returns the multi-line string still open at its end, and brackets and braces
    /// opened minus closed outside strings and comments.
    static (string? Open, int Delta) Walk(string line, string? open)
    {
        int i = 0, d = 0;
        while (i < line.Length)
        {
            if (open != null)
            {
                var end = CloseOf(line, i, open);
                if (end < 0) return (open, d);
                i = end + 3;
                open = null;
                continue;
            }
            var c = line[i];
            if (c == '#') break;
            if (string.CompareOrdinal(line, i, "\"\"\"", 0, 3) == 0) { open = "\"\"\""; i += 3; continue; }
            if (string.CompareOrdinal(line, i, "'''", 0, 3) == 0) { open = "'''"; i += 3; continue; }
            if (c == '"')
            {
                for (i++; i < line.Length && line[i] != '"'; i++) if (line[i] == '\\') i++;
                i++;
                continue;
            }
            if (c == '\'')
            {
                var close = line.IndexOf('\'', i + 1);
                i = close < 0 ? line.Length : close + 1;
                continue;
            }
            if (c is '[' or '{') d++;
            else if (c is ']' or '}') d--;
            i++;
        }
        return (open, d);
    }

    /// Where the multi-line string `open` closes on the line, from `from`; -1 if it doesn't.
    static int CloseOf(string line, int from, string open)
    {
        if (open == "'''") return line.IndexOf("'''", from, StringComparison.Ordinal);
        for (int i = from; i + 2 < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '"' && line[i + 1] == '"' && line[i + 2] == '"') return i;
        }
        return -1;
    }

    /// The line up to a # that is not inside a quoted string.
    static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote == '\0') { if (c == '#') return line[..i]; if (c is '"' or '\'') quote = c; }
            else if (c == '\\' && quote == '"') i++;
            else if (c == quote) quote = '\0';
        }
        return line;
    }

    public const string Foreign = "it already sets up “{0}” in a way OpenRewind doesn't change";

    public static McpLink Status(string? text, string name, McpCommandLine want, bool ignoreCase, out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(text)) return McpLink.NotAdded;
        var (lines, foreign) = Scan(text, name);
        if (foreign) { problem = string.Format(CultureInfo.InvariantCulture, Foreign, name); return McpLink.Unreadable; }
        if (!lines.Any(l => l.Own)) return McpLink.NotAdded;
        string? command = null;
        List<string>? args = new();
        var sawArgs = false;
        var extra = false;
        var off = false;
        var inMain = false;
        foreach (var l in lines)
        {
            if (l.Header) { inMain = l.OwnMain; if (l.Own && !l.OwnMain) extra = true; continue; }
            if (!inMain) continue;
            var body = StripComment(l.Text).Trim();
            if (body.Length == 0) continue;
            var eq = body.IndexOf('=');
            if (eq < 0) { extra = true; continue; }
            var key = body[..eq].Trim();
            var value = body[(eq + 1)..].Trim();
            if (key == "command") command = ParseString(value);
            else if (key == "args") { sawArgs = true; args = ParseStringArray(value); }
            else if (key == "enabled" && value is "false" or "true") off = value == "false";
            // Anything else (env, a url, …) is not what the button writes.
            else extra = true;
        }
        if (!sawArgs) args = new List<string>();
        if (extra || !want.SameAs(command, args, ignoreCase)) return McpLink.Different;
        return off ? McpLink.TurnedOff : McpLink.Added;
    }

    /// The file with openrewind's table written, in place of any it had. Throws McpConfigException
    /// when openrewind is set up some other way in the file.
    public static string Add(string? text, string name, McpCommandLine want)
    {
        text ??= "";
        var crlf = text.Contains("\r\n");
        var (lines, foreign) = Scan(text, name);
        if (foreign) throw new McpConfigException(string.Format(CultureInfo.InvariantCulture, Foreign, name));
        var table = Table(name, want);
        var output = new List<string>();
        var placed = false;
        if (lines.Any(l => l.Own))
        {
            foreach (var (l, i) in WithoutOwn(lines))
            {
                if (i != -1) output.Add(l!.Text);
                // The blank lines after the old table went with it; one goes back after the new one.
                else if (!placed) { output.AddRange(table); output.Add(""); placed = true; }
            }
            // Never two blank lines at the very end.
            while (output.Count > 1 && output[^1].Length == 0 && output[^2].Length == 0) output.RemoveAt(output.Count - 1);
        }
        else
        {
            output.AddRange(lines.Select(l => l.Text));
            // The file's last line ends with "\n", which Split leaves as an empty last line.
            while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
            if (output.Count > 0) output.Add("");
            output.AddRange(table);
            output.Add("");
        }
        var result = Join(output, crlf);
        // A file read wrongly could end up with two openrewind tables, which ChatGPT would refuse
        // to load at all. Check the result the same way and write nothing if it isn't exactly one.
        var (check, again) = Scan(result, name);
        if (again || check.Count(l => l.OwnMain) != 1 || Status(result, name, want, false, out _) != McpLink.Added)
            throw new McpConfigException($"OpenRewind couldn't work out where “{name}” goes in it");
        return result;
    }

    /// The file without openrewind's table, or null when it has none (nothing to change).
    public static string? Remove(string? text, string name)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var (lines, foreign) = Scan(text, name);
        if (!lines.Any(l => l.Own))
        {
            if (foreign) throw new McpConfigException(string.Format(CultureInfo.InvariantCulture, Foreign, name));
            return null;
        }
        // Where a table was taken out, no run of blank lines is left behind.
        var output = new List<string>();
        var seam = false;
        foreach (var (l, i) in WithoutOwn(lines))
        {
            if (i == -1) { seam = true; continue; }
            var blank = l!.Text.Trim().Length == 0;
            if (seam && blank && (output.Count == 0 || output[^1].Trim().Length == 0)) continue;
            seam = seam && blank;
            output.Add(l.Text);
        }
        if (seam)
        {
            // The table was last: the file ends where it did before it, with a newline if it had one.
            while (output.Count > 0 && output[^1].Trim().Length == 0) output.RemoveAt(output.Count - 1);
            if (output.Count > 0 && text.EndsWith('\n')) output.Add("");
        }
        return Join(output, text.Contains("\r\n"));
    }

    /// The lines that stay, with (null, -1) once for each run of openrewind's lines. The marker
    /// comment just above a table goes with it, and so do blank lines at the table's end.
    static IEnumerable<(Line? Line, int Index)> WithoutOwn(List<Line> lines)
    {
        var drop = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Own) continue;
            drop[i] = true;
            if (lines[i].Header && i > 0 && lines[i - 1].Text.Trim() == Marker) drop[i - 1] = true;
        }
        // Comments at the end of a table are usually about the table after it: they stay.
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Own || (i + 1 < lines.Count && lines[i + 1].Own)) continue;
            for (int k = i; k >= 0 && lines[k].Own && !lines[k].Header; k--)
            {
                var t = lines[k].Text.Trim();
                if (t.Length == 0) continue;
                if (!t.StartsWith('#')) break;
                if (t != Marker) drop[k] = false;
            }
        }
        for (int i = 0; i < lines.Count; i++)
        {
            if (drop[i]) { if (i == 0 || !drop[i - 1]) yield return (null, -1); continue; }
            yield return (lines[i], i);
        }
    }

    static string Join(List<string> lines, bool crlf) => string.Join(crlf ? "\r\n" : "\n", lines);

    static List<string> Table(string name, McpCommandLine want) => new()
    {
        Marker,
        $"[mcp_servers.{name}]",
        $"command = {Quote(want.Command)}",
        $"args = [{string.Join(", ", want.Args.Select(Quote))}]",
    };

    /// A TOML string: 'literal' when it can be (Windows paths read naturally), "basic" with escapes otherwise.
    public static string Quote(string s)
    {
        if (!s.Contains('\'') && !s.Any(c => char.IsControl(c))) return "'" + s + "'";
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// One TOML string value ("basic" or 'literal'), or null if `value` is not just one string.
    public static string? ParseString(string value)
    {
        var i = 0;
        var s = ReadString(value, ref i);
        return s != null && i == value.Length ? s : null;
    }

    /// A one-line array of strings, or null if it is anything else.
    public static List<string>? ParseStringArray(string value)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']')) return null;
        var list = new List<string>();
        var i = 1;
        while (true)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
            if (i < value.Length && value[i] == ']') return i == value.Length - 1 ? list : null;
            var s = ReadString(value, ref i);
            if (s == null) return null;
            list.Add(s);
            while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
            if (i < value.Length && value[i] == ',') { i++; continue; }
            if (i < value.Length && value[i] == ']') return i == value.Length - 1 ? list : null;
            return null;
        }
    }

    static string? ReadString(string v, ref int i)
    {
        if (i >= v.Length) return null;
        var q = v[i];
        if (q == '\'')
        {
            var end = v.IndexOf('\'', i + 1);
            if (end < 0) return null;
            var lit = v[(i + 1)..end];
            i = end + 1;
            return lit;
        }
        if (q != '"') return null;
        var sb = new StringBuilder();
        for (i++; i < v.Length; i++)
        {
            var c = v[i];
            if (c == '"') { i++; return sb.ToString(); }
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= v.Length) return null;
            switch (v[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'e': sb.Append('\u001b'); break;
                case 'u' or 'U':
                    var n = v[i] == 'u' ? 4 : 8;
                    if (i + n >= v.Length) return null;
                    if (!int.TryParse(v.AsSpan(i + 1, n), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) return null;
                    try { sb.Append(char.ConvertFromUtf32(code)); } catch (ArgumentOutOfRangeException) { return null; }
                    i += n;
                    break;
                default: return null;
            }
        }
        return null;
    }
}
/// The file changed between reading it and replacing it (the app wrote it meanwhile).
public sealed class McpConfigChangedException : Exception
{
    public McpConfigChangedException(string path) : base($"{path} changed while OpenRewind was saving it") { }
}

/// Reads and writes AI apps' setup files without ever leaving one half-written: the new text goes
/// to a new private temporary file in the same folder, which replaces the file in one step, and only
/// if nothing else changed the file meanwhile. The previous version is kept as one
/// "<name>.openrewind-backup" next to it (readable only by you), which Remove deletes again.
public static class McpConfigFile
{
    public const string BackupSuffix = ".openrewind-backup";

    public const string NotUtf8 = "it isn't saved as UTF-8 text";

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// Owner read and write only, for new files, temporary files and backups.
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// The file's text, or null when it doesn't exist. Bytes that aren't UTF-8 (a file saved as
    /// "ANSI") throw McpConfigException rather than being read as something else and written back.
    public static string? Read(string path)
    {
        path = Target(path);
        return File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null;
    }

    public static string Decode(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { return StrictUtf8.GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { throw new McpConfigException(NotUtf8); }
    }

    /// Writes `text` over the file (or creates it and its folder), if the file still holds
    /// `expected` (null: doesn't exist); otherwise throws McpConfigChangedException and changes nothing.
    /// A file that is a link to another (dotfiles kept elsewhere) is written where the link points.
    public static void Write(string path, string text, string? expected, bool backup = true)
    {
        path = Target(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var exists = File.Exists(path);
        // Keep a private file private (~/.claude.json is readable only by its owner); new files are private.
        var mode = exists && !OperatingSystem.IsWindows() ? File.GetUnixFileMode(path) : Private;
        var temp = $"{path}.openrewind-{Guid.NewGuid():N}.tmp";
        try
        {
            CreateNew(temp, new UTF8Encoding(false).GetBytes(text), mode);
            string? now;
            try { now = File.Exists(path) ? Decode(File.ReadAllBytes(path)) : null; }
            catch (McpConfigException) { now = "\0changed"; }
            if (now != expected) throw new McpConfigChangedException(path);
            if (backup && exists)
            {
                var copy = path + BackupSuffix;
                File.Delete(copy);
                CreateNew(copy, File.ReadAllBytes(path), Private);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// Takes away the backup Write keeps, if there is one.
    public static void DeleteBackup(string path)
    {
        try { File.Delete(Target(path) + BackupSuffix); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"connect: cannot delete a backup: {e.Message}"); }
    }

    /// A new file with `bytes`, made with `mode` from the start so it is never readable by others.
    static void CreateNew(string path, byte[] bytes, UnixFileMode mode)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = mode;
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// Changes every file with `change` (null result: leave it), or none of them. Reads them all
    /// first, writes them one by one, and puts back the ones already written if a later one fails.
    /// When an app writes a file meanwhile, starts over with what it wrote, up to three times.
    /// Removing (`removing`) keeps no backup and deletes the old one.
    public static int Change(IReadOnlyList<string> files, Func<string?, string?> change, bool removing = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return ChangeOnce(files, change, removing); }
            catch (McpConfigChangedException e) when (attempt < 3) { Log.Write($"connect: {e.Message}; trying again"); }
            catch (McpConfigChangedException e)
            {
                throw new McpConfigException($"{e.Message}. Quit the app and try again.");
            }
        }
    }

    static int ChangeOnce(IReadOnlyList<string> files, Func<string?, string?> change, bool removing)
    {
        var writes = new List<(string Path, string? Before, string After)>();
        foreach (var f in files)
        {
            string? before, after;
            try
            {
                before = Read(f);
                after = change(before);
            }
            catch (McpConfigException e) { throw new McpConfigException($"OpenRewind didn't change {f} because {e.Message}. Fix that file, or set it up by hand below."); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new McpConfigException($"OpenRewind can't read {f} ({e.Message})."); }
            if (after != null && after != before) writes.Add((f, before, after));
        }
        var done = new List<(string Path, string? Before, string After)>();
        foreach (var w in writes)
        {
            try { Write(w.Path, w.After, w.Before, backup: !removing); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or McpConfigChangedException)
            {
                foreach (var d in done) PutBack(d.Path, d.Before, d.After);
                if (e is McpConfigChangedException) throw;
                var also = done.Count > 0 ? $" The other file{(done.Count > 1 ? "s were" : " was")} put back as {(done.Count > 1 ? "they were" : "it was")}." : "";
                throw new McpConfigException($"Couldn't save {w.Path} ({e.Message}).{also}");
            }
            Log.Write($"connect: changed {w.Path}");
            done.Add(w);
        }
        if (removing) foreach (var f in files) DeleteBackup(f);
        return done.Count;
    }

    /// Undoes one write: the old text back, or no file if there was none.
    static void PutBack(string path, string? before, string after)
    {
        try
        {
            if (before == null) { if (Read(path) == after) File.Delete(Target(path)); }
            else Write(path, before, after, backup: false);
            Log.Write($"connect: put {path} back");
        }
        catch (Exception e) { Log.Write($"connect: cannot put {path} back: {e.Message}"); }
    }

    static string Target(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget != null && info.ResolveLinkTarget(true) is FileSystemInfo t) return t.FullName;
        }
        catch (IOException) { }
        return path;
    }
}

/// Where each AI app keeps its MCP setup. Paths only; the apps do the reading.
public static class McpTargets
{
    /// Claude Desktop's config files that exist or could: the usual one, and on Windows the one a
    /// Microsoft Store (MSIX) install really reads, %LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude.
    public static List<string> ClaudeDesktopFiles(string appData, string localAppData)
    {
        var files = new List<string>();
        try
        {
            var packages = Path.Combine(localAppData, "Packages");
            if (Directory.Exists(packages))
                foreach (var pkg in Directory.GetDirectories(packages, "Claude_*").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    files.Add(Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude_desktop_config.json"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var usual = Path.Combine(appData, "Claude", "claude_desktop_config.json");
        // The usual file only when there is no Store install, or the classic install's folder is there too.
        if (files.Count == 0 || Directory.Exists(Path.GetDirectoryName(usual))) files.Add(usual);
        return files;
    }

    /// ~/.claude.json, or .claude.json in CLAUDE_CONFIG_DIR when that is set.
    public static string ClaudeCodeFile(string userHome, string? configDir) =>
        string.IsNullOrWhiteSpace(configDir) ? Path.Combine(userHome, ".claude.json") : Path.Combine(configDir, ".claude.json");

    /// ~/.codex/config.toml, or config.toml in CODEX_HOME when that is set. The ChatGPT desktop app,
    /// the Codex app, CLI and IDE extension all read it.
    public static string CodexFile(string userHome, string? codexHome) =>
        Path.Combine(string.IsNullOrWhiteSpace(codexHome) ? Path.Combine(userHome, ".codex") : codexHome, "config.toml");

    public static string CursorFile(string userHome) => Path.Combine(userHome, ".cursor", "mcp.json");
}
