using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenRewind;

/// Same flow as the Mac app (Updater.swift): ask GitHub for the latest release, download the zip for
/// this PC (by itself when "Download Updates Automatically" is on), and when you click Update swap
/// OpenRewind.exe in place and restart. Nothing is installed without a click.
/// Everything here runs on the UI thread; the awaits come back to it.
sealed class Updater : IDisposable
{
    public enum Phase { Idle, Downloading, Ready, Installing, Failed }

    readonly Settings settings;
    readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 6 * 3600 * 1000 };
    DateTime? lastCheck;
    bool installWhenReady;
    string? readyZip;

    public ReleaseInfo? Latest { get; private set; }
    public Phase State { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    /// Quits this copy once the new exe is in place and started.
    public Action? QuitApp;

    /// "0.2.2" (the build script passes -p:Version).
    public static string Current { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];
    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    public static string AssetName => $"OpenRewind-windows-{Arch}.zip";
    static string Exe => Environment.ProcessPath ?? "";
    static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenRewind", "Updates");
    /// A published single-file build. `dotnet run` builds do not update themselves.
    static bool IsRelease => string.IsNullOrEmpty(typeof(Updater).Assembly.Location);

    public Updater(Settings settings)
    {
        this.settings = settings;
        timer.Tick += (_, _) => Check();
    }

    /// The latest release, when it is newer than this app.
    public ReleaseInfo? Available => Latest is ReleaseInfo r && UpdateCheck.IsNewer(r.Version, Current) ? r : null;

    public bool ShowBanner => Available is ReleaseInfo r && r.Version != settings.DismissedUpdateVersion;

    /// Swapping the exe needs a folder we can write (not Program Files without admin).
    public static bool CanInstall
    {
        get
        {
            try
            {
                if (!IsRelease || Path.GetDirectoryName(Exe) is not string dir || dir.Length == 0) return false;
                var probe = Path.Combine(dir, $".openrewind-write-test-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
    }

    public bool AutoDownload
    {
        get => settings.AutoDownloadUpdates;
        set
        {
            settings.AutoDownloadUpdates = value;
            settings.Save();
            if (value) DownloadIfWanted();
        }
    }

    // MARK: checking

    /// Check soon after launch, then every 6 hours.
    public void Start()
    {
        if (!IsRelease) return;
        var first = new System.Windows.Forms.Timer { Interval = 15_000 };
        first.Tick += (_, _) => { first.Dispose(); Check(); };
        first.Start();
        timer.Start();
    }

    /// Opening the timeline checks again when the last check is over an hour old.
    public void CheckIfStale()
    {
        if (!IsRelease || (lastCheck is DateTime t && DateTime.UtcNow - t < TimeSpan.FromHours(1))) return;
        Check();
    }

    /// Ask GitHub for the latest release. `done` gets an error message, or null when the check worked.
    public async void Check(Action<string?>? done = null)
    {
        lastCheck = DateTime.UtcNow;
        try
        {
            var r = await UpdateCheck.FetchLatest(http, AssetName, Current);
            if (r != Latest)
            {
                Log.Write($"latest release {r.Version}, this app is {Current}");
                Latest = r;
                Changed?.Invoke();
            }
            DownloadIfWanted();
            done?.Invoke(null);
        }
        catch (Exception e)
        {
            Log.Write($"update check failed: {e.Message}");
            done?.Invoke(e.Message);
        }
    }

    // MARK: banner actions

    /// The banner's Update button: download if needed, then install. Without a writable app folder,
    /// open the download in the browser instead.
    public void Update()
    {
        if (Available is not ReleaseInfo r) return;
        if (!CanInstall) { Open(r.AssetUrl); return; }
        if (State == Phase.Ready) { Install(); return; }
        installWhenReady = true;
        Download();
    }

    public void Dismiss()
    {
        settings.DismissedUpdateVersion = Available?.Version;
        settings.Save();
        Changed?.Invoke();
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"cannot open {url}: {e.Message}"); }
    }

    // MARK: downloading

    void DownloadIfWanted()
    {
        if (AutoDownload && Available != null && CanInstall) Download();
    }

    async void Download()
    {
        if (Available is not ReleaseInfo r || State is Phase.Downloading or Phase.Installing) return;
        var dest = Path.Combine(CacheDir, $"OpenRewind-{r.Version}-{Arch}.zip");
        if (State == Phase.Ready && readyZip == dest) { if (installWhenReady) Install(); return; }
        if (!(File.Exists(dest) && UpdateCheck.Verify(dest, r.Digest)))
        {
            Progress = 0;
            Set(Phase.Downloading);
            try
            {
                Directory.CreateDirectory(CacheDir);
                var part = dest + ".part";
                using (var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode}");
                    var total = resp.Content.Headers.ContentLength ?? 0;
                    await using var src = await resp.Content.ReadAsStreamAsync();
                    await using var dst = File.Create(part);
                    var buf = new byte[1 << 16];
                    long got = 0;
                    int n, shown = 0;
                    while ((n = await src.ReadAsync(buf)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n));
                        got += n;
                        if (total > 0 && (int)(got * 100 / total) != shown)
                        {
                            shown = (int)(got * 100 / total);
                            Progress = got / (double)total;
                            Changed?.Invoke();
                        }
                    }
                }
                if (!UpdateCheck.Verify(part, r.Digest)) { File.Delete(part); throw new IOException("the download was damaged"); }
                File.Move(part, dest, true);
            }
            catch (Exception e) { Fail($"download failed: {e.Message}"); return; }
        }
        Log.Write($"downloaded {Path.GetFileName(dest)}");
        readyZip = dest;
        Set(Phase.Ready);
        if (installWhenReady) Install();
    }

    void Set(Phase p)
    {
        State = p;
        if (p != Phase.Failed) Error = null;
        Changed?.Invoke();
    }

    void Fail(string why)
    {
        Log.Write($"update {why}");
        installWhenReady = false;
        Error = why;
        Set(Phase.Failed);
    }

    // MARK: installing

    /// A running exe cannot be overwritten but can be renamed: move it aside as OpenRewind.exe.old,
    /// put the new one in its place, start it, and quit. The new copy waits for this one to exit
    /// (--wait-pid) and deletes the .old file (CleanUp).
    void Install()
    {
        if (State != Phase.Ready || readyZip is not string zip || !CanInstall) return;
        installWhenReady = false;
        Set(Phase.Installing);
        var dir = Path.GetDirectoryName(Exe)!;
        var stage = Path.Combine(dir, ".openrewind-update");
        var old = Exe + ".old";
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            ZipFile.ExtractToDirectory(zip, stage);
            var fresh = Path.Combine(stage, "OpenRewind.exe");
            if (!File.Exists(fresh)) throw new IOException("the download has no OpenRewind.exe");
            if (File.Exists(old)) File.Delete(old);
            File.Move(Exe, old);
            try { File.Move(fresh, Exe); }
            catch { File.Move(old, Exe); throw; }
            // README and LICENSE ride along.
            foreach (var f in Directory.GetFiles(stage)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
            Directory.Delete(stage, true);
            File.Delete(zip);
        }
        catch (Exception e)
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            Fail($"install failed: {e.Message}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(Exe, $"--wait-pid={Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception e)
        {
            Fail($"the new version is in place but did not start ({e.Message}); start OpenRewind again");
            return;
        }
        Log.Write($"installed {Path.GetFileName(zip)}; restarting");
        QuitApp?.Invoke();
    }

    /// Run by the new copy at startup: wait for the old one to quit, then remove what it left behind.
    public static void CleanUp(string[] args)
    {
        if (args.FirstOrDefault(a => a.StartsWith("--wait-pid=")) is string w && int.TryParse(w["--wait-pid=".Length..], out var pid))
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(15_000); }
            catch (ArgumentException) { }  // already gone
        }
        var old = Exe + ".old";
        for (int i = 0; i < 10 && File.Exists(old); i++)
        {
            try { File.Delete(old); }
            catch { Thread.Sleep(300); }
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        http.Dispose();
    }
}
