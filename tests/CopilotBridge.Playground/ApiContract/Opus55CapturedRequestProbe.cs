using System.Text.Json.Nodes;
using System.Text;
using CopilotBridge.Playground.Headless;
using Xunit;

namespace CopilotBridge.Playground;

public partial class ModelProfileProbe
{
    public static IEnumerable<object[]> Opus55CaptureCases()
    {
        var path = Environment.GetEnvironmentVariable("OPUS55_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;

        foreach (var axis in new[]
                 {
                     "thinking-disabled", "thinking-enabled",
                     "tool-choice-any", "tool-choice-tool",
                 })
            yield return [path, axis];
    }

    /// <summary>
    /// Recheck rewrite-causing rejections on a real Claude Code request. The
    /// capture must come from a Kind=ClientBehavior Opus 5.5 run. The original
    /// stream, system blocks, tools, messages and beta header are preserved;
    /// only one axis changes. Set OPUS55_CAPTURE_PATH to an upstream-req audit.
    /// </summary>
    [Theory]
    [MemberData(nameof(Opus55CaptureCases))]
    public async Task Opus55_RealClientCapture_RejectedAxisStaysRejected(
        string capturePath, string axis)
    {
        var capture = JsonNode.Parse(await File.ReadAllTextAsync(capturePath))!.AsObject();
        var original = capture["body"]!.AsObject();
        Assert.Equal("claude-opus-5.5", original["model"]!.GetValue<string>());
        Assert.True(original["stream"]!.GetValue<bool>());
        Assert.True(original["system"]!.AsArray().Count >= 3);
        Assert.True(original["tools"]!.AsArray().Count > 0);
        Assert.Equal("adaptive", original["thinking"]!["type"]!.GetValue<string>());

        var body = original.DeepClone().AsObject();
        string expectedError;
        switch (axis)
        {
            case "thinking-disabled":
                body["thinking"] = new JsonObject { ["type"] = "disabled" };
                expectedError = "thinking.type.disabled";
                break;
            case "thinking-enabled":
                body["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = 8192,
                };
                expectedError = "thinking.type.enabled";
                break;
            case "tool-choice-any":
                body["tool_choice"] = new JsonObject { ["type"] = "any" };
                expectedError = "tool_choice";
                break;
            case "tool-choice-tool":
                body["tool_choice"] = new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = body["tools"]!.AsArray()[0]!["name"]!.GetValue<string>(),
                };
                expectedError = "tool_choice";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis), axis, null);
        }

        var beta = capture["headers"]?["anthropic-beta"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(beta));
        using var client = new PlaygroundClient();
        var (status, response) = await client.TryPostMessagesAsync(
            body.ToJsonString(), anthropicBeta: beta);

        _output.WriteLine($"[claude-opus-5.5] captured {axis} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(response, 300)}");
        Assert.Equal(400, (int)status);
        Assert.Contains(expectedError, response, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> Opus55BridgeReplayCases()
    {
        var path = Environment.GetEnvironmentVariable("OPUS55_CAPTURE_PATH");
        if (string.IsNullOrWhiteSpace(path)) yield break;
        yield return [path, "thinking-disabled"];
        yield return [path, "tool-choice-any"];
    }

    /// <summary>
    /// Replay the same real request through the HTTP edge after changing one
    /// rejected axis. The upstream audit must show the supported shape.
    /// </summary>
    [Theory]
    [MemberData(nameof(Opus55BridgeReplayCases))]
    public async Task Opus55_RealClientCapture_BridgeCoercesRejectedAxis(
        string capturePath, string axis)
    {
        var capture = JsonNode.Parse(await File.ReadAllTextAsync(capturePath))!.AsObject();
        var body = capture["body"]!.DeepClone().AsObject();
        Assert.Equal("claude-opus-5.5", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        if (axis == "thinking-disabled")
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        else if (axis == "tool-choice-any")
            body["tool_choice"] = new JsonObject
            {
                ["type"] = "any",
                ["disable_parallel_tool_use"] = true,
            };
        else
            throw new ArgumentOutOfRangeException(nameof(axis), axis, null);

        string traceDir;
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        {
            await using (var bridge = await ServeProcess.StartAsync(
                             new ServeInvocation(ServeScenario.Passthrough)))
            {
                traceDir = bridge.TraceDir;
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{bridge.BaseUrl}/cc/v1/messages");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer dummy");
                request.Headers.TryAddWithoutValidation("anthropic-beta",
                    capture["headers"]!["anthropic-beta"]!.GetValue<string>());
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await http.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Equal(200, (int)response.StatusCode);
                Assert.Contains("message_start", responseBody, StringComparison.Ordinal);
            }
        }

        var audits = Directory.GetFiles(traceDir, "*upstream-req.json");
        var audit = Assert.Single(audits);
        var sent = JsonNode.Parse(await File.ReadAllTextAsync(audit))!["body"]!.AsObject();
        Assert.Equal("claude-opus-5.5", sent["model"]!.GetValue<string>());
        if (axis == "thinking-disabled")
        {
            Assert.Equal("adaptive", sent["thinking"]!["type"]!.GetValue<string>());
            Assert.Equal("low", sent["output_config"]!["effort"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal("auto", sent["tool_choice"]!["type"]!.GetValue<string>());
            Assert.True(sent["tool_choice"]!["disable_parallel_tool_use"]!.GetValue<bool>());
        }
    }
}
