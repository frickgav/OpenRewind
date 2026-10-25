using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// The Ask AI window. The chat itself is AiChatPanel, which moves between this window and the
/// timeline overlay, so both show the same conversation.
sealed class AiChatForm : Form
{
    static AiChatForm? open;
    readonly AiChatPanel panel;

    /// One window at a time; the conversation lives in `convo`, so closing the window keeps it.
    public static void ShowFor(Settings settings, AiTools tools, AiConversation convo, Action<DateTime> openMoment, Action openSettings)
    {
        if (open is { IsDisposed: false }) { open.panel.ShowState(); open.Activate(); return; }
        open = new AiChatForm(AiChatPanel.Get(settings, tools, convo, openMoment, openSettings));
        open.Show();
        open.Activate();
    }

    /// After AI Settings saved: show the new provider, or the chat instead of "add your key".
    public static void SettingsChanged() => AiChatPanel.Current?.ShowState();

    AiChatForm(AiChatPanel panel)
    {
        this.panel = panel;
        var k = DeviceDpi / 96f;
        Text = "Ask OpenRewind";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size((int)(420 * k), (int)(420 * k));
        ClientSize = new Size((int)(560 * k), (int)(720 * k));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };
        panel.MoveTo(this, onClose: null);
    }

    /// The overlay is taking the chat: close this window without the chat in it.
    public static void Release() { if (open is { IsDisposed: false }) open.Close(); }

    /// Open and on screen (App Lock counts it as the history being in use).
    public static bool IsShown => open is { IsDisposed: false, Visible: true };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The chat outlives the window.
        if (panel.Parent == this) Controls.Remove(panel);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Ask AI: questions about what you did, saw or heard, answered by the provider you picked
/// using lookups in the local database (AiTools). Same chat as AIChatView on the Mac.
/// There is one panel; it sits in the Ask AI window or beside the timeline.
sealed class AiChatPanel : Panel
{
    public static AiChatPanel? Current { get; private set; }

    readonly Settings settings;
    readonly AiTools tools;
    readonly AiConversation convo;
    readonly Action<DateTime> openMoment;
    readonly Action openSettings;
    readonly float k;
    readonly Label subtitle;
    readonly FlowLayoutPanel list;
    readonly Panel notice, inputBar;
    readonly TextBox input;
    readonly PillButton send, closeButton;
    readonly Label thinking;
    CancellationTokenSource? cts;
    Action? onClose;

    public static AiChatPanel Get(Settings settings, AiTools tools, AiConversation convo, Action<DateTime> openMoment, Action openSettings) =>
        Current is { IsDisposed: false } p ? p : Current = new AiChatPanel(settings, tools, convo, openMoment, openSettings);

    /// Puts the chat into `host`. `onClose` shows a close button (in the overlay) and runs when the
    /// chat is closed there or moved back to its window.
    public void MoveTo(Control host, Action? onClose)
    {
        if (Parent == host) return;
        var before = this.onClose;
        if (Parent is AiChatForm) AiChatForm.Release();
        Parent?.Controls.Remove(this);
        this.onClose = onClose;
        closeButton.Visible = onClose != null;
        Dock = onClose != null ? DockStyle.Right : DockStyle.Fill;
        host.Controls.Add(this);
        before?.Invoke();
        ShowState();
    }

    public void Close() => onClose?.Invoke();

    public void FocusInput() { if (input.Visible) input.Focus(); }

    AiChatPanel(Settings settings, AiTools tools, AiConversation convo, Action<DateTime> openMoment, Action openSettings)
    {
        this.settings = settings;
        this.tools = tools;
        this.convo = convo;
        this.openMoment = openMoment;
        this.openSettings = openSettings;
        k = DeviceDpi / 96f;
        Width = (int)(420 * k);
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);

