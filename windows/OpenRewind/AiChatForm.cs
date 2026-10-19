using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Ask AI: questions about what you did, saw or heard, answered by the provider you picked
/// using lookups in the local database (AiTools). Same window as AIChatView on the Mac.
sealed class AiChatForm : Form
{
    static AiChatForm? open;
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
    readonly PillButton send;
    readonly Label thinking;
    CancellationTokenSource? cts;

    /// One window at a time; the conversation lives in `convo`, so closing the window keeps it.
    public static void ShowFor(Settings settings, AiTools tools, AiConversation convo, Action<DateTime> openMoment, Action openSettings)
    {
        if (open is { IsDisposed: false }) { open.ShowState(); open.Activate(); return; }
        open = new AiChatForm(settings, tools, convo, openMoment, openSettings);
        open.Show();
        open.Activate();
    }

    /// After AI Settings saved: show the new provider, or the chat instead of "add your key".
    public static void SettingsChanged() { if (open is { IsDisposed: false }) open.ShowState(); }

    AiChatForm(Settings settings, AiTools tools, AiConversation convo, Action<DateTime> openMoment, Action openSettings)
    {
        this.settings = settings;
        this.tools = tools;
        this.convo = convo;
        this.openMoment = openMoment;
        this.openSettings = openSettings;
        k = DeviceDpi / 96f;
        Text = "Ask OpenRewind";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size((int)(420 * k), (int)(420 * k));
        ClientSize = new Size((int)(560 * k), (int)(720 * k));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        // Header: title, provider · model, New Chat and Settings.
        var header = new Panel { Dock = DockStyle.Top, Height = (int)(64 * k), BackColor = Ui.Bg, Padding = new Padding((int)(16 * k), (int)(10 * k), (int)(16 * k), 0) };
        var title = new Label { Text = "Ask OpenRewind", Font = Ui.F(16 * k, true), AutoSize = true, Location = new Point((int)(16 * k), (int)(10 * k)) };
        subtitle = new Label { Font = Ui.F(12 * k), ForeColor = Ui.Faint, AutoSize = true, Location = new Point((int)(16 * k), (int)(36 * k)) };
        var newChat = new PillButton("New Chat") { Font = Ui.F(12.5f * k, true), Size = new Size((int)(96 * k), (int)(32 * k)) };
        var gear = new PillButton("Settings") { Font = Ui.F(12.5f * k, true), Size = new Size((int)(88 * k), (int)(32 * k)) };
        gear.Click += (_, _) => openSettings();
        header.Resize += (_, _) =>
        {
            gear.Location = new Point(header.Width - gear.Width - (int)(16 * k), (int)(14 * k));
            newChat.Location = new Point(gear.Left - newChat.Width - (int)(8 * k), (int)(14 * k));
        };
        header.Controls.AddRange(new Control[] { title, subtitle, newChat, gear });

        list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            BackColor = Ui.Bg, Padding = new Padding((int)(16 * k), (int)(8 * k), (int)(16 * k), (int)(8 * k)),
        };
        list.Resize += (_, _) => FitRows();
        newChat.Click += (_, _) => { cts?.Cancel(); convo.Clear(); list.Controls.Clear(); ShowSuggestions(); };

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

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    /// Shows the chat, or what is missing before it can be used.
    public void ShowState()
    {
        var ai = settings.Ai();
        subtitle.Text = ai.IsReady ? $"{ai.Kind.Title()} · {ai.Model}" : "";
        if (!ai.IsReady)
            ShowNotice("Add your AI key",
                "OpenRewind can answer questions about what you did and saw, using your own Anthropic, OpenAI or Gemini key, or a local model (Ollama, LM Studio).",
                "Open AI Settings…", openSettings);
        else if (!settings.AiPrivacyAccepted)
            ShowNotice("Before you start",
                $"To answer, the AI reads parts of your history: screen text, app and window names, web addresses and transcripts. Those parts are sent to {ai.Kind.Title()} using your key, and their privacy terms apply. Screenshots are never sent. Blocked apps and sites are never recorded, so the AI never sees them. Each lookup is listed in the chat so you can see what was sent.",
                "Continue", () => { settings.AiPrivacyAccepted = true; settings.Save(); ShowState(); });
        else
        {
            notice.Visible = false;
            list.Visible = inputBar.Visible = true;
            if (list.Controls.Count == 0) ShowSuggestions();
            input.Focus();
        }
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
        try { await convo.Ask(q, new AiClient(ai), tools, AddItem, cts.Token); }
        finally
        {
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
            AiChatKind.Tool => ToolRow(item.Text, item.Detail),
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
        var (text, links) = AiMarkdown.Plain(md);
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
        else if (url.StartsWith("https://") || url.StartsWith("http://")) Updater.Open(url);
    }

    /// "Searched screen for … · 2,345 characters sent"; click to see exactly what was sent.
    Control ToolRow(string what, string detail)
    {
        var panel = new Panel { BackColor = Ui.Bg, Margin = new Padding(0, (int)(2 * k), 0, (int)(2 * k)), Tag = "tool" };
        var head = new Label
        {
            Text = $"🔎 {what} · {detail.Length:N0} characters sent  ▸", ForeColor = Ui.Faint, Font = Ui.F(12 * k),
            AutoSize = false, Dock = DockStyle.Top, Height = (int)(22 * k), Cursor = Cursors.Hand,
        };
        var box = new TextBox
        {
            Text = detail.Replace("\n", "\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            BackColor = Ui.Card, ForeColor = Ui.Dim, Font = new Font("Consolas", 10 * k, GraphicsUnit.Pixel), Dock = DockStyle.Fill, Visible = false,
        };
        head.Click += (_, _) =>
        {
            box.Visible = !box.Visible;
            head.Text = head.Text[..^1] + (box.Visible ? "▾" : "▸");
            panel.Height = head.Height + (box.Visible ? (int)(200 * k) : 0);
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

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}

/// Settings → AI Settings: provider, key and model, saved per provider.
sealed class AiSettingsForm : Form
{
    static AiSettingsForm? open;
    readonly Settings settings;
    readonly float k;
    readonly ComboBox provider, model;
    readonly TextBox key, baseUrl;
    readonly Label status, urlLabel, urlHint, keyLabel;
    readonly LinkLabel keyLink;
    readonly PillButton refresh, test;
    bool working;

    public static void ShowFor(Settings settings)
    {
        if (open is { IsDisposed: false }) { open.Activate(); return; }
        open = new AiSettingsForm(settings);
        open.Show();
        open.Activate();
    }

    AiSettingsForm(Settings settings)
    {
        this.settings = settings;
        k = DeviceDpi / 96f;
        Text = "AI Settings";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ClientSize = new Size((int)(560 * k), (int)(560 * k));
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
        urlHint = Add("Ollama: http://localhost:11434/v1 · LM Studio: http://localhost:1234/v1 · OpenRouter: https://openrouter.ai/api/v1", 11, Ui.Faint, height: (int)(32 * k));

        keyLabel = Add("API key", 12.5f, Ui.Dim, bold: true);
        key = Field(new TextBox { BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true, PlaceholderText = "Paste your key" });
        keyLink = new LinkLabel { Text = "Saved in Windows Credential Manager. Get a key", Font = Ui.F(11.5f * k), ForeColor = Ui.Faint, LinkColor = Color.FromArgb(115, 165, 255), Location = new Point(x, y), Size = new Size(w, (int)(20 * k)) };
        keyLink.LinkClicked += (_, _) => { if (Kind.KeyPage() is string page) Updater.Open(page); };
        Controls.Add(keyLink);
        y += keyLink.Height + (int)(10 * k);

        Add("Model", 12.5f, Ui.Dim, bold: true);
        var bw = (int)(130 * k);
        model = Field(new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat }, w - bw - (int)(8 * k));
        refresh = new PillButton("Refresh List") { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(x + w - bw, model.Top, bw, model.Height) };
        refresh.Click += async (_, _) => await RefreshModels();
        Controls.Add(refresh);
        Add("Type any model name, so new models work as soon as they come out. Refresh List asks the provider for its models.", 11, Ui.Faint, height: (int)(34 * k));

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
        status.Text = "";
    }

    void Save()
    {
        settings.SaveAi(Current);
        settings.Save();
        status.Text = "Saved.";
        AiChatForm.SettingsChanged();
    }

    async Task RefreshModels()
    {
        if (working) return;
        var s = Current;
        Busy(true, $"Asking {s.Kind.Title()} for its models…");
        try
        {
            var list = await new AiClient(s).ListModels();
            settings.RememberModels(s.Kind, list);
            var typed = model.Text;
            model.Items.Clear();
            foreach (var m in settings.SavedModels(s.Kind)) model.Items.Add(m);
            model.Text = typed;
            status.Text = list.Count == 0 ? "The provider listed no models." : $"Found {list.Count} models. Pick one from the Model list.";
        }
        catch (Exception e) { status.Text = e.Message; }
        finally { Busy(false); }
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
