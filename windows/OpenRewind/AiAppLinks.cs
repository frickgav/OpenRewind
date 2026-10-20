using System.Diagnostics;
using System.Xml.Linq;

namespace OpenRewind;

/// One AI app that Connect AI Apps sets up with one click: where it keeps its MCP servers, and how
/// to add, update and remove the openrewind entry there. Only that entry in those files changes.
/// The Mac app has the same in MCPConnect.swift.
abstract class AiAppLink
{
    public abstract string Title { get; }
    /// The short name in "Restart …" and in messages.
    public abstract string AppName { get; }
    /// What the button does, for the line under the title.
    public abstract string About { get; }

    protected readonly McpCommandLine Want;
    protected AiAppLink(McpCommandLine want) => Want = want;

    public abstract (McpLink Link, string? Problem) Status();
    /// Adds or updates the entry; returns what to tell the user. Throws with a message for the user.
    public abstract string Add();
    public abstract string Remove();

    /// The app's own process names on this PC, and a test of the path, so Claude Desktop is not
    /// confused with Claude Code's claude.exe.
    protected virtual string[] ProcessNames => Array.Empty<string>();
    protected virtual bool IsAppPath(string path) => false;
    public bool CanRestart => ProcessNames.Length > 0 && DesktopApp.Running(ProcessNames, IsAppPath).Count > 0;
    public void Restart() => DesktopApp.Restart(ProcessNames, IsAppPath);

    protected static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// "Couldn't change <file>: <why>." The path is the user's own; nothing secret is in it.
    protected static McpConfigException Cannot(string path, string why) => new($"OpenRewind didn't change {path} because {why}. Fix that file, or set it up by hand below.");