        // Header: title, provider · model, New Chat, Settings, and Close in the overlay.
        var header = new Panel { Dock = DockStyle.Top, Height = (int)(64 * k), BackColor = Ui.Bg, Padding = new Padding((int)(16 * k), (int)(10 * k), (int)(16 * k), 0) };
        var title = new Label { Text = "Ask OpenRewind", Font = Ui.F(16 * k, true), AutoSize = true, Location = new Point((int)(16 * k), (int)(10 * k)) };
        subtitle = new Label { Font = Ui.F(12 * k), ForeColor = Ui.Faint, AutoSize = true, Location = new Point((int)(16 * k), (int)(36 * k)) };
        var newChat = new PillButton("New Chat") { Font = Ui.F(12.5f * k, true), Size = new Size((int)(96 * k), (int)(32 * k)) };
        var gear = new PillButton("Settings") { Font = Ui.F(12.5f * k, true), Size = new Size((int)(88 * k), (int)(32 * k)) };
        closeButton = new PillButton("✕") { Font = Ui.F(12.5f * k, true), Size = new Size((int)(32 * k), (int)(32 * k)), Visible = false };
        gear.Click += (_, _) => openSettings();
        closeButton.Click += (_, _) => Close();
        closeButton.VisibleChanged += (_, _) => header.PerformLayout();
        header.Layout += (_, _) =>
        {
            var right = header.Width - (int)(16 * k);
            if (closeButton.Visible) { closeButton.Location = new Point(right - closeButton.Width, (int)(14 * k)); right = closeButton.Left - (int)(8 * k); }
            gear.Location = new Point(right - gear.Width, (int)(14 * k));
            newChat.Location = new Point(gear.Left - newChat.Width - (int)(8 * k), (int)(14 * k));
        };
        header.Controls.AddRange(new Control[] { title, subtitle, newChat, gear, closeButton });
        list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            BackColor = Ui.Bg, Padding = new Padding((int)(16 * k), (int)(8 * k), (int)(16 * k), (int)(8 * k)),
        };
        list.Resize += (_, _) => FitRows();
        newChat.Click += (_, _) => { cts?.Cancel(); convo.Clear(); list.Controls.Clear(); ShowSuggestions(); ShowSubtitle(); };

        // Input: a text box and Send (Stop while working).
        inputBar = new Panel { Dock = DockStyle.Bottom, Height = (int)(64 * k), BackColor = Ui.Bg, Padding = new Padding((int)(12 * k)) };
        var boxBack = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Card, Padding = new Padding((int)(12 * k), (int)(10 * k), (int)(12 * k), 0) };
        input = new TextBox { BorderStyle = BorderStyle.None, BackColor = Ui.Card, ForeColor = Ui.Text, Dock = DockStyle.Fill, Font = Ui.F(14 * k), PlaceholderText = "Ask about what you did or saw…" };
        input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(input.Text); } };
        boxBack.Controls.Add(input);
        send = new PillButton("Send", primary: true) { Dock = DockStyle.Right, Width = (int)(84 * k), Font = Ui.F(13.5f * k, true) };
        send.Click += (_, _) => { if (cts != null) cts.Cancel(); else Send(input.Text); };
        inputBar.Controls.Add(boxBack);
        inputBar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = (int)(8 * k), BackColor = Ui.Bg });
        inputBar.Controls.Add(send);

        thinking = new Label { Text = "Thinking…", ForeColor = Ui.Faint, Font = Ui.F(12 * k), AutoSize = true, Margin = new Padding(0, (int)(6 * k), 0, 0) };
        notice = new Panel { Dock = DockStyle.Fill, BackColor = Ui.Bg, Padding = new Padding((int)(40 * k)) };

        Controls.Add(list);
        Controls.Add(notice);
        Controls.Add(inputBar);
        Controls.Add(header);
        ShowState();
    }

    /// Shows the chat, or what is missing before it can be used.
    public void ShowState()
    {
        var ai = settings.Ai();
        ShowSubtitle();
        if (!ai.IsReady)
            ShowNotice("Add your AI key",
                "OpenRewind can answer questions about what you did and saw, using your own Anthropic, OpenAI, Gemini or OpenRouter key, or a local model (Ollama, LM Studio).",
                "Open AI Settings…", openSettings);
        else if (!settings.AiPrivacyAccepted)
            ShowNotice("Before you start",
                $"To answer, the AI reads parts of your history: screen text, app and window names, web addresses and transcripts. Those parts are sent to {ai.Kind.Title()} using your key, and their privacy terms apply. "
                + (settings.AiScreenshots ? "You allowed screenshots, so it can also look at a few of those. " : "Screenshots are not sent unless you allow them in AI Settings. ")
                + "Blocked apps and sites are left out, but audio transcripts are not filtered by app. Each lookup is listed in the chat so you can see what was sent.",
                "Continue", () => { settings.AiPrivacyAccepted = true; settings.Save(); ShowState(); });
        else
        {
            notice.Visible = false;
            list.Visible = inputBar.Visible = true;
            if (list.Controls.Count == 0) ShowSuggestions();
            input.Focus();
        }
    }

    /// "Anthropic (Claude) · claude-sonnet-5-5 · This chat: 12,345 tokens, $0.04"
    void ShowSubtitle()
    {
        var ai = settings.Ai();
        if (!ai.IsReady) { subtitle.Text = ""; return; }
        var used = convo.Used.IsZero ? "" : $" · This chat: {convo.Used.Input + convo.Used.Output:N0} tokens"
            + (settings.AiPriceFor(ai.Model) != null || convo.Cost > 0 ? $", {AiPrice.Money(convo.Cost)}" : "");
        subtitle.Text = $"{ai.Kind.Title()} · {ai.Model}{used}";
    }

    void ShowNotice(string title, string text, string button, Action act)
    {
        list.Visible = inputBar.Visible = false;
        notice.Controls.Clear();
        var t = new Label { Text = title, Font = Ui.F(18 * k, true), Dock = DockStyle.Top, Height = (int)(40 * k), TextAlign = ContentAlignment.MiddleCenter };
        var d = new Label { Text = text, Font = Ui.F(13.5f * k), ForeColor = Ui.Dim, Dock = DockStyle.Top, Height = (int)(150 * k), TextAlign = ContentAlignment.TopCenter };
        var bar = new Panel { Dock = DockStyle.Top, Height = (int)(48 * k), BackColor = Ui.Bg };
        var b = new PillButton(button, primary: true) { Font = Ui.F(13.5f * k, true), Size = new Size((int)(200 * k), (int)(40 * k)) };
        b.Click += (_, _) => act();
        bar.Resize += (_, _) => b.Location = new Point((bar.Width - b.Width) / 2, 0);
        bar.Controls.Add(b);
        notice.Controls.Add(bar);
        notice.Controls.Add(d);
        notice.Controls.Add(t);
        notice.Controls.Add(new Panel { Dock = DockStyle.Top, Height = (int)(120 * k), BackColor = Ui.Bg });
        notice.Visible = true;
    }

    void ShowSuggestions()
    {
        var intro = Bubble("Ask about anything you did, saw or heard on this PC.", Ui.Dim, Ui.Bg);
        intro.Tag = "suggestion";
        AddRow(intro);
        foreach (var s in new[] { "What did I work on today?", "What was I doing yesterday afternoon?", "Summarize my meetings this week", "Find the article I was reading about" })
        {
            var chip = Bubble(s, Ui.Text, Ui.Card);
            chip.Cursor = Cursors.Hand;
            chip.Tag = "suggestion";
            chip.Click += (_, _) =>
            {
                if (s.EndsWith("about")) { input.Text = s + " "; input.Focus(); input.SelectionStart = input.Text.Length; }
                else Send(s);
            };
            AddRow(chip);
        }
    }

    async void Send(string text)
    {
        var q = text.Trim();
        var ai = settings.Ai();
        if (q.Length == 0 || cts != null || !ai.IsReady) return;
        input.Clear();
        foreach (var c in list.Controls.Cast<Control>().Where(c => c.Tag as string == "suggestion").ToList()) list.Controls.Remove(c);
        cts = new CancellationTokenSource();
        send.Text = "Stop";
        input.Enabled = false;
        AddRow(thinking);
        tools.Screenshots = settings.AiScreenshots ? f => ImageTools.ForAi(f.Source) : null;
        try { await convo.Ask(q, new AiClient(ai), tools, AddItem, settings.AiPriceFor(ai.Model), cts.Token); }
        finally
        {
            settings.AddAiUsage(ai.Kind, convo.LastUsed, convo.LastCost);
            ShowSubtitle();
            list.Controls.Remove(thinking);
            cts.Dispose();
            cts = null;
            send.Text = "Send";
            input.Enabled = true;
            input.Focus();
        }
    }

    void AddItem(AiChatItem item)
    {
        Control row = item.Kind switch
        {
            AiChatKind.User => UserRow(item.Text),
            AiChatKind.Assistant => AssistantRow(item.Text),
            AiChatKind.Tool => ToolRow(item.Text, item.Args, item.Detail),
            AiChatKind.Usage => UsageRow(item.Text),
            _ => Bubble("⚠ " + item.Text, Ui.Warn, Ui.Bg),
        };
        AddRow(row);
        // Keep "Thinking…" last while the answer is still coming.
        if (list.Controls.Contains(thinking)) list.Controls.SetChildIndex(thinking, list.Controls.Count - 1);
        list.ScrollControlIntoView(list.Controls[list.Controls.Count - 1]);
    }

    Label Bubble(string text, Color fore, Color back) => new()
    {
        Text = text, ForeColor = fore, BackColor = back, AutoSize = true, Font = Ui.F(13.5f * k),
        Padding = new Padding((int)(10 * k), (int)(7 * k), (int)(10 * k), (int)(7 * k)), Margin = new Padding(0, (int)(4 * k), 0, (int)(4 * k)),
    };

    Control UserRow(string text)
    {
        var l = Bubble(text, Color.White, Ui.Accent);
        l.Tag = "user";
        return l;
    }

    Control AssistantRow(string md)
    {
        var (text, links) = AiMarkdown.Plain(md, convo.MayOpen);
        var l = new LinkLabel
        {
            Text = text, ForeColor = Ui.Text, BackColor = Ui.Card, AutoSize = true, Font = Ui.F(13.5f * k),
            LinkColor = Color.FromArgb(115, 165, 255), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(115, 165, 255),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Padding = new Padding((int)(12 * k), (int)(9 * k), (int)(12 * k), (int)(9 * k)), Margin = new Padding(0, (int)(4 * k), 0, (int)(4 * k)),
        };
        l.Links.Clear();
        foreach (var link in links) l.Links.Add(link.Start, link.Length, link.Url);
        l.LinkClicked += (_, e) => OpenLink(e.Link?.LinkData as string);
        var copy = new ContextMenuStrip();
        copy.Items.Add("Copy", null, (_, _) => Clipboard.SetText(text));
        l.ContextMenuStrip = copy;
        return l;
    }

    void OpenLink(string? url)
    {
        if (url == null) return;
        if (AiTime.FromLink(url) is DateTime at) openMoment(at);
        else if (convo.MayOpen(url)) Updater.Open(url);
    }

    Control UsageRow(string text) => new Label
    {
        Text = text, ForeColor = Ui.Faint, BackColor = Ui.Bg, AutoSize = true, Font = Ui.F(11.5f * k),
        Margin = new Padding(0, 0, 0, (int)(8 * k)),
    };

    /// "Searched screen for … · 2,345 characters sent"; click to see the lookup the AI asked for
    /// and exactly what was sent back.
    Control ToolRow(string what, string args, string detail)
    {
        var panel = new Panel { BackColor = Ui.Bg, Margin = new Padding(0, (int)(2 * k), 0, (int)(2 * k)), Tag = "tool" };
        var head = new Label
        {
            Text = $"🔎 {what} · {detail.Length:N0} characters sent  ▸", ForeColor = Ui.Faint, Font = Ui.F(12 * k),
            AutoSize = false, Dock = DockStyle.Top, Height = (int)(22 * k), Cursor = Cursors.Hand,
        };
        var box = new TextBox
        {
            Text = $"The AI asked for:\n{args}\n\nSent back to the AI:\n{detail}".Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Ui.Card, ForeColor = Ui.Dim, Font = new Font("Consolas", 10 * k, GraphicsUnit.Pixel), Dock = DockStyle.Fill, Visible = false,
        };
        head.Click += (_, _) =>
        {
            box.Visible = !box.Visible;
            head.Text = head.Text[..^1] + (box.Visible ? "▾" : "▸");
            panel.Height = head.Height + (box.Visible ? (int)(240 * k) : 0);
        };
        panel.Controls.Add(box);
        panel.Controls.Add(head);
        panel.Height = head.Height;
        return panel;
    }

    void AddRow(Control c)
    {
        list.Controls.Add(c);
        FitRows();
    }

    /// Rows wrap to the window width; tool rows fill it.
    void FitRows()
    {
        var w = list.ClientSize.Width - list.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        if (w <= 0) return;
        list.SuspendLayout();
        foreach (Control c in list.Controls)
        {
            if (c is Panel) c.Width = w;
            else if (c is Label l)
            {
                l.MaximumSize = new Size(c.Tag as string == "user" ? (int)(w * 0.85) : w, 0);
                // Questions sit on the right, like the Mac app.
                if (c.Tag as string == "user") l.Margin = new Padding(Math.Max(0, w - l.PreferredSize.Width), l.Margin.Top, 0, l.Margin.Bottom);
            }
        }
        list.ResumeLayout();
    }

}

