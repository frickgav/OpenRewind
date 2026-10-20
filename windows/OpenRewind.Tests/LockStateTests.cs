using OpenRewind;
using Xunit;

/// App Lock's state for the MCP server (LockState): it fails closed.
public class LockStateTests : IDisposable
{
    readonly string home = Path.Combine(Path.GetTempPath(), "openrewind-lock-" + Guid.NewGuid());
    readonly DateTime now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    static bool Running(int pid) => pid == 42;

    public void Dispose() { try { Directory.Delete(home, true); } catch { } }

    [Fact]
    public void NoFileOrADamagedOneIsLocked()
    {
        Assert.False(LockState.IsUnlocked(home, 15, now, Running));
        Directory.CreateDirectory(home);
        File.WriteAllText(LockState.PathIn(home), "unlocked banana");
        Assert.False(LockState.IsUnlocked(home, 15, now, Running));
        File.WriteAllText(LockState.PathIn(home), "");
        Assert.False(LockState.IsUnlocked(home, 15, now, Running));
    }

    [Fact]
    public void UnlockedOnlyWhileTheAppRunsAndTheTimeHasNotRunOut()
    {
        LockState.Write(home, now.AddMinutes(-5), 42);
        Assert.True(LockState.IsUnlocked(home, 15, now, Running));
        // Lock Again After passed with nothing in use.
        Assert.False(LockState.IsUnlocked(home, 15, now.AddMinutes(11), Running));
        // "Only when the PC locks".
        Assert.True(LockState.IsUnlocked(home, 0, now.AddHours(5), Running));
        // The app quit or crashed: its process is gone.
        LockState.Write(home, now, 7);
        Assert.False(LockState.IsUnlocked(home, 0, now, Running));
        // Locked again.
        LockState.Write(home, null, 42);
        Assert.Equal("locked", File.ReadAllText(LockState.PathIn(home)));
        Assert.False(LockState.IsUnlocked(home, 0, now, Running));
    }

    [Fact]
    public void ThisProcessCountsAsRunningAndAMadeUpIdDoesNot()
    {
        Assert.True(LockState.ProcessRunning(Environment.ProcessId));
        Assert.False(LockState.ProcessRunning(int.MaxValue));
    }
}
