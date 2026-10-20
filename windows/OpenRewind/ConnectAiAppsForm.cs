using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Settings → Connect AI Apps: the switch that lets AI apps read the history (MCP), one button per
/// AI app that adds OpenRewind to it (and takes it out again), and the setup to paste by hand into others.
sealed class ConnectAiAppsForm : Form
{
    static ConnectAiAppsForm? open;
    readonly CheckBox allow;
    readonly List<Row> rows = new();
    readonly Settings settings;

    public static void ShowFor(Settings settings, Config config)
    {
        if (open is { IsDisposed: false }) { SettingsChanged(settings); open.Activate(); return; }
        open = new ConnectAiAppsForm(settings, config);
        open.Show();
        open.Activate();
    }

    /// The tray switch changed; keep the box here in step with it.
    public static void SettingsChanged(Settings settings)
    {
        if (open is { IsDisposed: false } f && f.allow.Checked != settings.McpEnabled) f.allow.Checked = settings.McpEnabled;
    }

    /// What every screen about MCP says about what AI apps get (the Mac app and README say the same).
    public const string WhatTheyGet =
        "Blocked apps and sites are left out of screen text and screenshots; in the activity summary a blocked app shows only as "
        + "“(blocked app)” with its time. Audio transcripts are not filtered by app. What the AI app reads goes to its AI provider. "
        + "Text on your screen could try to trick it, and many AI apps can open links or run commands, so check what it does with your history.";

    /// One AI app: its name, a line about it, what is set up now, and the buttons.
    sealed class Row
    {
        public required AiAppLink Link;
        public required Label Status;
        public required PillButton Add, Remove, Restart;
        /// What the last click did, shown until the setup changes some other way.
        public string? Message;
        public bool Failed;
        public McpLink? Last;
        public bool Busy;
    }

    ConnectAiAppsForm(Settings settings, Config config)
    {
        this.settings = settings;
        var k = DeviceDpi / 96f;
        Text = "Connect AI Apps";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScroll = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        HandleCreated += (_, _) => { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); };

        // Never bigger than the screen it opens on (150% scaling on a 1080p laptop leaves about 1000 px);
        // what does not fit scrolls. The scroll bar's width is kept free so nothing scrolls sideways.
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        var chrome = SizeFromClientSize(Size.Empty);
        ClientSize = new Size(Math.Min((int)(640 * k), work.Width - chrome.Width - 20), Math.Min((int)(900 * k), work.Height - chrome.Height - 20));

