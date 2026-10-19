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
/// (GifWriter). Only the files already on disk are used, so blocked apps stay blurred; nothing is captured again.
static class ClipExporter
{
    /// MF_MT_DEFAULT_STRIDE: rows come top to bottom.
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    /// Write `plan` to `path`. Reports progress from 0 to 1. The file is only there when this finishes without
    /// an exception; a cancelled or failed export leaves nothing behind.
    public static async Task Export(ClipPlan.Result plan, ClipFormat format, string path, IProgress<double> progress, CancellationToken ct)
    {
        if (plan.Frames.Count == 0) throw new InvalidOperationException("No screenshots in that range.");
        var first = plan.Frames.Select(f => ImageSize(f.Frame.Path)).FirstOrDefault(s => s != null)
                    ?? throw new InvalidOperationException("The screenshots in that range were deleted.");
        var (w, h) = ClipPlan.OutputSize(first.Width, first.Height, format);
        var tmp = Path.Combine(Path.GetDirectoryName(path) ?? ".", "." + Path.GetFileName(path) + ".part");
        try
        {
            if (format == ClipFormat.Gif) await Task.Run(() => WriteGif(plan.Frames, w, h, tmp, progress, ct), ct);
            else await Task.Run(() => WriteMp4(plan, w, h, tmp, progress, ct), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    static Size? ImageSize(string path)
    {
        try
        {
            using var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)), false, false);
            return img.Size;
        }
        catch { return null; }
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

    static void WriteGif(List<ClipFrame> frames, int w, int h, string path, IProgress<double> progress, CancellationToken ct)
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
            progress.Report((i + 1.0) / frames.Count);
        }
        gif.Finish();
    }

    static void WriteMp4(ClipPlan.Result plan, int w, int h, string path, IProgress<double> progress, CancellationToken ct)
    {
        var samples = ClipPlan.Samples(plan.Frames);
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
            // No sample ends the stream.
            if (ct.IsCancellationRequested || next >= samples.Count) { e.Request.Sample = null; return; }
            var s = samples[next++];
            if (s.Frame.Path != lastPath)
            {
                // A new array each time: the encoder may still hold the last one.
                pixels = Pixels(s.Frame.Path, w, h) ?? pixels ?? new byte[w * h * 4];
                lastPath = s.Frame.Path;
            }
            var sample = MediaStreamSample.CreateFromBuffer(pixels!.AsBuffer(), TimeSpan.FromSeconds(s.At));
            sample.Duration = TimeSpan.FromSeconds(s.Duration);
            e.Request.Sample = sample;
            progress.Report((double)next / samples.Count);
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)w;
        profile.Video.Height = (uint)h;
        profile.Video.FrameRate.Numerator = 30;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        // Screens are mostly still: a modest bitrate keeps text sharp without big files.
        profile.Video.Bitrate = (uint)Math.Clamp((long)w * h * 2, 1_000_000, 6_000_000);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        using var stream = FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways)
            .AsTask(ct).GetAwaiter().GetResult();
        var prepared = transcoder.PrepareMediaStreamSourceTranscodeAsync(source, stream, profile).AsTask(ct).GetAwaiter().GetResult();
        if (!prepared.CanTranscode) throw new InvalidOperationException($"Windows can't make this video ({prepared.FailureReason}).");
        prepared.TranscodeAsync().AsTask(ct).GetAwaiter().GetResult();
    }
}

/// Export Clip…: pick the range, MP4 or GIF and a speed; writes the file with progress and Cancel.
sealed class ExportClipForm : Form
{
    readonly TimelineModel model;
    readonly DateTimePicker from, to;
    readonly ComboBox format, speed;
    readonly Label info, status;
    readonly ProgressBar progress;
    readonly Button export, close;
    readonly LinkLabel show;
    CancellationTokenSource? running;
    string? saved;

