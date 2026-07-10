using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// <see cref="ModelNameMatcher.FindNearest"/> — the fuzzy "nearest known model"
/// logic behind the catalogs' best-effort fallback. These assert the CONTRACT
/// the router relies on, from the caller's point of view (given an unknown id +
/// the real known-id set, which known id is borrowed, and when is nothing
/// borrowed) — not the internal bigram/Jaccard math. The known-id sets mirror
/// the live catalog so the cases are the ones that actually occur.
/// </summary>
public class ModelNameMatcherTests
{
    // The Anthropic catalog's known ids (post-2026 reconciliation) — the real
    // set the router matches an unknown claude id against.
    private static readonly string[] Anthropic =
    [
        "claude-haiku-4.5", "claude-sonnet-4.5", "claude-sonnet-4.6", "claude-sonnet-5",
        "claude-opus-4.6", "claude-opus-4.7", "claude-opus-4.8",
    ];

    // The Codex/Responses catalog's known ids. Mirrors
    // CodexModelProfileCatalog.BuildDefault() so the fuzzy-match cases exercise
    // the real candidate set (incl. the gpt-5.6 codenames added 2026-07).
    private static readonly string[] Codex =
    [
        "gpt-5.3-codex", "gpt-5.4", "gpt-5.4-mini", "gpt-5.5",
        "gpt-5.6-luna", "gpt-5.6-sol", "gpt-5.6-terra",
        "gpt-5-mini", "mai-code-1-flash-picker",
    ];

    private static string? Nearest(string id, string[] known, out double score) =>
        ModelNameMatcher.FindNearest(id, known, out score);

    // ── The headline case: a newer model borrows its same-family sibling ──────

    [Fact]
    public void NewerSonnet_MatchesSonnetFamily_NotOpusOrHaiku()
    {
        // The whole point of the feature: a build that predates claude-sonnet-6
        // must route it under a sonnet profile, never opus/haiku.
        var match = Nearest("claude-sonnet-6", Anthropic, out var score);
        Assert.NotNull(match);
        Assert.StartsWith("claude-sonnet-", match);
        Assert.True(score >= ModelNameMatcher.DefaultMinSimilarity, $"score {score} below floor");
    }

    [Fact]
    public void NewerOpus_MatchesOpusFamily()
    {
        var match = Nearest("claude-opus-4.9", Anthropic, out _);
        Assert.NotNull(match);
        Assert.StartsWith("claude-opus-", match);
    }

    [Fact]
    public void NewerOpus_MajorBump_StillMatchesOpusFamily()
    {
        // A major-version jump (opus-5) must still land on the opus family, not
        // drift to sonnet/haiku just because the digits changed.
        var match = Nearest("claude-opus-5", Anthropic, out _);
        Assert.NotNull(match);
        Assert.StartsWith("claude-opus-", match);
    }

    [Fact]
    public void NewerHaiku_MatchesHaikuFamily()
    {
        var match = Nearest("claude-haiku-5", Anthropic, out _);
        Assert.NotNull(match);
        Assert.StartsWith("claude-haiku-", match);
    }

    // ── Family-then-version tie-break ─────────────────────────────────────────

    [Fact]
    public void SonnetTie_PrefersHighestVersionInFamily()
    {
        // claude-sonnet-5 is exact, so drop it: a hypothetical id equidistant-ish
        // between sonnet-4.5 and sonnet-4.6 must resolve to the HIGHER version.
        string[] known = ["claude-sonnet-4.5", "claude-sonnet-4.6"];
        var match = Nearest("claude-sonnet-4", known, out _);
        Assert.Equal("claude-sonnet-4.6", match);
    }

    [Fact]
    public void FamilyBeatsRawSimilarity_OnNearTie()
    {
        // Construct a near-tie where the same-family candidate must win even if a
        // cross-family id is a hair closer on raw bigrams. opus-5 shares the
        // 'opus' family token; it must not be pulled to a sonnet/haiku id.
        var match = Nearest("claude-opus-5", Anthropic, out _);
        Assert.NotNull(match);
        Assert.Contains("opus", match);
    }

    // ── Floor: cross-vendor / unrelated ids return null ───────────────────────
    //
    // NOTE on scope: the floor's job is to reject CROSS-VENDOR / structurally
    // unrelated ids. A same-vendor claude-* id — even an odd one — shares the
    // whole "claude-" prefix (many bigrams) with every known claude id, so it
    // clears the floor and gets forwarded under the nearest claude profile. That
    // is BY DESIGN: for a same-family id we'd rather forward and let Copilot be
    // the authority on whether the model truly exists than hard-refuse a model
    // the client legitimately selected. So the below-floor guarantees below are
    // about foreign / empty ids, not same-vendor typos.

