using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OpenRewind;

/// Writes a clip of saved screenshots to an MP4 (H.264, with the encoder built into Windows) or an animated GIF
/// (GifWriter). Only the files already on disk are used; nothing is captured again.
static class ClipExporter
{
    /// MF_MT_DEFAULT_STRIDE: rows come top to bottom.
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// Write `plan` as a `w`×`h` clip to `path`. Reports how many of the plan's frames are done, about ten times a
    /// second. The file is only there when this finishes without an exception; a cancelled or failed export
    /// leaves nothing behind.
    public static async Task Export(ClipPlan.Result plan, ClipFormat format, int w, int h, string path, IProgress<int> progress, CancellationToken ct)
    {
        if (plan.Frames.Count == 0) throw new InvalidOperationException("No screenshots in that range.");
        var tmp = Path.Combine(Path.GetDirectoryName(path) ?? ".", "." + Path.GetFileName(path) + ".part");
        try
        {
            var report = new Reporter(progress, plan.Frames.Count);
            if (format == ClipFormat.Gif) await Task.Run(() => WriteGif(plan.Frames, w, h, tmp, report, ct), ct);
            // The video stops itself when cancelled (it ends the stream), so the encoder lets go of the file.
            else await Task.Run(() => WriteMp4(plan, w, h, tmp, report, ct));
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            await Delete(tmp);
        }
    }

    /// Delete a file, waiting a little if the video encoder still holds it.
    static async Task Delete(string path)
    {
        for (int i = 0; i < 15 && File.Exists(path); i++)
        {
            try { File.Delete(path); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(200); }
        }
    }

    /// Size of a screenshot file, or null if it can't be read.
    public static Size? ImageSize(string path)
    {
        try
        {
            using var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)), false, false);
            return img.Size;
        }
        catch { return null; }
    }

    /// Passes progress on at most ten times a second (and always the last one), so a long clip does not flood the window.
    sealed class Reporter
    {
        readonly IProgress<int> progress;
        readonly int total;
        readonly Stopwatch clock = Stopwatch.StartNew();
        long last = long.MinValue / 2;
        int lastDone = -1;

        public Reporter(IProgress<int> progress, int total) { this.progress = progress; this.total = total; }

        public void Done(int done)
        {
            lock (this)
            {
                if (done == lastDone || (done < total && clock.ElapsedMilliseconds - last < 100)) return;
                last = clock.ElapsedMilliseconds;
                lastDone = done;
            }
            progress.Report(done);
        }
    }

    /// A screenshot scaled to fit `w`×`h` on black, as BGRA rows top to bottom. Null if the file is gone.
    static byte[]? Pixels(string path, int w, int h)
    {
        try
        {
            using var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = Math.Min((float)w / src.Width, (float)h / src.Height);
                float dw = src.Width * s, dh = src.Height * s;
                g.DrawImage(src, (w - dw) / 2, (h - dh) / 2, dw, dh);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var out_ = new byte[w * h * 4];
                for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, out_, y * w * 4, w * 4);
                return out_;
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
        {
            return null;
        }
    }

    static void WriteGif(List<ClipFrame> frames, int w, int h, string path, Reporter report, CancellationToken ct)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var gif = new GifWriter(file, w, h);
        byte[]? last = null;
        for (int i = 0; i < frames.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // A missing file keeps the picture before it.
            last = Pixels(frames[i].Frame.Path, w, h) ?? last ?? new byte[w * h * 4];
            gif.AddFrame(last, w * 4, frames[i].Duration);
            report.Done(i + 1);
        }
        gif.Finish();
    }

    static void WriteMp4(ClipPlan.Result plan, int w, int h, string path, Reporter report, CancellationToken ct)
    {
        // Each video sample, with the frame of the plan it shows.
        var samples = new List<(ClipFrame Sample, int Frame)>();
        for (int p = 0; p < plan.Frames.Count; p++)
            foreach (var s in ClipPlan.Samples(new[] { plan.Frames[p] })) samples.Add((s, p));
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)w, (uint)h);
        input.Properties[DefaultStride] = (uint)(w * 4);
        var source = new MediaStreamSource(new VideoStreamDescriptor(input))
        {
            BufferTime = TimeSpan.Zero,
            Duration = TimeSpan.FromSeconds(plan.Duration),
            CanSeek = false,
        };
        int next = 0;
        string? lastPath = null;
        byte[]? pixels = null;
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            // No sample ends the stream: at the end, or as soon as the export is cancelled.
            if (ct.IsCancellationRequested || next >= samples.Count) { e.Request.Sample = null; return; }
            var (s, frame) = samples[next++];
            if (s.Frame.Path != lastPath)
            {
                // A new array each time: the encoder may still hold the last one.
                pixels = Pixels(s.Frame.Path, w, h) ?? pixels ?? new byte[w * h * 4];
                lastPath = s.Frame.Path;
            }
            var sample = MediaStreamSample.CreateFromBuffer(pixels!.AsBuffer(), TimeSpan.FromSeconds(s.At));
            sample.Duration = TimeSpan.FromSeconds(s.Duration);
            e.Request.Sample = sample;
            report.Done(next >= samples.Count ? plan.Frames.Count : frame);
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)w;
        profile.Video.Height = (uint)h;
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)ClipPlan.VideoBitrate(w, h);
        // Windows.Media has no keyframe-interval setting; the encoder picks its own (the Mac asks for one every 2 s).

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        using var stream = FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways)
            .AsTask(ct).GetAwaiter().GetResult();
        var prepared = transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile).AsTask(ct).GetAwaiter().GetResult();
        if (!prepared.CanTranscode) throw new InvalidOperationException($"Windows can't make this video ({prepared.FailureReason}).");
        // Not cancelled from outside: a cancel ends the stream (above), and the encoder finishes and lets go of the file.
        prepared.TranscodeAsync().AsTask().GetAwaiter().GetResult();
        ct.ThrowIfCancellationRequested();
    }
}

