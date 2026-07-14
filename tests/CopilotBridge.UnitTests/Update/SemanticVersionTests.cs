using CopilotBridge.Cli.Update;
using Xunit;

namespace CopilotBridge.UnitTests.Update;

/// <summary>
/// Contract tests for <see cref="SemanticVersion"/> precedence, derived from the
/// SemVer 2.0.0 spec and the "Semantic version and channel selection"
/// requirement — NOT from the implementation. Each asserts an observable
/// ordering the update selector depends on.
/// </summary>
public class SemanticVersionTests
{
    private static SemanticVersion Parse(string s)
    {
        Assert.True(SemanticVersion.TryParse(s, out var v), $"expected '{s}' to parse");
        return v;
    }

    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V0.4.13", 0, 4, 13)]
    [InlineData("1.2.3+build.5", 1, 2, 3)]        // build metadata ignored
    [InlineData("v0.4.14-beta.1+abcdef", 0, 4, 14)]
    public void Parses_core_ignoring_prefix_and_build_metadata(string text, int mj, int mn, int pt)
    {
        var v = Parse(text);
        Assert.Equal(mj, v.Major);
        Assert.Equal(mn, v.Minor);
        Assert.Equal(pt, v.Patch);
    }

    [Theory]
    [InlineData("1.2")]           // too few components
    [InlineData("1.2.3.4")]       // too many
    [InlineData("1.2.x")]         // non-numeric core
    [InlineData("1.2.3-")]        // empty prerelease
    [InlineData("1.2.3-beta_1")]  // illegal identifier char
    [InlineData("99999999999999999999.0.0")] // overflows Int32
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_unparseable_or_overflowing(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("01.2.3")]        // leading-zero major
    [InlineData("1.02.3")]        // leading-zero minor
    [InlineData("1.2.03")]        // leading-zero patch
    [InlineData("1.2.3-01")]      // leading-zero numeric prerelease id
    [InlineData("1.2.3-beta.01")] // leading-zero numeric prerelease id (later)
    [InlineData("1.2.3+")]        // empty build metadata
    [InlineData("1.2.3+bad_meta")] // illegal build-metadata char
    [InlineData("1.2.3+a..b")]    // empty build-metadata identifier
    public void Rejects_semver_2_0_violations(string text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _), $"'{text}' must be rejected");
    }

    [Theory]
    [InlineData("0.1.0")]         // a single zero component is legal
    [InlineData("1.2.3-alpha.0")] // numeric prerelease id "0" is legal
    [InlineData("1.2.3+build.5")] // well-formed build metadata is legal
    [InlineData("1.2.3+21AF26D3")]
    public void Accepts_wellformed_edge_cases(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out _), $"'{text}' must parse");
    }

    [Fact]
    public void Stable_outranks_its_own_prerelease()
    {
        Assert.True(Parse("1.1.0").CompareTo(Parse("1.1.0-beta.2")) > 0);
        Assert.True(Parse("1.1.0-beta.2").CompareTo(Parse("1.1.0")) < 0);
    }

    [Fact]
    public void Numeric_prerelease_ranks_below_alphanumeric_and_orders_numerically()
    {
        // Spec example chain: alpha < alpha.1 < alpha.beta < beta < beta.2 < beta.11 < rc.1 < (stable)
        Assert.True(Parse("1.0.0-alpha").CompareTo(Parse("1.0.0-alpha.1")) < 0);
        Assert.True(Parse("1.0.0-alpha.1").CompareTo(Parse("1.0.0-alpha.beta")) < 0);
        Assert.True(Parse("1.0.0-alpha.beta").CompareTo(Parse("1.0.0-beta")) < 0);
        Assert.True(Parse("1.0.0-beta.2").CompareTo(Parse("1.0.0-beta.11")) < 0); // numeric, not lexical
        Assert.True(Parse("1.0.0-beta.11").CompareTo(Parse("1.0.0-rc.1")) < 0);
        Assert.True(Parse("1.0.0-rc.1").CompareTo(Parse("1.0.0")) < 0);
    }

    [Fact]
    public void Core_precedence_beats_prerelease_precedence()
    {
        Assert.True(Parse("1.0.1").CompareTo(Parse("1.0.0")) > 0);
        Assert.True(Parse("2.0.0-beta.1").CompareTo(Parse("1.9.9")) > 0);
    }

    [Theory]
    [InlineData("0.1.0-dev", true)]
    [InlineData("1.2.3-dev.4", true)]
    [InlineData("1.2.3-DEV", true)]
    [InlineData("1.2.3-beta.1", false)]
    [InlineData("1.2.3", false)]
    public void IsDevBuild_flags_dev_prerelease(string text, bool expected)
    {
        Assert.Equal(expected, Parse(text).IsDevBuild);
    }

    [Fact]
    public void Default_value_never_throws_on_any_member()
    {
        // TryParse hands back default(SemanticVersion) on every failure path; no
        // member may NRE on it (PreRelease coalesces to empty).
        SemanticVersion d = default;
        Assert.False(d.IsPreRelease);
        Assert.False(d.IsDevBuild);
        Assert.Empty(d.PreRelease);
        Assert.Equal(0, d.CompareTo(default));
        _ = d.GetHashCode();
        Assert.Equal("0.0.0", d.ToString());
    }
}
