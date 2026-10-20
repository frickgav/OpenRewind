using System.Diagnostics;

namespace OpenRewind;

/// App Lock's state for the other OpenRewind processes, mainly the MCP server that AI apps start: the
/// app writes a small file in the data folder whenever it locks or is unlocked, and the MCP server reads
/// it for each message. While App Lock is on, AI apps get history only while the app runs and is
/// unlocked. Anything else (no file, a damaged one, the app not running) counts as locked.
/// Same as LockState in AppLock.swift on the Mac.
///
/// File: "locked", or "unlocked <app process id> <last use, UTC ticks>". Like the lock itself, this keeps
/// out someone at the computer, not programs running as you (they could write the file).
public static class LockState
{
    public const string FileName = "app-lock";

    public static string PathIn(string home) => Path.Combine(home, FileName);

    /// Record the app's lock: `lastUsed` (UTC) is when it was unlocked or last used, null = locked.
    public static void Write(string home, DateTime? lastUsed, int pid)
    {
        var text = lastUsed is DateTime t ? $"unlocked {pid} {t.Ticks}" : "locked";
        var path = PathIn(home);
        try
        {
            Directory.CreateDirectory(home);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Write($"cannot save the lock state: {e.Message}"); }
    }

    /// True only when the file says the app is unlocked, that app is still running, and the unlock has
    /// not run out (`minutes` as in Settings → Lock Again After; 0 = until the PC locks).
    public static bool IsUnlocked(string home, int minutes, DateTime nowUtc, Func<int, bool> running)
    {
        try
        {
            var parts = File.ReadAllText(PathIn(home)).Trim().Split(' ');
            if (parts.Length != 3 || parts[0] != "unlocked" || !int.TryParse(parts[1], out var pid) || !long.TryParse(parts[2], out var ticks)) return false;
            if (!running(pid)) return false;
            return minutes <= 0 || nowUtc.Ticks - ticks < TimeSpan.FromMinutes(minutes).Ticks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// The app is still running under that id (an id reused by another program after a crash doesn't count).
    public static bool ProcessRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            using var me = Process.GetCurrentProcess();
            return !p.HasExited && string.Equals(p.ProcessName, me.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    public const string LockedMessage =
        "OpenRewind is locked (App Lock). Open OpenRewind's timeline on this computer to unlock it, then try again.";
}
