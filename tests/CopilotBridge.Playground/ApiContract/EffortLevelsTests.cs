using System.Text.Json;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies that Copilot's <c>/v1/messages</c> accepts adaptive thinking with each
/// reasoning_effort value advertised by the model. Each successful test = one
/// piece of ground truth for what the bridge can later forward verbatim.
/// </summary>
/// <remarks>
/// Retargeted from <c>claude-opus-4.7-1m-internal</c> to the opus-4.7 BASE id in
/// the 2026-07 reconciliation: Copilot retired the <c>-1m-internal</c> variant
/// (400 — <see cref="ModelProfileProbe.RetiredCandidate_LivenessProbe"/>) and the
/// base id now serves the same 1M context and the same effort range, so the
/// contract under test is preserved on a live id rather than deleted.
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class EffortLevelsTests
{
    [Theory]
    [InlineData("claude-opus-4.7", "low")]
    [InlineData("claude-opus-4.7", "medium")]
    [InlineData("claude-opus-4.7", "high")]
    [InlineData("claude-opus-4.7", "xhigh")]
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