        int x = (int)(24 * k), w = ClientSize.Width - 2 * x - SystemInformation.VerticalScrollBarWidth, y = (int)(18 * k);
        Label Add(string text, float size, Color color, bool bold = false, int lines = 0)
        {
            var font = Ui.F(size * k, bold);
            // Tall enough for the text wrapped at this width, so narrow windows do not cut lines off.
            var h = lines > 0 ? (int)(lines * size * 1.45f * k)
                : TextRenderer.MeasureText(text, font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height + (int)(4 * k);
            var l = new Label { Text = text, Font = font, ForeColor = color, Location = new Point(x, y), Size = new Size(w, h) };
            Controls.Add(l);
            y += l.Height + (int)(4 * k);
            return l;
        }

        Add("Connect AI Apps", 20, Ui.Text, bold: true);
        Add("Let Claude, ChatGPT, Cursor and other AI apps that support MCP search your OpenRewind history. They ask OpenRewind on this PC.", 12.5f, Ui.Dim);
        Add(WhatTheyGet, 11.5f, Ui.Dim);
        y += (int)(4 * k);

        allow = new CheckBox
        {
            Text = "Allow AI Apps to Read My History (MCP)", Checked = settings.McpEnabled, Font = Ui.F(13.5f * k, true), ForeColor = Ui.Text,
            Location = new Point(x, y), Size = new Size(w, (int)(26 * k)),
        };
        allow.CheckedChanged += (_, _) =>
        {
            if (settings.McpEnabled == allow.Checked) return;
            settings.McpEnabled = allow.Checked;
            settings.Save();
            Log.Write($"AI apps (MCP) {(allow.Checked ? "allowed" : "not allowed")}");
        };
        Controls.Add(allow);
        y += allow.Height + (int)(2 * k);
        Add("Off by default; adding an app below turns it on. While off, AI apps that you connected get a message asking you to turn this on. "
            + "Screenshots are shared only if “Let AI look at screenshots” is on in AI Settings.", 11, Ui.Faint);
        y += (int)(10 * k);

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenRewind.exe");
        if (McpCommand.InTemporaryFolder(exe))
            Add("OpenRewind is running from a temporary folder (straight from the zip?). Unzip it to a folder that stays, "
                + "like %LOCALAPPDATA%\\Programs\\OpenRewind, and open it from there first, so AI apps can keep starting it.", 12, Ui.Warn);
        var home = McpCommand.HomeArg(config);
        var want = new McpCommandLine(exe, McpSetup.Args(home));

        foreach (var link in new AiAppLink[]
                 {
                     JsonAppLink.ClaudeDesktop(want), new ClaudeCodeLink(want), new ChatGptLink(want), JsonAppLink.Cursor(want),
                 })
        {
            Add(link.Title, 14, Ui.Text, bold: true);
            Add(link.About, 11.5f, Ui.Dim);
            var status = Add("", 12, Ui.Dim, lines: 2);
            int bh = (int)(34 * k), gap = (int)(8 * k);
            var addButton = new PillButton("Add", primary: true) { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(x, y, (int)(170 * k), bh) };
            var removeButton = new PillButton("Remove") { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(addButton.Right + gap, y, (int)(100 * k), bh) };
            var restartButton = new PillButton($"Restart {link.AppName}") { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(removeButton.Right + gap, y, (int)(160 * k), bh) };
            var row = new Row { Link = link, Status = status, Add = addButton, Remove = removeButton, Restart = restartButton };
            addButton.Click += (_, _) => Act(row, add: true);
            removeButton.Click += (_, _) => Act(row, add: false);
            restartButton.Click += (_, _) => RestartApp(row);
            Controls.AddRange(new Control[] { addButton, removeButton, restartButton });
            rows.Add(row);
            y += bh + (int)(18 * k);
        }

        Add("Other AI apps", 14, Ui.Text, bold: true);
        Add("Most MCP apps take these lines in their MCP settings, with the path of this OpenRewind.exe:", 11.5f, Ui.Dim);
        Snippet("JSON", McpSetup.JsonConfig(exe, home), 7);
        Add("Claude Code by hand, in a terminal:", 11.5f, Ui.Dim);
        Snippet("Command", McpSetup.ClaudeCode(exe, home, windows: true), 3);

        void Snippet(string title, string code, int lines)
        {
            var bw = (int)(84 * k);
            var box = new TextBox
            {
                Text = code.Replace("\n", "\r\n"), ReadOnly = true, Multiline = true, WordWrap = true, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Ui.Card, ForeColor = Ui.Text, Font = new Font("Consolas", 12 * k, GraphicsUnit.Pixel),
                Location = new Point(x, y), Size = new Size(w - bw - (int)(8 * k), (int)((lines * 16 + 8) * k)),
                ScrollBars = ScrollBars.Vertical, AccessibleName = title,
            };
            Controls.Add(box);
            var copy = new PillButton("Copy") { Font = Ui.F(12.5f * k, true), Bounds = new Rectangle(x + w - bw, y, bw, (int)(34 * k)) };
            copy.Click += (_, _) =>
            {
                try { Clipboard.SetText(code.Replace("\n", "\r\n")); copy.Text = "Copied"; }
                catch (Exception e) { Log.Write($"cannot copy: {e.Message}"); copy.Text = "Failed"; }
            };
            Controls.Add(copy);
            y += box.Height + (int)(14 * k);
        }

        // Below the last snippet, so it scrolls into view with the rest on small screens.
        var close = new PillButton("Done", primary: true) { Font = Ui.F(13.5f * k, true) };
        close.SetBounds(x + w - (int)(110 * k), y + (int)(6 * k), (int)(110 * k), (int)(40 * k));
        close.Click += (_, _) => Close();
        Controls.Add(close);
        // Room under Done, and no empty space when everything fits.
        var bottom = close.Bottom + (int)(20 * k);
        if (bottom < ClientSize.Height) ClientSize = new Size(ClientSize.Width, bottom);
        else Controls.Add(new Label { Location = new Point(x, bottom - 1), Size = new Size(1, 1) });

        ShowStatus(clearMessages: true);
        // Back from Claude or a text editor: show what is set up now.
        Activated += (_, _) => ShowStatus(clearMessages: false);
    }

    bool reading, again;

