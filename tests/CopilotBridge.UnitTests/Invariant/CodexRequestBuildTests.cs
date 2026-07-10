using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// T2 request-build coverage — drives <see cref="ResponsesRequestBuilder.Build"/>
/// directly (internal, visible via <c>InternalsVisibleTo("copilot-unittests")</c>)
/// against the real shipping <see cref="CodexModelProfileCatalog"/>, asserting the
/// EXACT bytes it emits for the probe-derived coercions the A-suite leaves
/// untested:
/// <list type="bullet">
///   <item>per-model <c>reasoning.effort</c> fallback (<c>CoerceEffort</c> →
///         profile <c>DefaultEffort</c> for an unaccepted inbound effort);</item>
///   <item>tool drops in <c>WriteToolsWithDrops</c> (uniform
///         <c>image_generation</c> drop; <c>custom</c> drop for
///         <c>mai-code-1-flash-picker</c>);</item>
///   <item><c>max_output_tokens</c> round-trip (P2's fix);</item>
///   <item><c>service_tier</c> strip and <c>store</c>-only-when-true strip.</item>
/// </list>
/// The IR <see cref="MessagesRequest"/>s are built in-memory (no fixture needed):
/// each carries the minimal <c>OutputConfig.Effort</c> /
/// <c>ProviderExtensions["openai"]</c> bag that exercises one coercion, so the
/// emitted wire value is unambiguous.
/// </summary>
public class CodexRequestBuildTests
{
    // The production catalog (not a hand-built stub) so these assert the real
    // shipping profiles: large = gpt-5.3-codex (none/low/medium/high/xhigh),
    // small = gpt-5-mini (minimal/low/medium/high), flash = small + RejectsCustomTools.
    private static readonly CodexModelProfileCatalog Catalog = new();

    private static JsonObject Emit(MessagesRequest ir) =>
        JsonNode.Parse(ResponsesRequestBuilder.Build(ir, Catalog).Body)!.AsObject();

    /// <summary>Minimal IR carrying just the knobs under test (one user text turn).</summary>
    private static MessagesRequest Ir(string model, string? effort = null, JsonElement? bag = null, int maxTokens = 0)
    {
        ProviderExtensions? ext = bag is { } b
            ? new ProviderExtensions { ByProvider = new Dictionary<string, JsonElement> { ["openai"] = b } }
            : null;
        return new MessagesRequest
        {
            Model = model,
            MaxTokens = maxTokens,
            Messages = [new MessageParam { Role = Role.User, Content = [new TextBlockParam { Text = "hi" }] }],
            OutputConfig = effort is null ? null : new OutputConfig { Effort = effort },
            ProviderExtensions = ext,
        };
    }

