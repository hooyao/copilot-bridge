namespace CopilotBridge.Cli.Update;

/// <summary>
/// The precedence-relevant facts about one GitHub release, decoupled from the
/// wire DTO so <see cref="ReleaseSelector"/> is a pure function testable without
/// any HTTP. <see cref="Tag"/> is the raw <c>v&lt;semver&gt;</c> release tag.
/// </summary>
internal sealed record ReleaseCandidate(string Tag, bool IsDraft, bool IsPreRelease);

/// <summary>
/// Chooses the single update target from a set of discovered releases, applying
/// SemVer precedence and the configured stable/prerelease channel. Pure: it
/// makes no network call and does not consider publication order — only
/// semantic precedence decides.
/// </summary>
internal static class ReleaseSelector
{
    /// <summary>
    /// Select the highest eligible release strictly newer than
    /// <paramref name="installed"/>, or <c>null</c> when none qualifies.
    /// </summary>
    /// <param name="installed">The running product version.</param>
    /// <param name="candidates">All discovered releases (any order).</param>
    /// <param name="allowBeta">
    /// When false, only non-prerelease releases are eligible. When true, both
    /// stable and prerelease releases are eligible and compared together.
    /// </param>
    /// <remarks>
    /// A development build (<see cref="SemanticVersion.IsDevBuild"/>) never
    /// self-updates: the method returns <c>null</c> so a release archive can
    /// never clobber a local <c>dotnet run</c> / developer publish directory.
    /// Draft releases and tags that are not valid supported semantic versions
    /// are never candidates. Only a candidate strictly greater than the
    /// installed version (never equal — no reinstall — and never lower — no
    /// downgrade) is returned.
    /// </remarks>
    public static SelectedRelease? Select(
        SemanticVersion installed,
        IReadOnlyList<ReleaseCandidate> candidates,
        bool allowBeta)
    {
        if (installed.IsDevBuild)
        {
            return null;
        }

        SelectedRelease? best = null;
        foreach (var candidate in candidates)
        {
            if (candidate.IsDraft)
            {
                continue;
            }
            if (candidate.IsPreRelease && !allowBeta)
            {
                continue;
            }
            if (!SemanticVersion.TryParse(candidate.Tag, out var version))
            {
                continue;
            }
            if (version.CompareTo(installed) <= 0)
            {
                continue;
            }
            if (best is null || version.CompareTo(best.Version) > 0)
            {
                best = new SelectedRelease(candidate, version);
            }
        }

        return best;
    }
}

/// <summary>The chosen release plus its parsed version.</summary>
internal sealed record SelectedRelease(ReleaseCandidate Candidate, SemanticVersion Version);
