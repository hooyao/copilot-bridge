using System.Diagnostics;
using CopilotBridge.Update.Wire;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="InstallationLock"/> ("Single installation
/// transaction") and <see cref="ProcessIdentity"/> ("...verify the recorded PID
/// still identifies the initiating bridge ... never by name"). These are the
/// concurrency and never-kill-the-wrong-process guards.
/// </summary>
public class InstallationLockAndIdentityTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "cb-lock-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Second_acquire_on_same_install_is_rejected_then_reacquirable()
    {
        var install = TempDir();
        var lockRoot = TempDir();
        Directory.CreateDirectory(install);

        var first = InstallationLock.TryAcquire(install, lockRoot);
        Assert.NotNull(first);

        // A concurrent transaction against the same install must fail to acquire.
        var second = InstallationLock.TryAcquire(install, lockRoot);
        Assert.Null(second);

        // After release, it is reacquirable — a stale path never blocks forever.
        first!.Dispose();
        var third = InstallationLock.TryAcquire(install, lockRoot);
        Assert.NotNull(third);
        third!.Dispose();
    }

    [Fact]
    public void Different_install_dirs_do_not_collide()
    {
        var lockRoot = TempDir();
        var a = Path.Combine(TempDir(), "a");
        var b = Path.Combine(TempDir(), "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        using var la = InstallationLock.TryAcquire(a, lockRoot);
        using var lb = InstallationLock.TryAcquire(b, lockRoot);
        Assert.NotNull(la);
        Assert.NotNull(lb); // distinct install → distinct lock key
    }

    [Fact]
    public void Current_process_matches_its_own_identity()
    {
        using var self = Process.GetCurrentProcess();
        var ticks = ProcessIdentity.CurrentStartTicks();
        Assert.True(ProcessIdentity.Matches(self.Id, ticks, expectedExePath: null));
    }

    [Fact]
    public void Wrong_start_ticks_do_not_match_a_reused_pid()
    {
        using var self = Process.GetCurrentProcess();
        // Same live PID but a different start time = a reused PID, must not match.
        Assert.False(ProcessIdentity.Matches(self.Id, expectedStartTicks: 1, expectedExePath: null));
    }

    [Fact]
    public void Nonexistent_pid_does_not_match()
    {
        // A PID that is (almost certainly) not a live process.
        Assert.False(ProcessIdentity.Matches(999_999_999, expectedStartTicks: 123, expectedExePath: null));
    }

    [Fact]
    public void Wrong_executable_path_does_not_match()
    {
        using var self = Process.GetCurrentProcess();
        var ticks = ProcessIdentity.CurrentStartTicks();
        var bogus = OperatingSystem.IsWindows()
            ? @"C:\definitely\not\me.exe"
            : "/definitely/not/me";
        Assert.False(ProcessIdentity.Matches(self.Id, ticks, bogus));
    }

    [Fact]
    public void Check_is_tri_state()
    {
        using var self = Process.GetCurrentProcess();
        var ticks = ProcessIdentity.CurrentStartTicks();

        // Alive + correct identity → Matched.
        Assert.Equal(IdentityCheck.Matched, ProcessIdentity.Check(self.Id, ticks, expectedExePath: null));
        // Reused PID (wrong start time) → AbsentOrReused, NOT InspectionFailed.
        Assert.Equal(IdentityCheck.AbsentOrReused, ProcessIdentity.Check(self.Id, expectedStartTicks: 1, expectedExePath: null));
        // No such process → AbsentOrReused.
        Assert.Equal(IdentityCheck.AbsentOrReused, ProcessIdentity.Check(999_999_999, expectedStartTicks: 123, expectedExePath: null));
    }
}
