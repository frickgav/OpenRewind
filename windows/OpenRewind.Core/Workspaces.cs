using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenRewind;

/// One thing that was open: an app window ("app"), a browser tab ("tab") or a document ("document").
/// `AppId` is how to start the app again: the exe path on Windows, the bundle id on the Mac.
public sealed record WorkspaceItem(string Kind, string App, string AppId, string Title = "", string Url = "", string Path = "");

/// What was open at one moment, saved about once a minute while it changes.
public sealed record WorkspaceSnapshot(long Id, DateTime Time, List<WorkspaceItem> Items);

/// Restore Workspace: while recording, the apps, windows, documents and browser tabs that are open are
/// saved now and then (only when the set changed); on the timeline, Shift+O opens again what was open at
/// the shown moment, after asking which. Never closes anything. Mac: Workspaces.swift, same table and JSON.
public static class Workspace
{
    /// Kinds of item.
    public const string App = "app", Tab = "tab", Document = "document";

    public static string ToJson(IEnumerable<WorkspaceItem> items)
    {
        var a = new JsonArray();
        foreach (var i in items)
        {
            var o = new JsonObject { ["kind"] = i.Kind, ["app"] = i.App, ["app_id"] = i.AppId };
            if (i.Title.Length > 0) o["title"] = i.Title;
            if (i.Url.Length > 0) o["url"] = i.Url;
            if (i.Path.Length > 0) o["path"] = i.Path;
            a.Add(o);
        }
        return a.ToJsonString();
    }

