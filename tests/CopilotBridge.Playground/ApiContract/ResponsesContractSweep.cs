using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// B1/B2/B3 for the Copilot <c>/responses</c> (Responses/Codex) backend
/// (`docs/ir-definition-design.md` §7.B). Promotes <see cref="ResponsesProbe"/>
/// from print-only to ASSERTING: one aggregate sweep hits every verified cell
/// (per-model effort, field/tool rejections, structured multimodal function
/// output, and the live SSE event set), builds a structured facts object, and:
///   B1 — asserts each Responses model in <see cref="ResponsesProbe.AllModels"/>
///        answered;
///   B2 — diffs the live facts against the committed Responses snapshot and
///        FAILS with a readable diff on any drift;
///   B3 — compares every rewrite-driving live fact with the shipping
///        <see cref="CodexModelProfileCatalog"/> in both directions.
/// Tagged Integration (inherited).
/// </summary>
[SupportedOSPlatform("windows")]
public partial class ResponsesProbe
{
    internal const string ResponsesSnapshotFile = "copilot-responses-contract-snapshot.json";

    // Full Codex effort vocabulary incl. the boundary values that split the
    // profiles: "large" reject minimal; "small" reject none + xhigh; "xlarge"
    // (the gpt-5.6 codenames) additionally ACCEPT max — the only distinguishing
    // capability, so max must be in the swept vocabulary or B2 can't detect a
    // model gaining/losing it.
    private static readonly string[] EffortVocabulary = ["minimal", "none", "low", "medium", "high", "xhigh", "max"];

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
                // No current model has a documented 5xx rejection contract. A
                // transient 5xx must abort through the standard classifier instead
                // of being recorded as tools_rejected and poisoning the snapshot.
                var accepted = WireAcceptance.IsAccepted(
                    status, body, $"{model} tool={label}");
                if (!accepted)
                    toolRejected.Add(label);
            }

            // ── structured multimodal function output (exact-model capability) ──
            var multimodal = await MultimodalFunctionOutputProbe.ProbeAsync(client, model);
            _output.WriteLine(
                $"[{model}] multimodal function output first={(int)multimodal.FirstStatus} "
                + $"second={(int?)multimodal.SecondStatus} understood={multimodal.Supported}");

            models[model] = new JsonObject
            {
                ["effort"] = new JsonObject { ["accepted"] = effortAccepted, ["rejected"] = effortRejected },
                ["fields_rejected"] = fieldRejected,
                ["tools_rejected"] = toolRejected,
                ["supports_multimodal_function_output"] = multimodal.Supported,
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
                         + "The shipping Codex profile catalog is checked against every "
                         + "rewrite-driving fact in B3. Drift (B2) fails "
                         + "B_ResponsesContract_SweepAssertAndDetectDrift; regenerate with "
                         + "BRIDGE_REGEN_CONTRACT_SNAPSHOT=1 and review.",
            },
            ["models"] = models,
            ["sse_event_types"] = sseEvents,
        };

        // ── B1: every model in AllModels answered. ──
        Assert.Equal(AllModels.Length, models.Count);
        Assert.True(sseEvents.Count > 0, "captured no SSE event types from the streaming probe");

        // ── B3: every backend fact that causes a request rewrite matches the catalog. ──
        AssertCatalogMatchesLive(models);

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

    private static void AssertCatalogMatchesLive(JsonObject models)
    {
        var catalog = new CodexModelProfileCatalog();
        Assert.Equal(
            catalog.KnownIds.Order(StringComparer.Ordinal),
            models.Select(entry => entry.Key).Order(StringComparer.Ordinal));

        foreach (var model in catalog.KnownIds)
        {
            var profile = Assert.IsType<CodexModelProfile>(catalog.Get(model));
            var facts = Assert.IsType<JsonObject>(models[model]);
            var effort = Assert.IsType<JsonObject>(facts["effort"]);
            var accepted = Assert.IsType<JsonArray>(effort["accepted"])
                .Select(value => value!.GetValue<string>())
                .Order(StringComparer.Ordinal)
                .ToArray();
            var expectedAccepted = profile.AcceptedEfforts.Order(StringComparer.Ordinal).ToArray();
            Assert.True(
                expectedAccepted.SequenceEqual(accepted, StringComparer.Ordinal),
                $"{model}: catalog accepted efforts [{string.Join(',', expectedAccepted)}] "
                + $"!= live [{string.Join(',', accepted)}]");

            var fieldsRejected = Assert.IsType<JsonArray>(facts["fields_rejected"])
                .Select(value => value!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                CodexModelProfileCatalog.StripsServiceTier == fieldsRejected.Contains("service_tier"),
                $"{model}: catalog/live service_tier rewrite fact differs");
            Assert.True(
                CodexModelProfileCatalog.StripsStoreTrue == fieldsRejected.Contains("store_true"),
                $"{model}: catalog/live store:true rewrite fact differs");

            var toolsRejected = Assert.IsType<JsonArray>(facts["tools_rejected"])
                .Select(value => value!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(
                CodexModelProfileCatalog.DropsImageGenerationTool == toolsRejected.Contains("image_generation"),
                $"{model}: catalog/live image_generation rewrite fact differs");
            Assert.True(
                profile.RejectsCustomTools == toolsRejected.Contains("custom_apply_patch"),
                $"{model}: catalog/live custom-tool rewrite fact differs");
            var liveSupportsMultimodal = facts["supports_multimodal_function_output"]?.GetValue<bool>()
                ?? throw new InvalidDataException($"{model}: live facts omit multimodal function-output support");
            Assert.True(
                profile.SupportsMultimodalFunctionOutput == liveSupportsMultimodal,
                $"{model}: catalog/live multimodal function-output fact differs");
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
