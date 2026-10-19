using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Pick websites and links to block: type one, or tick sites and pages from what was recorded lately.
/// Used by the setup window and by Blocked Apps & Sites. The Mac app has the same in SitePickerView.
sealed class SitePicker : Form
{
    readonly float k;
    readonly Database db;
    readonly HashSet<string> already;
    readonly TextBox typed, search;
    readonly RowList list;
    readonly Label empty, note;
    readonly PillButton sitesTab, pagesTab, ok;
    List<RecentSite> sites = new();
    bool showPages;
    /// Rules ticked so far, kept across the two tabs and the search box.
    readonly HashSet<string> chosen = new();

    /// Adds what the user picks to `settings` and starts hiding it at once. True when anything was added.
    public static bool Pick(IWin32Window? owner, Settings settings, Database db)
    {
        using var dlg = new SitePicker(db, settings.BlockedSites);
        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
        var add = dlg.chosen.Where(r => !settings.BlockedSites.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
        if (add.Count == 0) return false;
        settings.BlockedSites.AddRange(add);
        settings.Save();
        Blocklist.Current = settings.ToBlocklist();
        Log.Write($"block list: {add.Count} sites added, {settings.BlockedSites.Count} in all");
        return true;
    }

    SitePicker(Database db, IEnumerable<string> blocked)
    {
        this.db = db;
        already = new HashSet<string>(blocked, StringComparer.OrdinalIgnoreCase);
        k = DeviceDpi / 96f;
        Text = "Block Websites and Links";
        Icon = Program.AppIcon;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size((int)(520 * k), (int)(620 * k));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding((int)(18 * k));
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        // Type a site or paste a link.
        var entry = new Panel { Dock = DockStyle.Top, Height = (int)(44 * k), BackColor = Ui.Bg, Padding = new Padding(0, (int)(4 * k), 0, (int)(4 * k)) };
        typed = Box("Type a site (chase.com) or paste a link");
        var add = new PillButton("Add") { Dock = DockStyle.Right, Width = (int)(80 * k), Font = Ui.F(13.5f * k, true) };
        typed.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddTyped(); } };
        add.Click += (_, _) => AddTyped();
        entry.Controls.Add(typed.Parent!);
        entry.Controls.Add(new Panel { Dock = DockStyle.Right, Width = (int)(8 * k), BackColor = Ui.Bg });
        entry.Controls.Add(add);
        note = new Label { Dock = DockStyle.Top, Height = (int)(24 * k), ForeColor = Ui.Faint, Font = Ui.F(12.5f * k), BackColor = Ui.Bg,
                           Text = "A site covers all its pages; a link covers that page and the ones under it." };

        // Recent sites or pages, with a search box.
        var head = new Panel { Dock = DockStyle.Top, Height = (int)(46 * k), BackColor = Ui.Bg, Padding = new Padding(0, (int)(6 * k), 0, (int)(4 * k)) };
        var recent = new Label { Text = "Recently visited", Font = Ui.F(15 * k, true), ForeColor = Ui.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Ui.Bg };
        sitesTab = new PillButton("Sites", primary: true) { Dock = DockStyle.Right, Width = (int)(80 * k), Font = Ui.F(13 * k, true) };
        pagesTab = new PillButton("Pages") { Dock = DockStyle.Right, Width = (int)(80 * k), Font = Ui.F(13 * k, true) };
        sitesTab.Click += (_, _) => ShowTab(false);
        pagesTab.Click += (_, _) => ShowTab(true);
        head.Controls.Add(recent);
        head.Controls.Add(sitesTab);
        head.Controls.Add(new Panel { Dock = DockStyle.Right, Width = (int)(6 * k), BackColor = Ui.Bg });
        head.Controls.Add(pagesTab);
        search = Box("Search recent sites");
        search.TextChanged += (_, _) => Fill();
        var searchWrap = new Panel { Dock = DockStyle.Top, Height = (int)(46 * k), BackColor = Ui.Bg, Padding = new Padding(0, (int)(4 * k), 0, (int)(6 * k)) };
        searchWrap.Controls.Add(search.Parent!);

        list = new RowList(k) { Dock = DockStyle.Fill, Visible = false };
        empty = new Label { Text = "Looking for sites…", ForeColor = Ui.Dim, Font = Ui.F(13 * k), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, BackColor = Ui.Bg };

