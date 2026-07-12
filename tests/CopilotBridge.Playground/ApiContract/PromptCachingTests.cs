using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies that <c>cache_control: {type:"ephemeral"}</c> on a system block is
/// honored end-to-end by Copilot's <c>/v1/messages</c>. Sends the same request
/// twice; expects <c>cache_creation_input_tokens &gt; 0</c> on the first and
/// <c>cache_read_input_tokens &gt; 0</c> on the second.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class PromptCachingTests
{
    // ~6450 tokens — comfortably above Anthropic's 1024 (Sonnet/Opus) and 2048 (Haiku) thresholds.
    private static readonly string LongSystemPrompt = string.Concat(Enumerable.Repeat(
        "You are a meticulous technical assistant. " +
        "Provide concise, accurate answers without unnecessary preamble. " +
        "When unsure, state that explicitly rather than guessing. " +
        "Prefer concrete examples to abstract explanations. " +
        "Match the user's terseness in your replies. ", 100));

    [Theory]
    [InlineData("claude-sonnet-4.6")]
    [InlineData("claude-haiku-4.5")]
    public async Task EphemeralCache_HitsOnSecondIdenticalRequest(string model)
    {
        var json = BuildRequest(model).ToJsonString();

        using var client = new PlaygroundClient();
        var resp1 = await client.PostMessagesAsync(json);
        var u1 = ExtractUsage(resp1);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var resp2 = await client.PostMessagesAsync(json);
        var u2 = ExtractUsage(resp2);

        // Allow either a fresh cache (request 1 creates, request 2 reads) or a pre-warmed
        // cache (request 1 already reads). What matters is: request 2 reads cached tokens,
        // and the total cached prefix size is the same across both calls.
        var total1 = u1.CacheCreation + u1.CacheRead;
        var total2 = u2.CacheCreation + u2.CacheRead;
        Assert.True(total1 > 0,
            $"Expected request 1 to engage cache (creation or read); got creation={u1.CacheCreation}, read={u1.CacheRead}.");
        Assert.True(u2.CacheRead > 0,
            $"Expected request 2 to hit cache; got cache_creation={u2.CacheCreation}, cache_read={u2.CacheRead}");
        Assert.Equal(total1, total2);
    }

    private static JsonObject BuildRequest(string model) => new()
    {
        ["model"] = model,
        ["system"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = LongSystemPrompt,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            },
        },
        ["messages"] = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "What is your purpose? Answer in 5 words or fewer.",
            },
        },
        ["max_tokens"] = 64,
    };

    private record Usage(int InputTokens, int CacheCreation, int CacheRead, int OutputTokens);

    private static Usage ExtractUsage(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var u = doc.RootElement.GetProperty("usage");
        return new Usage(
            InputTokens: u.GetProperty("input_tokens").GetInt32(),
            CacheCreation: u.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0,
            CacheRead: u.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0,
            OutputTokens: u.GetProperty("output_tokens").GetInt32());
    }
}
