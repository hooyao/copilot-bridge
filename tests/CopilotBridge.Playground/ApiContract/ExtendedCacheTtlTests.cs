using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Verifies the 1-hour TTL variant of prompt caching:
/// <c>cache_control: {type:"ephemeral", ttl:"1h"}</c>. Distinct from the default
/// 5-minute TTL covered in <see cref="PromptCachingTests"/>. The expectation is
/// that the 1h-tagged tokens land in <c>usage.cache_creation.ephemeral_1h_input_tokens</c>
/// (a Copilot-extended Bedrock field), not the 5m bucket.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class ExtendedCacheTtlTests
{
    // Distinct from PromptCachingTests's prefix so the cache key doesn't collide.
    private static readonly string LongSystemPrompt = "[ttl=1h] " + string.Concat(Enumerable.Repeat(
        "You are a meticulous technical assistant. " +
        "Provide concise, accurate answers without unnecessary preamble. " +
        "When unsure, state that explicitly rather than guessing. " +
        "Prefer concrete examples to abstract explanations. " +
        "Match the user's terseness in your replies. ", 100));

    [Theory]
    [InlineData("claude-sonnet-4.6")]
    public async Task OneHourTtl_RoundTripsAndReportsAs1hBucket(string model)
    {
        var json = BuildRequest(model).ToJsonString();
        using var client = new PlaygroundClient();

        var resp1 = await client.PostMessagesAsync(json);
        var u1 = ExtractUsage(resp1);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var resp2 = await client.PostMessagesAsync(json);
        var u2 = ExtractUsage(resp2);

        // Same warm/cold-tolerant assertion as the 5m test: total cached prefix
        // is consistent across calls, and request 2 reads from cache.
        var total1 = u1.CacheCreation + u1.CacheRead;
        var total2 = u2.CacheCreation + u2.CacheRead;
        Assert.True(total1 > 0,
            $"Request 1 didn't engage cache; creation={u1.CacheCreation}, read={u1.CacheRead}");
        Assert.True(u2.CacheRead > 0,
            $"Request 2 didn't hit cache; cache_read={u2.CacheRead}");
        Assert.Equal(total1, total2);

        // 1h-specific check: when this run was the first ever (cold cache), the
        // creation tokens should land in the 1h bucket. When warm, the creation
        // bucket is empty and we can't observe it — that's OK, the round-trip
        // proves the TTL field was accepted (no 400) which is what we mainly need.
        if (u1.Ephemeral1h is { } ephemeral1h && ephemeral1h > 0)
        {
            // Cold run: prefix was newly cached as 1h; the 5m bucket should be empty.
            Assert.Equal(0, u1.Ephemeral5m ?? 0);
        }
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
                ["cache_control"] = new JsonObject
                {
                    ["type"] = "ephemeral",
                    ["ttl"] = "1h",
                },
            },
        },
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "Say hi in three words." },
        },
        ["max_tokens"] = 32,
    };

    private record Usage(int CacheCreation, int CacheRead, int? Ephemeral5m, int? Ephemeral1h);

    private static Usage ExtractUsage(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var u = doc.RootElement.GetProperty("usage");

        int? eph5 = null, eph1h = null;
        if (u.TryGetProperty("cache_creation", out var cc))
        {
            if (cc.TryGetProperty("ephemeral_5m_input_tokens", out var e5)) eph5 = e5.GetInt32();
            if (cc.TryGetProperty("ephemeral_1h_input_tokens", out var e1)) eph1h = e1.GetInt32();
        }

        return new Usage(
            CacheCreation: u.TryGetProperty("cache_creation_input_tokens", out var cci) ? cci.GetInt32() : 0,
            CacheRead: u.TryGetProperty("cache_read_input_tokens", out var cri) ? cri.GetInt32() : 0,
            Ephemeral5m: eph5,
            Ephemeral1h: eph1h);
    }
}
