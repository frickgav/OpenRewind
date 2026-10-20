using System.Diagnostics;
using System.Drawing;

namespace OpenRewind;

/// Settings → Transcription Model, for a model that is not on this PC yet: says how big it is and
/// where it comes from, downloads it with a progress bar, and checks it before it is used.
/// Closing the window stops the download; the next try carries on where it stopped.
sealed class ModelDownloadForm : Form
{
    static ModelDownloadForm? open;
    readonly SpeechModel model;
    readonly string home;
    readonly Action done;
    readonly Label status;
    readonly ProgressBar bar;
    readonly PillButton go, cancel;
    CancellationTokenSource? cts;

    /// `done` runs on the UI thread once the model is downloaded and checked.
    public static void ShowFor(SpeechModel model, string home, Action done)
    {
        if (open is { IsDisposed: false })
        {
            if (open.model.Id == model.Id) { open.Activate(); return; }
            if (open.cts != null) { open.Activate(); return; }  // one download at a time
            open.Close();
        }
        open = new ModelDownloadForm(model, home, done);
        open.Show();
        open.Activate();
    }

    ModelDownloadForm(SpeechModel model, string home, Action done)
    {
        this.model = model; this.home = home; this.done = done;
        var k = DeviceDpi / 96f;
        Text = $"Download {model.Name}";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Ui.Bg;
        ForeColor = Ui.Text;
        Font = Ui.F(14 * k);
        ClientSize = new Size((int)(520 * k), 100);

        int x = (int)(24 * k), w = ClientSize.Width - 2 * x, y = (int)(20 * k);
        Label Add(string text, float size, Color color, bool bold = false)
        {
            var font = Ui.F(size * k, bold);
            var h = TextRenderer.MeasureText(text, font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak).Height + (int)(4 * k);
            var l = new Label { Text = text, Font = font, ForeColor = color, Location = new Point(x, y), Size = new Size(w, h) };
            Controls.Add(l);
            y += l.Height + (int)(6 * k);
            return l;
        }

        Add($"Download {model.Name}?", 18, Ui.Text, bold: true);
        Add(model.Detail, 12.5f, Ui.Dim);
        Add($"{SpeechModels.FormatSize(model.Size)} from Hugging Face, saved in {model.Dir(home)}. It runs on this PC: your audio never leaves it. "
            + "Each file is checked against its known fingerprint before it is used.", 12, Ui.Dim);
        if (model.Engine == SpeechEngine.Parakeet) Add(SpeechModels.ParakeetCredit, 11, Ui.Faint);
        if (model.Engine == SpeechEngine.Whisper && Transcriber.FindBinary(null, home) == null)
            Add($"Whisper also needs whisper-cli.exe in {Path.Combine(home, "whisper")} (see the README). NVIDIA Parakeet v3 needs nothing else.", 12, Ui.Warn);
        y += (int)(4 * k);

        bar = new ProgressBar { Location = new Point(x, y), Size = new Size(w, (int)(14 * k)), Maximum = 1000, Visible = false };
        Controls.Add(bar);
        y += bar.Height + (int)(8 * k);
        status = Add("Two lines\nof room for an error", 12, Ui.Dim);
        status.Text = "";

        cancel = new PillButton("Cancel") { Font = Ui.F(13.5f * k, true) };
        cancel.SetBounds(x + w - (int)(250 * k), y + (int)(4 * k), (int)(110 * k), (int)(40 * k));
        cancel.Click += (_, _) => Close();
        go = new PillButton("Download", primary: true) { Font = Ui.F(13.5f * k, true) };
        go.SetBounds(x + w - (int)(130 * k), y + (int)(4 * k), (int)(130 * k), (int)(40 * k));
        go.Click += (_, _) => Start();
        Controls.Add(cancel);
        Controls.Add(go);
        ClientSize = new Size(ClientSize.Width, go.Bottom + (int)(20 * k));
    }

    async void Start()
    {
        if (cts != null) return;
        cts = new CancellationTokenSource();
        go.Enabled = false;
        go.Text = "Downloading";
        bar.Visible = true;
        status.ForeColor = Ui.Dim;
        status.Text = "Starting…";
        var sw = Stopwatch.StartNew();
        long first = -1;
        // Progress<T> reports on this (UI) thread.
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            if (IsDisposed) return;
            if (first < 0) first = p.Done;  // bytes already there from an earlier try
            bar.Value = (int)Math.Clamp(p.Done * 1000 / Math.Max(1, p.Total), 0, 1000);
            var rate = (p.Done - first) / Math.Max(0.5, sw.Elapsed.TotalSeconds);
            var left = rate > 0 && sw.Elapsed.TotalSeconds > 3 ? $" · {SpeechModels.About((p.Total - p.Done) / rate)} left" : "";
            status.Text = $"{SpeechModels.FormatSize(p.Done)} of {SpeechModels.FormatSize(p.Total)}{left}";
        });
        try
        {
            await SpeechModels.Download(model, home, progress, cts.Token);
            status.Text = "Checking the files…";
            var problem = await Task.Run(() => SpeechModels.Problem(model, home));
            if (problem != null) throw new InvalidDataException(problem);
            cts = null;
            Close();
            done();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Write($"downloading {model.Name} failed: {e.Message}");
            cts = null;
            if (IsDisposed) return;
            status.ForeColor = Ui.Warn;
            status.Text = $"The download stopped: {e.Message}";
            go.Text = "Try Again";
            go.Enabled = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cts?.Cancel();
        if (open == this) open = null;
        base.OnFormClosed(e);
    }
}
