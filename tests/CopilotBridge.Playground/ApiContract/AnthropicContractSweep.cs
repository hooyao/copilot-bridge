using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// B1/B2/B3 for the Copilot <c>/v1/messages</c> (Anthropic) backend
/// (`docs/ir-definition-design.md` §7.B). This promotes <see cref="ModelProfileProbe"/>
/// from print-only to ASSERTING: one aggregate sweep hits every (model × axis)
/// cell, builds a structured acceptance-facts object, and then:
///   B1 — asserts each model produced a usable facts row (the live wire spoke);
///   B2 — diffs the live facts against the committed Anthropic contract snapshot
///        and FAILS with a readable diff on any drift (the 2026-06-05
///        "Copilot widened effort" episode would be an automatic red here);
///   B3 — asserts <see cref="ModelProfileCatalog"/> still matches the live facts
///        (each per-model claim — "opus accepts only medium" — confirmed live;
///        a mismatch names the catalog row to reconcile).
///
/// One aggregate test, not a per-cell theory: the snapshot must be built from
/// ALL cells atomically, and a single live run keeps quota cost bounded. Tagged
/// Integration (inherited) — skipped in CI, run on demand / on a drift check.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ModelProfileProbe
{
    internal const string AnthropicSnapshotFile = "copilot-anthropic-contract-snapshot.json";

    // The effort vocabulary the bridge can emit (output_config.effort).
    private static readonly string[] EffortValues = ["low", "medium", "high", "xhigh", "max"];
    // The thinking shapes the bridge can emit.
    private static readonly (string Type, int? Budget)[] ThinkingShapes =
        [("adaptive", null), ("enabled", 8192), ("disabled", null)];

    [Fact]
    public async Task B_AnthropicContract_SweepAssertAndDetectDrift()
    {
        using var client = new PlaygroundClient();
        var models = new JsonObject();

        foreach (var model in AllModels)
        {
            var (effortAccepted, effortRejected) = (new JsonArray(), new JsonArray());
            foreach (var effort in EffortValues)
            {
                var payload =
                    "{\"model\":\"" + model + "\",\"max_tokens\":8,"
                    + "\"messages\":[{\"role\":\"user\",\"content\":\"reply: ok\"}],"
                    + "\"output_config\":{\"effort\":\"" + effort + "\"}}";
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostMessagesAsync(payload), $"{model} effort={effort}");
                if (WireAcceptance.IsAccepted(status, body, $"{model} effort={effort}"))
                    effortAccepted.Add(effort);
                else
                    effortRejected.Add(effort);
            }

            var (thinkingAccepted, thinkingRejected) = (new JsonArray(), new JsonArray());
            foreach (var (type, budget) in ThinkingShapes)
            {
                var thinkingJson = type == "enabled"
                    ? "{\"type\":\"enabled\",\"budget_tokens\":" + budget + "}"
                    : "{\"type\":\"" + type + "\"}";
                var payload =
                    "{\"model\":\"" + model + "\",\"max_tokens\":16384,"
                    + "\"messages\":[{\"role\":\"user\",\"content\":\"reply: ok\"}],"
                    + "\"thinking\":" + thinkingJson + "}";
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostMessagesAsync(payload), $"{model} thinking={type}");
                if (WireAcceptance.IsAccepted(status, body, $"{model} thinking={type}"))
                    thinkingAccepted.Add(type);
                else
                    thinkingRejected.Add(type);
            }

            // Mid-conversation system probe. Use the LEGAL placement U·S
            // (system immediately after a user turn, at array end) — the 4.8
            // rule is "predecessor=user, successor=assistant-or-end-of-array".
            // U·A·S (system after an assistant) is ILLEGAL even on 4.8, so
            // probing that would wrongly record 4.8 as rejecting mid-conv system.
            // This single legal-placement probe is the acceptance fact the
            // catalog's AcceptsMidConversationSystem encodes.
            var midConvPayload =
                "{\"model\":\"" + model + "\",\"max_tokens\":64,"
                + "\"messages\":["
                + "{\"role\":\"user\",\"content\":\"hi\"},"
                + "{\"role\":\"system\",\"content\":\"From now on, respond in pirate-speak.\"}"
                + "]}";
            var (mcStatus, mcBody) = await ProbeRetry.WithRetry(
                () => client.TryPostMessagesAsync(midConvPayload), $"{model} mid-conv-system");
            var midConvAccepted = WireAcceptance.IsAccepted(mcStatus, mcBody, $"{model} mid-conv-system");

            models[model] = new JsonObject
            {
                ["effort"] = new JsonObject
                {
                    ["accepted"] = effortAccepted,
                    ["rejected"] = effortRejected,
                },
                ["thinking"] = new JsonObject
                {
                    ["accepted"] = thinkingAccepted,
                    ["rejected"] = thinkingRejected,
                },
                ["mid_conv_system"] = midConvAccepted,
            };
        }

        var facts = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["backend"] = "copilot-anthropic",
                ["endpoint"] = "/v1/messages",
                ["account_type"] = "enterprise",
                ["models_probed"] = AllModels.Length,
                ["note"] = "Live wire-truth of what Copilot's /v1/messages accepts per model. "
                         + "Drift here (B2) fails B_AnthropicContract_SweepAssertAndDetectDrift; "
                         + "regenerate with BRIDGE_REGEN_CONTRACT_SNAPSHOT=1 and review. "
                         + "Guards ModelProfileCatalog (B3 in the same test).",
            },
            ["models"] = models,
        };

        // ── B1: every model produced a fact row (the live backend answered). ──
        Assert.Equal(AllModels.Length, models.Count);

        // ── B2: drift detection vs the committed snapshot. ──
        var (diffs, seeded) = ContractSnapshot.SeedOrDiff(AnthropicSnapshotFile, facts);
        if (seeded)
        {
            _output.WriteLine($"[seeded] {AnthropicSnapshotFile} — review & commit.");
        }
        else if (diffs.Count > 0)
        {
            _output.WriteLine($"=== Anthropic backend DRIFT ({diffs.Count}) ===");
            foreach (var d in diffs) _output.WriteLine("  " + d);
            Assert.Fail(
                $"Copilot /v1/messages drifted from {AnthropicSnapshotFile} in {diffs.Count} fact(s). "
                + "Review the diff above: update the snapshot (BRIDGE_REGEN_CONTRACT_SNAPSHOT=1) "
                + "and reconcile ModelProfileCatalog if a guarded fact changed.\n  "
                + string.Join("\n  ", diffs));
        }

        // ── B3: ModelProfileCatalog still matches the live facts. ──
        AssertCatalogMatchesLive(models);
    }

    /// <summary>
    /// B3 — tie <see cref="ModelProfileCatalog"/> to the live probe result. For
    /// each catalogued model, the catalog's claims (accepted efforts, accepted
    /// thinking shapes, mid-conv-system support) must match what the live backend
    /// just did. A mismatch fails naming the catalog row to reconcile — so the
    /// catalog's correctness is tied to current reality, not a frozen moment.
    /// </summary>
    private void AssertCatalogMatchesLive(JsonObject liveModels)
    {
        var catalog = new ModelProfileCatalog();
        var mismatches = new List<string>();

        foreach (var (model, factsNode) in liveModels)
        {
            var profile = catalog.Get(model);
            if (profile is null) continue; // sibling/variant ids not separately catalogued — skip
            var facts = factsNode!.AsObject();

            // Effort: catalog AcceptedEfforts must equal the live accepted set.
            var liveEffort = facts["effort"]!["accepted"]!.AsArray()
                .Select(n => n!.GetValue<string>()).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var catEffort = profile.AcceptedEfforts.OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (!liveEffort.SequenceEqual(catEffort))
                mismatches.Add(
                    $"{model}: catalog AcceptedEfforts=[{string.Join(",", catEffort)}] "
                    + $"but live accepts=[{string.Join(",", liveEffort)}]");

            // Thinking: every shape the catalog claims to accept must be live-accepted.
            var liveThinking = facts["thinking"]!["accepted"]!.AsArray()
                .Select(n => n!.GetValue<string>()).ToHashSet();
            foreach (var shape in profile.Thinking.AcceptedShapes)
                if (!liveThinking.Contains(shape))
                    mismatches.Add(
                        $"{model}: catalog Thinking accepts '{shape}' but live rejected it "
                        + $"(live accepts=[{string.Join(",", liveThinking)}])");

            // Mid-conv system.
            var liveMidConv = facts["mid_conv_system"]!.GetValue<bool>();
            if (profile.AcceptsMidConversationSystem != liveMidConv)
                mismatches.Add(
                    $"{model}: catalog AcceptsMidConversationSystem={profile.AcceptsMidConversationSystem} "
                    + $"but live={liveMidConv}");
        }

        if (mismatches.Count > 0)
        {
            _output.WriteLine($"=== B3 catalog-vs-live mismatches ({mismatches.Count}) ===");
            foreach (var m in mismatches) _output.WriteLine("  " + m);
            Assert.Fail(
                $"ModelProfileCatalog disagrees with the live /v1/messages backend in {mismatches.Count} "
                + "place(s). Reconcile the named rows:\n  " + string.Join("\n  ", mismatches));
        }
    }
}