    /// Items from the saved JSON; anything unreadable is skipped.
    public static List<WorkspaceItem> Parse(string? json)
    {
        var list = new List<WorkspaceItem>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray a) return list;
            foreach (var n in a)
            {
                if (n is not JsonObject o) continue;
                string S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
                var kind = S("kind");
                if (kind is not (App or Tab or Document)) continue;
                list.Add(new WorkspaceItem(kind, S("app"), S("app_id"), S("title"), S("url"), S("path")));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// Two snapshots with the same fingerprint have the same things open. Window titles are left out (they
    /// change with every keystroke in some apps); apps, tab addresses and document paths count.
    public static string Fingerprint(IEnumerable<WorkspaceItem> items) =>
        string.Join("\n", items.Select(i => $"{i.Kind}\u001F{i.AppId.ToLowerInvariant()}\u001F{(i.Kind == App ? "" : i.Url + "\u001F" + i.Path)}")
                                .Distinct().OrderBy(s => s, StringComparer.Ordinal));

    /// File types that run something when opened (programs, scripts, shortcuts, installers, app bundles,
    /// disk images): Restore Workspace never opens them, whatever a saved snapshot says. Same list on the Mac.
    static readonly HashSet<string> runs = new(StringComparer.OrdinalIgnoreCase) { "exe", "com", "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "msi", "msp", "msix", "appx", "scr", "pif", "lnk", "url", "reg", "cpl", "jar", "appref-ms", "application", "gadget", "inf", "scf", "sh", "bash", "zsh", "csh", "command", "tool", "app", "pkg", "mpkg", "workflow", "terminal", "scpt", "scptd", "applescript", "py", "pyw", "rb", "pl", "php", "action", "prefpane", "kext", "plugin", "webloc", "inetloc", "fileloc", "desktop", "run", "bin", "dll", "sys", "drv", "ocx", "xll", "ade", "adp", "mde", "chm", "settingcontent-ms", "library-ms", "diagcab", "iso", "img", "vhd", "vhdx", "dmg" };

    /// A document that is safe to open again: it has an extension, and not one that runs something.
    public static bool IsSafeDocument(string path)
    {
        var name = path[(path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..].TrimEnd('.', ' ');
        int dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 && !runs.Contains(name[(dot + 1)..]);
    }

    /// Only web pages (http and https) are opened again.
    public static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// What may be opened again: not blocked apps, private windows, blocked words or sites; tabs only with
    /// a web address; documents only when the file is still there. One entry per tab, document and app.
    public static List<WorkspaceItem> Restorable(IEnumerable<WorkspaceItem> items, Blocklist list, Func<string, bool> fileExists)
    {
        var out_ = new List<WorkspaceItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items)
        {
            if (list.BlocksWindow(i.AppId.Length > 0 ? Blocklist.IdFor(i.AppId) : null, i.App, i.Title)) continue;
            if (list.BlocksApp(i.AppId, i.App)) continue;
            switch (i.Kind)
            {
                case Tab:
                    if (!IsWebUrl(i.Url) || list.BlocksUrl(i.Url) || list.HasKeyword(i.Url)) continue;
                    if (!seen.Add("tab:" + i.Url)) continue;
                    break;
                case Document:
                    if (i.Path.Length == 0 || !IsSafeDocument(i.Path) || !fileExists(i.Path)) continue;
                    if (!seen.Add("doc:" + i.Path)) continue;
                    break;
                default:
                    if (i.AppId.Length == 0 || !seen.Add("app:" + i.AppId)) continue;
                    break;
            }
            out_.Add(i);
        }
        return out_;
    }

    /// One line for the confirmation list: "Chrome — Pricing page (example.com)".
    public static string Describe(WorkspaceItem i)
    {
        var what = i.Kind switch
        {
            Tab => i.Title.Length > 0 ? $"{i.Title} ({Host(i.Url)})" : i.Url,
            Document => i.Path[(i.Path.LastIndexOfAny(new[] { '/', '\\' }) + 1)..],
            _ => i.Title,
        };
        return what.Length > 0 && what != i.App ? $"{i.App} — {what}" : i.App;
    }

    static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}

public sealed partial class Database
{
    /// The Mac app creates the same table.
    void MigrateWorkspaces()
    {
        Exec("""
        CREATE TABLE IF NOT EXISTS workspaces (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp TEXT NOT NULL,
            items TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_workspaces_ts ON workspaces(timestamp);
        """);
    }

    bool HasTable(string name) =>
        ScalarLong("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = ?", name) > 0;

    /// Save what is open at `time` (UTC), unless it is the same as the last snapshot. Returns the new id, or null.
    public long? SaveWorkspace(DateTime time, IReadOnlyList<WorkspaceItem> items)
    {
        lock (gate)
        {
            var last = Strings("SELECT items FROM workspaces ORDER BY timestamp DESC, id DESC LIMIT 1").FirstOrDefault();
            if (last != null && Workspace.Fingerprint(Workspace.Parse(last)) == Workspace.Fingerprint(items)) return null;
            using var cmd = Cmd("INSERT INTO workspaces (timestamp, items) VALUES (?, ?); SELECT last_insert_rowid();", Time.Iso(time), Workspace.ToJson(items));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// What was open at `time`: the last snapshot at or before it, within a day. Null when none.
    public WorkspaceSnapshot? WorkspaceAt(DateTime time)
    {
        lock (gate)
        {
            if (!HasTable("workspaces")) return null;
            return Rows("SELECT id, timestamp, items FROM workspaces WHERE timestamp <= ? AND timestamp >= ? ORDER BY timestamp DESC, id DESC LIMIT 1",
                        r => (Id: r.GetInt64(0), Time: Time.ParseIso(r.GetString(1)), Items: r.GetString(2)), Time.Iso(time), Time.Iso(time.AddDays(-1)))
                .Where(x => x.Time != null).Select(x => new WorkspaceSnapshot(x.Id, x.Time!.Value, Workspace.Parse(x.Items))).FirstOrDefault();
        }
    }

    /// Snapshots go with the recordings of their time (Delete Recordings and Keep Screen Recordings For).
    public void DeleteWorkspacesWith(DateTime? from, DateTime? to)
    {
        var (where, args) = Range("timestamp", from, to);
        Run($"DELETE FROM workspaces {where}", args);
    }
}
