using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Copilot's Opus 5.5 count-tokens endpoint accepts controls its Messages
/// endpoint rejects. Keep the native count route byte-faithful to that observed
/// contract rather than applying Messages-only coercions to a different API.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class Opus55CountTokensHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;

    public Opus55CountTokensHeadlessTests(BridgeFixture bridge) => _bridge = bridge;

    [Theory]
    [InlineData("tool-choice-any")]
    [InlineData("thinking-disabled")]
    public async Task NativeCountTokens_PreservesAcceptedControl(string axis)
    {
        var marker = $"probe-{Guid.NewGuid():N}";
        var body = new JsonObject
        {
            ["model"] = "claude-opus-5-5", // client id; Copilot accepts this alias
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "hi " + marker },
            },
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "lookup",
                    ["description"] = "Look up a query",
                    ["input_schema"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject(),
                    },
                },
            },
        };
        if (axis == "tool-choice-any")
            body["tool_choice"] = new JsonObject { ["type"] = "any" };
        else
            body["thinking"] = new JsonObject { ["type"] = "disabled" };

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await http.PostAsync(
            $"{_bridge.BaseUrl}/cc/v1/messages/count_tokens",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var responseBody = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(JsonNode.Parse(responseBody)?["input_tokens"]?.GetValue<int>() > 0);

        var sent = FindUpstreamRequestByMarker(marker);
        Assert.NotNull(sent);
        Assert.Equal("claude-opus-5-5", sent!["body"]!["model"]!.GetValue<string>());
        if (axis == "tool-choice-any")
            Assert.Equal("any", sent["body"]!["tool_choice"]!["type"]!.GetValue<string>());
        else
            Assert.Equal("disabled", sent["body"]!["thinking"]!["type"]!.GetValue<string>());
    }

    private JsonObject? FindUpstreamRequestByMarker(string marker)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var files = Directory.GetFiles(_bridge.LogDirectory, "*-upstream-req.json")
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (var file in files)
            {
                string raw;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    raw = reader.ReadToEnd();
                }
                catch (IOException) { continue; }
                if (!raw.Contains(marker, StringComparison.Ordinal)) continue;
                try { return JsonNode.Parse(raw)?.AsObject(); }
                catch (JsonException) { /* audit may still be flushing */ }
            }
            Thread.Sleep(50);
        }
        return null;
    }
}