/// Settings → AI Settings: provider, key and model, saved per provider.
sealed class AiSettingsForm : Form
{
    static AiSettingsForm? open;
    readonly Settings settings;
    readonly float k;
    readonly ComboBox provider, model;
    readonly TextBox key, baseUrl, priceIn, priceOut;
    readonly CheckBox screenshots;
    readonly Label status, urlLabel, urlHint, keyLabel;
    readonly LinkLabel keyLink, usage;
    readonly PillButton refresh, test;
    bool working;

    public static void ShowFor(Settings settings)
    {
        if (open is { IsDisposed: false }) { open.Activate(); return; }
        open = new AiSettingsForm(settings);
        open.Show();
        open.Activate();
    }

    /// The same fields inside the Settings window's AI tab, at their own size.
    public static AiSettingsForm Embedded(Settings settings) =>
        new(settings) { TopLevel = false, FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false };

    AiSettingsForm(Settings settings)
    {
        this.settings = settings;
        k = DeviceDpi / 96f;
        Text = "AI Settings";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size((int)(560 * k), (int)(820 * k));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        int x = (int)(24 * k), w = ClientSize.Width - 2 * x, y = (int)(18 * k);
        Label Add(string text, float size, Color color, bool bold = false, int height = 0)
        {
            var l = new Label { Text = text, Font = Ui.F(size * k, bold), ForeColor = color, Location = new Point(x, y), Size = new Size(w, height > 0 ? height : (int)(size * 1.6f * k)) };
            Controls.Add(l);
            y += l.Height + (int)(4 * k);
            return l;
        }
        T Field<T>(T c, int width = 0) where T : Control
        {
            c.Location = new Point(x, y);
            c.Width = width > 0 ? width : w;
            c.BackColor = Ui.Card;
            c.ForeColor = Ui.Text;
            c.Font = Ui.F(14 * k);
            Controls.Add(c);
            y += c.Height + (int)(6 * k);
            return c;
        }

        Add("AI Settings", 20, Ui.Text, bold: true);
        Add("Use your own key. Questions and the history the AI looks up go straight from this PC to the provider you pick.", 12.5f, Ui.Dim, height: (int)(40 * k));
        y += (int)(6 * k);

        Add("Provider", 12.5f, Ui.Dim, bold: true);
        provider = Field(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat });
        foreach (var p in AiProviders.All) provider.Items.Add(p.Title());
        y += (int)(6 * k);