    /// Reads every app's setup again, off the window's thread (~/.claude.json can be large), and shows it.
    async void ShowStatus(bool clearMessages)
    {
        if (reading) { again = true; return; }
        reading = true;
        var todo = rows.Where(r => !r.Busy).ToList();
        var found = await Task.Run(() => todo.Select(r =>
        {
            McpLink link;
            string? problem;
            try { (link, problem) = r.Link.Status(); }
            catch (Exception e) { Log.Write($"connect: {r.Link.Title}: {e.Message}"); (link, problem) = (McpLink.Unreadable, "OpenRewind can't read this app's setup."); }
            var running = link is McpLink.Added && SafeCanRestart(r.Link);
            return (Row: r, Link: link, Problem: problem, Running: running);
        }).ToList());
        reading = false;
        if (IsDisposed) return;
        foreach (var (row, link, problem, running) in found)
        {
            if (row.Busy) continue;
            // A message from the last click stays until the setup changes some other way.
            if (clearMessages || (row.Last != null && row.Last != link)) row.Message = null;
            row.Last = link;
            row.Status.ForeColor = row.Message != null && row.Failed ? Ui.Warn
                : link switch { McpLink.Added => Ui.Good, McpLink.Unreadable or McpLink.TurnedOff => Ui.Warn, _ => Ui.Dim };
            row.Status.Text = row.Message ?? link switch
            {
                McpLink.Added => running ? $"Added. Restart {row.Link.AppName} if it doesn't list OpenRewind yet." : "Added.",
                McpLink.Different => "Set up for another copy of OpenRewind or with other options. Update points it at this one.",
                McpLink.TurnedOff => $"Turned off in {row.Link.AppName}. Turn On switches it back on.",
                McpLink.Unreadable => problem ?? "OpenRewind can't change this app's setup. Set it up by hand below.",
                _ => "Not added.",
            };
            row.Add.Text = link switch
            {
                McpLink.Added => "Added ✓",
                McpLink.Different => "Update",
                McpLink.TurnedOff => "Turn On",
                _ => $"Add to {row.Link.AppName}",
            };
            row.Add.Enabled = link is McpLink.NotAdded or McpLink.Different or McpLink.TurnedOff;
            row.Remove.Visible = link is McpLink.Added or McpLink.Different or McpLink.TurnedOff;
            row.Restart.Visible = running;
            row.Restart.Left = row.Remove.Visible ? row.Remove.Right + (row.Remove.Left - row.Add.Right) : row.Remove.Left;
        }
        if (again) { again = false; ShowStatus(clearMessages: false); }
    }

    static bool SafeCanRestart(AiAppLink link)
    {
        try { return link.CanRestart; } catch (Exception e) { Log.Write($"connect: cannot look for {link.AppName}: {e.Message}"); return false; }
    }

    /// Adds or removes in the background (Claude Code's command can take a few seconds), then shows how it went.
    async void Act(Row row, bool add)
    {
        if (row.Busy) return;
        row.Busy = true;
        row.Add.Enabled = row.Remove.Enabled = false;
        row.Status.ForeColor = Ui.Dim;
        row.Status.Text = add ? "Adding…" : "Removing…";
        if (add && !settings.McpEnabled)
        {
            // Adding an app is asking for it to work: turn on the switch it needs.
            settings.McpEnabled = true;
            settings.Save();
            allow.Checked = true;
            Log.Write("AI apps (MCP) allowed");
        }
        string message;
        var failed = false;
        try { message = await Task.Run(() => add ? row.Link.Add() : row.Link.Remove()); }
        catch (McpConfigException e) { message = e.Message; failed = true; }
        catch (Exception e)
        {
            Log.Write($"connect: {row.Link.Title}: {e.GetType().Name}: {e.Message}");
            message = $"Couldn't save the change: {e.Message}";
            failed = true;
        }
        Done(row, message, failed);
    }

    async void RestartApp(Row row)
    {
        if (row.Busy) return;
        var app = row.Link.AppName;
        if (MessageBox.Show(this,
                $"OpenRewind will close {app} and open it again, so it loads OpenRewind.\n\n"
                + $"If {app} is still writing a reply, or you have a message you haven't sent, it will be lost.",
                $"Restart {app}?", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        row.Busy = true;
        row.Restart.Enabled = false;
        row.Status.Text = $"Restarting {app}…";
        string message;
        var failed = false;
        try { await Task.Run(row.Link.Restart); message = $"Restarted {app}."; }
        catch (Exception e)
        {
            Log.Write($"connect: cannot restart {app}: {e.Message}");
            message = $"Couldn't restart {app}. Quit and reopen it yourself.";
            failed = true;
        }
        Done(row, message, failed);
    }

    /// Shows the setup as it is now, with what just happened under the app's name.
    void Done(Row row, string message, bool failed)
    {
        if (IsDisposed) return;
        row.Busy = false;
        row.Remove.Enabled = row.Restart.Enabled = true;
        row.Message = message;
        row.Failed = failed;
        // The next read sets Last without taking the message away.
        row.Last = null;
        row.Status.Text = message;
        row.Status.ForeColor = failed ? Ui.Warn : Ui.Dim;
        ShowStatus(clearMessages: false);
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
