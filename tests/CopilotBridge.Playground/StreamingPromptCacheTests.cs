using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Streaming-mode prompt-cache verification — complements
/// <see cref="PromptCachingTests"/> which uses the non-streaming path.
/// Question being answered: does the presence of <c>[DONE]</c> in the response
/// stream affect the next request's cache key? (Hypothesis: no, because
/// <c>[DONE]</c> is a transport marker that never enters the reconstructed
/// assistant message — the next request's body is determined by what the
/// caller assembles, not by what was in the previous response's wire stream.)
///
/// Sends the same <c>stream:true</c> body twice. If the cache works, the
/// second response's <c>message_delta.usage.cache_read_input_tokens</c> is
/// positive. If it doesn't, either prompt caching is broken on the streaming
/// path or the <c>[DONE]</c> marker somehow contaminates the server-side
/// cache key (which we don't expect).
/// </summary>
public class StreamingPromptCacheTests
{
    private readonly ITestOutputHelper _output;

    public StreamingPromptCacheTests(ITestOutputHelper output) => _output = output;

    // ~6400 tokens — comfortably above the 1024-token min for sonnet.
    private static readonly string LongSystemPrompt = string.Concat(Enumerable.Repeat(
        "You are a precise technical assistant. Reply only with what is asked. " +
        "When data is required, return it verbatim without paraphrasing. " +
        "Avoid filler words. Avoid restating the question. ", 100));

    [Fact]
    public async Task SameStreamingBody_HitsCacheOnSecondRequest()
    {
        var body = BuildStreamingBody();

        using var client = new PlaygroundClient();

        var (sawDone1, cacheRead1, cacheCreation1) = await SendAndCollect(client, body);
        _output.WriteLine($"req1: saw_done={sawDone1} cache_creation={cacheCreation1} cache_read={cacheRead1}");

        await Task.Delay(TimeSpan.FromSeconds(2));

        var (sawDone2, cacheRead2, cacheCreation2) = await SendAndCollect(client, body);
        _output.WriteLine($"req2: saw_done={sawDone2} cache_creation={cacheCreation2} cache_read={cacheRead2}");

        // Sanity: confirm Copilot ships [DONE] in streaming mode (otherwise this
        // whole test setup misses the thing we're trying to verify).
        Assert.True(sawDone1, "Expected first response to include the [DONE] terminator.");
        Assert.True(sawDone2, "Expected second response to include the [DONE] terminator.");

        // The real assertion: server cached the prefix from request 1, served
        // it on request 2. [DONE] presence on the wire didn't break this.
        Assert.True(cacheRead2 > 0,
            $"Expected req2 to hit cache; got cache_creation={cacheCreation2}, cache_read={cacheRead2}. " +
            "If this fails, the server's cache key may include something other than the request body.");
    }

    private static string BuildStreamingBody() => new JsonObject
    {
        ["model"] = "claude-sonnet-4.6",
        ["stream"] = true,
        ["max_tokens"] = 32,
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
            new JsonObject { ["role"] = "user", ["content"] = "Reply with: ok" },
        },
    }.ToJsonString();

    /// <summary>
    /// Reads the streaming response and returns: whether <c>[DONE]</c> was
    /// observed, plus cache_read and cache_creation token counts pulled out of
    /// the final <c>message_delta</c> event's usage (or message_start as a
    /// fallback if message_delta wasn't present).
    /// </summary>
    private static async Task<(bool SawDone, int CacheRead, int CacheCreation)> SendAndCollect(
        PlaygroundClient client, string body)
    {
        var sawDone = false;
        var cacheRead = 0;
        var cacheCreation = 0;

        await foreach (var evt in client.PostMessagesStreamAsync(body))
        {
            if (evt.EventType == "message" && evt.Data == "[DONE]")
            {
                sawDone = true;
                continue;
            }
            // message_start has initial usage; message_delta has the final.
            // We take whichever has non-zero counters last.
            try
            {
                var doc = JsonDocument.Parse(evt.Data);
                var root = doc.RootElement;
                JsonElement usage = default;
                if (root.TryGetProperty("usage", out var directUsage))
                    usage = directUsage;
                else if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var nestedUsage))
                    usage = nestedUsage;

                if (usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.GetInt32() > 0)
                        cacheRead = cr.GetInt32();
                    if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.GetInt32() > 0)
                        cacheCreation = cc.GetInt32();
                }
            }
            catch (JsonException)
            {
                // Not a JSON event (e.g. some Copilot extension) — skip.
            }
        }

        return (sawDone, cacheRead, cacheCreation);
    }
}