    [Fact]
    public void UnrelatedVendor_BelowFloor_ReturnsNull()
    {
        // A totally foreign vendor id shares almost no bigrams with any claude id.
        Assert.Null(Nearest("mistral-large-2", Anthropic, out var score));
        Assert.True(score < ModelNameMatcher.DefaultMinSimilarity, $"score {score} unexpectedly >= floor");
    }

    [Fact]
    public void UnrelatedVendor_Llama_BelowFloor_ReturnsNull()
    {
        Assert.Null(Nearest("llama-3-70b-instruct", Anthropic, out _));
    }

    [Fact]
    public void SameVendorOddId_IsForwarded_ByDesign()
    {
        // Documents the deliberate scope above: an unrecognized claude-* id
        // borrows the nearest claude profile rather than 400ing. If this ever
        // starts returning null, the floor got too aggressive and would block
        // real new Claude models — the exact thing this feature exists to prevent.
        var match = Nearest("claude-sonnet-6", Anthropic, out _);
        Assert.NotNull(match);
        Assert.StartsWith("claude-", match);
    }

    [Fact]
    public void EmptyId_ReturnsNull()
    {
        Assert.Null(Nearest("", Anthropic, out var score));
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void EmptyKnownSet_ReturnsNull()
    {
        Assert.Null(Nearest("claude-sonnet-6", Array.Empty<string>(), out _));
    }

    // ── Exact-ish and score sanity ────────────────────────────────────────────

    [Fact]
    public void ExactId_ScoresOne_AndReturnsItself()
    {
        var match = Nearest("claude-opus-4.8", Anthropic, out var score);
        Assert.Equal("claude-opus-4.8", match);
        Assert.Equal(1.0, score, 3);
    }

    [Fact]
    public void CaseInsensitive_MatchesRegardlessOfCasing()
    {
        var match = Nearest("CLAUDE-SONNET-6", Anthropic, out _);
        Assert.NotNull(match);
        Assert.StartsWith("claude-sonnet-", match);
    }

    // ── Codex / gpt side ──────────────────────────────────────────────────────

    [Fact]
    public void NewerGpt_MatchesGptFamily()
    {
        var match = Nearest("gpt-6", Codex, out var score);
        Assert.NotNull(match);
        Assert.StartsWith("gpt-", match);
        Assert.True(score >= ModelNameMatcher.DefaultMinSimilarity, $"score {score} below floor");
    }

    [Fact]
    public void NewerGptMinor_MatchesGptFamily()
    {
        var match = Nearest("gpt-5.6", Codex, out _);
        Assert.NotNull(match);
        Assert.StartsWith("gpt-", match);
    }

    [Fact]
    public void UnknownGpt56Variant_MatchesAGpt56Sibling()
    {
        // An unknown gpt-5.6 variant (a new codename Copilot might ship) must land
        // on one of the real gpt-5.6 profiles — NOT on gpt-5.5 (large) — so it
        // borrows the xlarge contract (accepts `max`) rather than clamping max to
        // xhigh. This is the load-bearing contract; WHICH sibling wins the tie is
        // an implementation detail (family-then-version-then-ordinal), so we assert
        // the family prefix, not a specific id.
        var match = Nearest("gpt-5.6-nova", Codex, out var score);
        Assert.NotNull(match);
        Assert.StartsWith("gpt-5.6-", match);
        Assert.True(score >= ModelNameMatcher.DefaultMinSimilarity, $"score {score} below floor");
    }

    [Fact]
    public void MaiCodeVariant_MatchesMaiCodeFamily()
    {
        // A renamed mai-code suffix (the exact churn that happened: -internal →
        // -picker) must land back on the mai-code profile, not a gpt one.
        var match = Nearest("mai-code-1-flash-preview", Codex, out _);
        Assert.Equal("mai-code-1-flash-picker", match);
    }

    [Fact]
    public void GptId_DoesNotMatchAnthropicSet()
    {
        // Cross-vendor guard: a gpt id must not fuzzy-match into the claude set.
        Assert.Null(Nearest("gpt-6", Anthropic, out _));
    }

    // ── ParseFamilyVersion (the tie-break primitive) ──────────────────────────

    [Theory]
    [InlineData("claude-sonnet-5", "sonnet", 5, -1)]
    [InlineData("claude-opus-4.8", "opus", 4, 8)]
    [InlineData("claude-haiku-4.5", "haiku", 4, 5)]
    [InlineData("gpt-5.5", "gpt", 5, 5)]
    [InlineData("gpt-5.3-codex", "codex", 5, 3)]   // family = first non-vendor alpha token
    [InlineData("mai-code-1-flash-picker", "code", 1, -1)]
    public void ParseFamilyVersion_ExtractsFamilyAndVersion(string id, string family, int major, int minor)
    {
        var (f, mj, mn) = ModelNameMatcher.ParseFamilyVersion(id);
        Assert.Equal(family, f);
        Assert.Equal(major, mj);
        Assert.Equal(minor, mn);
    }
}
