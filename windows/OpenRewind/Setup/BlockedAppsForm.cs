using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Settings → Blocked Apps & Sites: the apps, websites, private windows and title words that are blurred.
/// Every change is saved and used by recording at once.
sealed class BlockedAppsForm : Form
{
    static BlockedAppsForm? open;
    readonly Settings settings;
    readonly Database db;
    readonly float k;
    readonly RowList apps, sites, words;
    readonly CheckRow privateRow;
    readonly TextBox wordBox;

    /// One window at a time; a second click brings it to the front.
    public static void ShowFor(Settings settings, Database db)
    {
        if (open is { IsDisposed: false }) { open.Activate(); return; }
        open = new BlockedAppsForm(settings, db);
        open.Show();
        open.Activate();
    }

    /// The app locked (see AppLock): the recent pages list shows history, so close.
    public static void Release() { if (open is { IsDisposed: false }) open.Close(); }

    BlockedAppsForm(Settings settings, Database db)
    {
        this.settings = settings;
        this.db = db;
        k = DeviceDpi / 96f;
        Text = "Blocked Apps & Sites";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size((int)(560 * k), (int)(560 * k));
        ClientSize = new Size((int)(640 * k), (int)(860 * k));
        // Never taller than the screen; the lists scroll instead.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        if (Height > work.Height - 40) Height = Math.Max(MinimumSize.Height, work.Height - 40);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        Padding = new Padding((int)(24 * k));
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        var intro = Label("Blocked apps, sites and windows are blurred in every screenshot, wherever they are on screen, and their text is never saved. The lock screen is never recorded.", 13.5f, Ui.Dim, (int)(44 * k));

        var appsHead = Header("Apps", out var addApps, "Add apps…");
        addApps.Click += (_, _) => AddApps();
        apps = new RowList(k) { Dock = DockStyle.Fill };

        var sitesHead = Header("Websites and links", out var addSites, "Add sites…");
        addSites.Click += (_, _) => { if (SitePicker.Pick(this, settings, db)) RefreshLists(); };
        var sitesHint = Label("A browser tab showing one of these is hidden, checked each time it is in front.", 12.5f, Ui.Faint, (int)(24 * k));
        sites = new RowList(k) { Dock = DockStyle.Fill };

        privateRow = new CheckRow("Private browsing windows", "Incognito, InPrivate and private windows, when the browser names them in the title", settings.BlockPrivateWindows, k) { Dock = DockStyle.Top };
        privateRow.CheckedChanged += r => { settings.BlockPrivateWindows = r.Checked; Save(); };

        var wordsHead = Header("Words in window titles", out var addBanks, "Add bank sites");
        addBanks.Click += (_, _) => { AddSites(BlockCatalog.BankingDomains); AddWords(BlockCatalog.BankingSites); };
        var hint = Label("A browser's window title is the page title, so \"PayPal\" hides PayPal tabs. Whole words only.", 12.5f, Ui.Faint, (int)(24 * k));
        var entry = new Panel { Dock = DockStyle.Top, Height = (int)(44 * k), BackColor = Ui.Bg, Padding = new Padding(0, (int)(4 * k), 0, (int)(4 * k)) };
        var boxBack = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding((int)(12 * k), (int)(8 * k), (int)(12 * k), 0) };
        wordBox = new TextBox { BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text, Dock = DockStyle.Fill, Font = Ui.F(14 * k), PlaceholderText = "Add a word or site name, then press Enter" };
        wordBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddWords(new[] { wordBox.Text }); wordBox.Clear(); } };
        boxBack.Controls.Add(wordBox);
        var addWord = new PillButton("Add") { Dock = DockStyle.Right, Width = (int)(80 * k), Font = Ui.F(13.5f * k, true) };
        addWord.Click += (_, _) => { AddWords(new[] { wordBox.Text }); wordBox.Clear(); };
        entry.Controls.Add(boxBack);
        entry.Controls.Add(new Panel { Dock = DockStyle.Right, Width = (int)(8 * k), BackColor = Ui.Bg });
        entry.Controls.Add(addWord);
        words = new RowList(k) { Dock = DockStyle.Fill };

        // Three parts: apps, websites, then private windows and title words.
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Ui.Bg };
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 28));
        split.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        var top = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Margin = Padding.Empty };
        top.Controls.Add(apps);
        top.Controls.Add(appsHead);
        var middle = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Margin = Padding.Empty };
        middle.Controls.Add(sites);
        middle.Controls.Add(sitesHint);
        middle.Controls.Add(sitesHead);
        middle.Controls.Add(Spacer(10));
        var bottom = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Margin = Padding.Empty };
        bottom.Controls.Add(words);
        bottom.Controls.Add(entry);
        bottom.Controls.Add(hint);
        bottom.Controls.Add(wordsHead);
        bottom.Controls.Add(Spacer(10));
        bottom.Controls.Add(privateRow);
        bottom.Controls.Add(Spacer(12));
        split.Controls.Add(top, 0, 0);
        split.Controls.Add(middle, 0, 1);
        split.Controls.Add(bottom, 0, 2);
        Controls.Add(split);
        Controls.Add(Spacer(8));
        Controls.Add(intro);
        RefreshLists();
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    void RefreshLists()
    {
        apps.SetRows(settings.BlockedApps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a =>
        {
            var row = new CheckRow(a.Name, a.Id.Length > 0 ? a.Id + ".exe" : null, true, k) { Removable = true, Tag2 = a };
            row.Removed += r => { settings.BlockedApps.Remove((BlockedApp)r.Tag2!); Save(); RefreshLists(); };
            return (Control)row;
        }));
        sites.SetRows(settings.BlockedSites.OrderBy(SiteRules.SiteOf, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).Select(r =>
        {
            var row = new CheckRow(r, r.Contains('/') || r.Contains('?') ? "This link and the pages under it" : "The whole site", true, k) { Removable = true, Tag2 = r, Height = (int)(52 * k) };
            row.Removed += x => { settings.BlockedSites.RemoveAll(y => y.Equals((string)x.Tag2!, StringComparison.OrdinalIgnoreCase)); Save(); RefreshLists(); };
            return (Control)row;
        }));
        if (settings.BlockedSites.Count == 0)
            sites.SetRows(new[] { new Label { Text = "No blocked sites. Click Add sites… to pick from the sites you visit.", ForeColor = Ui.Faint, BackColor = Ui.Bg, Height = (int)(40 * k), TextAlign = ContentAlignment.MiddleLeft } });
        words.SetRows(settings.BlockedKeywords.OrderBy(w => w, StringComparer.CurrentCultureIgnoreCase).Select(w =>
        {
            var row = new CheckRow(w, null, true, k) { Removable = true, Tag2 = w, Height = (int)(40 * k) };
            row.Removed += r => { settings.BlockedKeywords.RemoveAll(x => x.Equals((string)r.Tag2!, StringComparison.OrdinalIgnoreCase)); Save(); RefreshLists(); };
            return (Control)row;
        }));
    }

    void AddSites(IEnumerable<string> add)
    {
        foreach (var r in add.Select(SiteRules.Normalize).OfType<string>())
            if (!settings.BlockedSites.Contains(r, StringComparer.OrdinalIgnoreCase)) settings.BlockedSites.Add(r);
    }

    void AddWords(IEnumerable<string> add)
    {
        foreach (var w in add.Select(w => w.Trim()).Where(w => w.Length > 0))
            if (!settings.BlockedKeywords.Contains(w, StringComparer.OrdinalIgnoreCase)) settings.BlockedKeywords.Add(w);
        Save();
        RefreshLists();
    }

    /// A list of the apps on this PC to tick, in a small dialog.
    void AddApps()
    {
        using var dlg = new Form
        {
            Text = "Add Apps to Block", Icon = Program.AppIcon, BackColor = Ui.Bg, ForeColor = Ui.Text, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size((int)(480 * k), (int)(560 * k)), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            Padding = new Padding((int)(18 * k)), ShowInTaskbar = false,
        };
        dlg.HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(dlg.Handle, 20, ref on, 4); };
        var picker = new AppPicker(k, Array.Empty<string>(), settings.BlockedApps.Select(a => a.Id)) { Dock = DockStyle.Fill };
        var bar = new Panel { Dock = DockStyle.Bottom, Height = (int)(56 * k), BackColor = Ui.Bg };
        var ok = new PillButton("Block Selected", primary: true) { Font = Ui.F(13.5f * k, true) };
        var cancel = new PillButton("Cancel") { Font = Ui.F(13.5f * k, true) };
        bar.Resize += (_, _) =>
        {
            ok.SetBounds(bar.Width - (int)(150 * k), (int)(14 * k), (int)(150 * k), (int)(38 * k));
            cancel.SetBounds(ok.Left - (int)(110 * k), (int)(14 * k), (int)(100 * k), (int)(38 * k));
        };
        ok.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        bar.Controls.Add(ok);
        bar.Controls.Add(cancel);
        dlg.Controls.Add(picker);
        dlg.Controls.Add(bar);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        foreach (var a in picker.Chosen)
            if (!settings.BlockedApps.Any(b => b.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase))) settings.BlockedApps.Add(new BlockedApp(a.Id, a.Name));
        Save();
        RefreshLists();
    }

    void Save()
    {
        settings.Save();
        Blocklist.Current = settings.ToBlocklist();
    }

    Panel Header(string title, out PillButton button, string buttonText)
    {
        var p = new Panel { Dock = DockStyle.Top, Height = (int)(44 * k), BackColor = Ui.Bg };
        var l = new Label { Text = title, Font = Ui.F(17 * k, true), ForeColor = Ui.Text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        button = new PillButton(buttonText) { Dock = DockStyle.Right, Width = (int)(140 * k), Font = Ui.F(13 * k, true) };
        var pad = new Panel { Dock = DockStyle.Right, Width = 1, BackColor = Ui.Bg };
        var wrap = new Panel { Dock = DockStyle.Right, Width = (int)(140 * k), BackColor = Ui.Bg, Padding = new Padding(0, (int)(5 * k), 0, (int)(5 * k)) };
        wrap.Controls.Add(button);
        button.Dock = DockStyle.Fill;
        p.Controls.Add(l);
        p.Controls.Add(pad);
        p.Controls.Add(wrap);
        return p;
    }

    Label Label(string text, float px, Color color, int height) =>
        new() { Text = text, Font = Ui.F(px * k), ForeColor = color, BackColor = Ui.Bg, Dock = DockStyle.Top, Height = height, AutoSize = false };

    Panel Spacer(int px) => new() { Dock = DockStyle.Top, Height = (int)(px * k), BackColor = Ui.Bg };

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
