using System.Diagnostics;

namespace CopilotBridge.Update.Wire;

/// <summary>The tri-state outcome of a process-identity check.</summary>
internal enum IdentityCheck
{
    /// <summary>The exact recorded process is alive.</summary>
    Matched,
    /// <summary>The process is positively gone, or a different process reused the PID.</summary>
    AbsentOrReused,
    /// <summary>Start-time / main-module could not be read (transient access failure).</summary>
    InspectionFailed,
}

/// <summary>
/// Verifies a process's identity before the updater ever terminates it, so a
/// reused PID can never cause the wrong process to be killed. Identity is the
/// tuple (PID still alive, same start time, same main-module executable path).
/// The updater only ever acts on the exact parent recorded in the plan — never a
/// process selected by name.
/// </summary>
internal static class ProcessIdentity
{
    /// <summary>Capture the start-time ticks of a process for later comparison.</summary>
    public static long StartTicks(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            // Access-denied or exited — 0 means "unknown", which never matches a
            // recorded non-zero value, so identity checks fail safe.
            return 0;
        }
    }

    /// <summary>Start-time ticks of the current process.</summary>
    public static long CurrentStartTicks() => StartTicks(Process.GetCurrentProcess());

    /// <summary>
    /// True when a live process with <paramref name="pid"/> exists AND its start
    /// time matches <paramref name="expectedStartTicks"/> AND (when an expected
    /// executable path is given) its main module path matches. Any mismatch,
    /// exit, or access failure returns false.
    /// </summary>
    public static bool Matches(int pid, long expectedStartTicks, string? expectedExePath)
        => Check(pid, expectedStartTicks, expectedExePath) == IdentityCheck.Matched;

    /// <summary>
    /// Tri-state identity check. <see cref="IdentityCheck.Matched"/> = the exact
    /// process is alive; <see cref="IdentityCheck.AbsentOrReused"/> = positively
    /// gone or a different process reused the PID; <see cref="IdentityCheck.InspectionFailed"/>
    /// = we could not read start-time/main-module (a transient access failure).
    /// Callers must treat InspectionFailed as "unknown — do not proceed" rather
    /// than "gone", so a cutover never races a possibly-live parent.
    /// </summary>
    public static IdentityCheck Check(int pid, long expectedStartTicks, string? expectedExePath)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return IdentityCheck.AbsentOrReused; // no such process
        }

        try
        {
            if (process.HasExited)
            {
                return IdentityCheck.AbsentOrReused;
            }

            var ticks = StartTicks(process);
            if (ticks == 0)
            {
                // 0 means "could not read start time" (access denied / raced) —
                // NOT a positive mismatch.
                return IdentityCheck.InspectionFailed;
            }
            if (ticks != expectedStartTicks)
            {
                return IdentityCheck.AbsentOrReused; // PID reused by a different process
            }

            if (!string.IsNullOrEmpty(expectedExePath))
            {
                var actual = SafeMainModulePath(process);
                if (actual is null)
                {
                    return IdentityCheck.InspectionFailed; // could not read the module path
                }
                if (!PathsEqual(actual, expectedExePath))
                {
                    return IdentityCheck.AbsentOrReused;
                }
            }
            return IdentityCheck.Matched;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? SafeMainModulePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        // Conservative containment/identity comparison: case-insensitive only on
        // Windows. On case-sensitive macOS/Linux volumes, a case-folded compare
        // could treat two DIFFERENT executables as the same identity and let the
        // updater terminate the wrong process.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }
}
