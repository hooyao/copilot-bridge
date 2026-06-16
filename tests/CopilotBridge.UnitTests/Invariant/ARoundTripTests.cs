using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Common;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// A-invariant suite (<c>docs/ir-definition-design.md</c> §7.0/§7.3) — asserts
/// MATHEMATICAL PROPERTIES OF OUR OWN translators, using the committed
/// <c>cc-request-*.json</c> captures only as input samples. Nothing here
/// asserts what Copilot currently accepts — that is the B-contract suite's job
/// (change 2). These hold no matter how Copilot evolves.
///
/// For Claude Code the inbound adapter is identity (Anthropic shape == IR), so
/// the "round trip" is parse→serialize through the exact source-gen path the
/// hot-path strategy uses. A1 asserts that is self-inverse under the §7.1
/// fidelity bar; A3/A4 assert the new provider-extensions bag survives and
/// carries un-modeled knobs; A2 asserts opaque fields are byte-identical.
/// </summary>
public class ARoundTripTests
{
    private readonly ITestOutputHelper _output;

    public ARoundTripTests(ITestOutputHelper output) => _output = output;

    public static IEnumerable<object[]> Fixtures() =>
        IrRoundTrip.AllFixtureSlugs().Select(s => new object[] { s });

    // ── A1: round-trip self-inverse on every real capture ────────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A1_RoundTripIsSelfInverse(string slug)
    {
        var inbound = IrRoundTrip.LoadFixtureBodyJson(slug);
        var original = IrRoundTrip.ParseNode(inbound);
        var roundTripped = IrRoundTrip.RoundTripNode(inbound);

        var harness = FieldDiffHarness.Default();
        var violations = harness.Violations(original, roundTripped);

        if (violations.Count > 0)
        {
            _output.WriteLine($"=== A1 VIOLATIONS for {slug} ===");
            foreach (var v in violations) _output.WriteLine(v.ToString());
        }
        Assert.True(violations.Count == 0,
            $"{slug}: round trip introduced {violations.Count} fidelity violation(s); see test output.");
    }

    // ── A2: opaque fields byte-identical (raw-text compare, not value compare) ─
    // The 4 baseline captures are single-turn text (no tool_use/thinking blocks
    // in the messages), so the opaque-field carriers tested here are: tool
    // input_schema (JsonElement) on tool DEFINITIONS, and thinking budget. The
    // tool_use.Input / thinking.Signature message-block carriers get their
    // dedicated coverage from A2b once a tool-call capture is harvested. This
    // A2 still proves the JsonElement byte-passthrough rule on real data today.

    [Fact]
    public void A2_ToolInputSchema_ByteIdentical()
    {
        var slug = "haiku45-enabled-thinking-tools";
        var inbound = IrRoundTrip.LoadFixtureBodyJson(slug);

        var original = JsonNode.Parse(inbound)!.AsObject();
        var roundTripped = IrRoundTrip.RoundTripNode(inbound).AsObject();

        var origTools = original["tools"]!.AsArray();
        var rtTools = roundTripped["tools"]!.AsArray();
        Assert.Equal(origTools.Count, rtTools.Count);

        for (var i = 0; i < origTools.Count; i++)
        {
            var oSchema = origTools[i]!["input_schema"];
            var rSchema = rtTools[i]!["input_schema"];
            // input_schema.Properties is held as JsonElement → must survive
            // byte-for-byte (the LiteLLM #1 lesson: reparse must not reorder
            // keys or reformat). Compare the raw serialized text.
            Assert.Equal(
                oSchema?["properties"]?.ToJsonString(),
                rSchema?["properties"]?.ToJsonString());
        }
    }

    // ── A3: bag survival canary (guards Vercel drop-the-bag #5942/#9731) ──────

