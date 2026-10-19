using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Settings → Connect AI Apps: the switch that lets AI apps read the history (MCP), and the setup
/// to paste into Claude Desktop, Claude Code and Cursor, with this copy's path filled in.
sealed class ConnectAiAppsForm : Form
{
    static ConnectAiAppsForm? open;
    readonly CheckBox allow;

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

    ConnectAiAppsForm(Settings settings, Config config)
    {
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
        Label Add(string text, float size, Color color, bool bold = false)
        {
            var font = Ui.F(size * k, bold);
            // Tall enough for the text wrapped at this width, so narrow windows do not cut lines off.
            var h = TextRenderer.MeasureText(text, font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height + (int)(4 * k);
            var l = new Label { Text = text, Font = font, ForeColor = color, Location = new Point(x, y), Size = new Size(w, h) };
            Controls.Add(l);
            y += l.Height + (int)(4 * k);
            return l;
        }

        Add("Connect AI Apps", 20, Ui.Text, bold: true);
        Add("Let Claude, Cursor and other AI apps that support MCP search your OpenRewind history. They ask OpenRewind on this PC.", 12.5f, Ui.Dim);
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
        Add("Off by default. While off, AI apps that you connected get a message asking you to turn this on. "
            + "Screenshots are shared only if “Let AI look at screenshots” is on in AI Settings.", 11, Ui.Faint);
        y += (int)(10 * k);

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "OpenRewind.exe");
        if (McpCommand.InTemporaryFolder(exe))
            Add("OpenRewind is running from a temporary folder (straight from the zip?). Unzip it to a folder that stays, "
                + "like %LOCALAPPDATA%\\Programs\\OpenRewind, and open it from there first, so the path below keeps working.", 12, Ui.Warn);
        var home = McpCommand.HomeArg(config);
        var json = McpSetup.JsonConfig(exe, home);
        Snippet("Claude Desktop", $"Paste into {McpSetup.ClaudeDesktopFile(true)} (Settings → Developer → Edit Config), then quit and reopen Claude. "
                + "If the file already has “mcpServers”, add just the openrewind part.", json, 7);
        Snippet("Claude Code", "Run once in a terminal:", McpSetup.ClaudeCode(exe, home, windows: true), 3);
        Snippet("Cursor", $"Paste into {McpSetup.CursorFile(true)} (Cursor Settings → MCP), then restart Cursor.", json, 7);

        void Snippet(string title, string hint, string code, int lines)
        {
            Add(title, 14, Ui.Text, bold: true);
            Add(hint, 11.5f, Ui.Dim);
            var bw = (int)(84 * k);
            var box = new TextBox
            {
                Text = code.Replace("\n", "\r\n"), ReadOnly = true, Multiline = true, WordWrap = true, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Ui.Card, ForeColor = Ui.Text, Font = new Font("Consolas", 12 * k, GraphicsUnit.Pixel),
                Location = new Point(x, y), Size = new Size(w - bw - (int)(8 * k), (int)((lines * 16 + 8) * k)),
                ScrollBars = ScrollBars.Vertical,
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
    }

    protected override void OnFormClosed(FormClosedEventArgs e) { open = null; base.OnFormClosed(e); }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