    const string TimeFormat = "MMM d   h:mm:ss tt";

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
            Value = utc.ToLocalTime(), Width = 220, Margin = new Padding(0, 3, 0, 3),
        };
        from = Picker(startUtc);
        to = Picker(endUtc);
        format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(0, 3, 0, 3) };
        format.Items.AddRange(new object[] { "MP4 video", "Animated GIF" });
        format.SelectedIndex = 0;
        speed = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220, Margin = new Padding(0, 3, 0, 3) };
        foreach (var s in ClipPlan.Speeds) speed.Items.Add(ClipPlan.SpeedLabel(s));
        speed.SelectedIndex = 0;

        grid.Controls.Add(Caption("From"), 0, 0); grid.Controls.Add(from, 1, 0);
        grid.Controls.Add(Caption("To"), 0, 1); grid.Controls.Add(to, 1, 1);
        grid.Controls.Add(Caption("Format"), 0, 2); grid.Controls.Add(format, 1, 2);
        grid.Controls.Add(Caption("Speed"), 0, 3); grid.Controls.Add(speed, 1, 3);

        info = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 10, 0, 4) };
        progress = new ProgressBar { Width = 360, Height = 14, Visible = false, Margin = new Padding(0, 6, 0, 2), Maximum = 1000 };
        status = new Label { AutoSize = true, MaximumSize = new Size(360, 0), Margin = new Padding(0, 2, 0, 2) };
        show = new LinkLabel { Text = "Show in folder", AutoSize = true, Visible = false, Margin = new Padding(0, 2, 0, 2) };
        show.LinkClicked += (_, _) =>
        {
            if (saved == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saved}\""); } catch { }
        };
        export = new Button { Text = "Export…", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
        close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        export.Click += async (_, _) => await Run();
        close.Click += (_, _) => { if (running != null) running.Cancel(); else Close(); };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(close);
        buttons.Controls.Add(export);

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        stack.Controls.AddRange(new Control[] { grid, info, progress, status, show, buttons });
        Controls.Add(stack);
        AcceptButton = export;
        CancelButton = close;

        from.ValueChanged += (_, _) => UpdateInfo();
        to.ValueChanged += (_, _) => UpdateInfo();
        format.SelectedIndexChanged += (_, _) => UpdateInfo();
        speed.SelectedIndexChanged += (_, _) => UpdateInfo();
        UpdateInfo();
    }

    ClipFormat Format => format.SelectedIndex == 1 ? ClipFormat.Gif : ClipFormat.Mp4;
    double Speed => ClipPlan.Speeds[Math.Max(0, speed.SelectedIndex)];
    DateTime Start => from.Value.ToUniversalTime();
    DateTime End => to.Value.ToUniversalTime();

    /// The screenshots in the range, as the timeline shows them (with its app filter), and what the clip will be.
    (ClipPlan.Result? Plan, string? Problem) Plan()
    {
        if (End <= Start) return (null, "The end is before the start.");
        if (End - Start > ClipPlan.MaxLength) return (null, $"A clip can cover at most {ClipPlan.MaxLength.TotalHours:0} hours.");
        var frames = AppFilter.Apply(model.Db.TimelineFrames(Start), model.AppFilterName);
        var list = Blocklist.Current;
        var plan = ClipPlan.Make(frames, Start, End, Speed, Format, f => ClipPlan.Hides(list, f));
        if (plan.Frames.Count == 0)
            return (null, plan.Blocked > 0 ? "Every screenshot in that range is from a blocked app or site." : "No screenshots in that range.");
        return (plan, null);
    }

    void UpdateInfo()
    {
        if (running != null) return;
        var (plan, problem) = Plan();
        export.Enabled = plan != null;
        if (plan == null) { info.Text = problem; return; }
        var text = $"{plan.Frames.Count} picture{(plan.Frames.Count == 1 ? "" : "s")} · {ClipPlan.Length(plan.RealSeconds)} of screen time → a {ClipPlan.Length(plan.Duration)} clip";
        if (model.AppFilterName is string app) text += $"\nOnly {app}, as the timeline shows it.";
        if (plan.Blocked > 0) text += $"\n{plan.Blocked} screenshot{(plan.Blocked == 1 ? " is" : "s are")} from blocked apps or sites and left out.";
        info.Text = text;
    }

    async Task Run()
    {
        var (plan, problem) = Plan();
        if (plan == null) { info.Text = problem; return; }
        var ext = Format == ClipFormat.Gif ? "gif" : "mp4";
        using (var dialog = new SaveFileDialog
        {
            Title = "Export Clip",
            Filter = Format == ClipFormat.Gif ? "Animated GIF|*.gif" : "MP4 video|*.mp4",
            FileName = ClipPlan.FileName(from.Value, ext),
            InitialDirectory = Environment.GetFolderPath(Format == ClipFormat.Gif ? Environment.SpecialFolder.MyPictures : Environment.SpecialFolder.MyVideos),
            OverwritePrompt = true,
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            saved = dialog.FileName;
        }
        running = new CancellationTokenSource();
        foreach (var c in new Control[] { from, to, format, speed, export }) c.Enabled = false;
        close.Text = "Cancel";
        progress.Value = 0;
        progress.Visible = true;
        show.Visible = false;
        status.Text = "Exporting…";
        var path = saved;
        var report = new Progress<double>(p => progress.Value = Math.Clamp((int)(p * 1000), 0, 1000));
        try
        {
            await ClipExporter.Export(plan, Format, path, report, running.Token);
            status.Text = $"Saved {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024.0 / 1024:0.0} MB).";
            show.Visible = true;
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
            running.Dispose();
            running = null;
            progress.Visible = false;
            foreach (var c in new Control[] { from, to, format, speed, export }) c.Enabled = true;
            close.Text = "Close";
            UpdateInfo();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing while it runs cancels; the form closes once the export has stopped.
        if (running != null) { running.Cancel(); e.Cancel = true; return; }
        base.OnFormClosing(e);
    }
}
