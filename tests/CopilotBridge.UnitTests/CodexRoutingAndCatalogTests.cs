using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Codex routing + profile catalog (change 3, tasks 2.1/2.3). Asserts that the
/// six Codex/Responses model ids resolve to the native <c>/responses</c> backend
/// (not the legacy <c>/chat/completions</c>), that <c>Normalize</c> no-ops on
/// them (so the snapshot-keyed lookups hit), and that the
/// <see cref="CodexModelProfileCatalog"/> rows match the live contract snapshot's
/// two inverted effort profiles. CI-safe — pure logic, no network.
/// </summary>
public class CodexRoutingAndCatalogTests
{
    private static readonly CopilotModelRegistry Registry = new();

    public static IEnumerable<object[]> CodexIds() => new[]
    {
        new object[] { "gpt-5.3-codex" },
        new object[] { "gpt-5.4" },
        new object[] { "gpt-5.4-mini" },
        new object[] { "gpt-5.5" },
        new object[] { "gpt-5-mini" },
        new object[] { "mai-code-1-flash-picker" },
    };

    [Theory]
    [MemberData(nameof(CodexIds))]
    public void CodexIds_RouteToResponsesBackend(string id)
    {
        var target = Registry.Resolve(id);
        Assert.NotNull(target);
        Assert.Equal(BackendVendor.CopilotResponses, target!.Vendor);
        Assert.Equal("/responses", target.Endpoint);
        Assert.Equal(id, target.ModelId);
    }

    [Theory]
    [MemberData(nameof(CodexIds))]
    public void Normalize_NoOpsOnCodexIds(string id)
    {
        // The catalog + routing key on the normalized id; if Normalize mutated a
        // Codex id, every snapshot-derived lookup would miss. Codex sends already-
        // dotted slugs with no date suffix, so Normalize must be identity.
        Assert.Equal(id, CopilotModelRegistry.Normalize(id));
    }

    [Fact]
    public void NonCodexGptId_StillRoutesToOpenAiChat()
    {
        // A gpt id NOT in the Codex set keeps the existing CopilotOpenAi routing —
        // the Codex match is an explicit allowlist, not a gpt- prefix takeover.
        var target = Registry.Resolve("gpt-4o");
        Assert.NotNull(target);
        Assert.Equal(BackendVendor.CopilotOpenAi, target!.Vendor);
        Assert.Equal("/chat/completions", target.Endpoint);
    }

    [Fact]
    public void ClaudeIds_Unaffected_StillAnthropic()
    {
        var target = Registry.Resolve("claude-opus-4-8");
        Assert.NotNull(target);
        Assert.Equal(BackendVendor.CopilotAnthropic, target!.Vendor);
        Assert.Equal("/v1/messages", target.Endpoint);
    }

    /// <summary>
    /// claude-sonnet-5 (added in the 2026 reconciliation) routes to the Anthropic
    /// <c>/v1/messages</c> backend like every other claude id, and Normalize is
    /// identity on the bare id (no consecutive digit-pair to merge, no date suffix)
    /// — so the ModelProfileCatalog lookup keyed on the normalized id hits. A
    /// hypothetical dated form strips its 8-digit suffix back to claude-sonnet-5.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-5", "claude-sonnet-5")]
    [InlineData("claude-sonnet-5-20260601", "claude-sonnet-5")]
    public void Sonnet5_NormalizesToCanonical_AndRoutesToAnthropic(string sent, string expected)
    {
        Assert.Equal(expected, CopilotModelRegistry.Normalize(sent));

        var target = Registry.Resolve(sent);
        Assert.NotNull(target);
        Assert.Equal(BackendVendor.CopilotAnthropic, target!.Vendor);
        Assert.Equal("/v1/messages", target.Endpoint);
        Assert.Equal(expected, target.ModelId);
    }

    // ── Catalog rows match the change-2 contract snapshot ────────────────────

    [Theory]
    [InlineData("gpt-5.3-codex", "none,low,medium,high,xhigh", false)]
    [InlineData("gpt-5.4",       "none,low,medium,high,xhigh", false)]
    [InlineData("gpt-5.4-mini",  "none,low,medium,high,xhigh", false)]
    [InlineData("gpt-5.5",       "none,low,medium,high,xhigh", false)]
    [InlineData("gpt-5-mini",    "minimal,low,medium,high",    false)]
    [InlineData("mai-code-1-flash-picker", "minimal,low,medium,high", true)]
    public void Catalog_EffortProfilesMatchSnapshot(string id, string expectedEfforts, bool rejectsCustom)
    {
        var catalog = new CodexModelProfileCatalog();
        var profile = catalog.Get(id);
        Assert.NotNull(profile);
        Assert.Equal(expectedEfforts.Split(','), profile!.AcceptedEfforts);
        Assert.Equal(rejectsCustom, profile.RejectsCustomTools);
    }

    [Fact]
    public void Catalog_HasAllSixModels_AndUniformCoercions()
    {
        var catalog = new CodexModelProfileCatalog();
        Assert.Equal(6, catalog.Count);
        // The two uniform coercions are catalog-level facts (apply to every model).
        Assert.True(CodexModelProfileCatalog.StripsServiceTier);
        Assert.True(CodexModelProfileCatalog.DropsImageGenerationTool);
    }

    [Fact]
    public void Catalog_UnknownModel_ReturnsNull()
    {
        var catalog = new CodexModelProfileCatalog();
        Assert.Null(catalog.Get("gpt-9-imaginary"));
    }

    // ── H1: registering the Codex strategy must not perturb /cc routing ───────
    // The shared StrategyRegistry now holds BOTH the Anthropic passthrough and
    // the Codex/Responses strategy. A claude target must still select Anthropic;
    // a gpt target selects Codex. This is the routing half of the hot-path
    // byte-equality guarantee (the serialization half is change-1's H1).

    [Fact]
    public void H1_SharedRegistry_RoutesByVendor_CcUnaffected()
    {
        var anthropic = new CopilotBridge.Cli.Pipeline.Strategies.Anthropic.CopilotMessagesPassthroughStrategy(
            copilot: null!,
            tracing: Microsoft.Extensions.Options.Options.Create(new CopilotBridge.Cli.Hosting.Options.TracingOptions()),
            log: NullLogger<CopilotBridge.Cli.Pipeline.Strategies.Anthropic.CopilotMessagesPassthroughStrategy>.Instance);
        var codex = new CopilotBridge.Cli.Pipeline.Strategies.Codex.CopilotResponsesStrategy(
            copilot: null!, profiles: new CodexModelProfileCatalog(),
            tracing: Microsoft.Extensions.Options.Options.Create(new CopilotBridge.Cli.Hosting.Options.TracingOptions()),
            log: NullLogger<CopilotBridge.Cli.Pipeline.Strategies.Codex.CopilotResponsesStrategy>.Instance);

        var registry = new CopilotBridge.Cli.Pipeline.Strategies.StrategyRegistry<MessagesRequest>(
            new CopilotBridge.Cli.Pipeline.Strategies.IUpstreamStrategy<MessagesRequest>[] { anthropic, codex });

        // A claude target → the Anthropic passthrough (unchanged by Codex registration).
        var ccTarget = new RouteTarget(BackendVendor.CopilotAnthropic, "/v1/messages", "claude-opus-4.8");
        Assert.Same(anthropic, registry.Resolve(ccTarget));

        // A gpt target → the Codex strategy.
        var codexTarget = new RouteTarget(BackendVendor.CopilotResponses, "/responses", "gpt-5.3-codex");
        Assert.Same(codex, registry.Resolve(codexTarget));
    }
}
