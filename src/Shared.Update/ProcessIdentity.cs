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

/// <summary>Outcome of scanning for another process from one installation.</summary>
internal enum OtherProcessStatus
{
    None,
    Found,
    InspectionFailed,
}

/// <summary>
/// A sibling scan result. Both <see cref="OtherProcessStatus.Found"/> and
/// <see cref="OtherProcessStatus.InspectionFailed"/> block update mutation: an
/// unreadable candidate is uncertainty, never evidence that the install is free.
/// </summary>
internal readonly record struct OtherProcessCheck(OtherProcessStatus Status, int? ProcessId)
{
    public bool BlocksUpdate => Status != OtherProcessStatus.None;
}

/// <summary>Comparison of two already-canonical executable path strings.</summary>
internal enum CanonicalPathComparison
{
    Equal,
    Different,
    /// <summary>The strings differ only by case on macOS, whose target volume may
    /// be case-sensitive or case-insensitive. Treat as unsafe uncertainty.</summary>
    CaseSemanticsUnknown,
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
    /// <summary>
    /// Find another live process whose main module is the same canonical
    /// executable path. The caller may supply the one process expected to be
    /// running (the current bridge in the startup gate, or the recorded parent in
    /// the updater); it is excluded only while PID and start time both match.
    /// </summary>
    /// <remarks>
    /// Positive matches deliberately compare executable paths, never process
    /// names. A matching name is used only to classify an unreadable process as
    /// unsafe uncertainty; it is never selected for termination. Thus readable
    /// independent installations do not collide, while an inaccessible candidate
    /// cannot silently permit mutation.
    /// </remarks>
    public static OtherProcessCheck CheckForOtherProcessAtPath(
        string expectedExePath,
        int? excludedPid = null,
        long? excludedStartTicks = null)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            // If the process table itself cannot be inspected, there is no safe
            // basis for mutating an executable that may still be in use.
            return new OtherProcessCheck(OtherProcessStatus.InspectionFailed, null);
        }

        var expectedProcessName = Path.GetFileNameWithoutExtension(expectedExePath);
        var nameComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        foreach (var process in processes)
        {
            using (process)
            {
                int? processId = null;
                var candidateByName = false;
                try
                {
                    try
                    {
                        processId = process.Id;
                        candidateByName = string.Equals(
                            process.ProcessName, expectedProcessName, nameComparison);
                    }
                    catch
                    {
                        // Without even a name, this process cannot be ruled out as
                        // the managed executable. Fail closed; never select it for
                        // termination.
                        return new OtherProcessCheck(
                            OtherProcessStatus.InspectionFailed, processId);
                    }

                    try
                    {
                        if (process.HasExited)
                        {
                            continue; // only a positively exited process is ignored
                        }
                    }
                    catch
                    {
                        var livenessUncertain = ClassifyLivenessInspectionFailure(
                            candidateByName, processId);
                        if (livenessUncertain.BlocksUpdate)
                        {
                            return livenessUncertain;
                        }
                        continue;
                    }

                    // A PID alone is not an identity: after the original process
                    // exits, the OS may reuse that number for a new same-install
                    // process. Exclude only while PID AND start time still identify
                    // the exact process the caller expects.
                    if (processId == excludedPid
                        && excludedStartTicks is > 0
                        && StartTicks(process) == excludedStartTicks.Value)
                    {
                        continue;
                    }

                    var actualPath = SafeMainModulePath(process);
                    if (actualPath is not null
                        && TryPathsEqual(actualPath, expectedExePath, out var pathsEqual))
                    {
                        if (pathsEqual)
                        {
                            return new OtherProcessCheck(OtherProcessStatus.Found, processId);
                        }
                        continue;
                    }

                    // Only a failed module/canonical-path inspection falls back
                    // to the name as a conservative candidate filter. A readable
                    // different installation was already dismissed above.
                    var uncertain = ClassifyInspectionFailure(candidateByName, processId);
                    if (uncertain.BlocksUpdate)
                    {
                        return uncertain;
                    }
                }
                catch
                {
                    if (candidateByName)
                    {
                        return ClassifyInspectionFailure(candidateByName, processId);
                    }
                }
            }
        }

        return new OtherProcessCheck(OtherProcessStatus.None, null);
    }

    /// <summary>
    /// Policy for a process whose module/path inspection failed. A name-matching
    /// candidate blocks mutation; an unrelated process remains irrelevant. This
    /// never authorizes killing by name.
    /// </summary>
    internal static OtherProcessCheck ClassifyInspectionFailure(
        bool candidateByName, int? processId)
        => candidateByName
            ? new OtherProcessCheck(OtherProcessStatus.InspectionFailed, processId)
            : new OtherProcessCheck(OtherProcessStatus.None, null);

    /// <summary>
    /// A failed liveness probe is not proof that a name-matching candidate exited.
    /// Keep it as unsafe uncertainty; unrelated process names remain irrelevant.
    /// </summary>
    internal static OtherProcessCheck ClassifyLivenessInspectionFailure(
        bool candidateByName, int? processId)
        => ClassifyInspectionFailure(candidateByName, processId);

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
                if (!TryPathsEqual(actual, expectedExePath, out var pathsEqual))
                {
                    return IdentityCheck.InspectionFailed;
                }
                if (!pathsEqual)
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

    /// <summary>
    /// Compare existing executable paths after resolving symlink/junction
    /// components. <see cref="Path.GetFullPath(string)"/> alone is only lexical:
    /// an installation reached through a directory junction could otherwise look
    /// different from the canonical main-module path reported by the OS.
    /// </summary>
    internal static bool PathsEqual(string a, string b)
        => TryPathsEqual(a, b, out var equal)
            ? equal
            : LexicalPathsEqual(a, b);

    private static bool TryPathsEqual(string a, string b, out bool equal)
    {
        try
        {
            var comparison = CompareCanonicalPathStrings(
                CanonicalizeExistingPath(a), CanonicalizeExistingPath(b),
                isWindows: OperatingSystem.IsWindows(),
                isMacOS: OperatingSystem.IsMacOS());
            equal = comparison == CanonicalPathComparison.Equal;
            // A case-only difference on macOS cannot be classified safely from
            // strings alone: APFS can be either case-sensitive or insensitive.
            // Return "inspection failed" so callers block mutation and, crucially,
            // never treat the ambiguity as identity authorization for a kill.
            return comparison != CanonicalPathComparison.CaseSemanticsUnknown;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            equal = LexicalPathsEqual(a, b);
            return false;
        }
    }

    internal static CanonicalPathComparison CompareCanonicalPathStrings(
        string a, string b, bool isWindows, bool isMacOS)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return CanonicalPathComparison.Equal;
        }
        if (isWindows && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalPathComparison.Equal;
        }
        if (isMacOS && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return CanonicalPathComparison.CaseSemanticsUnknown;
        }
        return CanonicalPathComparison.Different;
    }

    private static string CanonicalizeExistingPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var current = root;
        var remainder = fullPath[root.Length..];
        foreach (var component in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            var resolved = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (resolved is not null)
            {
                current = Path.GetFullPath(resolved.FullName);
            }
        }

        return NormalizeWindowsDevicePath(current);
    }

    private static bool LexicalPathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            NormalizeWindowsDevicePath(Path.GetFullPath(a)),
            NormalizeWindowsDevicePath(Path.GetFullPath(b)),
            comparison);
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }
        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path[4..];
        }
        return path;
    }
}
