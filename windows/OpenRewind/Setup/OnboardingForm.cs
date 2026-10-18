using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenRewind;

/// First-run setup: welcome, permissions, what to block, and a short animated demo.
/// Closing it early keeps whatever was picked so far.
sealed class OnboardingForm : Form
{
    readonly Settings settings;
    /// Called once at the end; true when the user wants the guided tour in the real timeline.
    readonly Action<bool> done;
    readonly float k;
    readonly Panel body;
    readonly PillButton back, next, skipTour;
    readonly List<Func<Control>> pages;
    int page;
    bool finished;
    readonly System.Windows.Forms.Timer poll = new() { Interval = 1500 };

    // Choices made on the pages.
    bool launchAtLogin = true;
    CheckRow? pwRow, moneyRow, privateRow, bankRow;
    AppPicker? picker;

    public const string Hotkey = "Ctrl+Alt+T";

    public OnboardingForm(Settings settings, Action<bool> done)
    {
        this.settings = settings;
        this.done = done;
        k = DeviceDpi / 96f;
        Text = "Welcome to OpenRewind";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        ClientSize = new Size((int)(880 * k), (int)(640 * k));
        Font = Ui.F(14 * k);

        body = new Panel { Bounds = new Rectangle((int)(40 * k), (int)(32 * k), ClientSize.Width - (int)(80 * k), ClientSize.Height - (int)(120 * k)), BackColor = Ui.Bg };
        Controls.Add(body);

        int by = ClientSize.Height - (int)(66 * k), bh = (int)(42 * k);
        back = new PillButton("Back") { Bounds = new Rectangle((int)(40 * k), by, (int)(110 * k), bh), Font = Ui.F(14 * k, true) };
        next = new PillButton("Get started", primary: true) { Bounds = new Rectangle(ClientSize.Width - (int)(220 * k), by, (int)(180 * k), bh), Font = Ui.F(14 * k, true) };
        skipTour = new PillButton("Finish") { Bounds = new Rectangle(ClientSize.Width - (int)(360 * k), by, (int)(128 * k), bh), Font = Ui.F(14 * k, true), Visible = false };
        back.Click += (_, _) => Show(page - 1);
        next.Click += (_, _) => { if (page == pages.Count - 1) Finish(tour: true); else Show(page + 1); };
        skipTour.Click += (_, _) => Finish(tour: false);
        Controls.AddRange(new Control[] { back, next, skipTour });

        pages = new() { WelcomePage, PermissionsPage, BlockPage, TourPage };
        HandleCreated += (_, _) =>
        {
            // Dark title bar to match (Windows 10 20H1+ / 11).
            int on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, 4);
        };
        Show(0);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Step dots, centered in the footer.
        var g = e.Graphics;
        Ui.Smooth(g);
        float d = 8 * k, gap = 10 * k, total = pages.Count * d + (pages.Count - 1) * gap;
        float x = (ClientSize.Width - total) / 2, y = ClientSize.Height - 45 * k - d / 2;
        for (int i = 0; i < pages.Count; i++)
        {
            float w = i == page ? d * 3 : d;
            Ui.Fill(g, new RectangleF(x, y, w, d), d / 2, i == page ? Ui.Accent : Ui.Stroke);
            x += w + gap;
        }
    }

    void Show(int i)
    {
        if (i < 0 || i >= pages.Count) return;
        // Keep the block page's controls alive: its choices are read when setup ends.
        foreach (Control c in body.Controls.Cast<Control>().ToList()) { body.Controls.Remove(c); if (!IsKept(c)) c.Dispose(); }
        page = i;
        var content = pages[i]();
        content.Dock = DockStyle.Fill;
        body.Controls.Add(content);
        back.Visible = i > 0;
        skipTour.Visible = i == pages.Count - 1;
        next.Text = i switch { 0 => "Get started", 3 => "Try it now", _ => "Continue" };
        poll.Enabled = i == 1;
        Invalidate();
    }

    Panel? blockPage;
    bool IsKept(Control c) => c == blockPage;

    // MARK: pages

    Control WelcomePage()
    {
        var p = new Panel { BackColor = Ui.Bg };
        var logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Size = new Size((int)(96 * k), (int)(96 * k)) };
        try { logo.Image = new Icon(Program.AppIcon, 256, 256).ToBitmap(); } catch { logo.Image = Program.AppIcon.ToBitmap(); }
        p.Controls.Add(logo);
        var title = Heading("Welcome to OpenRewind", 32);
        var sub = Paragraph("Rewind anything you've seen or heard on this PC, and search it in a second. Everything stays on your computer.");
        title.TextAlign = sub.TextAlign = ContentAlignment.TopCenter;
        p.Controls.Add(title);
        p.Controls.Add(sub);
        var cards = new (string Icon, string Title, string Text)[]
        {
            ("⏪", "Rewind", $"Press {Hotkey} to scroll back through your day."),
            ("\U0001F50D", "Search", "Find any word you saw on screen or heard in a meeting."),
            ("\U0001F512", "Private", "Nothing is uploaded. You choose what it never sees."),
        };
        var cardControls = cards.Select(c => FeatureCard(c.Icon, c.Title, c.Text)).ToList();
        cardControls.ForEach(c => p.Controls.Add(c));
        p.Resize += (_, _) =>
        {
            logo.Location = new Point((p.Width - logo.Width) / 2, (int)(24 * k));
            title.SetBounds(0, logo.Bottom + (int)(18 * k), p.Width, (int)(48 * k));
            sub.SetBounds((int)(60 * k), title.Bottom + (int)(6 * k), p.Width - (int)(120 * k), (int)(54 * k));
            int gap = (int)(16 * k), w = (p.Width - 2 * gap) / 3, y = sub.Bottom + (int)(30 * k);
            for (int i = 0; i < cardControls.Count; i++) cardControls[i].SetBounds(i * (w + gap), y, w, (int)(150 * k));
        };
        return p;
    }

    Control FeatureCard(string icon, string title, string text)
    {
        var c = new Panel { BackColor = Ui.Bg };
        c.Paint += (_, e) =>
        {
            var g = e.Graphics;
            Ui.Smooth(g);
            Ui.Fill(g, new RectangleF(0.5f, 0.5f, c.Width - 1, c.Height - 1), 14 * k, Ui.Card, Ui.Stroke);
            TextRenderer.DrawText(g, icon, Ui.F(24 * k), new Point((int)(18 * k), (int)(16 * k)), Ui.Text);
            TextRenderer.DrawText(g, title, Ui.F(16 * k, true), new Point((int)(18 * k), (int)(60 * k)), Ui.Text);
            TextRenderer.DrawText(g, text, Ui.F(13 * k), new Rectangle((int)(18 * k), (int)(86 * k), c.Width - (int)(34 * k), c.Height - (int)(90 * k)), Ui.Dim, TextFormatFlags.WordBreak);
        };
        return c;
    }

    Control PermissionsPage()
    {
        var p = new Panel { BackColor = Ui.Bg };
        var title = Heading("Quick setup", 28);
        var sub = Paragraph("Windows doesn't ask for permission to record the screen, so there's only one thing to check.");
        p.Controls.Add(title);
        p.Controls.Add(sub);

        var screen = new StatusRow(k, "\U0001F5A5", "Screen recording", "Ready. Nothing to allow on Windows.") { Ok = true };
        var mic = new StatusRow(k, "\U0001F399", "Microphone", "Turns your meetings and calls into text you can search.") { Action = "Open settings" };
        mic.ActionClicked += () => OpenUri("ms-settings:privacy-microphone");
        void RefreshMic()
        {
            mic.Ok = MicrophoneAllowed();
            mic.Status = mic.Ok ? "Allowed" : "Turned off in Windows";
            mic.Invalidate();
        }
        RefreshMic();
        poll.Tick += (_, _) => { if (!mic.IsDisposed) RefreshMic(); };
        var login = new CheckRow("Start OpenRewind when Windows starts", "So your timeline has no gaps after a restart.", launchAtLogin, k);
        login.CheckedChanged += r => launchAtLogin = r.Checked;
        var rows = new Control[] { screen, mic, login };
        p.Controls.AddRange(rows);
        p.Resize += (_, _) =>
        {
            title.SetBounds(0, (int)(10 * k), p.Width, (int)(44 * k));
            sub.SetBounds(0, title.Bottom, p.Width, (int)(30 * k));
            int y = sub.Bottom + (int)(26 * k);
            foreach (var r in rows) { r.SetBounds(0, y, p.Width, r is StatusRow ? (int)(76 * k) : (int)(64 * k)); y = r.Bottom + (int)(10 * k); }
        };
        return p;
    }

    Control BlockPage()
    {
        if (blockPage != null) return blockPage;
        var p = new Panel { BackColor = Ui.Bg };
        var title = Heading("What should OpenRewind never see?", 28);
        var sub = Paragraph("Anything you pick is blurred in every screenshot and its text is never saved. You can change this anytime in Settings → Blocked Apps.");
        p.Controls.Add(title);
        p.Controls.Add(sub);

        bool first = !settings.OnboardingDone;
        var blocked = new HashSet<string>(settings.BlockedApps.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        bool Has(BlockCategory c) => BlockCatalog.Windows.Where(a => a.Category == c).All(a => blocked.Contains(a.Id));
        string Names(BlockCategory c) => string.Join(", ", BlockCatalog.Windows.Where(a => a.Category == c).Take(4).Select(a => a.Name)) + " and more";

        pwRow = new CheckRow("\U0001F511  Password managers", Names(BlockCategory.PasswordManager), first || Has(BlockCategory.PasswordManager), k);
        moneyRow = new CheckRow("\U0001F4B0  Money and crypto apps", "Quicken, Ledger Live, Exodus, Trezor Suite", first || Has(BlockCategory.Finance), k);
        privateRow = new CheckRow("\U0001F576  Private browsing windows", "Incognito, InPrivate and private windows", settings.BlockPrivateWindows, k);
        bankRow = new CheckRow("\U0001F3E6  Bank and payment websites", "Chase, PayPal, Bank of America, Coinbase and more", first || BlockCatalog.BankingSites.All(s => settings.BlockedKeywords.Contains(s, StringComparer.OrdinalIgnoreCase)), k);
        var groups = new[] { pwRow, moneyRow, privateRow, bankRow };
        p.Controls.AddRange(groups);

        var otherLabel = new Label { Text = "Other apps on this PC", Font = Ui.F(13 * k, true), ForeColor = Ui.Dim, BackColor = Ui.Bg };
        p.Controls.Add(otherLabel);
        var catalogIds = BlockCatalog.Windows.Select(a => a.Id);
        picker = new AppPicker(k, settings.BlockedApps.Select(a => a.Id).Except(catalogIds, StringComparer.OrdinalIgnoreCase), catalogIds);
        p.Controls.Add(picker);

        p.Resize += (_, _) =>
        {
            title.SetBounds(0, (int)(10 * k), p.Width, (int)(44 * k));
            sub.SetBounds(0, title.Bottom, p.Width, (int)(48 * k));
            int top = sub.Bottom + (int)(16 * k), colW = (p.Width - (int)(20 * k)) / 2;
            int y = top;
            foreach (var g in groups) { g.SetBounds(0, y, colW, (int)(62 * k)); y = g.Bottom + (int)(6 * k); }
            int x2 = colW + (int)(20 * k);
            otherLabel.SetBounds(x2, top, colW, (int)(24 * k));
            picker.SetBounds(x2, otherLabel.Bottom + (int)(4 * k), colW, p.Height - otherLabel.Bottom - (int)(8 * k));
        };
        blockPage = p;
        return p;
    }

    Control TourPage()
    {
        var p = new Panel { BackColor = Ui.Bg };
        var title = Heading("Here's how it works", 28);
        var sub = Paragraph($"Press {Hotkey} anytime. Scroll to go back, type to search. Click Try it now for a quick guided tour of the real thing.");
        var demo = new DemoView(k, Hotkey);
        p.Controls.AddRange(new Control[] { title, sub, demo });
        p.Resize += (_, _) =>
        {
            title.SetBounds(0, (int)(10 * k), p.Width, (int)(44 * k));
            sub.SetBounds(0, title.Bottom, p.Width, (int)(48 * k));
            demo.SetBounds(0, sub.Bottom + (int)(12 * k), p.Width, p.Height - sub.Bottom - (int)(12 * k));
        };
        return p;
    }

    Label Heading(string text, float px) =>
        new() { Text = text, Font = Ui.F(px * k, true), ForeColor = Ui.Text, BackColor = Ui.Bg, AutoSize = false };

    Label Paragraph(string text) =>
        new() { Text = text, Font = Ui.F(15 * k), ForeColor = Ui.Dim, BackColor = Ui.Bg, AutoSize = false };

    // MARK: finish

    void Finish(bool tour)
    {
        if (finished) return;
        finished = true;
        Apply();
        Close();
        done(tour);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!finished) { finished = true; Apply(); done(false); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { poll.Dispose(); blockPage?.Dispose(); }
        base.Dispose(disposing);
    }

    /// Save the choices. Pages never visited keep their defaults.
    void Apply()
    {
        BlockPage();  // builds the defaults if the user never got that far
        var apps = new List<BlockedApp>();
        foreach (var a in BlockCatalog.Windows)
        {
            bool on = a.Category == BlockCategory.PasswordManager ? pwRow!.Checked : moneyRow!.Checked;
            if (on) apps.Add(new BlockedApp(a.Id, a.Name));
        }
        // Apps picked from the list, plus earlier picks the list did not show (not installed any more).
        var unticked = new HashSet<string>(picker!.Unchosen.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        var catalog = new HashSet<string>(BlockCatalog.Windows.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        apps.AddRange(settings.BlockedApps.Where(a => !catalog.Contains(a.Id) && !unticked.Contains(a.Id)));
        apps.AddRange(picker.Chosen.Select(a => new BlockedApp(a.Id, a.Name)));
        settings.BlockedApps = apps.DistinctBy(a => a.Id.ToLowerInvariant()).ToList();

        var words = settings.BlockedKeywords.Except(BlockCatalog.BankingSites, StringComparer.OrdinalIgnoreCase).ToList();
        if (bankRow!.Checked) words.AddRange(BlockCatalog.BankingSites);
        settings.BlockedKeywords = words;
        settings.BlockPrivateWindows = privateRow!.Checked;
        settings.OnboardingDone = true;
        settings.Save();
        Blocklist.Current = settings.ToBlocklist();
        try { if (Settings.LaunchAtLogin != launchAtLogin) Settings.LaunchAtLogin = launchAtLogin; }
        catch (Exception e) { Log.Write($"launch at login failed: {e.Message}"); }
        Log.Write($"setup done: {settings.BlockedApps.Count} blocked apps, {settings.BlockedKeywords.Count} blocked words, private windows {(settings.BlockPrivateWindows ? "hidden" : "recorded")}");
    }

    /// Windows' privacy switches for the microphone (all apps, and desktop apps like this one).
    static bool MicrophoneAllowed()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        static bool Denied(RegistryKey hive, string path)
        {
            try { using var k = hive.OpenSubKey(path); return string.Equals(k?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
        return !Denied(Registry.LocalMachine, key) && !Denied(Registry.CurrentUser, key) && !Denied(Registry.CurrentUser, key + @"\NonPackaged");
    }

    static void OpenUri(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"cannot open {uri}: {e.Message}"); }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// A row with an icon, a title, a line of help, and on the right either a green check or a button.
sealed class StatusRow : Control
{
    readonly float k;
    readonly string icon, title, detail;
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public string? Action { get; set; }
    public event System.Action? ActionClicked;
    RectangleF actionRect;

    public StatusRow(float k, string icon, string title, string detail)
    {
        this.k = k;
        this.icon = icon;
        this.title = title;
        this.detail = detail;
        DoubleBuffered = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Ui.Bg;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = !Ok && Action != null && actionRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!Ok && Action != null && actionRect.Contains(e.Location)) ActionClicked?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ui.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Ui.Fill(g, r, 12 * k, Ui.Card, Ui.Stroke);
        TextRenderer.DrawText(g, icon, Ui.F(22 * k), new Rectangle((int)(14 * k), 0, (int)(40 * k), Height), Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        float x = 66 * k, right = 200 * k;
        TextRenderer.DrawText(g, title, Ui.F(15 * k, true), new Rectangle((int)x, (int)(14 * k), (int)(Width - x - right), (int)(24 * k)), Ui.Text, TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, detail, Ui.F(13 * k), new Rectangle((int)x, (int)(40 * k), (int)(Width - x - right), (int)(22 * k)), Ui.Dim, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        float cx = Width - 20 * k;
        if (Ok)
        {
            var label = Status ?? "Ready";
            var size = TextRenderer.MeasureText(label, Ui.F(13.5f * k, true));
            float s = 20 * k;
            TextRenderer.DrawText(g, label, Ui.F(13.5f * k, true), new Point((int)(cx - size.Width), (int)((Height - size.Height) / 2)), Ui.Good);
            using var b = new SolidBrush(Ui.Good);
            var cr = new RectangleF(cx - size.Width - s - 8 * k, (Height - s) / 2, s, s);
            g.FillEllipse(b, cr);
            using var pen = new Pen(Color.White, 2 * k) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLines(pen, new[] { new PointF(cr.X + s * 0.28f, cr.Y + s * 0.52f), new PointF(cr.X + s * 0.44f, cr.Y + s * 0.68f), new PointF(cr.X + s * 0.74f, cr.Y + s * 0.34f) });
        }
        else if (Action != null)
        {
            var size = TextRenderer.MeasureText(Action, Ui.F(13.5f * k, true));
            actionRect = new RectangleF(cx - size.Width - 28 * k, (Height - 34 * k) / 2, size.Width + 28 * k, 34 * k);
            Ui.Fill(g, actionRect, 17 * k, Ui.Accent);
            TextRenderer.DrawText(g, Action, Ui.F(13.5f * k, true), Rectangle.Round(actionRect), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Status != null)
            {
                var ss = TextRenderer.MeasureText(Status, Ui.F(12.5f * k));
                TextRenderer.DrawText(g, Status, Ui.F(12.5f * k), new Point((int)(actionRect.X - ss.Width - 10 * k), (int)((Height - ss.Height) / 2)), Ui.Warn);
            }
        }
    }
}
