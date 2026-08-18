using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using CopilotBridge.Playground.Headless;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Live probe for the exact gpt-5.6-sol reasoning-item replay contract. It first
/// obtains a fresh encrypted reasoning item, then replays the same continuation
/// while independently removing identity/summary/content fields. This separates
/// backend requirements from what the bridge or Claude Code happens to model.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class ReasoningReplayRequirementsProbe
{
    private readonly ITestOutputHelper _output;

    public ReasoningReplayRequirementsProbe(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Gpt56Sol_ReasoningReplay_FieldRequirements()
    {
        const string firstUserText =
            "Privately solve this carefully before answering: find the least positive integer n such that "
            + "n leaves remainder 1 modulo 7, remainder 2 modulo 9, and remainder 3 modulo 11. "
            + "Answer with only n.";
        var firstRequest = new JsonObject
        {
            ["model"] = "gpt-5.6-sol",
            ["input"] = new JsonArray
            {
                Message("user", "input_text", firstUserText),
            },
            ["reasoning"] = new JsonObject { ["effort"] = "xhigh", ["summary"] = "detailed" },
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["stream"] = false,
        };

        using var client = new PlaygroundClient();
        var (firstStatus, firstBody) = await client.TryPostResponsesAsync(firstRequest.ToJsonString());
        Assert.Equal(System.Net.HttpStatusCode.OK, firstStatus);
        var firstResponse = JsonNode.Parse(firstBody)!.AsObject();
        var originalOutput = firstResponse["output"]!.AsArray();
        var originalReasoning = originalOutput
            .Select(node => node!.AsObject())
            .SingleOrDefault(item => item["type"]!.GetValue<string>() == "reasoning");
        Assert.True(originalReasoning is not null,
            "Fresh xhigh+detailed response contained no reasoning item: " + firstBody);
        Assert.False(string.IsNullOrEmpty(
            originalReasoning["encrypted_content"]?.GetValue<string>()));

        // Every variant's recorded outcome is asserted, not just the control: the
        // carrier only needs `summary` because the backend REJECTS its absence, so a
        // backend that started accepting blob-only — or started rejecting a
        // summary-bearing replay — must redden this probe rather than leave the
        // contract silently stale.
        var expected = new Dictionary<string, System.Net.HttpStatusCode>
        {
            ["full"] = System.Net.HttpStatusCode.OK,
            ["blob-only"] = System.Net.HttpStatusCode.BadRequest,
            ["id+blob"] = System.Net.HttpStatusCode.BadRequest,
            ["blob+summary"] = System.Net.HttpStatusCode.OK,
            ["id+blob+summary"] = System.Net.HttpStatusCode.OK,
            ["id+blob+content"] = System.Net.HttpStatusCode.BadRequest,
            ["blob+summary+content"] = System.Net.HttpStatusCode.OK,
        };

        var results = new Dictionary<string, System.Net.HttpStatusCode>();
        foreach (var variant in expected.Keys)
        {
            var replayOutput = new JsonArray();
            foreach (var item in originalOutput)
            {
                if (item!["type"]!.GetValue<string>() != "reasoning")
                {
                    replayOutput.Add(item.DeepClone());
                    continue;
                }
                replayOutput.Add(ReasoningVariant(originalReasoning, variant));
            }
            replayOutput.Add(Message(
                "user", "input_text", "Now reply with exactly: replay-ok"));

            var followUp = new JsonObject
            {
                ["model"] = "gpt-5.6-sol",
                ["input"] = new JsonArray(Message("user", "input_text", firstUserText)),
                ["reasoning"] = new JsonObject { ["effort"] = "xhigh", ["summary"] = "detailed" },
                ["include"] = new JsonArray("reasoning.encrypted_content"),
                ["stream"] = false,
            };
            foreach (var item in replayOutput)
                followUp["input"]!.AsArray().Add(item!.DeepClone());

            var (status, body) = await client.TryPostResponsesAsync(followUp.ToJsonString());
            results[variant] = status;
            _output.WriteLine(
                $"{variant} → {(int)status} {status}: "
                + (body.Length <= 300 ? body : body[..300]));
        }

        var drift = expected
            .Where(kv => results[kv.Key] != kv.Value)
            .Select(kv => $"{kv.Key}: expected {(int)kv.Value}, got {(int)results[kv.Key]}")
            .ToList();
        Assert.True(drift.Count == 0,
            "gpt-5.6-sol reasoning-replay contract drifted — the bridge's carrier fields "
            + "are derived from these outcomes:\n  " + string.Join("\n  ", drift));

        // The specific fact the carrier design rests on, stated once more so a future
        // reader sees WHY summary is preserved.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, results["blob-only"]);
        Assert.Equal(System.Net.HttpStatusCode.OK, results["blob+summary"]);
    }

    /// <summary>
    /// Backend fact behind the bridge's Codex response-header synthesis: Copilot's
    /// reported input usage already charges replayed encrypted reasoning even though
    /// the Copilot HTTP response omits <c>X-Reasoning-Included</c>. The bridge-behavior
    /// unit test cannot guard this fact; paired live requests must go red if it drifts.
    /// </summary>
    [Fact]
    public async Task Gpt56Sol_InputUsageAlreadyChargesReplayedReasoning()
    {
        const string firstUserText =
            "Compute (987654321 * 1234567) + (7654321 * 98765). "
            + "Verify the result carefully and return only the final integer.";
        var firstRequest = new JsonObject
        {
            ["model"] = "gpt-5.6-sol",
            ["input"] = new JsonArray(Message("user", "input_text", firstUserText)),
            ["reasoning"] = new JsonObject
            {
                ["effort"] = "max",
                ["summary"] = "detailed",
                ["context"] = "all_turns",
            },
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["stream"] = false,
        };

        await using var bridge = await ServeProcess.StartAsync(
            new ServeInvocation(ServeScenario.Passthrough));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        async Task<(System.Net.HttpStatusCode Status, string Body)> Post(JsonObject body)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"{bridge.BaseUrl}/codex/responses");
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "probe-local");
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var (firstStatus, firstBody) = await Post(firstRequest);
        Assert.Equal(System.Net.HttpStatusCode.OK, firstStatus);
        var firstOutput = JsonNode.Parse(firstBody)!["output"]!.AsArray();
        var reasoningItems = firstOutput
            .Where(item => item!["type"]!.GetValue<string>() == "reasoning")
            .Select(item => item!.AsObject())
            .ToList();
        Assert.NotEmpty(reasoningItems);
        Assert.All(reasoningItems, item =>
        {
            Assert.False(string.IsNullOrEmpty(item["encrypted_content"]?.GetValue<string>()));
            Assert.NotNull(item["summary"]);
        });

        JsonObject FollowUp(bool includeReasoning)
        {
            var input = new JsonArray(Message("user", "input_text", firstUserText));
            foreach (var item in firstOutput)
            {
                if (!includeReasoning && item!["type"]!.GetValue<string>() == "reasoning")
                    continue;
                input.Add(item!.DeepClone());
            }
            input.Add(Message("user", "input_text", "Return exactly BETA."));
            return new JsonObject
            {
                ["model"] = "gpt-5.6-sol",
                ["input"] = input,
                ["reasoning"] = new JsonObject
                {
                    ["effort"] = "low",
                    ["summary"] = "auto",
                    ["context"] = "all_turns",
                },
                ["include"] = new JsonArray("reasoning.encrypted_content"),
                ["stream"] = false,
            };
        }

        var (withStatus, withBody) = await Post(FollowUp(true));
        var (withoutStatus, withoutBody) = await Post(FollowUp(false));
        Assert.Equal(System.Net.HttpStatusCode.OK, withStatus);
        Assert.Equal(System.Net.HttpStatusCode.OK, withoutStatus);

        static long InputTokens(string body) =>
            JsonNode.Parse(body)!["usage"]!["input_tokens"]!.GetValue<long>();
        var withReasoning = InputTokens(withBody);
        var withoutReasoning = InputTokens(withoutBody);
        var delta = withReasoning - withoutReasoning;

        _output.WriteLine(
            $"reasoning_items={reasoningItems.Count} with={withReasoning} "
            + $"without={withoutReasoning} delta={delta}");
        Assert.True(delta > 0,
            $"Copilot input usage no longer charges replayed reasoning: "
            + $"with={withReasoning}, without={withoutReasoning}. Re-evaluate "
            + "X-Reasoning-Included synthesis before changing this contract.");
    }

    private static JsonObject ReasoningVariant(JsonObject original, string variant)
    {
        if (variant == "full") return original.DeepClone().AsObject();

        var result = new JsonObject { ["type"] = "reasoning" };
        if (variant.Contains("id", StringComparison.Ordinal))
            result["id"] = original["id"]!.DeepClone();
        result["encrypted_content"] = original["encrypted_content"]!.DeepClone();
        if (variant.Contains("summary", StringComparison.Ordinal))
            result["summary"] = original["summary"]!.DeepClone();
        if (variant.Contains("content", StringComparison.Ordinal))
            result["content"] = original["content"]!.DeepClone();
        return result;
    }

    private static JsonObject Message(string role, string partType, string text) =>
        new()
        {
            ["type"] = "message",
            ["role"] = role,
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = partType, ["text"] = text },
            },
        };
}
