using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// The Journal window: one page per day. The top is an AI recap (when an AI provider is set up and
/// the user asks for it, or turned on "Write Each Day's Recap Automatically"); below it, stats
/// worked out on this PC from the database. Same page as JournalView on the Mac.
sealed class JournalForm : Form
{
    static JournalForm? open;
    readonly Settings settings;
    readonly Database db;
    readonly AiTools tools;
    readonly Action<DateTime> openMoment;
    readonly Action openSettings;
    readonly float k;
    readonly DateTimePicker picker;
    readonly PillButton prev, next, today, copy, save;
    readonly FlowLayoutPanel page;
    DateTime day = DateTime.Today;
    JournalStats? stats;
    JournalRecap? recap;
    /// Bumped on every load, so a slow load for a day the user already left is dropped.
    int loadId;
    /// Why the stats could not be worked out (a busy database), if they could not.
    string? statsError;
    string? error;
    List<AiChatItem> lookups = new();

    public static void ShowFor(Settings settings, Database db, AiTools tools, Action<DateTime> openMoment, Action openSettings)
    {
        if (open is { IsDisposed: false }) { open.ShowDay(open.day); open.Activate(); return; }
        open = new JournalForm(settings, db, tools, openMoment, openSettings);
        open.Show();
        open.Activate();
    }

    /// After a recap was written in the background or AI Settings changed.
    public static void Reload() { if (open is { IsDisposed: false } f) f.ShowDay(f.day); }

    JournalForm(Settings settings, Database db, AiTools tools, Action<DateTime> openMoment, Action openSettings)
    {
        this.settings = settings;
        this.db = db;
        this.tools = tools;
        this.openMoment = openMoment;
        this.openSettings = openSettings;
        k = DeviceDpi / 96f;
        Text = "Journal";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size((int)(480 * k), (int)(480 * k));
        ClientSize = new Size((int)(620 * k), (int)(780 * k));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        // Day picker on the left, Markdown buttons on the right.
        var bar = new Panel { Dock = DockStyle.Top, Height = (int)(60 * k), BackColor = Ui.Bg };
        PillButton Button(string text, int width) =>
            new(text) { Font = Ui.F(12.5f * k, true), Size = new Size((int)(width * k), (int)(32 * k)) };
        prev = Button("‹", 36);
        next = Button("›", 36);
        today = Button("Today", 70);
        copy = Button("Copy as Markdown", 150);
        save = Button("Save as Markdown…", 156);
        picker = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "ddd d MMM yyyy", Width = (int)(150 * k), Font = Ui.F(13 * k), MaxDate = DateTime.Today.AddDays(1) };
        prev.Click += (_, _) => ShowDay(day.AddDays(-1));
        next.Click += (_, _) => ShowDay(day.AddDays(1));
        today.Click += (_, _) => ShowDay(DateTime.Today);
        picker.ValueChanged += (_, _) => { if (picker.Value.Date != day) ShowDay(picker.Value.Date); };
        copy.Click += (_, _) => { if (Markdown() is string md) Flash(copy, CopyText(md) ? "Copied" : "Could not copy"); };
        save.Click += (_, _) => SaveMarkdown();
        bar.Layout += (_, _) =>
        {
            int x = (int)(16 * k), y = (int)(14 * k);
            prev.Location = new Point(x, y);
            picker.Location = new Point(prev.Right + (int)(6 * k), y + (prev.Height - picker.Height) / 2);
            next.Location = new Point(picker.Right + (int)(6 * k), y);
            today.Location = new Point(next.Right + (int)(6 * k), y);
            save.Location = new Point(bar.Width - (int)(16 * k) - save.Width, y);
            copy.Location = new Point(save.Left - (int)(8 * k) - copy.Width, y);
            // In a narrow window Copy would sit on the day picker; Save stays.
            copy.Visible = copy.Left > today.Right + (int)(8 * k);
        };
        bar.Controls.AddRange(new Control[] { prev, picker, next, today, copy, save });

