using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// B1/B2 for the Copilot <c>/responses</c> (Responses/Codex) backend
/// (`docs/ir-definition-design.md` §7.B). Promotes <see cref="ResponsesProbe"/>
/// from print-only to ASSERTING: one aggregate sweep hits every verified cell
/// (per-model effort, the field rejections, the tool rejections, and the live
/// SSE event set), builds a structured facts object, and:
///   B1 — asserts each of the 6 Responses models answered;
///   B2 — diffs the live facts against the committed Responses snapshot and
///        FAILS with a readable diff on any drift.
///
/// NO B3 here: the Responses-side catalog + coercions don't exist yet — they're
/// change 3 (`add-codex-responses-client`). This change only RECORDS the live
/// Responses wire-truth as the snapshot change 3's Codex profile catalog will be
/// built against (noted in the snapshot header). Tagged Integration (inherited).
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ResponsesProbe
{
    internal const string ResponsesSnapshotFile = "copilot-responses-contract-snapshot.json";

    // Full Codex effort vocabulary incl. the two boundary values that split the
    // "large" vs "small" profiles (research §2.2): large reject minimal; small
    // reject none + xhigh.
    private static readonly string[] EffortVocabulary = ["minimal", "none", "low", "medium", "high", "xhigh"];

    [Fact]
    public async Task B_ResponsesContract_SweepAssertAndDetectDrift()
    {
        using var client = new PlaygroundClient();
        var models = new JsonObject();

        foreach (var model in AllModels)
        {
            // ── effort accept/reject ──
            var (effortAccepted, effortRejected) = (new JsonArray(), new JsonArray());
            foreach (var effort in EffortVocabulary)
            {
                var payload =
                    "{\"model\":\"" + model + "\","
                    + "\"instructions\":\"Reply with exactly: ok\","
                    + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"reply: ok\"}]}],"
                    + "\"stream\":false,\"store\":false,"
                    + "\"reasoning\":{\"effort\":\"" + effort + "\"},\"include\":[\"reasoning.encrypted_content\"]}";
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostResponsesAsync(payload), $"{model} effort={effort}");
                if (WireAcceptance.IsAccepted(status, body, $"{model} effort={effort}"))
                    effortAccepted.Add(effort);
                else
                    effortRejected.Add(effort);
            }

            // ── field rejections (store:true / service_tier are the verified 400s) ──
            var fieldRejected = new JsonArray();
            foreach (var (label, extra) in ResponsesFieldProbes)
            {
                var payload =
                    "{\"model\":\"" + model + "\","
                    + "\"instructions\":\"Reply with exactly: ok\","
                    + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"reply: ok\"}]}],"
                    + "\"stream\":false" + extra + "}";
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostResponsesAsync(payload), $"{model} field={label}");
                if (!WireAcceptance.IsAccepted(status, body, $"{model} field={label}"))
                    fieldRejected.Add(label);
            }

            // ── tool rejections (image_generation is the verified 400; flash 500s on custom) ──
            var toolRejected = new JsonArray();
            foreach (var (label, toolJson) in ResponsesToolProbes)
            {
                var payload =
                    "{\"model\":\"" + model + "\","
                    + "\"instructions\":\"You may use tools.\","
                    + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hello\"}]}],"
                    + "\"stream\":false,\"tool_choice\":\"auto\",\"tools\":[" + toolJson + "]}";
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostResponsesAsync(payload), $"{model} tool={label}");
                // 5xx handling is scoped to the ONE documented cell where a server
                // error is the contract fact: mai-code-1-flash-internal 500s on
                // custom/apply_patch tools (research §2.4). For every other cell a
                // 5xx is a flaky upstream, not a rejection — use the throwing
                // classifier so a transient 5xx ABORTS the sweep instead of being
                // silently recorded as tools_rejected (which would poison the
                // snapshot and, after a human regen, the Codex catalog). Reviewer
                // #7: a blanket 5xx-as-reject across all tool probes was too broad.
                var isFlashCustomCell =
                    string.Equals(model, "mai-code-1-flash-internal", StringComparison.OrdinalIgnoreCase)
                    && label == "custom_apply_patch";
                var accepted = isFlashCustomCell
                    ? WireAcceptance.IsAcceptedTreating5xxAsReject(status)
                    : WireAcceptance.IsAccepted(status, body, $"{model} tool={label}");
                if (!accepted)
                    toolRejected.Add(label);
            }

            models[model] = new JsonObject
            {
                ["effort"] = new JsonObject { ["accepted"] = effortAccepted, ["rejected"] = effortRejected },
                ["fields_rejected"] = fieldRejected,
                ["tools_rejected"] = toolRejected,
            };
        }

        // ── SSE event set (one capture; the grammar is per-backend, not per-model) ──
        var sseEvents = await CaptureSseEventTypes(client);

        var facts = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["backend"] = "copilot-responses",
                ["endpoint"] = "/responses",
                ["account_type"] = "enterprise",
                ["models_probed"] = AllModels.Length,
                ["note"] = "Live wire-truth of what Copilot's /responses accepts per model. "
                         + "RECORDED FOR change 3 (add-codex-responses-client): its Codex profile "
                         + "catalog + coercions are built against THIS snapshot (no catalog exists "
                         + "yet, so there is no B3 here). Drift (B2) fails "
                         + "B_ResponsesContract_SweepAssertAndDetectDrift; regenerate with "
                         + "BRIDGE_REGEN_CONTRACT_SNAPSHOT=1 and review.",
            },
            ["models"] = models,
            ["sse_event_types"] = sseEvents,
        };

        // ── B1: all 6 models answered. ──
        Assert.Equal(AllModels.Length, models.Count);
        Assert.True(sseEvents.Count > 0, "captured no SSE event types from the streaming probe");

        // ── B2: drift detection vs the committed snapshot. ──
        var (diffs, seeded) = ContractSnapshot.SeedOrDiff(ResponsesSnapshotFile, facts);
        if (seeded)
        {
            _output.WriteLine($"[seeded] {ResponsesSnapshotFile} — review & commit.");
        }
        else if (diffs.Count > 0)
        {
            _output.WriteLine($"=== Responses backend DRIFT ({diffs.Count}) ===");
            foreach (var d in diffs) _output.WriteLine("  " + d);
            Assert.Fail(
                $"Copilot /responses drifted from {ResponsesSnapshotFile} in {diffs.Count} fact(s). "
                + "Review the diff above: update the snapshot (BRIDGE_REGEN_CONTRACT_SNAPSHOT=1) and, "
                + "in change 3, reconcile the Codex profile catalog / coercions if a guarded fact changed.\n  "
                + string.Join("\n  ", diffs));
        }
    }

    private static readonly (string Label, string Json)[] ResponsesFieldProbes =
    [
        ("store_true", ",\"store\":true"),
        ("service_tier", ",\"service_tier\":\"default\""),
        ("prompt_cache_key", ",\"prompt_cache_key\":\"probe-cache-key-123\""),
        ("reasoning_summary", ",\"reasoning\":{\"effort\":\"low\",\"summary\":\"auto\"}"),
        ("encrypted_content_include", ",\"reasoning\":{\"effort\":\"low\"},\"include\":[\"reasoning.encrypted_content\"]"),
    ];

    private static readonly (string Label, string Json)[] ResponsesToolProbes =
    [
        ("function", """{"type":"function","name":"get_time","description":"Get the current time","parameters":{"type":"object","properties":{},"required":[]},"strict":false}"""),
        ("custom_apply_patch", """{"type":"custom","name":"apply_patch","description":"Edit files","format":{"type":"grammar","syntax":"lark","definition":"start: /.+/"}}"""),
        ("web_search", """{"type":"web_search"}"""),
        ("image_generation", """{"type":"image_generation","output_format":"png"}"""),
    ];

    /// <summary>
    /// Capture the SORTED SET of <c>event:</c> types Copilot emits on a forced
    /// tool-call stream. The event GRAMMAR is the contract fact (a new/renamed
    /// event = drift); per-event ordering and payloads are not snapshotted (too
    /// volatile). Records absence of a stray <c>[DONE]</c> as its own fact.
    /// </summary>
    private static async Task<JsonArray> CaptureSseEventTypes(PlaygroundClient client)
    {
        const string model = "gpt-5.3-codex";
        const string toolsJson = """[{"type":"function","name":"get_time","description":"Get the current time","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":[]},"strict":false}]""";
        var payload =
            "{\"model\":\"" + model + "\","
            + "\"instructions\":\"When asked the time, call the get_time tool.\","
            + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"What time is it? Use the tool.\"}]}],"
            + "\"stream\":true,\"store\":false,\"tool_choice\":\"auto\",\"tools\":" + toolsJson + "}";

        var (_, raw) = await client.TryPostResponsesRawStreamAsync(payload);
        var hasDone = raw.Contains("[DONE]", StringComparison.Ordinal);
        var types = raw.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("event:", StringComparison.Ordinal))
            .Select(l => l["event:".Length..].Trim())
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var arr = new JsonArray();
        foreach (var t in types) arr.Add(t);
        // Record the terminator contract explicitly: Codex's parser requires
        // response.completed and tolerates no [DONE]. A future [DONE] is drift.
        arr.Add(hasDone ? "<HAS_DONE_TERMINATOR>" : "<no-done-terminator>");
        return arr;
    }
}
