using System.Text.Json.Nodes;
using OpenRewind;

public class McpConnectTests : IDisposable
{
    const string Name = "openrewind";
    static readonly McpCommandLine Exe = new(@"C:\Program Files\Open Rewind\OpenRewind.exe", new[] { "mcp" });
    static readonly McpCommandLine Unicode = new("/Users/zoë/Apps/Ӧpen Rewind ✨/OpenRewind.app/Contents/MacOS/OpenRewind", new[] { "mcp", "--home", "/Volumes/外付け/.openrewind" });

    readonly string dir = Path.Combine(Path.GetTempPath(), "openrewind-connect-test-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static McpLink JsonStatus(string? text, McpCommandLine want) => McpJsonConfig.Status(text, Name, want, ignoreCase: true, out _);
    static McpLink TomlStatus(string? text, McpCommandLine want) => McpTomlConfig.Status(text, Name, want, ignoreCase: true, out _);

    // JSON (Claude Desktop, Cursor, Claude Code)

    [Fact]
    public void JsonAddToMissingFileMakesJustTheEntry()
    {
        var text = McpJsonConfig.Add(null, Name, Exe);
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Single(root);
        var entry = root["mcpServers"]![Name]!;
        Assert.Equal(Exe.Command, (string?)entry["command"]);
        Assert.Equal("mcp", (string?)entry["args"]![0]);
        Assert.EndsWith("\n", text);
        Assert.Equal(McpLink.Added, JsonStatus(text, Exe));
        Assert.Equal(McpLink.NotAdded, JsonStatus(null, Exe));
        Assert.Equal(McpLink.NotAdded, JsonStatus("", Exe));
    }

    [Fact]
    public void JsonAddKeepsEverythingElse()
    {
        const string before = """
            {
              "globalShortcut": "Ctrl+Space",
              "mcpServers": {
                "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/Users/me"] },
                "weather": { "command": "uvx", "args": ["weather"], "env": { "UNITS": "metric" } }
              },
              "preferences": { "big": 12345678901234567890, "ratio": 1.50, "list": [1, 2, 3], "nothing": null },
              "zLast": true
            }
            """;
        var after = McpJsonConfig.Add(before, Name, Exe);
        var a = JsonNode.Parse(after)!.AsObject();
        var b = JsonNode.Parse(before)!.AsObject();
        // Keys stay in their order, the new server goes last among the servers.
        Assert.Equal(new[] { "globalShortcut", "mcpServers", "preferences", "zLast" }, a.Select(p => p.Key));
        Assert.Equal(new[] { "filesystem", "weather", Name }, a["mcpServers"]!.AsObject().Select(p => p.Key));
        Assert.True(JsonNode.DeepEquals(b["mcpServers"]!["filesystem"], a["mcpServers"]!["filesystem"]));
        Assert.True(JsonNode.DeepEquals(b["mcpServers"]!["weather"], a["mcpServers"]!["weather"]));
        // Numbers are kept as written, even ones too big for a double to hold exactly.
        Assert.Contains("12345678901234567890", after);
        Assert.Contains("1.50", after);
        Assert.True(JsonNode.DeepEquals(b["preferences"], a["preferences"]));

        // Removing it gives back what was there.
        var removed = McpJsonConfig.Remove(after, Name)!;
        Assert.True(JsonNode.DeepEquals(b, JsonNode.Parse(removed)));
        Assert.Equal(McpLink.NotAdded, JsonStatus(removed, Exe));
    }

    [Fact]
    public void JsonAddTwiceIsTheSameAsOnce()
    {
        var once = McpJsonConfig.Add("""{"mcpServers":{"x":{"command":"x"}}}""", Name, Exe);
        var twice = McpJsonConfig.Add(once, Name, Exe);
        Assert.Equal(once, twice);
        Assert.Equal(McpLink.Added, JsonStatus(twice, Exe));
    }

    [Fact]
    public void JsonOldPathIsDifferentAndAddUpdatesItInPlace()
    {
        const string old = """{"mcpServers":{"a":{"command":"a"},"openrewind":{"command":"D:\\old\\OpenRewind.exe","args":["mcp"]},"z":{"command":"z"}}}""";
        Assert.Equal(McpLink.Different, JsonStatus(old, Exe));
        var updated = McpJsonConfig.Add(old, Name, Exe);
        Assert.Equal(McpLink.Added, JsonStatus(updated, Exe));
        Assert.Equal(new[] { "a", Name, "z" }, JsonNode.Parse(updated)!["mcpServers"]!.AsObject().Select(p => p.Key));
        // Other options than the button's count as different too.
        Assert.Equal(McpLink.Different, JsonStatus("""{"mcpServers":{"openrewind":{"command":"C:\\Program Files\\Open Rewind\\OpenRewind.exe","args":["mcp","--home","D:\\x"]}}}""", Exe));
        Assert.Equal(McpLink.Different, JsonStatus("""{"mcpServers":{"openrewind":{"type":"http","url":"http://127.0.0.1:1"}}}""", Exe));
        Assert.Equal(McpLink.Different, JsonStatus("""{"mcpServers":{"openrewind":"nonsense"}}""", Exe));
    }

    [Fact]
    public void JsonPathCaseMattersOnlyWhenAsked()
    {
        var text = McpJsonConfig.Add(null, Name, Exe);
        var lower = Exe with { Command = Exe.Command.ToLowerInvariant() };
        Assert.Equal(McpLink.Added, McpJsonConfig.Status(text, Name, lower, ignoreCase: true, out _));
        Assert.Equal(McpLink.Different, McpJsonConfig.Status(text, Name, lower, ignoreCase: false, out _));
    }

    [Fact]
    public void JsonPathsWithSpacesAndUnicodeRoundTrip()
    {
        var text = McpJsonConfig.Add("{}", Name, Unicode);
        // Written as is, not as \u escapes, so the file stays readable.
        Assert.Contains("zoë", text);
        Assert.Contains("外付け", text);
        Assert.Equal(McpLink.Added, McpJsonConfig.Status(text, Name, Unicode, ignoreCase: false, out _));
        var quotes = new McpCommandLine("C:\\a \"b\"\\c.exe", new[] { "mcp" });
        Assert.Equal(McpLink.Added, JsonStatus(McpJsonConfig.Add(null, Name, quotes), quotes));
    }

    [Fact]
    public void JsonClaudeCodeEntryHasItsType()
    {
        var text = McpJsonConfig.Add("""{"numStartups":5,"projects":{}}""", Name, Exe, stdioType: true);
        var entry = JsonNode.Parse(text)!["mcpServers"]![Name]!;
        Assert.Equal("stdio", (string?)entry["type"]);
        Assert.Empty(entry["env"]!.AsObject());
        Assert.Equal(McpLink.Added, JsonStatus(text, Exe));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"mcpServers\": {}} trailing")]
    [InlineData("{\"a\": 1,}")]
    [InlineData("// a comment\n{}")]
    [InlineData("[1, 2]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"mcpServers\": [1]}")]
    [InlineData("{\"mcpServers\": {\"x\": 1, \"x\": 2}}")]
    [InlineData("{\"a\": 1, \"a\": 2}")]
    public void JsonMalformedIsNeverRewritten(string bad)
    {
        Assert.Equal(McpLink.Unreadable, McpJsonConfig.Status(bad, Name, Exe, true, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
        Assert.Throws<McpConfigException>(() => McpJsonConfig.Add(bad, Name, Exe));
        Assert.Throws<McpConfigException>(() => McpJsonConfig.Remove(bad, Name));
    }

    [Fact]
    public void JsonRemoveWhenAbsentChangesNothing()
    {
        Assert.Null(McpJsonConfig.Remove(null, Name));
        Assert.Null(McpJsonConfig.Remove("{}", Name));
        Assert.Null(McpJsonConfig.Remove("""{"mcpServers":{"other":{"command":"o"}}}""", Name));
        var removed = McpJsonConfig.Remove("""{"mcpServers":{"openrewind":{"command":"o"},"other":{"command":"o"}}}""", Name)!;
        Assert.Equal(new[] { "other" }, JsonNode.Parse(removed)!["mcpServers"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void JsonKeepsWindowsLineEndings()
    {
        var text = McpJsonConfig.Add("{\r\n  \"a\": 1\r\n}\r\n", Name, Exe);
        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // TOML (ChatGPT desktop app / Codex: ~/.codex/config.toml)

    [Fact]
    public void TomlAddToMissingFile()
    {
        var text = McpTomlConfig.Add(null, Name, Exe);
        Assert.Equal(McpTomlConfig.Marker + "\n[mcp_servers.openrewind]\ncommand = 'C:\\Program Files\\Open Rewind\\OpenRewind.exe'\nargs = ['mcp']\n", text);
        Assert.Equal(McpLink.Added, TomlStatus(text, Exe));
        Assert.Equal(McpLink.NotAdded, TomlStatus(null, Exe));
    }

    const string CodexConfig = """
        # My Codex settings
        model = "gpt-5.5"
        approval_policy = "on-request"   # keep asking

        [mcp_servers.context7]
        command = "npx"
        args = ["-y", "@upstash/context7-mcp"]

        [mcp_servers.context7.env]
        TOKEN_NAME = "x"

        # Browser tools
        [mcp_servers.chrome]
        url = "http://localhost:3000/mcp"
        enabled_tools = [
          "open",
          "screenshot",
        ]

        [profiles.fast]
        model = "gpt-5.5-mini"
        notes = '''
        [mcp_servers.openrewind]
        not a table, just text
        '''

        """;

    [Fact]
    public void TomlAddAppendsAndRemoveRestoresExactly()
    {
        Assert.Equal(McpLink.NotAdded, TomlStatus(CodexConfig, Exe));
        var added = McpTomlConfig.Add(CodexConfig, Name, Exe);
        Assert.StartsWith(CodexConfig, added);
        Assert.Equal(McpLink.Added, TomlStatus(added, Exe));
        Assert.Equal(added, McpTomlConfig.Add(added, Name, Exe));
        Assert.Equal(CodexConfig, McpTomlConfig.Remove(added, Name));
        Assert.Null(McpTomlConfig.Remove(CodexConfig, Name));
    }

    [Fact]
    public void TomlUpdatesAnOldTableInPlaceAndKeepsTheNextTablesComment()
    {
        const string before = """
            model = "x"

            [mcp_servers.openrewind]
            command = "/Applications/Old/OpenRewind.app/Contents/MacOS/OpenRewind"
            args = ["mcp"]
            enabled = false

            [mcp_servers.openrewind.env]
            A = "b"

            # About the next one
            [mcp_servers.other]
            command = "o"

            """;
        Assert.Equal(McpLink.Different, TomlStatus(before, Exe));
        var updated = McpTomlConfig.Add(before, Name, Exe);
        Assert.Equal(McpLink.Added, TomlStatus(updated, Exe));
        Assert.Equal("""
            model = "x"

            # Added by OpenRewind (Connect AI Apps). Remove it there or delete this table.
            [mcp_servers.openrewind]
            command = 'C:\Program Files\Open Rewind\OpenRewind.exe'
            args = ['mcp']

            # About the next one
            [mcp_servers.other]
            command = "o"

            """, updated);
        Assert.Equal("""
            model = "x"

            # About the next one
            [mcp_servers.other]
            command = "o"

            """, McpTomlConfig.Remove(updated, Name));
    }

    [Fact]
    public void TomlRemoveFromTheMiddleLeavesNoGap()
    {
        var text = "a = 1\n\n" + McpTomlConfig.Marker + "\n[mcp_servers.openrewind]\ncommand = 'x'\n\n[b]\nc = 2\n";
        Assert.Equal("a = 1\n\n[b]\nc = 2\n", McpTomlConfig.Remove(text, Name));
        // Written by `codex mcp add`, with a quoted name and no marker.
        var quoted = "[mcp_servers.\"openrewind\"]\ncommand = \"x\"\nargs = [\"mcp\"]\n";
        Assert.Equal(McpLink.Different, TomlStatus(quoted, Exe));
        Assert.Equal("", McpTomlConfig.Remove(quoted, Name));
    }

    [Theory]
    [InlineData("mcp_servers = { openrewind = { command = \"x\" } }\n")]
    [InlineData("mcp_servers = {}\n")]
    [InlineData("[mcp_servers]\nopenrewind = { command = \"x\" }\n")]
    [InlineData("[mcp_servers]\n\"openrewind\".command = \"x\"\n")]
    [InlineData("mcp_servers.openrewind.command = \"x\"\n")]
    public void TomlLeavesOtherWaysOfSettingItUpAlone(string text)
    {
        Assert.Equal(McpLink.Unreadable, McpTomlConfig.Status(text, Name, Exe, true, out var problem));
        Assert.Contains("openrewind", problem);
        Assert.Throws<McpConfigException>(() => McpTomlConfig.Add(text, Name, Exe));
    }

    [Fact]
    public void TomlOtherServersSectionIsFine()
    {
        var text = "[mcp_servers]\n\n[mcp_servers.other]\ncommand = \"o\"\n";
        var added = McpTomlConfig.Add(text, Name, Exe);
        Assert.Equal(McpLink.Added, TomlStatus(added, Exe));
        Assert.Equal(text, McpTomlConfig.Remove(added, Name));
    }

    [Fact]
    public void TomlPathsWithQuotesSpacesAndUnicode()
    {
        Assert.Equal(McpLink.Added, McpTomlConfig.Status(McpTomlConfig.Add("", Name, Unicode), Name, Unicode, false, out _));
        var odd = new McpCommandLine("/Users/o'brien/My \"Apps\"/OpenRewind", new[] { "mcp", "--home", "C:\\data\\" });
        var text = McpTomlConfig.Add("x = 1\n", Name, odd);
        Assert.Contains("command = \"/Users/o'brien/My \\\"Apps\\\"/OpenRewind\"", text);
        Assert.Contains("'C:\\data\\'", text);
        Assert.Equal(McpLink.Added, McpTomlConfig.Status(text, Name, odd, false, out _));
    }

    [Fact]
    public void TomlStringsParseLikeToml()
    {
        Assert.Equal("a\"b\\c\té", McpTomlConfig.ParseString("\"a\\\"b\\\\c\\t\\u00e9\""));
        Assert.Equal(@"C:\x", McpTomlConfig.ParseString(@"'C:\x'"));
        Assert.Null(McpTomlConfig.ParseString("\"open"));
        Assert.Null(McpTomlConfig.ParseString("\"a\" junk"));
        Assert.Null(McpTomlConfig.ParseString("\"\\u12\""));
        Assert.Equal(new[] { "mcp", "--home", "x y" }, McpTomlConfig.ParseStringArray("[ \"mcp\", '--home' , \"x y\", ]")!);
        Assert.Empty(McpTomlConfig.ParseStringArray("[]")!);
        Assert.Null(McpTomlConfig.ParseStringArray("[1, 2]"));
        Assert.Null(McpTomlConfig.ParseStringArray("[\"a\""));
    }

    [Fact]
    public void TomlKeepsWindowsLineEndings()
    {
        var text = McpTomlConfig.Add("a = 1\r\n", Name, Exe);
        Assert.Equal("a = 1\r\n\r\n" + McpTomlConfig.Marker + "\r\n[mcp_servers.openrewind]\r\ncommand = 'C:\\Program Files\\Open Rewind\\OpenRewind.exe'\r\nargs = ['mcp']\r\n", text);
        Assert.Equal("a = 1\r\n", McpTomlConfig.Remove(text, Name));
    }

    // Files

    [Fact]
    public void WriteBacksUpAndReplaces()
    {
        var path = Path.Combine(dir, "sub dir ✨", "claude_desktop_config.json");
        Assert.Null(McpConfigFile.Read(path));
        McpConfigFile.Write(path, "{}\n");
        Assert.Equal("{}\n", McpConfigFile.Read(path));
        Assert.False(File.Exists(path + McpConfigFile.BackupSuffix));
        McpConfigFile.Write(path, "{\"a\":1}\n");
        Assert.Equal("{\"a\":1}\n", File.ReadAllText(path));
        Assert.Equal("{}\n", File.ReadAllText(path + McpConfigFile.BackupSuffix));
        Assert.Equal(new[] { "claude_desktop_config.json", "claude_desktop_config.json" + McpConfigFile.BackupSuffix },
            Directory.GetFiles(Path.GetDirectoryName(path)!).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void WriteKeepsAPrivateFilePrivate()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".claude.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        McpConfigFile.Write(path, "{\"a\":1}");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void WriteGoesThroughALink()
    {
        Directory.CreateDirectory(dir);
        var real = Path.Combine(dir, "dotfiles-mcp.json");
        var link = Path.Combine(dir, "mcp.json");
        File.WriteAllText(real, "{}");
        try { File.CreateSymbolicLink(link, real); } catch (Exception) { return; } // Windows without the right to make links.
        McpConfigFile.Write(link, "{\"a\":1}");
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("{\"a\":1}", File.ReadAllText(real));
    }

    [Fact]
    public void ClaudeDesktopFilesIncludeTheStoreInstall()
    {
        var appData = Path.Combine(dir, "Roaming");
        var local = Path.Combine(dir, "Local");
        Assert.Equal(new[] { Path.Combine(appData, "Claude", "claude_desktop_config.json") }, McpTargets.ClaudeDesktopFiles(appData, local));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc"));
        Directory.CreateDirectory(Path.Combine(local, "Packages", "Clock_123"));
        var store = Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
        Assert.Equal(new[] { store }, McpTargets.ClaudeDesktopFiles(appData, local));
        Directory.CreateDirectory(Path.Combine(appData, "Claude"));
        Assert.Equal(new[] { store, Path.Combine(appData, "Claude", "claude_desktop_config.json") }, McpTargets.ClaudeDesktopFiles(appData, local));
    }

    [Fact]
    public void ConfigHomesFollowTheirVariables()
    {
        Assert.Equal(Path.Combine("h", ".codex", "config.toml"), McpTargets.CodexFile("h", null));
        Assert.Equal(Path.Combine("c", "config.toml"), McpTargets.CodexFile("h", "c"));
        Assert.Equal(Path.Combine("h", ".claude.json"), McpTargets.ClaudeCodeFile("h", ""));
        Assert.Equal(Path.Combine("d", ".claude.json"), McpTargets.ClaudeCodeFile("h", "d"));
        Assert.Equal(Path.Combine("h", ".cursor", "mcp.json"), McpTargets.CursorFile("h"));
    }
}