    [Fact]
    public void A3_BagCanary_SurvivesByteIdentical()
    {
        // Parse a real capture into the IR, inject an unknown provider key with
        // a deliberately nested/awkward value, serialize via the hot-path
        // context, and assert the canary emerges byte-identical.
        var inbound = IrRoundTrip.LoadFixtureBodyJson("plain-opus48");
        var ir = IrRoundTrip.Parse(inbound);

        using var canaryDoc = JsonDocument.Parse(
            """{"__canary__":{"nested":[1,2,{"deep":true}],"s":"keep me"},"store":false}""");
        var withBag = ir with
        {
            ProviderExtensions = new ProviderExtensions
            {
                ByProvider = new Dictionary<string, JsonElement>
                {
                    ["openai"] = canaryDoc.RootElement.Clone(),
                },
            },
        };

        var bytes = IrRoundTrip.SerializeLikeHotPath(withBag);
        var node = JsonNode.Parse(bytes)!.AsObject();

        // The envelope must be present and carry the openai key verbatim.
        var openai = node["provider_extensions"]?["by_provider"]?["openai"];
        Assert.NotNull(openai);
        var canary = openai!["__canary__"];
        Assert.NotNull(canary);
        Assert.Equal("""{"nested":[1,2,{"deep":true}],"s":"keep me"}""", canary!.ToJsonString());
        Assert.False(openai["store"]!.GetValue<bool>());

        // And it must round-trip back into the IR and out again unchanged.
        var reparsed = IrRoundTrip.Parse(Encoding.UTF8.GetString(bytes));
        var reBytes = IrRoundTrip.SerializeLikeHotPath(reparsed);
        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(reBytes));
    }

    // ── A4: bag transports un-modeled Codex/Responses knobs intact ───────────

    [Fact]
    public void A4_UnmodeledKnobs_TransitTheBagIntact()
    {
        // store / include / prompt_cache_key / text.verbosity have NO typed home
        // in the Anthropic IR. They must ride ProviderExtensions["openai"] and
        // reappear intact after a full parse→serialize→parse cycle.
        var inbound = IrRoundTrip.LoadFixtureBodyJson("opus47-adaptive-effort-medium");
        var ir = IrRoundTrip.Parse(inbound);

        const string knobsJson =
            """
            {"store":false,"include":["reasoning.encrypted_content"],"prompt_cache_key":"abc-123","text":{"verbosity":"low"}}
            """;
        using var knobs = JsonDocument.Parse(knobsJson);
        var withKnobs = ir with
        {
            ProviderExtensions = new ProviderExtensions
            {
                ByProvider = new Dictionary<string, JsonElement> { ["openai"] = knobs.RootElement.Clone() },
            },
        };

        // Full cycle through the hot-path serializer and back.
        var bytes = IrRoundTrip.SerializeLikeHotPath(withKnobs);
        var reparsed = IrRoundTrip.Parse(Encoding.UTF8.GetString(bytes));

        var recovered = reparsed.ProviderExtensions!.ByProvider["openai"];
        // Compare normalized JSON (key set + values), independent of key order.
        var expected = JsonNode.Parse(knobsJson)!.ToJsonString();
        var actual = JsonNode.Parse(recovered.GetRawText())!.ToJsonString();
        Assert.Equal(expected, actual);

        // And the IR body itself is unchanged by the bag (the adaptive effort,
        // model, etc. all still there).
        Assert.Equal(ir.Model, reparsed.Model);
        Assert.Equal("medium", reparsed.OutputConfig?.Effort);
    }

    // ── A2b: message-level opaque carriers byte-identical (real tool-call capture) ─
    // The multi-turn captures carry the carriers the single-turn ones lack:
    // tool_use.Input (JsonElement) and thinking.Signature. Both must survive the
    // IR round trip byte-for-byte (raw-text compare) — the LiteLLM #1 + thinking-
    // signature bug classes the IR design exists to avoid.

    [Theory]
    [InlineData("sonnet46-toolcall-thinking-multiturn")]
    [InlineData("opus47-toolcall-multiturn")]
    public void A2b_ToolUseInputAndSignature_ByteIdentical(string slug)
    {
        var inbound = IrRoundTrip.LoadFixtureBodyJson(slug);
        var original = JsonNode.Parse(inbound)!.AsObject();
        var roundTripped = IrRoundTrip.RoundTripNode(inbound).AsObject();

        var origBlocks = AllMessageBlocks(original);
        var rtBlocks = AllMessageBlocks(roundTripped);
        Assert.Equal(origBlocks.Count, rtBlocks.Count);

        var sawToolUse = false;
        var sawThinking = false;
        for (var i = 0; i < origBlocks.Count; i++)
        {
            var (oType, oNode) = origBlocks[i];
            var (rType, rNode) = rtBlocks[i];
            Assert.Equal(oType, rType);

            if (oType == "tool_use")
            {
                sawToolUse = true;
                // input is JsonElement → byte-identical raw text required.
                Assert.Equal(oNode["input"]!.ToJsonString(), rNode["input"]!.ToJsonString());
                // call id must be preserved verbatim (referential integrity anchor).
                Assert.Equal(oNode["id"]!.GetValue<string>(), rNode["id"]!.GetValue<string>());
            }
            else if (oType == "thinking")
            {
                sawThinking = true;
                // signature is an opaque token — any mutation → upstream 400.
                Assert.Equal(oNode["signature"]!.GetValue<string>(), rNode["signature"]!.GetValue<string>());
                Assert.Equal(oNode["thinking"]!.GetValue<string>(), rNode["thinking"]!.GetValue<string>());
            }
        }

        Assert.True(sawToolUse, $"{slug}: expected a tool_use block to exercise A2b.");
        if (slug.Contains("thinking"))
            Assert.True(sawThinking, $"{slug}: expected a thinking block with a signature.");
    }

    // ── A5: tool-call/result pairing survives the multi-turn round trip ──────

    [Theory]
    [InlineData("sonnet46-toolcall-thinking-multiturn")]
    [InlineData("opus47-toolcall-multiturn")]
    public void A5_ToolPairingIntegrity_Survives(string slug)
    {
        var inbound = IrRoundTrip.LoadFixtureBodyJson(slug);
        var roundTripped = IrRoundTrip.RoundTripNode(inbound).AsObject();

        // Collect (in order) the tool_use ids and the tool_result tool_use_ids
        // from the ROUND-TRIPPED body; the linkage and ordering must match the
        // original exactly.
        var origPairs = ToolPairing(JsonNode.Parse(inbound)!.AsObject());
        var rtPairs = ToolPairing(roundTripped);

        Assert.Equal(origPairs.toolUseIds, rtPairs.toolUseIds);
        Assert.Equal(origPairs.toolResultIds, rtPairs.toolResultIds);
        // Every tool_result references a tool_use that exists (closed linkage).
        Assert.All(rtPairs.toolResultIds, id => Assert.Contains(id, rtPairs.toolUseIds));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static List<(string type, JsonObject node)> AllMessageBlocks(JsonObject body)
    {
        var blocks = new List<(string, JsonObject)>();
        foreach (var m in body["messages"]!.AsArray())
        {
            if (m!["content"] is not JsonArray content) continue;
            foreach (var b in content)
            {
                if (b is JsonObject bo && bo["type"]?.GetValue<string>() is { } t)
                    blocks.Add((t, bo));
            }
        }
        return blocks;
    }

    private static (List<string> toolUseIds, List<string> toolResultIds) ToolPairing(JsonObject body)
    {
        var tu = new List<string>();
        var tr = new List<string>();
        foreach (var (type, node) in AllMessageBlocks(body))
        {
            if (type == "tool_use") tu.Add(node["id"]!.GetValue<string>());
            else if (type == "tool_result") tr.Add(node["tool_use_id"]!.GetValue<string>());
        }
        return (tu, tr);
    }
}
