using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies Copilot accepts the <c>context_management</c> request field paired with
/// the <c>anthropic-beta: context-management-2025-06-27</c> header. We don't try to
/// trigger <c>applied_edits</c> here — that requires a multi-turn conversation with
/// many tool uses, which is outside this experiment's scope. Just proving "field
/// accepted" is enough ground truth for the bridge passthrough.
/// </summary>
[Trait("Category", "Integration")]
public class ContextManagementTests
{
    private const string BetaHeader = "context-management-2025-06-27";

    [Theory]
    [InlineData("claude-sonnet-4.6")]
    public async Task ContextManagementField_AcceptedWithBeta(string model)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "Say hi briefly." },
            },
            ["max_tokens"] = 64,
            ["context_management"] = new JsonObject
            {
                ["edits"] = new JsonArray
                {
                    // Won't trigger here (need 100k input tokens of tool_use history),
                    // but Copilot still has to accept the field structure.
                    new JsonObject
                    {
                        ["type"] = "clear_tool_uses_20250919",
                        ["trigger"] = new JsonObject
                        {
                            ["type"] = "input_tokens",
                            ["value"] = 100000,
                        },
                        ["keep"] = new JsonObject
                        {
                            ["type"] = "tool_uses",
                            ["value"] = 3,
                        },
                    },
                },
            },
        };

        using var client = new PlaygroundClient();
        var responseBody = await client.PostMessagesAsync(
            payload.ToJsonString(),
            anthropicBeta: BetaHeader);
        using var doc = JsonDocument.Parse(responseBody);

        Assert.Equal("message", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("end_turn", doc.RootElement.GetProperty("stop_reason").GetString());
    }
}
