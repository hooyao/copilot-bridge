using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies parallel tool calls — Claude emits multiple <c>tool_use</c> blocks
/// in a single assistant turn when given multiple independent tasks. Bridge will
/// need to round-trip all of them with multiple <c>tool_result</c> blocks in a
/// single user message.
/// </summary>
[Trait("Category", "Integration")]
public class ParallelToolUseTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    public async Task ParallelToolCalls_RoundTripBothToolsToFinalAnswer(string model)
    {
        const string userPrompt =
            "Use the provided tools to compute these two things, then state both results clearly: " +
            "(1) 137 + 258, and (2) 17 * 23.";

        using var client = new PlaygroundClient();

        // ---- Turn 1: expect two tool_use blocks ----
        var turn1 = BuildRequest(model, new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
        });
        var resp1Body = await client.PostMessagesAsync(turn1.ToJsonString());
        var resp1 = JsonNode.Parse(resp1Body)!;

        Assert.Equal("tool_use", resp1["stop_reason"]?.GetValue<string>());

        var assistantContent = resp1["content"]!.AsArray();
        var toolUses = assistantContent
            .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
            .ToList();
        Assert.True(toolUses.Count >= 2,
            $"Expected at least 2 parallel tool_use blocks; got {toolUses.Count}. Content: {resp1["content"]}");

        // Execute both tools locally and build a tool_result array.
        var toolResults = new JsonArray();
        foreach (var tu in toolUses)
        {
            var name = tu!["name"]!.GetValue<string>();
            var input = tu["input"]!;
            int result = name switch
            {
                "add_numbers" => input["a"]!.GetValue<int>() + input["b"]!.GetValue<int>(),
                "multiply_numbers" => input["a"]!.GetValue<int>() * input["b"]!.GetValue<int>(),
                _ => throw new InvalidOperationException($"Unexpected tool: {name}"),
            };
            toolResults.Add(new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = tu["id"]!.GetValue<string>(),
                ["content"] = result.ToString(),
            });
        }

        // ---- Turn 2: send both tool_results in a single user message ----
        var turn2 = BuildRequest(model, new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
            new JsonObject { ["role"] = "assistant", ["content"] = resp1["content"]!.DeepClone() },
            new JsonObject { ["role"] = "user", ["content"] = toolResults },
        });
        var resp2Body = await client.PostMessagesAsync(turn2.ToJsonString());
        var resp2 = JsonNode.Parse(resp2Body)!;

        Assert.Equal("end_turn", resp2["stop_reason"]?.GetValue<string>());

        var finalText = string.Concat(resp2["content"]!.AsArray()
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]!.GetValue<string>()));

        Assert.Contains("395", finalText);   // 137 + 258
        Assert.Contains("391", finalText);   // 17 * 23
    }

    private static JsonObject NewAddTool() => new()
    {
        ["name"] = "add_numbers",
        ["description"] = "Add two integers and return their sum.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["a"] = new JsonObject { ["type"] = "integer" },
                ["b"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray { "a", "b" },
        },
    };

    private static JsonObject NewMultiplyTool() => new()
    {
        ["name"] = "multiply_numbers",
        ["description"] = "Multiply two integers and return their product.",
        ["input_schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["a"] = new JsonObject { ["type"] = "integer" },
                ["b"] = new JsonObject { ["type"] = "integer" },
            },
            ["required"] = new JsonArray { "a", "b" },
        },
    };

    private static JsonObject BuildRequest(string model, JsonArray messages) => new()
    {
        ["model"] = model,
        ["messages"] = messages,
        ["tools"] = new JsonArray { NewAddTool(), NewMultiplyTool() },
        ["max_tokens"] = 1024,
    };
}