        urlLabel = Add("Server address", 12.5f, Ui.Dim, bold: true);
        baseUrl = Field(new TextBox { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "http://localhost:11434/v1" });
        urlHint = Add(UrlHint, 11, Ui.Faint, height: (int)(32 * k));
        baseUrl.TextChanged += (_, _) => ShowUrlHint();

        keyLabel = Add("API key", 12.5f, Ui.Dim, bold: true);
        key = Field(new TextBox { BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true, PlaceholderText = "Paste your key" });
        keyLink = new LinkLabel { Text = "Saved in Windows Credential Manager. Get a key", Font = Ui.F(11.5f * k), ForeColor = Ui.Faint, LinkColor = Color.FromArgb(115, 165, 255), Location = new Point(x, y), Size = new Size(w, (int)(20 * k)) };
        keyLink.LinkClicked += (_, _) => { if (Kind.KeyPage() is string page) Updater.Open(page); };
        Controls.Add(keyLink);
        y += keyLink.Height + (int)(10 * k);

        Add("Model", 12.5f, Ui.Dim, bold: true);
        var bw = (int)(130 * k);
        // Typing filters the list, which matters for OpenRouter's hundreds of models.
        model = Field(new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, MaxDropDownItems = 20,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems,
        }, w - bw - (int)(8 * k));
        refresh = new PillButton("Refresh List") { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(x + w - bw, model.Top, bw, model.Height) };
        refresh.Click += async (_, _) => await RefreshModels(quiet: false);
        Controls.Add(refresh);
        Add("Type any model name, so new models work as soon as they come out. Refresh List asks the provider for its models.", 11, Ui.Faint, height: (int)(34 * k));
        y += (int)(6 * k);

        Add("Price per million tokens (US$, input / output)", 12.5f, Ui.Dim, bold: true);
        var half = (w - (int)(8 * k)) / 2;
        var priceTop = y;
        priceIn = Field(new TextBox { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Input, e.g. 2" }, half);
        y = priceTop;
        priceOut = Field(new TextBox { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Output, e.g. 10" }, half);
        priceOut.Left = x + half + (int)(8 * k);
        Add("Filled in for Claude, GPT-6 and OpenRouter models. For others, copy the price from the provider's pricing page; leave empty to see tokens only.", 11, Ui.Faint, height: (int)(34 * k));
        model.TextChanged += (_, _) => ShowPrice();
        y += (int)(6 * k);

        screenshots = new CheckBox
        {
            Text = "Let AI look at screenshots", Checked = settings.AiScreenshots, Font = Ui.F(13.5f * k, true), ForeColor = Ui.Text,
            Location = new Point(x, y), Size = new Size(w, (int)(26 * k)),
        };
        Controls.Add(screenshots);
        y += screenshots.Height + (int)(2 * k);
        Add("Off by default. When on, the AI can ask for a few screenshots when text is not enough. Those images go to the provider and cost more than text. Blocked apps are blurred in them.", 11, Ui.Faint, height: (int)(50 * k));
        y += (int)(6 * k);

        usage = new LinkLabel { Font = Ui.F(12 * k), ForeColor = Ui.Dim, LinkColor = Color.FromArgb(115, 165, 255), Location = new Point(x, y), Size = new Size(w, (int)(40 * k)) };
        usage.LinkClicked += (_, _) => { settings.AiTotals.Remove(Kind.Id()); settings.Save(); ShowUsage(); };
        Controls.Add(usage);

        status = new Label { Font = Ui.F(12 * k), ForeColor = Ui.Dim, Location = new Point(x, ClientSize.Height - (int)(130 * k)), Size = new Size(w, (int)(60 * k)) };
        Controls.Add(status);
        var save = new PillButton("Save", primary: true) { Font = Ui.F(13.5f * k, true) };
        test = new PillButton("Test") { Font = Ui.F(13.5f * k, true) };
        save.SetBounds(ClientSize.Width - x - (int)(110 * k), ClientSize.Height - (int)(60 * k), (int)(110 * k), (int)(40 * k));
        test.SetBounds(save.Left - (int)(110 * k), save.Top, (int)(100 * k), (int)(40 * k));
        save.Click += (_, _) => Save();
        test.Click += async (_, _) => await Test();
        Controls.Add(save);
        Controls.Add(test);

        provider.SelectedIndexChanged += (_, _) => ShowProvider(Kind);
        provider.SelectedIndex = Array.IndexOf(AiProviders.All, settings.Ai().Kind);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    AiProviderKind Kind => AiProviders.All[Math.Max(0, provider.SelectedIndex)];

    const string UrlHint = "Ollama: http://localhost:11434/v1 · LM Studio: http://localhost:1234/v1 ";

    /// Plain http to another computer sends the key and history unencrypted; say so under the address.
    void ShowUrlHint()
    {
        var risky = AiFormat.IsPlainHttpRemote(baseUrl.Text);
        urlHint.Text = risky ? "Plain http to another computer: your key and history travel unencrypted. Use https, or a server on this PC or your own network." : UrlHint;
        urlHint.ForeColor = risky ? Ui.Warn : Ui.Faint;
    }

    AiSettings Current => new(Kind, model.Text.Trim(), baseUrl.Text.Trim(), key.Text.Trim());

    void ShowProvider(AiProviderKind kind)
    {
        var s = settings.Ai(kind);
        key.Text = s.Key ?? "";
        baseUrl.Text = s.BaseUrl;
        model.Items.Clear();
        foreach (var m in settings.SavedModels(kind)) model.Items.Add(m);
        model.Text = s.Model;
        urlLabel.Visible = baseUrl.Visible = urlHint.Visible = kind == AiProviderKind.Custom;
        keyLabel.Text = kind.NeedsKey() ? "API key" : "API key (if the server needs one)";
        keyLink.Text = "Saved in Windows Credential Manager." + (kind.KeyPage() != null ? " Get a key" : "");
        keyLink.Links.Clear();
        if (kind.KeyPage() != null) keyLink.Links.Add(keyLink.Text.Length - 9, 9);
        ShowPrice();
        ShowUsage();
        status.Text = "";
        // OpenRouter lists its models without a key; the others need one.
        if (kind == AiProviderKind.OpenRouter || (s.Key ?? "").Length > 0) _ = RefreshModels(quiet: true);
    }

    void ShowPrice()
    {
        var p = settings.AiPriceFor(model.Text.Trim());
        priceIn.Text = p is { } a ? a.Input.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
        priceOut.Text = p is { } b ? b.Output.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
    }

    /// "Used with OpenAI since 19 Oct: 120,000 tokens in, 8,000 out, about $0.38. Reset"
    void ShowUsage()
    {
        var t = settings.AiTotals.GetValueOrDefault(Kind.Id());
        if (t == null) { usage.Text = $"Nothing used with {Kind.Title()} yet."; usage.Links.Clear(); return; }
        var cost = t.Cost > 0 || !t.Unpriced ? $", {(t.Unpriced ? "at least " : "about ")}{AiPrice.Money(t.Cost)}" : "";
        usage.Text = $"Used with {Kind.Title()} since {t.Since.ToLocalTime():d MMM yyyy}: {t.Input:N0} tokens in, {t.Output:N0} out{cost}. Reset";
        usage.Links.Clear();
        usage.Links.Add(usage.Text.Length - 5, 5);
    }

    void Save()
    {
        settings.SaveAi(Current);
        var m = model.Text.Trim();
        var ok = double.TryParse(priceIn.Text, out var pin) & double.TryParse(priceOut.Text, out var pout);
        if (ok && pin >= 0 && pout >= 0)
        {
            if (AiPrice.Default(m) == new AiPrice(pin, pout)) settings.AiPrices.Remove(m);
            else settings.AiPrices[m] = new[] { pin, pout };
        }
        else if (priceIn.Text.Trim().Length == 0 && priceOut.Text.Trim().Length == 0) settings.AiPrices.Remove(m);
        settings.AiScreenshots = screenshots.Checked;
        settings.Save();
        status.Text = "Saved.";
        MeetingWatcher.RecheckConsent(settings, this);
        _ = RefreshModels(quiet: true);
        AiChatForm.SettingsChanged();
        JournalForm.Reload();
    }

    /// Fills the model list from the provider. Quiet: in the background, saying nothing if it fails.
    async Task RefreshModels(bool quiet)
    {
        if (working) return;
        var s = Current;
        if (s.Kind.NeedsKey() && s.Kind != AiProviderKind.OpenRouter && string.IsNullOrEmpty(s.Key)) { if (!quiet) status.Text = "Add your key first."; return; }
        Busy(true, quiet ? null : $"Asking {s.Kind.Title()} for its models…");
        try
        {
            var list = await new AiClient(s).ListModelsWithPrices();
            if (IsDisposed || Kind != s.Kind) return;
            foreach (var (id, price) in list)
                if (price is { } p) settings.AiListedPrices[id] = new[] { p.Input, p.Output };
            settings.RememberModels(s.Kind, list.Select(m => m.Id));
            var typed = model.Text;
            model.BeginUpdate();
            model.Items.Clear();
            foreach (var m in settings.SavedModels(s.Kind)) model.Items.Add(m);
            model.EndUpdate();
            model.Text = typed;
            if (!quiet) status.Text = list.Count == 0 ? "The provider listed no models." : $"Found {list.Count} models. Type in the Model box to search them.";
        }
        catch (Exception e) { if (!quiet && !IsDisposed) status.Text = e.Message; }
        finally { if (!IsDisposed) Busy(false); }
    }

    async Task Test()
    {
        if (working) return;
        var s = Current;
        if (!s.IsReady) { status.Text = "Add a key and a model first."; return; }
        Busy(true, "Testing…");
        try
        {
            var r = await new AiClient(s).Send("Reply with the single word OK.", new[] { new AiMessage { Role = AiRole.User, Text = "Test" } }, Array.Empty<AiToolSpec>());
            status.Text = $"It works. {s.Model} replied: {(r.Text.Length > 80 ? r.Text[..80] : r.Text)}";
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { Busy(false); }
    }

    void Busy(bool on, string? text = null)
    {
        working = on;
        refresh.Enabled = test.Enabled = !on;
        if (text != null) status.Text = text;
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