/// Export Clip…: pick the range, MP4 or GIF and a speed. Before exporting it says how long the clip is, how many
/// frames, and roughly how big and how long it takes; while exporting it shows the frame it is on, the time so far
/// and the time left, with Cancel.
sealed class ExportClipForm : Form
{
    readonly TimelineModel model;
    readonly DateTimePicker from, to;
    readonly ComboBox format, speed;
    readonly Label info, status;
    readonly ProgressBar progress;
    readonly Button export, close;
    readonly FlowLayoutPanel doneLinks;
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer ticker = new() { Interval = 500 };
    readonly Stopwatch clock = new();
    CancellationTokenSource? running;
    bool closeWhenStopped;
    string? saved;
    /// Bumped on every change, so an older count that finishes late is ignored.
    int version;
    int doneFrames, totalFrames;

    const string TimeFormat = "MMM d, yyyy   h:mm:ss tt";

    /// What the clip is made from: the range, format, speed and the timeline's app filter.
    sealed record Inputs(DateTime Start, DateTime End, double Speed, ClipFormat Format, string? App);

    /// The clip for some inputs and its output size, or why there is none.
    sealed record Info(Inputs Inputs, ClipPlan.Result? Plan, int W, int H, string? Problem);

    public ExportClipForm(TimelineModel model, DateTime startUtc, DateTime endUtc)
    {
        this.model = model;
        Text = "Export Clip";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(16);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label Caption(string s) => new() { Text = s, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) };
        DateTimePicker Picker(DateTime utc) => new()
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = TimeFormat, ShowUpDown = true,
            Value = utc.ToLocalTime(), Width = 250, Margin = new Padding(0, 3, 0, 3),
        };
        from = Picker(startUtc);
        to = Picker(endUtc);
        format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Margin = new Padding(0, 3, 0, 3) };
        format.Items.AddRange(new object[] { "MP4 video", "Animated GIF" });
        format.SelectedIndex = 0;
        speed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250, Margin = new Padding(0, 3, 0, 3) };
        foreach (var s in ClipPlan.Speeds) speed.Items.Add(ClipPlan.SpeedLabel(s));
        speed.SelectedIndex = 0;

        grid.Controls.Add(Caption("From"), 0, 0); grid.Controls.Add(from, 1, 0);
        grid.Controls.Add(Caption("To"), 0, 1); grid.Controls.Add(to, 1, 1);
        grid.Controls.Add(Caption("Format"), 0, 2); grid.Controls.Add(format, 1, 2);
        grid.Controls.Add(Caption("Speed"), 0, 3); grid.Controls.Add(speed, 1, 3);

        info = new Label { AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 10, 0, 4), Text = "Counting the frames…" };
        progress = new ProgressBar { Width = 400, Height = 14, Visible = false, Margin = new Padding(0, 6, 0, 2), Maximum = 1000, MarqueeAnimationSpeed = 30 };
        status = new Label { AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 2, 0, 2) };
        LinkLabel Link(string text, Action act)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 2, 16, 2) };
            l.LinkClicked += (_, _) => { if (saved != null) try { act(); } catch (Exception e) { Log.Write($"clip open failed: {e.Message}"); } };
            return l;
        }
        doneLinks = new FlowLayoutPanel { AutoSize = true, Visible = false, Margin = new Padding(0), WrapContents = false };
        doneLinks.Controls.Add(Link("Show in Explorer", () => Process.Start("explorer.exe", $"/select,\"{saved}\"")));
        doneLinks.Controls.Add(Link("Open", () => Process.Start(new ProcessStartInfo(saved!) { UseShellExecute = true })));
        export = new Button { Text = "Export…", AutoSize = true, Margin = new Padding(0, 0, 8, 0), Enabled = false };
        close = new Button { Text = "Close", AutoSize = true };
        export.Click += async (_, _) => await Run();
        close.Click += (_, _) =>
        {
            if (running == null) { Close(); return; }
            running.Cancel();
            close.Enabled = false;
            status.Text = "Cancelling…";
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(export);

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        stack.Controls.AddRange(new Control[] { grid, info, progress, status, doneLinks, buttons });
        Controls.Add(stack);
        AcceptButton = export;
        CancelButton = close;

        debounce.Tick += async (_, _) => { debounce.Stop(); await Count(); };
        ticker.Tick += (_, _) => ShowProgress();
        from.ValueChanged += (_, _) => UpdateInfo();
        to.ValueChanged += (_, _) => UpdateInfo();
        format.SelectedIndexChanged += (_, _) => UpdateInfo();
        speed.SelectedIndexChanged += (_, _) => UpdateInfo();
        UpdateInfo();
    }

    Inputs Snapshot() => new(from.Value.ToUniversalTime(), to.Value.ToUniversalTime(),
        ClipPlan.Speeds[Math.Max(0, speed.SelectedIndex)], format.SelectedIndex == 1 ? ClipFormat.Gif : ClipFormat.Mp4, model.AppFilterName);

    /// The screenshots in the range, as the timeline shows them (with its app filter), and what the clip will be.
    /// Reads only that range of the database; runs off the window's thread.
    static Info Compute(Database db, Inputs i, Blocklist list)
    {
        if (i.End <= i.Start) return new(i, null, 0, 0, "The end is before the start.");
        if (i.End - i.Start > ClipPlan.MaxLength) return new(i, null, 0, 0, $"A clip can cover at most {ClipPlan.MaxLength.TotalHours:0} hours.");
        var frames = AppFilter.Apply(db.TimelineFrames(i.Start, i.End), i.App);
        var plan = ClipPlan.Make(frames, i.Start, i.End, i.Speed, i.Format, f => ClipPlan.Hides(list, f));
        if (plan.Frames.Count == 0)
            return new(i, null, 0, 0, plan.Blocked > 0 ? "Every screenshot in that range is from a blocked app or site." : "No screenshots in that range.");
        if (plan.Frames.Select(f => ClipExporter.ImageSize(f.Frame.Path)).FirstOrDefault(s => s != null) is not Size size)
            return new(i, null, 0, 0, "The screenshots in that range were deleted.");
        var (w, h) = ClipPlan.OutputSize(size.Width, size.Height, i.Format);
        return new(i, plan, w, h, null);
    }

    Task<Info> ComputeInBackground(Inputs i)
    {
        var db = model.Db;
        var list = Blocklist.Current;
        return Task.Run(() => Compute(db, i, list));
    }

    /// Count again a moment after the last change, so stepping through times stays quick.
    void UpdateInfo()
    {
        if (running != null) return;
        version++;
        export.Enabled = false;
        debounce.Stop();
        debounce.Start();
    }

    async Task Count()
    {
        int v = version;
        var x = await ComputeInBackground(Snapshot());
        if (v != version || running != null || IsDisposed) return;
        export.Enabled = x.Plan != null;
        info.Text = Describe(x);
    }

    string Describe(Info x)
    {
        if (x.Plan == null) return x.Problem ?? "";
        var text = ClipPlan.Summary(x.Plan, x.Inputs.Format, x.W, x.H);
        if (x.Inputs.App is string app) text += $"\nOnly {app}, as the timeline shows it.";
        if (x.Plan.Blocked > 0) text += $"\n{x.Plan.Blocked} screenshot{(x.Plan.Blocked == 1 ? " is" : "s are")} from blocked apps or sites and left out.";
        return text;
    }

    /// "Preparing…" until the first frame, then the frame it is on, the time so far and the time left.
    void ShowProgress()
    {
        if (running == null || running.IsCancellationRequested) return;
        var elapsed = clock.Elapsed.TotalSeconds;
        if (totalFrames == 0) { status.Text = $"Preparing… {ClipPlan.Length(elapsed)} elapsed"; return; }
        if (progress.Style != ProgressBarStyle.Continuous && doneFrames > 0) progress.Style = ProgressBarStyle.Continuous;
        progress.Value = Math.Clamp(doneFrames * 1000 / totalFrames, 0, 1000);
        status.Text = ClipPlan.ProgressText(doneFrames, totalFrames, elapsed);
    }

    async Task Run()
    {
        var inputs = Snapshot();
        var ext = inputs.Format == ClipFormat.Gif ? "gif" : "mp4";
        using (var dialog = new SaveFileDialog
        {
            Title = "Export Clip",
            Filter = inputs.Format == ClipFormat.Gif ? "Animated GIF|*.gif" : "MP4 video|*.mp4",
            FileName = ClipPlan.FileName(from.Value, ext),
            InitialDirectory = Environment.GetFolderPath(inputs.Format == ClipFormat.Gif ? Environment.SpecialFolder.MyPictures : Environment.SpecialFolder.MyVideos),
            OverwritePrompt = true,
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            saved = dialog.FileName;
        }
        var path = saved;
        // Something on screen right away, before anything is read.
        running = new CancellationTokenSource();
        foreach (var c in new Control[] { from, to, format, speed, export }) c.Enabled = false;
        close.Text = "Cancel";
        doneLinks.Visible = false;
        progress.Style = ProgressBarStyle.Marquee;
        progress.Visible = true;
        doneFrames = totalFrames = 0;
        clock.Restart();
        ticker.Start();
        ShowProgress();
        var token = running.Token;
        try
        {
            // Counted again, so a block list changed since is applied.
            var x = await ComputeInBackground(inputs);
            if (x.Plan == null) { status.Text = x.Problem; saved = null; return; }
            token.ThrowIfCancellationRequested();
            totalFrames = x.Plan.Frames.Count;
            ShowProgress();
            var report = new Progress<int>(n => { doneFrames = n; ShowProgress(); });
            await ClipExporter.Export(x.Plan, inputs.Format, x.W, x.H, path, report, token);
            status.Text = $"Saved {Path.GetFileName(path)} ({ClipPlan.FileSize(new FileInfo(path).Length)}) in {ClipPlan.Length(clock.Elapsed.TotalSeconds)}.";
            doneLinks.Visible = true;
        }
        catch (OperationCanceledException)
        {
            status.Text = "Cancelled. Nothing was saved.";
            saved = null;
        }
        catch (Exception e)
        {
            Log.Write($"clip export failed: {e}");
            status.Text = $"Could not export the clip: {e.Message}";
            saved = null;
        }
        finally
        {
            ticker.Stop();
            running.Dispose();
            running = null;
            progress.Visible = false;
            foreach (var c in new Control[] { from, to, format, speed }) c.Enabled = true;
            close.Text = "Close";
            close.Enabled = true;
            if (closeWhenStopped) Close();
            else UpdateInfo();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing while it runs cancels, and the window closes once the export has stopped.
        if (running != null)
        {
            closeWhenStopped = true;
            running.Cancel();
            close.Enabled = false;
            status.Text = "Cancelling…";
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        debounce.Dispose();
        ticker.Dispose();
        base.OnFormClosed(e);
    }
}