    /// The status of several files read as one: added only when every one has it.
    protected (McpLink, string?) StatusOf(IEnumerable<string> files, Func<string?, (McpLink, string?)> one)
    {
        var all = new List<McpLink>();
        foreach (var f in files)
        {
            string? text;
            try { text = McpConfigFile.Read(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (McpLink.Unreadable, $"OpenRewind can't read {f} ({e.Message})."); }
            var (link, problem) = one(text);
            if (link == McpLink.Unreadable) return (link, $"OpenRewind can't change {f} because {problem}. Set it up by hand below.");
            all.Add(link);
        }
        if (all.Count == 0 || all.All(l => l == McpLink.NotAdded)) return (McpLink.NotAdded, null);
        return (all.All(l => l == McpLink.Added) ? McpLink.Added : McpLink.Different, null);
    }

    /// Works out every file's new text first and writes only when all of them can be changed.
    protected static int Change(IEnumerable<string> files, Func<string?, string?> change)
    {
        var writes = new List<(string Path, string Text)>();
        foreach (var f in files)
        {
            string? text;
            try { text = McpConfigFile.Read(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new McpConfigException($"OpenRewind can't read {f} ({e.Message})."); }
            string? after;
            try { after = change(text); }
            catch (McpConfigException e) { throw Cannot(f, e.Message); }
            if (after != null && after != text) writes.Add((f, after));
        }
        foreach (var (path, text) in writes)
        {
            McpConfigFile.Write(path, text);
            Log.Write($"connect: changed {path}");
        }
        return writes.Count;
    }
}

/// Claude Desktop and Cursor: an "mcpServers" entry in a JSON file.
sealed class JsonAppLink : AiAppLink
{
    readonly Func<List<string>> files;
    readonly string title, app, about;
    readonly string[] names;
    readonly Func<string, bool> isApp;

    public JsonAppLink(McpCommandLine want, string title, string app, string about, Func<List<string>> files, string[] processNames, Func<string, bool> isApp) : base(want)
    {
        this.title = title; this.app = app; this.about = about; this.files = files; names = processNames; this.isApp = isApp;
    }

    public override string Title => title;
    public override string AppName => app;
    public override string About => about;
    protected override string[] ProcessNames => names;
    protected override bool IsAppPath(string path) => isApp(path);

    public override (McpLink Link, string? Problem) Status() =>
        StatusOf(files(), t => (McpJsonConfig.Status(t, McpSetup.Name, Want, ignoreCase: true, out var p), p));

    public override string Add()
    {
        Change(files(), t => McpJsonConfig.Add(t, McpSetup.Name, Want));
        return $"Added. Quit and reopen {app} to use it.";
    }

    public override string Remove()
    {
        Change(files(), t => McpJsonConfig.Remove(t, McpSetup.Name));
        return $"Removed. Quit and reopen {app} to finish.";
    }

    public static JsonAppLink ClaudeDesktop(McpCommandLine want) => new(want, "Claude Desktop", "Claude",
        "Adds OpenRewind to Claude's claude_desktop_config.json.",
        () => McpTargets.ClaudeDesktopFiles(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        new[] { "claude" },
        // The installer's %LOCALAPPDATA%\AnthropicClaude, or the Store's WindowsApps\Claude_…; never Claude Code's claude.exe.
        p => p.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase));

    public static JsonAppLink Cursor(McpCommandLine want) => new(want, "Cursor", "Cursor",
        "Adds OpenRewind to Cursor's mcp.json, for every project.",
        () => new List<string> { McpTargets.CursorFile(Home) },
        new[] { "Cursor" }, p => p.Contains(@"\cursor\", StringComparison.OrdinalIgnoreCase));
}

/// The ChatGPT desktop app (Work chats) and Codex: [mcp_servers.openrewind] in ~/.codex/config.toml.
sealed class ChatGptLink : AiAppLink
{
    public ChatGptLink(McpCommandLine want) : base(want) { }

    static string File => McpTargets.CodexFile(Home, Environment.GetEnvironmentVariable("CODEX_HOME"));

    public override string Title => "ChatGPT";
    public override string AppName => "ChatGPT";
    public override string About =>
        "For Work chats in the ChatGPT desktop app, and Codex. Regular chats and chatgpt.com only reach servers on the "
        + "internet, and OpenRewind never puts your history there.";

    protected override string[] ProcessNames => new[] { "ChatGPT", "Codex" };
    protected override bool IsAppPath(string path) => path.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    public override (McpLink Link, string? Problem) Status() =>
        StatusOf(new[] { File }, t => (McpTomlConfig.Status(t, McpSetup.Name, Want, ignoreCase: true, out var p), p));

    public override string Add()
    {
        Change(new[] { File }, t => McpTomlConfig.Add(t, McpSetup.Name, Want));
        return "Added. Quit and reopen ChatGPT, then ask in a Work chat.";
    }

    public override string Remove()
    {
        Change(new[] { File }, t => McpTomlConfig.Remove(t, McpSetup.Name));
        return "Removed. Quit and reopen ChatGPT to finish.";
    }
}

/// Claude Code: `claude mcp add --scope user` when its claude.exe is found, which keeps Claude Code
/// in charge of its own ~/.claude.json; otherwise the same entry is written there directly.
sealed class ClaudeCodeLink : AiAppLink
{
    public ClaudeCodeLink(McpCommandLine want) : base(want) { }

    static string File => McpTargets.ClaudeCodeFile(Home, Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"));

    public override string Title => "Claude Code";
    public override string AppName => "Claude Code";
    public override string About => "Adds OpenRewind to Claude Code for every project (user scope).";

    public override (McpLink Link, string? Problem) Status() =>
        StatusOf(new[] { File }, t => (McpJsonConfig.Status(t, McpSetup.Name, Want, ignoreCase: true, out var p), p));

    public override string Add()
    {
        var cli = FindCli();
        if (cli != null)
        {
            // `add` refuses a name that is already there, so an old entry goes first.
            if (Status().Link != McpLink.NotAdded) Run(cli, "mcp", "remove", "--scope", "user", McpSetup.Name);
            var args = new List<string> { "mcp", "add", "--scope", "user", McpSetup.Name, "--", Want.Command };
            args.AddRange(Want.Args);
            if (Run(cli, args.ToArray()) && Status().Link == McpLink.Added) return "Added. New Claude Code sessions can use it.";
            Log.Write("connect: claude mcp add did not add it; writing the file instead");
        }
        Change(new[] { File }, t => McpJsonConfig.Add(t, McpSetup.Name, Want, stdioType: true));
        return "Added. New Claude Code sessions can use it.";
    }

    public override string Remove()
    {
        var cli = FindCli();
        if (cli != null && Run(cli, "mcp", "remove", "--scope", "user", McpSetup.Name) && Status().Link == McpLink.NotAdded)
            return "Removed from Claude Code.";
        Change(new[] { File }, t => McpJsonConfig.Remove(t, McpSetup.Name));
        return "Removed from Claude Code.";
    }

    /// claude.exe from the native installer, or one on PATH. A claude.cmd from npm is skipped: it
    /// would need cmd.exe to run, and the file is written directly instead.
    static string? FindCli()
    {
        var places = new List<string> { Path.Combine(Home, ".local", "bin", "claude.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { places.Add(Path.Combine(dir.Trim().Trim('"'), "claude.exe")); } catch (ArgumentException) { }
        }
        // Not Claude Desktop's claude.exe, which has nothing to do with these commands.
        return places.FirstOrDefault(p => System.IO.File.Exists(p)
            && !p.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase)
            && !p.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase));
    }

    /// Runs the CLI without a window, for at most 30 seconds. Its output goes to the log, not on screen.
    static bool Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var errors = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } Log.Write($"connect: claude {args[1]} timed out"); return false; }
            p.WaitForExit();
            if (p.ExitCode != 0) Log.Write($"connect: claude {args[1]} exited {p.ExitCode}: {FirstLine(errors.Result)}{FirstLine(output.Result)}");
            return p.ExitCode == 0;
        }
        catch (Exception e) { Log.Write($"connect: cannot run claude: {e.Message}"); return false; }
    }

