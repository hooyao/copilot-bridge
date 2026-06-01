using System.Text.Json;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies <c>thinking: {type:"enabled", budget_tokens:N}</c> (the explicit-budget
/// flavor, distinct from <c>type:"adaptive"</c> in <see cref="EffortLevelsTests"/>).
/// With explicit thinking, the model is required to emit thinking blocks on any
/// non-trivial prompt — making it a reliable assertion target.
/// </summary>
[Trait("Category", "Integration")]
public class ExplicitThinkingTests
{
    // claude-opus-4.7-1m-internal returns HTTP 400 when sent thinking:{type:"enabled"};
    // it appears to require thinking:{type:"adaptive"} + output_config.effort
    // (see EffortLevelsTests) and rejects the explicit-budget shape entirely.
    // Sonnet/Haiku models accept both flavors.
    [Theory]
    [InlineData("claude-sonnet-4.6", 2048)]
    public async Task ExplicitThinkingBudget_ProducesThinkingBlock(string model, int budgetTokens)
    {
        // A multi-step word problem reliably triggers thinking when explicit budget is set.
        var payload = $$"""
          {
            "model": "{{model}}",
            "messages": [
              { "role": "user", "content": "A person has 24 marbles, gives away 7, finds 13 more, then loses half of what remains. How many marbles does the person have at the end? Show your work briefly." }
            ],
            "max_tokens": 8192,
            "thinking": { "type": "enabled", "budget_tokens": {{budgetTokens}} }
          }
          """;

        using var client = new PlaygroundClient();
        var response = await client.PostMessagesAsync(payload);
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;

        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());

        var hasThinking = false;
        var hasText = false;
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            var type = block.GetProperty("type").GetString();
            if (type == "thinking") hasThinking = true;
            if (type == "text") hasText = true;
        }
        Assert.True(hasThinking, $"Expected thinking block with explicit budget={budgetTokens}");
        Assert.True(hasText, "Expected final text block");
    }
}
