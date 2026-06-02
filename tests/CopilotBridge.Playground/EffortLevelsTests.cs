using System.Text.Json;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies that Copilot's <c>/v1/messages</c> accepts adaptive thinking with each
/// reasoning_effort value advertised by the model. Each successful test = one
/// piece of ground truth for what the bridge can later forward verbatim.
/// </summary>
[Trait("Category", "Integration")]
public class EffortLevelsTests
{
    [Theory]
    [InlineData("claude-opus-4.7-1m-internal", "low")]
    [InlineData("claude-opus-4.7-1m-internal", "medium")]
    [InlineData("claude-opus-4.7-1m-internal", "high")]
    [InlineData("claude-opus-4.7-1m-internal", "xhigh")]
    public async Task AdaptiveThinking_AcceptsEffort(string model, string level)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "messages": [
              { "role": "user", "content": "What is 17 * 23? Reason briefly first, then state the answer." }
            ],
            "max_tokens": 4096,
            "thinking": { "type": "adaptive" },
            "output_config": { "effort": "{{level}}" }
          }
          """;

        using var client = new PlaygroundClient();
        var response = await client.PostMessagesAsync(payload);

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.True(root.GetProperty("content").GetArrayLength() > 0,
            $"Response had no content blocks. Full body: {response}");
    }
}