    static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
}

/// Finds a desktop app's running processes and restarts it, from the Store (MSIX) or not.
static class DesktopApp
{
    public static List<Process> Running(string[] names, Func<string, bool> isApp)
    {
        var found = new List<Process>();
        foreach (var name in names)
            foreach (var p in Process.GetProcessesByName(name))
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path != null && isApp(path)) found.Add(p); else p.Dispose();
            }
        return found;
    }

    /// Ends every process of the app (Electron apps run several) and starts it again the way
    /// Windows would: a Store app through its app id, others through the program it was started from.
    public static void Restart(string[] names, Func<string, bool> isApp)
    {
        var running = Running(names, isApp);
        if (running.Count == 0) return;
        // The first process started is the app itself; the rest are its helpers.
        var main = running.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).First();
        var exe = main.MainModule!.FileName;
        foreach (var p in running)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        foreach (var p in running)
        {
            try { p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
        Log.Write($"connect: restarting {Path.GetFileName(exe)}");
        if (StoreAppId(exe) is string aumid)
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo(Launcher(exe)) { UseShellExecute = true });
    }

    /// The Squirrel installer keeps a launcher above the versioned app-x.y.z folder; it starts the newest version.
    static string Launcher(string exe)
    {
        var dir = Path.GetDirectoryName(exe);
        var parent = dir == null ? null : Path.GetDirectoryName(dir);
        if (dir != null && parent != null && Path.GetFileName(dir).StartsWith("app-", StringComparison.OrdinalIgnoreCase))
        {
            var stub = Path.Combine(parent, Path.GetFileName(exe));
            if (File.Exists(stub)) return stub;
        }
        return exe;
    }

    /// "PackageFamilyName!AppId" for an exe inside C:\Program Files\WindowsApps\<package>, read
    /// from the package's AppxManifest.xml; null for anything else.
    static string? StoreAppId(string exe)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(exe); dir != null; dir = Path.GetDirectoryName(dir))
            {
                var manifest = Path.Combine(dir, "AppxManifest.xml");
                if (!File.Exists(manifest)) continue;
                var folder = Path.GetFileName(dir);
                // <Name>_<Version>_<Arch>_<ResourceId>_<PublisherId>; the family name is Name_PublisherId.
                var parts = folder.Split('_');
                if (parts.Length < 5) return null;
                var family = parts[0] + "_" + parts[^1];
                var doc = XDocument.Load(manifest);
                var exeName = Path.GetFileName(exe);
                var apps = doc.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
                var app = apps.FirstOrDefault(a => ((string?)a.Attribute("Executable"))?.EndsWith(exeName, StringComparison.OrdinalIgnoreCase) == true) ?? apps.FirstOrDefault();
                var id = (string?)app?.Attribute("Id");
                return id == null ? null : $"{family}!{id}";
            }
        }
        catch (Exception e) { Log.Write($"connect: cannot read the app's package: {e.Message}"); }
        return null;
    }
}
