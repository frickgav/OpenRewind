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
    /// The file is there but OpenRewind can't read or safely change it (not valid JSON, an
    /// openrewind entry it did not write, …). Nothing is changed; the user sets it up by hand.
    Unreadable,
}

/// How the AI app starts the server: the program and its arguments.
public sealed record McpCommandLine(string Command, IReadOnlyList<string> Args)
{
    /// Paths on Windows ignore letter case; on the Mac they usually do too, but a path the
    /// user typed is compared exactly there.
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
        // Reading every key once finds duplicate keys, which JsonObject only reports on first use.
        try { _ = root.Count; } catch (ArgumentException) { throw new McpConfigException(NotJson); }
        var servers = root[ServersKey];
        if (servers == null) return (root, null);
        if (servers is not JsonObject so) throw new McpConfigException($"its “{ServersKey}” is not a JSON object");
        try { _ = so.Count; } catch (ArgumentException) { throw new McpConfigException(NotJson); }
        return (root, so);
    }

    public static McpLink Status(string? text, string name, McpCommandLine want, bool ignoreCase, out string? problem)
    {
        problem = null;
        JsonObject? servers;
        try { servers = Parse(text).Servers; }
        catch (McpConfigException e) { problem = e.Message; return McpLink.Unreadable; }
        if (servers?[name] is not JsonNode entry) return McpLink.NotAdded;
        if (entry is not JsonObject o) return McpLink.Different;
        var command = o["command"] is JsonValue c && c.TryGetValue(out string? cs) ? cs : null;
        List<string>? args = null;
        if (o["args"] is JsonArray a)
        {
            args = new List<string>();
            foreach (var item in a)
                if (item is JsonValue v && v.TryGetValue(out string? s)) args.Add(s); else { args = null; break; }
        }
        else if (o["args"] == null) args = new List<string>();
        // A server that also sets a URL, or isn't stdio, is not what the button adds.
        if (o["url"] != null || (o["type"] is JsonValue t && t.TryGetValue(out string? ts) && ts != "stdio")) return McpLink.Different;
        return want.SameAs(command, args, ignoreCase) ? McpLink.Added : McpLink.Different;
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

    static readonly Regex AnyHeader = new(@"^\s*\[", RegexOptions.Compiled);
    static readonly Regex ServersHeader = new(@"^\s*\[\s*mcp_servers\s*\]\s*(#.*)?$", RegexOptions.Compiled);
    static readonly Regex InlineServers = new(@"^\s*mcp_servers\s*=", RegexOptions.Compiled);

    static Regex OwnHeader(string name) =>
        new($@"^\s*\[\s*mcp_servers\s*\.\s*(?:{Regex.Escape(name)}|""{Regex.Escape(name)}""|'{Regex.Escape(name)}')\s*(\.[^\]]*)?\]\s*(#.*)?$");

    static Regex DottedKey(string name) =>
        new($@"^\s*mcp_servers\s*\.\s*(?:{Regex.Escape(name)}|""{Regex.Escape(name)}""|'{Regex.Escape(name)}')\s*[.=]");

    static Regex KeyInServers(string name) =>
        new($@"^\s*(?:{Regex.Escape(name)}|""{Regex.Escape(name)}""|'{Regex.Escape(name)}')\s*[.=]");

    /// One line of the file and what it is.
    sealed record Line(string Text, bool Own, bool OwnMain, bool Header);

    /// The file as lines, which of them belong to openrewind's tables, and whether openrewind is
    /// also defined in a way only the user should change.
    static (List<Line> Lines, bool Foreign) Scan(string text, string name)
    {
        var own = OwnHeader(name);
        var dotted = DottedKey(name);
        var keyInServers = KeyInServers(name);
        var lines = new List<Line>();
        bool inOwn = false, inServers = false, foreign = false, atRoot = true;
        string? multiline = null;
        var depth = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (multiline != null)
            {
                // Inside a """ or ''' string: not a header or a key, whatever it looks like.
                if (CountOf(raw, multiline) % 2 == 1) multiline = null;
                lines.Add(new Line(raw, inOwn, false, false));
                continue;
            }
            if (depth > 0)
            {
                // The rest of an array (or table) value that spans lines.
                depth = Math.Max(0, depth + BracketDelta(raw));
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
            }
            else
            {
                if (!inOwn)
                {
                    if (dotted.IsMatch(raw)) foreign = true;
                    if (inServers && keyInServers.IsMatch(raw)) foreign = true;
                    if (atRoot && InlineServers.IsMatch(raw)) foreign = true;
                }
                depth = Math.Max(0, BracketDelta(raw));
            }
            lines.Add(new Line(raw, inOwn, header && inOwn && !ownMatch!.Groups[1].Success, header));
            foreach (var q in new[] { "\"\"\"", "'''" })
                if (CountOf(StripComment(raw), q) % 2 == 1) { multiline = q; depth = 0; break; }
        }
        return (lines, foreign);
    }

    /// Brackets and braces opened minus closed on a line, outside strings and comments.
    static int BracketDelta(string line)
    {
        int d = 0;
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0') { if (c == '\\' && quote == '"') i++; else if (c == quote) quote = '\0'; continue; }
            if (c == '#') break;
            if (c is '"' or '\'') quote = c;
            else if (c is '[' or '{') d++;
            else if (c is ']' or '}') d--;
        }
        return d;
    }

    static int CountOf(string s, string what)
    {
        int n = 0;
        for (int i = s.IndexOf(what, StringComparison.Ordinal); i >= 0; i = s.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
        return n;
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
            // Anything else (env, enabled = false, a url, …) is not what the button writes.
            else extra = true;
        }
        if (!sawArgs) args = new List<string>();
        return !extra && want.SameAs(command, args, ignoreCase) ? McpLink.Added : McpLink.Different;
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
        return Join(output, crlf);
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

/// Reads and writes AI apps' setup files without ever leaving one half-written: the old file is
/// copied to "<name>.openrewind-backup" next to it, the new text goes to a temporary file in the
/// same folder, and that replaces the file in one step. Nothing else is touched.
public static class McpConfigFile
{
    public const string BackupSuffix = ".openrewind-backup";
    const string TempSuffix = ".openrewind-tmp";

    /// The file's text, or null when it doesn't exist.
    public static string? Read(string path)
    {
        path = Target(path);
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    /// Writes `text` over the file (or creates it and its folder). A file that is a link to another
    /// (dotfiles kept elsewhere) is written where the link points, so the link stays.
    public static void Write(string path, string text)
    {
        path = Target(path);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = path + TempSuffix;
        var exists = File.Exists(path);
        if (exists) File.Copy(path, path + BackupSuffix, overwrite: true);
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        // Keep a private file private (~/.claude.json is readable only by its owner).
        if (exists && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
        File.Move(temp, path, overwrite: true);
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