        var bar = new Panel { Dock = DockStyle.Bottom, Height = (int)(56 * k), BackColor = Ui.Bg };
        ok = new PillButton("Block Selected", primary: true) { Font = Ui.F(13.5f * k, true), Enabled = false };
        var cancel = new PillButton("Cancel") { Font = Ui.F(13.5f * k, true) };
        bar.Resize += (_, _) =>
        {
            ok.SetBounds(bar.Width - (int)(150 * k), (int)(14 * k), (int)(150 * k), (int)(38 * k));
            cancel.SetBounds(ok.Left - (int)(110 * k), (int)(14 * k), (int)(100 * k), (int)(38 * k));
        };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => Close();
        bar.Controls.Add(ok);
        bar.Controls.Add(cancel);

        Controls.Add(list);
        Controls.Add(empty);
        Controls.Add(searchWrap);
        Controls.Add(head);
        Controls.Add(note);
        Controls.Add(entry);
        Controls.Add(bar);
        LoadSites();
    }

    TextBox Box(string placeholder)
    {
        var back = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding((int)(12 * k), (int)(8 * k), (int)(12 * k), 0) };
        var box = new TextBox { BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text, Dock = DockStyle.Fill, Font = Ui.F(14 * k), PlaceholderText = placeholder };
        back.Controls.Add(box);
        return box;
    }

    void AddTyped()
    {
        var rule = SiteRules.Normalize(typed.Text);
        if (rule == null)
        {
            note.Text = typed.Text.Trim().Length == 0 ? note.Text : $"\"{typed.Text.Trim()}\" is not a website address.";
            note.ForeColor = Ui.Warn;
            return;
        }
        chosen.Add(rule);
        typed.Clear();
        note.Text = already.Contains(rule) ? $"{rule} is already blocked." : $"{rule} will be blocked.";
        note.ForeColor = Ui.Good;
        ok.Enabled = true;
        Fill();
    }

    void LoadSites()
    {
        Task.Run(() =>
        {
            List<RecentSite> found;
            try { found = RecentSites.Group(db.RecentPages()); }
            catch (Exception e) { Log.Write($"recent sites: {e.Message}"); found = new(); }
            BeginInvokeSafe(() => { sites = found; Fill(); });
        });
    }

    void ShowTab(bool pages)
    {
        showPages = pages;
        sitesTab.Primary = !pages;
        pagesTab.Primary = pages;
        sitesTab.Invalidate();
        pagesTab.Invalidate();
        search.PlaceholderText = pages ? "Search recent pages" : "Search recent sites";
        Fill();
    }

    /// Rebuild the rows for the tab and search text. Sites or pages already blocked are left out.
    void Fill()
    {
        var q = search.Text.Trim();
        var now = DateTime.UtcNow;
        var rows = new List<Control>();
        bool Hit(params string[] fields) => q.Length == 0 || fields.Any(f => f.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        CheckRow Row(string title, string detail, string rule)
        {
            var r = new CheckRow(title, detail, chosen.Contains(rule), k) { Tag2 = rule };
            r.CheckedChanged += x =>
            {
                if (x.Checked) chosen.Add((string)x.Tag2!); else chosen.Remove((string)x.Tag2!);
                ok.Enabled = chosen.Count > 0;
            };
            return r;
        }
        if (!showPages)
        {
            foreach (var s in sites.Where(s => !already.Contains(s.Site) && Hit(s.Site)).Take(400))
                rows.Add(Row(s.Site, $"{RecentSites.Ago(s.LastSeen, now)} · {s.Pages.Count} {(s.Pages.Count == 1 ? "page" : "pages")}", s.Site));
        }
        else
        {
            foreach (var p in sites.SelectMany(s => s.Pages).OrderByDescending(p => p.LastSeen, StringComparer.Ordinal))
            {
                if (SiteRules.Normalize(p.Url) is not string rule || already.Contains(rule) || !Hit(p.Title, p.Url)) continue;
                rows.Add(Row(p.Title.Length > 0 ? p.Title : rule, rule, rule));
                if (rows.Count >= 400) break;
            }
        }
        list.SetRows(rows);
        list.Visible = rows.Count > 0;
        empty.Visible = rows.Count == 0;
        empty.Text = sites.Count == 0
            ? "No websites recorded yet. Sites you visit show up here,\nor type one above."
            : q.Length > 0 ? $"Nothing matches \"{q}\"" : "Every recent site is already blocked";
    }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed) return;
        try { if (IsHandleCreated) BeginInvoke(a); else HandleCreated += (_, _) => BeginInvoke(a); } catch (ObjectDisposedException) { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
