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

    [Fact]
    public void Wrong_start_time_does_not_exclude_a_reused_pid_from_sibling_scan()
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("test process path unavailable");

        // Model PID reuse with the current live process: the numeric PID matches
        // the excluded PID, but the supplied start time belongs to a different
        // process, so this executable must still be reported as a conflict.
        var found = ProcessIdentity.CheckForOtherProcessAtPath(
            path, Environment.ProcessId, excludedStartTicks: 1);

        Assert.Equal(OtherProcessStatus.Found, found.Status);
    }

    [Fact]
    public void Unknown_zero_start_time_never_excludes_matching_pid()
    {
        var path = Environment.ProcessPath
            ?? throw new InvalidOperationException("test process path unavailable");

        // StartTicks uses 0 to mean inspection failed/unknown. Treating that as
        // an exact identity would skip a live same-install process solely because
        // both observations were unknown. The scan must keep it as a blocker.
        var found = ProcessIdentity.CheckForOtherProcessAtPath(
            path, Environment.ProcessId, excludedStartTicks: 0);

        Assert.True(found.BlocksUpdate);
        Assert.NotEqual(OtherProcessStatus.None, found.Status);
    }

    [Fact]
    public void Unreadable_name_matching_candidate_blocks_update_without_becoming_kill_identity()
    {
        var result = ProcessIdentity.ClassifyInspectionFailure(
            candidateByName: true, processId: 12345);

        Assert.Equal(OtherProcessStatus.InspectionFailed, result.Status);
        Assert.True(result.BlocksUpdate);
        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public void Failed_liveness_probe_does_not_treat_name_matching_candidate_as_exited()
    {
        var result = ProcessIdentity.ClassifyLivenessInspectionFailure(
            candidateByName: true, processId: 54321);

        Assert.Equal(OtherProcessStatus.InspectionFailed, result.Status);
        Assert.True(result.BlocksUpdate);
        Assert.Equal(54321, result.ProcessId);
    }

    [Fact]
    public void Unreadable_unrelated_process_does_not_block_installation()
    {
        var result = ProcessIdentity.ClassifyInspectionFailure(
            candidateByName: false, processId: 12345);

        Assert.Equal(OtherProcessStatus.None, result.Status);
        Assert.False(result.BlocksUpdate);
        Assert.Null(result.ProcessId);
    }

    [Fact]
    public void Mac_case_only_path_difference_is_unsafe_uncertainty_not_kill_identity()
    {
        var result = ProcessIdentity.CompareCanonicalPathStrings(
            "/Applications/Copilot/copilot-bridge",
            "/applications/copilot/COPILOT-BRIDGE",
            isWindows: false,
            isMacOS: true);

        Assert.Equal(CanonicalPathComparison.CaseSemanticsUnknown, result);
    }

    [Fact]
    public void Case_only_path_difference_remains_distinct_on_case_sensitive_platform()
    {
        var result = ProcessIdentity.CompareCanonicalPathStrings(
            "/opt/Bridge/copilot-bridge",
            "/opt/bridge/copilot-bridge",
            isWindows: false,
            isMacOS: false);

        Assert.Equal(CanonicalPathComparison.Different, result);
    }

    [Fact]
    public void Executable_path_comparison_resolves_directory_symlink_aliases()
    {
        var root = TempDir();
        var realDir = Path.Combine(root, "real");
        var aliasDir = Path.Combine(root, "alias");
        Directory.CreateDirectory(realDir);
        var executable = Path.Combine(realDir, OperatingSystem.IsWindows() ? "bridge.exe" : "bridge");
        File.WriteAllText(executable, "fixture");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(aliasDir, realDir);
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                // Windows requires symlink privilege unless Developer Mode is on.
                // Linux/macOS CI exercise the alias contract unconditionally.
                return;
            }

            Assert.True(ProcessIdentity.PathsEqual(executable, Path.Combine(aliasDir, Path.GetFileName(executable))));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Executable_path_comparison_resolves_windows_junction_aliases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TempDir();
        var realDir = Path.Combine(root, "real");
        var aliasDir = Path.Combine(root, "junction");
        Directory.CreateDirectory(realDir);
        var executable = Path.Combine(realDir, "bridge.exe");
        File.WriteAllText(executable, "fixture");

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(aliasDir);
            start.ArgumentList.Add(realDir);
            using var mklink = Process.Start(start)!;
            mklink.WaitForExit();
            var output = mklink.StandardOutput.ReadToEnd() + mklink.StandardError.ReadToEnd();
            Assert.True(mklink.ExitCode == 0, output);

            Assert.True(ProcessIdentity.PathsEqual(
                executable, Path.Combine(aliasDir, Path.GetFileName(executable))));
        }
        finally
        {
            try { if (Directory.Exists(aliasDir)) Directory.Delete(aliasDir); } catch { /* best effort */ }
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Executable_path_comparison_keeps_distinct_installations_separate()
    {
        var root = TempDir();
        var installA = Path.Combine(root, "a");
        var installB = Path.Combine(root, "b");
        Directory.CreateDirectory(installA);
        Directory.CreateDirectory(installB);
        var name = OperatingSystem.IsWindows() ? "copilot-bridge.exe" : "copilot-bridge";
        var a = Path.Combine(installA, name);
        var b = Path.Combine(installB, name);
        File.WriteAllText(a, "same bytes");
        File.WriteAllText(b, "same bytes");

        try
        {
            Assert.False(ProcessIdentity.PathsEqual(a, b));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
