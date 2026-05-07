using System.Text.Json;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies how Copilot reports output truncation when the response hits the
/// requested <c>max_tokens</c> ceiling: <c>stop_reason</c> must be
/// <c>"max_tokens"</c>, and <c>output_tokens</c> should be close to the limit.
/// Claude Code maps this to its <c>FinishedCompletionReason.Length</c> path
/// (<c>messagesApi.ts:1006-1018</c>).
/// </summary>
public class MaxTokensTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    public async Task TightMaxTokens_TruncatesWithMaxTokensStop(string model)
    {
        // A prompt the model cannot satisfy in 16 tokens, forcing truncation.
        var payload = $$"""
          {
            "model": "{{model}}",
            "messages": [
              { "role": "user", "content": "Count from 1 to 100, one number per line." }
            ],
            "max_tokens": 16
          }
          """;

        using var client = new PlaygroundClient();
        var response = await client.PostMessagesAsync(payload);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal("max_tokens", root.GetProperty("stop_reason").GetString());

        var outputTokens = root.GetProperty("usage").GetProperty("output_tokens").GetInt32();
        // Allow a small overshoot — tokenizers don't always cleanly stop at the boundary,
        // but any reasonable implementation stays close to the requested ceiling.
        Assert.InRange(outputTokens, 1, 32);
    }
}