        page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            BackColor = Ui.Bg, Padding = new Padding((int)(20 * k), (int)(4 * k), (int)(20 * k), (int)(20 * k)),
        };
        page.Resize += (_, _) => Fit();
        Controls.Add(page);
        Controls.Add(bar);
        JournalJobs.Changed += OnJob;
        ShowDay(DateTime.Today);
    }

    /// A recap closing the window does not stop (it is being paid for) keeps writing in JournalJobs.
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        JournalJobs.Changed -= OnJob;
        open = null;
        base.OnFormClosed(e);
    }

    void OnJob(string key, JournalWriter.Result? r)
    {
        if (IsDisposed) return;
        if (r != null && key == Journal.Key(day)) { error = r.Error; lookups = r.Lookups; }
        recap = db.Recap(Journal.Key(day));
        Render();
    }

    /// The clipboard can be held by another app for a moment.
    static bool CopyText(string s)
    {
        try { Clipboard.SetText(s); return true; }
        catch (ExternalException e) { Log.Write($"copy failed: {e.Message}"); return false; }
    }

    /// The recap as it may be shown and exported: not one written before the block list grew.
    JournalRecap? ShownRecap => recap != null && recap.CurrentFor(tools.Blocked ?? Blocklist.Current) ? recap : null;

    /// Shows a day: its saved recap right away, its stats once they are worked out.
    void ShowDay(DateTime d)
    {
        d = d.Date > DateTime.Today ? DateTime.Today : d.Date;
        if (d != day) { error = null; lookups = new(); }
        day = d;
        if (picker.Value.Date != d) picker.Value = d;
        next.Enabled = d < DateTime.Today;
        today.Enabled = d != DateTime.Today;
        recap = db.Recap(Journal.Key(d));
        stats = null;
        statsError = null;
        Render();
        var id = ++loadId;
        Task.Run(() => db.JournalStats(d, tools)).ContinueWith(t =>
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (id != loadId || IsDisposed) return;
                    if (t.IsCompletedSuccessfully) stats = t.Result;
                    else
                    {
                        statsError = t.Exception?.GetBaseException().Message ?? "it was stopped";
                        Log.Write($"journal stats for {Journal.Key(d)} failed: {statsError}");
                    }
                    Render();
                });
            }
            catch (InvalidOperationException) { }  // the window closed meanwhile
        });
    }

    string? Markdown() => stats == null ? null : Journal.Markdown(stats, ShownRecap);

    void SaveMarkdown()
    {
        if (Markdown() is not string md) return;
        using var dlg = new SaveFileDialog
        {
            Title = "Save as Markdown", Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*", FileName = $"Journal {Journal.Key(day)}.md",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dlg.FileName, md); }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Could not save the journal", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    static void Flash(PillButton b, string text)
    {
        var old = b.Text;
        b.Text = text;
        var t = new System.Windows.Forms.Timer { Interval = 1200 };
        t.Tick += (_, _) => { t.Dispose(); if (!b.IsDisposed) b.Text = old; };
        t.Start();
    }

    // MARK: AI recap

    /// Write Recap and Rewrite. The page updates through OnJob.
    async void WriteRecap()
    {
        if (JournalJobs.IsWriting(Journal.Key(day)) || !settings.Ai().IsReady) return;
        error = null;
        lookups = new();
        try { await JournalJobs.Write(day, settings, db, tools, auto: false); }
        catch (Exception e) { Log.Write($"journal recap failed: {e.Message}"); }
    }

    // MARK: drawing

    void Render()
    {
        page.SuspendLayout();
        var scroll = page.VerticalScroll.Value;
        foreach (Control c in page.Controls.Cast<Control>().ToList()) { page.Controls.Remove(c); c.Dispose(); }
        Add(Words(Journal.DayTitle(day), 20, Ui.Text, bold: true, top: 8));
        if (day == DateTime.Today) Add(Words("Today so far.", 12, Ui.Faint));
        RecapRows();
        if (statsError != null) Add(Words($"⚠ Could not add up the day: {statsError}. Try again with ‹ and ›.", 13, Ui.Warn, top: 18));
        else if (stats == null) Add(Words("Adding up the day…", 13, Ui.Faint, top: 18));
        else
            foreach (var sec in Journal.Sections(stats))
            {
                Add(Words(sec.Title, 15, Ui.Text, bold: true, top: 18));
                foreach (var line in sec.Lines) Add(Line(line));
            }
        Add(Words("Worked out on this PC without AI. Blocked apps, sites and windows are left out.", 11.5f, Ui.Faint, top: 22));
        copy.Enabled = save.Enabled = stats != null;
        page.ResumeLayout();
        Fit();
        page.AutoScrollPosition = new Point(0, scroll);
    }

    void RecapRows()
    {
        var ai = settings.Ai();
        var key = Journal.Key(day);
        Add(Words("Recap", 15, Ui.Text, bold: true, top: 16));
        if (JournalJobs.WritingOtherThan(key) is string other && Journal.FromKey(other) is DateTime od)
            Add(Words($"Writing the recap for {Journal.DayTitle(od)}…", 12.5f, Ui.Faint));
        if (JournalJobs.IsWriting(key))
        {
            Add(Words($"Asking {ai.Kind.Title()} to write the recap…", 13.5f, Ui.Dim));
            Add(Buttons(("Stop", false, () => JournalJobs.Stop(key))));
        }
        else if (recap != null && ShownRecap == null)
        {
            Add(Words("Your block list changed since this recap was written, so it is hidden: it may mention something you blocked. "
                     + "Rewrite it to leave that out.", 12.5f, Ui.Dim));
            if (ai.IsReady) Add(Buttons(("Rewrite", false, WriteRecap)));
        }
        else if (recap != null)
        {
            Add(RecapText(recap.Text));
            var cost = recap.Cost is double c ? $" · {AiPrice.Money(c)}" : "";
            Add(Words($"Written {recap.Created.ToLocalTime():g}{(recap.Partial ? " (before the day was over)" : "")} by {AiProviders.FromId(recap.Provider).Title()} · {recap.Model} · "
                     + $"{recap.Usage.Input:N0} tokens in, {recap.Usage.Output:N0} out{cost}", 11.5f, Ui.Faint));
            if (ai.IsReady) Add(Buttons(("Rewrite", false, WriteRecap)));
        }
        else if (ai.IsReady)
        {
            Add(Words(Journal.RecapConsent(ai.Kind.Title()), 12.5f, Ui.Dim));
            Add(Buttons(("Write Recap", true, WriteRecap)));
        }
        else
        {
            Add(Words("Set up an AI provider to get a written recap of each day. The rest of this page is worked out on this PC without AI.", 12.5f, Ui.Dim));
            Add(Buttons(("AI Settings…", false, openSettings)));
        }
        if (error != null) Add(Words("⚠ " + error, 12.5f, Ui.Warn));
        if (lookups.Count > 0) Add(SentRow());
        var auto = new CheckBox
        {
            Text = $"Write each day's recap automatically with {ai.Kind.Title()}", Checked = settings.JournalAutoRecapOn(ai), Font = Ui.F(12.5f * k, true), ForeColor = Ui.Text,
            AutoSize = true, Margin = new Padding(0, (int)(12 * k), 0, 0),
        };
        auto.CheckedChanged += (_, _) => { settings.SetJournalAutoRecap(auto.Checked); BeginInvoke(Render); };
        Add(auto);
        Add(Words(Journal.AutoRecapInfo(ai.Kind.Title()), 11.5f, Ui.Faint));
        if (settings.JournalAutoRecap && !settings.JournalAutoRecapOn(ai))
            Add(Words($"Paused: it was turned on for another AI provider. Turn it on again to use {ai.Kind.Title()}.", 11.5f, Ui.Warn));
    }

    /// The recap as the model wrote it; only timeline links open.
    Control RecapText(string md)
    {
        var (text, links) = AiMarkdown.Plain(md, url => AiTime.FromLink(url) != null);
        var l = new LinkLabel
        {
            Text = text, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Font = Ui.F(13.5f * k),
            LinkColor = Color.FromArgb(115, 165, 255), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(115, 165, 255),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Padding = new Padding((int)(12 * k), (int)(9 * k), (int)(12 * k), (int)(9 * k)), Margin = new Padding(0, (int)(6 * k), 0, (int)(4 * k)),
        };
        l.Links.Clear();
        foreach (var link in links) l.Links.Add(link.Start, link.Length, link.Url);
        l.LinkClicked += (_, e) => { if (e.Link?.LinkData is string u && AiTime.FromLink(u) is DateTime at) openMoment(at); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy", null, (_, _) =>
        {
            if (!CopyText(text)) MessageBox.Show(this, "Another app is using the clipboard. Try again.", "Could not copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
        l.ContextMenuStrip = menu;
        return l;
    }

    /// "What was sent: 3 lookups, 12,345 characters"; click to see each lookup and its answer.
    Control SentRow()
    {
        var panel = new Panel { BackColor = Ui.Bg, Margin = new Padding(0, (int)(6 * k), 0, 0), Tag = "fill" };
        var chars = lookups.Sum(l => l.Detail.Length);
        var head = new Label
        {
            Text = $"🔎 What was sent: {lookups.Count} lookups, {chars:N0} characters  ▸", ForeColor = Ui.Faint, Font = Ui.F(12 * k),
            Dock = DockStyle.Top, Height = (int)(22 * k), Cursor = Cursors.Hand,
        };
        var box = new TextBox
        {
            Text = string.Join("\n\n", lookups.Select(l => $"{l.Text}\nThe lookup:\n{l.Args}\n\nSent to the AI:\n{l.Detail}")).Replace("\n", "\r\n"),
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Ui.Card, ForeColor = Ui.Dim, Font = new Font("Consolas", 10 * k, GraphicsUnit.Pixel), Dock = DockStyle.Fill, Visible = false,
        };
        head.Click += (_, _) =>
        {
            box.Visible = !box.Visible;
            head.Text = head.Text[..^1] + (box.Visible ? "▾" : "▸");
            panel.Height = head.Height + (box.Visible ? (int)(260 * k) : 0);
        };
        panel.Controls.Add(box);
        panel.Controls.Add(head);
        panel.Height = head.Height;
        return panel;
    }

    Label Words(string text, float size, Color color, bool bold = false, int top = 2) => new()
    {
        Text = text, ForeColor = color, BackColor = Ui.Bg, AutoSize = true, Font = Ui.F(size * k, bold),
        Margin = new Padding(0, (int)(top * k), 0, (int)(2 * k)),
    };

    /// A stats line; one with a time opens that moment in the timeline.
    Control Line(JournalLine line)
    {
        if (line.At is not DateTime at) return Words("• " + line.Text, 13, Ui.Dim);
        var l = new LinkLabel
        {
            Text = "• " + line.Text, ForeColor = Ui.Dim, BackColor = Ui.Bg, AutoSize = true, Font = Ui.F(13 * k),
            LinkColor = Color.FromArgb(115, 165, 255), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(115, 165, 255),
            LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, (int)(2 * k), 0, (int)(2 * k)),
        };
        l.Links.Clear();
        l.Links.Add(2, line.Text.Length);
        l.LinkClicked += (_, _) => openMoment(at);
        return l;
    }

    Control Buttons(params (string Text, bool Primary, Action Act)[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, BackColor = Ui.Bg, WrapContents = false, Margin = new Padding(0, (int)(8 * k), 0, (int)(2 * k)) };
        foreach (var (text, primary, act) in buttons)
        {
            var b = new PillButton(text, primary) { Font = Ui.F(13 * k, true), Size = new Size((int)(Math.Max(110, text.Length * 9 + 36) * k), (int)(36 * k)), Margin = new Padding(0, 0, (int)(8 * k), 0) };
            // Later, since the page is rebuilt and this button disposed while its click is still running.
            b.Click += (_, _) => BeginInvoke(act);
            row.Controls.Add(b);
        }
        return row;
    }

    void Add(Control c) => page.Controls.Add(c);

    /// Text wraps to the window width.
    void Fit()
    {
        var w = page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (w <= 0) return;
        page.SuspendLayout();
        foreach (Control c in page.Controls)
        {
            if (c is Label l) l.MaximumSize = new Size(w, 0);
            else if (c.Tag as string == "fill") c.Width = w;
        }
        page.ResumeLayout();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
