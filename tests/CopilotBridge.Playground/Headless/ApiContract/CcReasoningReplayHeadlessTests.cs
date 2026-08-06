using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Live two-turn proof that Responses reasoning state survives the Claude client edge.
/// Turn 1 asks live gpt-5.6-sol for work that produces an encrypted reasoning item; the
/// client-facing stream must carry it as a hidden redacted-thinking block. Turn 2 echoes
/// that block back exactly as Claude Code would, and the upstream request must contain a
/// restored reasoning item carrying the summary gpt-5.6-sol requires — proven by the
/// upstream accepting it rather than returning the missing-summary 400.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CcReasoningReplayHeadlessTests
{
    private readonly ITestOutputHelper _output;

    public CcReasoningReplayHeadlessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ReasoningState_SurvivesClaudeEdge_AndIsAcceptedOnEcho()
    {
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(ServeScenario.CcToGpt));
        var reader = new BridgeLogReader(bridge.TraceDir);
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        const string task =
            "Work out privately, then answer with only the number: the least positive integer n "
            + "with n mod 7 = 1, n mod 9 = 2, and n mod 11 = 3.";

        var first = await PostAsync(http, bridge.BaseUrl, new JsonObject
        {
            ["model"] = "claude-opus-5",
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray { UserMessage(task) },
            ["stream"] = true,
        }, cts.Token);

        // The hidden carrier the client would store for this assistant turn.
        var carrier = FindRedactedThinkingData(first.Events);
        Assert.False(string.IsNullOrEmpty(carrier),
            "client-facing stream carried no redacted_thinking block: " + first.Raw);
        // It must be opaque: no reasoning plaintext, no bridge marker property.
        Assert.DoesNotContain("\"bridge_reasoning_item\"", first.Raw, StringComparison.Ordinal);

        // Turn 2: echo the assistant turn back verbatim, exactly as Claude Code does.
        var second = await PostAsync(http, bridge.BaseUrl, new JsonObject
        {
            ["model"] = "claude-opus-5",
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray
            {
                UserMessage(task),
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "redacted_thinking",
                            ["data"] = carrier,
                        },
                        new JsonObject { ["type"] = "text", ["text"] = "Working." },
                    },
                },
                UserMessage("Now reply with exactly: replay-ok"),
            },
            ["stream"] = true,
        }, cts.Token);

        // Select the request by CONTENT, not position: a turn can produce several
        // upstream records (title/preflight), so "the last one" is not reliably the
        // echo request under test.
        var echoRequest = reader.ReadNew().LastOrDefault(e =>
            e.UpstreamBody?["model"]?.GetValue<string>() == "gpt-5.6-sol"
            && e.UpstreamBody?["input"] is JsonArray input
            && input.Any(i => i?["type"]?.GetValue<string>() == "reasoning"));
        Assert.True(echoRequest is not null,
            "no upstream request carried a restored reasoning item");
        var reasoning = echoRequest!.UpstreamBody!["input"]!.AsArray()
            .Select(i => i!.AsObject())
            .Single(i => i["type"]?.GetValue<string>() == "reasoning");

        _output.WriteLine($"turn1={first.Status} turn2={second.Status} upstream={echoRequest.UpstreamStatus}");
        _output.WriteLine("restored reasoning: " + reasoning.ToJsonString());

        // The restored item must carry what live probes proved gpt-5.6-sol requires.
        Assert.False(string.IsNullOrEmpty(reasoning["encrypted_content"]?.GetValue<string>()));
        Assert.NotNull(reasoning["summary"]);
        // And the upstream must have ACCEPTED it — a dropped summary is a 400 here.
        Assert.InRange(echoRequest.UpstreamStatus, 200, 299);
        Assert.Equal(System.Net.HttpStatusCode.OK, second.Status);
    }

    private static JsonObject UserMessage(string text) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = text },
        },
    };

    private static async Task<(System.Net.HttpStatusCode Status, string Raw, List<JsonObject> Events)> PostAsync(
        HttpClient http, string baseUrl, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/cc/v1/messages");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        var events = new List<JsonObject>();
        foreach (var line in raw.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;
            if (JsonNode.Parse(payload) is JsonObject obj) events.Add(obj);
        }
        return (response.StatusCode, raw, events);
    }

    private static string FindRedactedThinkingData(List<JsonObject> events)
    {
        foreach (var evt in events)
        {
            if (evt["type"]?.GetValue<string>() != "content_block_start") continue;
            var block = evt["content_block"]?.AsObject();
            if (block?["type"]?.GetValue<string>() != "redacted_thinking") continue;
            return block["data"]?.GetValue<string>() ?? "";
        }
        return "";
    }
}