    /// <summary>Parse a JSON literal into a detached <see cref="JsonElement"/> for the openai bag.</summary>
    private static JsonElement Bag(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ── CoerceEffort: per-model DefaultEffort fallback, read back off the wire ────
    // Contract (change #2, docs/gpt55-runaway-diagnosis.md): an inbound effort the
    // model's profile does NOT accept is replaced by that profile's deliberate
    // DefaultEffort — NOT a nearest-accepted-neighbor guess. Anthropic's 'max' (no
    // Codex equivalent) is the motivating case: it must land on the profile default
    // (large → xhigh, small → high), never be silently down-clamped to medium.
    // expectedEffort == null means the coercion produced no effort AND (with no
    // reasoning_summary in the bag) Build emits NO reasoning object at all.

    [Theory]
    // large profile (gpt-5.3-codex): accepts none/low/medium/high/xhigh; DefaultEffort=xhigh.
    [InlineData("gpt-5.3-codex", "max", "xhigh")]     // Anthropic max, unaccepted → profile default xhigh (THE #2 fix)
    [InlineData("gpt-5.3-codex", "minimal", "xhigh")] // unaccepted → default (NOT neighbor 'low')
    [InlineData("gpt-5.3-codex", "none", "none")]     // accepted → kept verbatim
    [InlineData("gpt-5.3-codex", "medium", "medium")] // accepted → kept
    [InlineData("gpt-5.3-codex", "xhigh", "xhigh")]   // accepted by large → kept
    // gpt-5.5 specifically — the model from the runaway incident. max → xhigh, never medium.
    [InlineData("gpt-5.5", "max", "xhigh")]
    // xlarge profile (gpt-5.6 codenames): accept none/low/medium/high/xhigh/max,
    // reject minimal; DefaultEffort=xhigh. The distinguishing contract vs large:
    // 'max' is ACCEPTED, so Anthropic's top tier passes through VERBATIM instead of
    // being clamped to xhigh (Gpt56_Effort_ReProbe: max → 200). Only 'minimal' —
    // the one value the set rejects — falls back to the xhigh default.
    [InlineData("gpt-5.6-sol",   "max", "max")]         // ACCEPTED → verbatim (NOT clamped to xhigh like gpt-5.5)
    [InlineData("gpt-5.6-luna",  "max", "max")]
    [InlineData("gpt-5.6-terra", "max", "max")]
    [InlineData("gpt-5.6-sol",   "xhigh", "xhigh")]     // accepted → kept
    [InlineData("gpt-5.6-sol",   "minimal", "xhigh")]   // the only rejected value → profile default xhigh
    [InlineData("gpt-5.6-luna",  "minimal", "xhigh")]   // per-row DefaultEffort guard: NOT max (the documented footgun)
    [InlineData("gpt-5.6-terra", "minimal", "xhigh")]   // per-row DefaultEffort guard: NOT max
    [InlineData("gpt-5.6-sol",   "none", "none")]       // accepted → kept
    // small profile (gpt-5-mini): accepts minimal/low/medium/high; DefaultEffort=high.
    [InlineData("gpt-5-mini", "max", "high")]         // unaccepted → small default high
    [InlineData("gpt-5-mini", "xhigh", "high")]       // unaccepted (small rejects xhigh) → default high
    [InlineData("gpt-5-mini", "none", "high")]        // unaccepted (small rejects none) → default high (NOT dropped)
    [InlineData("gpt-5-mini", "minimal", "minimal")]  // accepted → kept
    [InlineData("gpt-5-mini", "medium", "medium")]    // accepted → kept
    // unknown model → below the fuzzy floor → no profile → passthrough unclamped
    // (a genuinely unrelated id borrows nothing; the router surfaced it elsewhere).
    [InlineData("totally-unknown-model", "minimal", "minimal")]
    [InlineData("totally-unknown-model", "max", "max")]     // no profile → 'max' passes through untouched
    // unknown-but-CLOSE Codex id → GetNearest borrows the nearest profile's default.
    // 'gpt-5.6' is closest to the large profiles (accept xhigh, default xhigh);
    // a close SMALL-family id ('gpt-5-nano' ~ gpt-5-mini) borrows the small default high.
    [InlineData("gpt-5.6", "max", "xhigh")]
    [InlineData("gpt-5-nano", "max", "high")]
    // case-insensitivity: the accepted check is OrdinalIgnoreCase, so an accepted
    // value keeps its original case; an unaccepted value is replaced by the profile
    // default (emitted in the default's own case).
    [InlineData("gpt-5.3-codex", "MEDIUM", "MEDIUM")] // accepted case-insensitively → original case preserved
    [InlineData("gpt-5.3-codex", "MINIMAL", "xhigh")] // unaccepted → profile default xhigh
    public void CoerceEffort_FallsBackToProfileDefault(string model, string inboundEffort, string? expectedEffort)
    {
        var emitted = Emit(Ir(model, effort: inboundEffort));

        if (expectedEffort is null)
        {
            // Dropped effort + no summary → the whole reasoning object is omitted.
            Assert.Null(emitted["reasoning"]);
        }
        else
        {
            Assert.Equal(expectedEffort, emitted["reasoning"]!["effort"]!.GetValue<string>());
        }
    }

    // ── Tool drops: image_generation (uniform) + custom (flash only) ─────────────

    /// <summary>Three-tool bag: a function (sibling), image_generation (uniform drop), custom (flash drop).</summary>
    private static JsonElement ToolsBag() => Bag("""
        {
          "tools": [
            { "type": "function", "name": "shell", "parameters": { "type": "object" } },
            { "type": "image_generation" },
            { "type": "custom", "name": "apply_patch", "format": { "type": "grammar" } }
          ]
        }
        """);

    [Theory]
    [InlineData("gpt-5.3-codex", true, 2)]                 // large: custom kept → function + custom
    [InlineData("mai-code-1-flash-picker", false, 1)]    // flash: custom dropped → function only
    public void WriteToolsWithDrops_DropsImageGen_AndCustomForFlash(string model, bool customKept, int expectedCount)
    {
        var emitted = Emit(Ir(model, bag: ToolsBag()));
        var tools = emitted["tools"]!.AsArray();
        var types = tools.Select(t => t!["type"]!.GetValue<string>()).ToList();

        // image_generation is a uniform 400 → always dropped.
        Assert.DoesNotContain("image_generation", types);
        // The function sibling is always kept, byte-faithfully.
        Assert.Contains("function", types);
        var fn = tools.First(t => t!["type"]!.GetValue<string>() == "function");
        Assert.Equal("shell", fn!["name"]!.GetValue<string>());
        // custom: kept for the large profile, dropped for flash (RejectsCustomTools).
        Assert.Equal(customKept, types.Contains("custom"));

        Assert.Equal(expectedCount, tools.Count);
    }

    // ── max_output_tokens round-trip (P2's fix: emit only when MaxTokens > 0) ────

    [Theory]
    [InlineData(0, false)]      // Codex omits it → IR MaxTokens 0 → no field emitted
    [InlineData(2048, true)]    // a Codex that sends it → IR MaxTokens carries → emitted
    public void MaxOutputTokens_EmittedOnlyWhenPositive(int maxTokens, bool expectPresent)
    {
        var emitted = Emit(Ir("gpt-5.3-codex", maxTokens: maxTokens));

        if (expectPresent)
            Assert.Equal(maxTokens, emitted["max_output_tokens"]!.GetValue<int>());
        else
            Assert.Null(emitted["max_output_tokens"]);
    }

    // ── service_tier strip (uniform 400) — siblings untouched ───────────────────

    [Fact]
    public void ServiceTier_AlwaysStripped_SiblingsKept()
    {
        var bag = Bag("""{ "service_tier": "default", "prompt_cache_key": "abc" }""");
        var emitted = Emit(Ir("gpt-5.3-codex", bag: bag));

        Assert.Null(emitted["service_tier"]);                            // stripped
        Assert.Equal("abc", emitted["prompt_cache_key"]!.GetValue<string>()); // sibling verbatim
    }

    // ── store: stripped only when true (Codex sends false → harmless, keep) ─────

    [Theory]
    [InlineData(true, false)]   // store:true → stripped
    [InlineData(false, true)]   // store:false → kept (value false)
    public void Store_StrippedOnlyWhenTrue(bool storeValue, bool expectKept)
    {
        var storeJson = storeValue ? "true" : "false";
        var bag = Bag($$"""{ "store": {{storeJson}}, "prompt_cache_key": "abc" }""");
        var emitted = Emit(Ir("gpt-5.3-codex", bag: bag));

        Assert.Equal("abc", emitted["prompt_cache_key"]!.GetValue<string>()); // sibling survives either way

        if (expectKept)
        {
            Assert.NotNull(emitted["store"]);
            Assert.False(emitted["store"]!.GetValue<bool>());
        }
        else
        {
            Assert.Null(emitted["store"]);
        }
    }
}
