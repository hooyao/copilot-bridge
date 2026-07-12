using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Two-turn round-trip: send a request with a tool definition + a prompt that
/// requires the tool, parse the model's <c>tool_use</c> block, execute locally,
/// send the <c>tool_result</c> back, and verify the model produces a final
/// natural-language answer. This exercises the full Anthropic tool protocol
/// the bridge will need to passthrough.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class ToolUseTests
{
    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    public async Task ToolUseAndResult_RoundTripsToFinalAnswer(string model)
    {
        const string userPrompt = "What is 137 + 258? Use the add_numbers tool to compute it, then state the result.";
        using var client = new PlaygroundClient();

        // ---- Turn 1: model decides to call the tool ----
        var turn1 = BuildRequest(model, new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
        });
        var resp1Body = await client.PostMessagesAsync(turn1.ToJsonString());
        var resp1 = JsonNode.Parse(resp1Body)!;

        Assert.Equal("tool_use", resp1["stop_reason"]?.GetValue<string>());

        var assistantContent = resp1["content"]!.AsArray();
        var toolUseBlock = assistantContent.FirstOrDefault(b => b?["type"]?.GetValue<string>() == "tool_use");
        Assert.NotNull(toolUseBlock);
        Assert.Equal("add_numbers", toolUseBlock!["name"]!.GetValue<string>());

        var toolUseId = toolUseBlock["id"]!.GetValue<string>();
        var input = toolUseBlock["input"]!;
        var a = input["a"]!.GetValue<int>();
        var b = input["b"]!.GetValue<int>();
        var sum = a + b;

        Assert.Equal(137, a);
        Assert.Equal(258, b);
        Assert.Equal(395, sum);

        // ---- Turn 2: send tool_result back, expect final text answer ----
        var turn2Messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
            new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = resp1["content"]!.DeepClone(),
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = toolUseId,
                        ["content"] = sum.ToString(),
                    },
                },
            },
        };
        var turn2 = BuildRequest(model, turn2Messages);
        var resp2Body = await client.PostMessagesAsync(turn2.ToJsonString());
        var resp2 = JsonNode.Parse(resp2Body)!;

        Assert.Equal("end_turn", resp2["stop_reason"]?.GetValue<string>());

        var finalContent = resp2["content"]!.AsArray();
        var textBlock = finalContent.FirstOrDefault(b => b?["type"]?.GetValue<string>() == "text");
        Assert.NotNull(textBlock);
        var finalText = textBlock!["text"]!.GetValue<string>();
        Assert.Contains("395", finalText);
    }

    private static JsonObject NewAddNumbersTool() => new()
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

    private static JsonObject BuildRequest(string model, JsonArray messages) => new()
    {
        ["model"] = model,
        ["messages"] = messages,
        ["tools"] = new JsonArray { NewAddNumbersTool() },
        ["max_tokens"] = 1024,
    };
}
