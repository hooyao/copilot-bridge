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
    public void Dev_build_never_self_updates()
    {
        var result = ReleaseSelector.Select(
            V("0.1.0-dev"),
            [Stable("v9.9.9")],
            allowBeta: true);

        Assert.Null(result);
    }
}
