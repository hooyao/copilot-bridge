using System.Text.Json.Nodes;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

public partial class ResponsesProbe
{
    private static readonly string[] OctoberReviewedModels =
        ["gpt-5.4-nano", "gpt-6.1-terra", "mai-code-2-flash"];
    internal static readonly string[] OctoberCandidates =
    [
        "gpt-5.4-nano", "gpt-5.6-luna-utility", "gpt-6.1-terra",
        "mai-code-2-flash",
    ];

    /// <summary>
    /// Discovery is only a candidate list. Probe exact-id liveness before
    /// collecting the same backend facts guarded by the full B2/B3 sweep.
    /// Evidence stays outside the committed snapshot until it is reviewed.
    /// </summary>
    [Fact]
    public async Task October2026_CandidateContracts_Probe()
    {
        var evidence = Path.Combine(Path.GetDirectoryName(ContractSnapshot.PathFor(ResponsesSnapshotFile))!,
            "..", "tests", "behavior-runs", "model-sync-20261027");
        Directory.CreateDirectory(evidence);
        await Parallel.ForEachAsync(OctoberCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (model, ct) =>
            {
                using var client = new PlaygroundClient();
                var payload = new JsonObject
                {
                    ["model"] = model,
                    ["instructions"] = "Reply with exactly: ok",
                    ["input"] = "reply: ok",
                    ["stream"] = false,
                    ["store"] = false,
                };
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostResponsesAsync(payload.ToJsonString(), ct: ct), model + " liveness");
                _output.WriteLine($"[{model}] liveness -> {(int)status}: {Truncate(body, 1800)}");
                var accepted = WireAcceptance.IsAccepted(status, body, model + " liveness");
                var facts = accepted
                    ? await ProbeResponsesModelFactsAsync(client, model)
                    : new JsonObject { ["unavailable_status"] = (int)status, ["error_body"] = body };
                await File.WriteAllTextAsync(Path.Combine(evidence, model + "-facts.json"),
                    facts.ToJsonString(new() { WriteIndented = true }), ct);
                _output.WriteLine($"[{model}] facts: {facts.ToJsonString()}");
            });
    }

    [Fact]
    public async Task October2026_ReviewedContracts_MatchSnapshotAndCatalog()
    {
        var snapshot = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)!["models"]!.AsObject();
        var live = new JsonObject();
        foreach (var model in OctoberReviewedModels)
        {
            using var client = new PlaygroundClient();
            var facts = await ProbeResponsesModelFactsAsync(client, model);
            live[model] = facts;
            var diff = ContractSnapshot.Diff(snapshot[model]!.AsObject(), facts);
            Assert.True(diff.Count == 0, model + ": " + string.Join("\n", diff));
        }
        AssertCatalogMatchesLive(live, requireCompleteCatalog: false);
    }

    public static IEnumerable<object[]> OctoberModelRows() =>
        OctoberReviewedModels.Select(model => new object[] { model });

    [Fact]
    public void October2026_CommittedBackendFacts_MatchCatalog()
    {
        var snapshot = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)!["models"]!.AsObject();
        var reviewed = new JsonObject();
        foreach (var model in OctoberReviewedModels) reviewed[model] = snapshot[model]!.DeepClone();
        AssertCatalogMatchesLive(reviewed, requireCompleteCatalog: false);
    }

    /// <summary>
    /// Reconfirm every silent rewrite on the exact model's real streaming
    /// Codex payload. Mutate one axis per replay, preserving all other bytes'
    /// JSON values, reasoning siblings, input history, and tool definitions.
    /// </summary>
    [Theory]
    [MemberData(nameof(OctoberModelRows))]
    public async Task October2026_RealCodexBytes_RewriteBoundaries(string model)
    {
        var (path, captured) = LoadNewestRealCodexBodyForModel(model);
        Assert.Equal(model, captured["model"]?.GetValue<string>());
        Assert.Equal(true, captured["stream"]?.GetValue<bool>());
        using var client = new PlaygroundClient();
        var (baselineStatus, baselineBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(WireAcceptance.IsAccepted(baselineStatus, baselineBody, model + " unchanged capture"),
            WireAcceptance.ErrorMessage(baselineBody));

        var facts = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)!["models"]![model]!;
        foreach (var rejected in facts["effort"]!["rejected"]!.AsArray())
        {
            var effort = rejected!.GetValue<string>();
            var body = captured.DeepClone().AsObject();
            (body["reasoning"] ??= new JsonObject()).AsObject()["effort"] = effort;
            await AssertCapturedAxisRejectedAsync(client, model, body, "effort=" + effort);
            // Search validation precedes model effort validation. When it
            // masks Nano's minimal rejection, independently confirm the
            // model-level boundary without search on the same captured bytes.
            if (model == "gpt-5.4-nano" && effort == "minimal")
            {
                var baseline = captured.DeepClone().AsObject();
                if (baseline["tools"] is JsonArray tools)
                    for (var index = tools.Count - 1; index >= 0; index--)
                        if (tools[index]?["type"]?.GetValue<string>() == "web_search") tools.RemoveAt(index);
                var (status, response) = await client.TryPostResponsesAsync(baseline.ToJsonString());
                Assert.True(WireAcceptance.IsAccepted(status, response, model + " captured without search"),
                    WireAcceptance.ErrorMessage(response));
                (baseline["reasoning"] ??= new JsonObject()).AsObject()["effort"] = effort;
                await AssertCapturedAxisRejectedAsync(client, model, baseline, "minimal without search");
            }
        }
        foreach (var axis in new[] { "store_true", "service_tier", "image_generation" })
        {
            var body = captured.DeepClone().AsObject();
            switch (axis)
            {
                case "store_true": body["store"] = true; break;
                case "service_tier": body["service_tier"] = "default"; break;
                case "image_generation":
                    (body["tools"] ??= new JsonArray()).AsArray().Add(JsonNode.Parse("""{"type":"image_generation","output_format":"png"}"""));
                    break;
            }
            await AssertCapturedAxisRejectedAsync(client, model, body, axis);
        }
        if (model == "mai-code-2-flash")
        {
            var body = captured.DeepClone().AsObject();
            Assert.Contains(body["tools"]!.AsArray(), tool => tool?["type"]?.GetValue<string>() == "web_search");
            (body["reasoning"] ??= new JsonObject()).AsObject()["effort"] = "minimal";
            await AssertCapturedAxisRejectedAsync(client, model, body, "minimal+web_search");
            var tools = body["tools"]!.AsArray();
            for (var index = tools.Count - 1; index >= 0; index--)
                if (tools[index]?["type"]?.GetValue<string>() == "web_search") tools.RemoveAt(index);
            var (status, response) = await client.TryPostResponsesAsync(body.ToJsonString());
            Assert.True(WireAcceptance.IsAccepted(status, response, model + " minimal without search"),
                WireAcceptance.ErrorMessage(response));
        }
        _output.WriteLine($"[{model}] captured rewrite boundaries confirmed: {path}");
    }

    private async Task AssertCapturedAxisRejectedAsync(PlaygroundClient client, string model, JsonObject body, string axis)
    {
        var (status, response) = await client.TryPostResponsesAsync(body.ToJsonString());
        _output.WriteLine($"[{model}] captured {axis} -> {(int)status}: {Truncate(response, 500)}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
    }
}
