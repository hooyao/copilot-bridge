namespace CopilotBridge.Update.Wire;

/// <summary>
/// Path-confinement helpers shared by the CLI (when building a plan) and the
/// updater (when validating one). Every managed target must resolve inside the
/// canonical installation root, and every extracted entry must resolve inside
/// its staging root — release-derived names must never become arbitrary paths.
/// </summary>
internal static class UpdatePaths
{
    /// <summary>
    /// True when <paramref name="candidate"/>, fully resolved, is
    /// <paramref name="root"/> itself or a descendant of it. Uses the OS path
    /// comparison (case-insensitive on Windows/macOS, case-sensitive on Linux).
    /// </summary>
    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = AppendSeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        var fullCandidateDir = AppendSeparator(fullCandidate);

        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // candidate == root, or candidate is strictly under root/.
        return fullCandidateDir.Equals(fullRoot, comparison)
            || fullCandidate.StartsWith(fullRoot, comparison);
    }

    /// <summary>
    /// Resolve <paramref name="relativeEntry"/> against <paramref name="root"/>
    /// and return the full path only when it stays inside root. Rejects absolute
    /// entries, <c>..</c> traversal, and mixed-separator escapes. Returns null
    /// on any violation.
    /// </summary>
    public static string? ResolveContained(string root, string relativeEntry)
    {
        if (string.IsNullOrEmpty(relativeEntry))
        {
            return null;
        }

        // Normalize both separators so a '\' inside a tar entry can't dodge the
        // '/'-based checks on Windows, and vice versa.
        var normalized = relativeEntry.Replace('\\', '/');

        // Reject rooted/absolute entries outright (e.g. "/etc/x", "C:/x").
        if (normalized.StartsWith('/') || Path.IsPathRooted(relativeEntry) || Path.IsPathRooted(normalized))
        {
            return null;
        }

        var combined = Path.Combine(root, normalized);
        var full = Path.GetFullPath(combined);
        return IsInside(root, full) ? full : null;
    }

    private static string AppendSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
