using CopilotBridge.Cli.Update;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="ReleaseSelector"/> from the "Semantic version
/// and channel selection" requirement scenarios. Publication order is expressed
/// by list order to prove it never decides.
/// </summary>
public class ReleaseSelectorTests
{
    private static SemanticVersion V(string s)
    {
        Assert.True(SemanticVersion.TryParse(s, out var v));
        return v;
    }

    private static ReleaseCandidate Stable(string tag) => new(tag, IsDraft: false, IsPreRelease: false);
    private static ReleaseCandidate Pre(string tag) => new(tag, IsDraft: false, IsPreRelease: true);

    [Fact]
    public void Stable_channel_ignores_a_newer_beta()
    {
        var result = ReleaseSelector.Select(
            V("1.0.0"),
            [Stable("v1.0.1"), Pre("v1.1.0-beta.1")],
            allowBeta: false);

        Assert.NotNull(result);
        Assert.Equal("v1.0.1", result!.Candidate.Tag);
    }

    [Fact]
    public void Beta_channel_selects_highest_semantic_version()
    {
        var result = ReleaseSelector.Select(
            V("1.0.0"),
            [Stable("v1.0.1"), Pre("v1.1.0-beta.1")],
            allowBeta: true);

        Assert.NotNull(result);
        Assert.Equal("v1.1.0-beta.1", result!.Candidate.Tag);
    }

    [Fact]
    public void Stable_supersedes_its_prerelease()
    {
        var result = ReleaseSelector.Select(
            V("1.1.0-beta.2"),
            [Stable("v1.1.0")],
            allowBeta: false);

        Assert.NotNull(result);
        Assert.Equal("v1.1.0", result!.Candidate.Tag);
    }

    [Fact]
    public void No_downgrade_from_a_higher_prerelease_line()
    {
        var result = ReleaseSelector.Select(
            V("2.0.0-beta.1"),
            [Stable("v1.9.9")],
            allowBeta: false);

        Assert.Null(result);
    }

    [Fact]
    public void Publication_time_does_not_override_precedence()
    {
        // A "more recently published" lower version (listed last) must lose to the
        // higher one regardless of order.
        var result = ReleaseSelector.Select(
            V("1.0.0"),
            [Stable("v1.5.0"), Stable("v1.2.0"), Stable("v1.1.0")],
            allowBeta: false);

        Assert.NotNull(result);
        Assert.Equal("v1.5.0", result!.Candidate.Tag);
    }

    [Fact]
    public void Current_or_newer_installation_is_not_reinstalled()
    {
        Assert.Null(ReleaseSelector.Select(V("1.5.0"), [Stable("v1.5.0")], allowBeta: false));
        Assert.Null(ReleaseSelector.Select(V("1.6.0"), [Stable("v1.5.0")], allowBeta: false));
    }

    [Fact]
    public void Drafts_and_unparseable_tags_are_never_candidates()
    {
        var result = ReleaseSelector.Select(
            V("1.0.0"),
            [
                new ReleaseCandidate("v2.0.0", IsDraft: true, IsPreRelease: false),
                new ReleaseCandidate("not-a-version", IsDraft: false, IsPreRelease: false),
                Stable("v1.0.1"),
            ],
            allowBeta: false);

        Assert.NotNull(result);
        Assert.Equal("v1.0.1", result!.Candidate.Tag);
    }

    [Fact]
    public void Mislabeled_prerelease_tag_is_excluded_on_the_stable_channel()
    {
        // A maintainer tags v1.1.0-beta.1 but forgets GitHub's "prerelease"
        // checkbox (IsPreRelease: false). The SemVer string is authoritative: a
        // stable-channel user (allowBeta: false) must NOT be offered it, so the
        // stable v1.0.1 wins instead.
        var result = ReleaseSelector.Select(
            V("1.0.0"),
            [Stable("v1.0.1"), new ReleaseCandidate("v1.1.0-beta.1", IsDraft: false, IsPreRelease: false)],
            allowBeta: false);

        Assert.NotNull(result);
        Assert.Equal("v1.0.1", result!.Candidate.Tag);
    }

    [Fact]
    public void Mislabeled_prerelease_tag_is_still_offered_on_the_beta_channel()
    {
        // The same mislabeled build IS eligible when the user opted into betas —
        // the SemVer-prerelease exclusion only applies to the stable channel.
        var result = ReleaseSelector.Select(
            V("1.0.0"),
            [Stable("v1.0.1"), new ReleaseCandidate("v1.1.0-beta.1", IsDraft: false, IsPreRelease: false)],
            allowBeta: true);

        Assert.NotNull(result);
        Assert.Equal("v1.1.0-beta.1", result!.Candidate.Tag);
    }

    [Fact]
    public void Selected_release_reports_prerelease_for_presentation_from_either_signal()
    {
        // 8-3: the channel shown to the user derives from SelectedRelease.IsPreRelease,
        // which must be true when EITHER GitHub's flag OR the SemVer says prerelease —
        // so a beta accepted despite a missed checkbox is never announced as Stable.
        var mislabeled = ReleaseSelector.Select(
            V("1.0.0"),
            [new ReleaseCandidate("v1.1.0-beta.1", IsDraft: false, IsPreRelease: false)],
            allowBeta: true);
        Assert.NotNull(mislabeled);
        Assert.True(mislabeled!.IsPreRelease, "SemVer prerelease must count even if GitHub's flag is false");

        var flagged = ReleaseSelector.Select(
            V("1.0.0"),
            [new ReleaseCandidate("v1.1.0", IsDraft: false, IsPreRelease: true)],
            allowBeta: true);
        Assert.NotNull(flagged);
        Assert.True(flagged!.IsPreRelease, "GitHub's flag must count even if the SemVer is a release");

        var stable = ReleaseSelector.Select(
            V("1.0.0"),
            [Stable("v1.1.0")],
            allowBeta: false);
        Assert.NotNull(stable);
        Assert.False(stable!.IsPreRelease, "a true stable release is not a prerelease");
    }

    [Fact]
    public void Dev_build_never_self_updates()
    {
        var result = ReleaseSelector.Select(
            V("0.1.0-dev"),
            [Stable("v9.9.9")],
            allowBeta: true);

        Assert.Null(result);
    }
}
