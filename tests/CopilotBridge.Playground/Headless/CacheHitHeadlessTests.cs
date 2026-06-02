using System.Net.ServerSentEvents;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Verifies bridge's <c>DoneFilterStage</c> is cache-neutral: posting the same
/// streaming body with <c>cache_control</c> twice through the bridge hits
/// Copilot's prompt cache on the second call.
///
/// Drives the test via raw <see cref="HttpClient"/> through the in-process
/// bridge rather than <c>claude.exe</c> because claude.exe's <c>--bare</c>
/// mode strips automatic <c>cache_control</c> injection — that path can't
/// engage prompt caching at all, so claude.exe-driven cache tests would only
/// be testing <c>--bare</c>'s behavior, not the bridge's. This test still
/// fully exercises the bridge's request pipeline + response SSE stream +
/// DoneFilterStage.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CacheHitHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CacheHitHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    // ~6400 tokens — comfortably above sonnet's 1024 minimum.
    private static readonly string LongSystemPrompt = string.Concat(Enumerable.Repeat(
        "You are a precise technical assistant. Reply only with what is asked. " +
        "When data is required, return it verbatim without paraphrasing. " +
        "Avoid filler words. Avoid restating the question. ", 100));

    [Fact]
    public async Task TwoStreamingPosts_ThroughBridge_SecondHitsCache_AndDoneIsFiltered()
    {
        var body = BuildBodyWithCacheControl();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        var (cacheCreation1, cacheRead1, sawDone1) = await PostStreamingThroughBridge(http, body);
        _output.WriteLine($"req1: cache_creation={cacheCreation1} cache_read={cacheRead1} bridge_emitted_done={sawDone1}");

        await Task.Delay(TimeSpan.FromSeconds(2));

        var (cacheCreation2, cacheRead2, sawDone2) = await PostStreamingThroughBridge(http, body);
        _output.WriteLine($"req2: cache_creation={cacheCreation2} cache_read={cacheRead2} bridge_emitted_done={sawDone2}");

        // Both responses MUST be missing [DONE] — that's DoneFilterStage's job.
        Assert.False(sawDone1, "Bridge should have filtered [DONE] from response 1 but client saw it.");
        Assert.False(sawDone2, "Bridge should have filtered [DONE] from response 2 but client saw it.");

        // The whole point: bridge's [DONE] filter is cache-neutral.
        Assert.True(cacheRead2 > 0,
            $"Expected req2 to hit Copilot's prompt cache; got cache_creation={cacheCreation2} cache_read={cacheRead2}. " +
            "If this fails, DoneFilterStage or some other response-side stage is somehow influencing the next request's cache key.");
    }

    private static string BuildBodyWithCacheControl() => new JsonObject
    {
        ["model"] = "claude-sonnet-4-6",
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
    /// POSTs to the bridge's <c>/cc/v1/messages</c> endpoint and reads the SSE
    /// stream. Returns: final cache_creation / cache_read counts plus a flag
    /// indicating whether the client observed the <c>[DONE]</c> terminator
    /// (which should be filtered by the bridge).
    /// </summary>
    private async Task<(int CacheCreation, int CacheRead, bool SawDone)> PostStreamingThroughBridge(
        HttpClient http, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var cacheCreation = 0;
        var cacheRead = 0;
        var sawDone = false;

        var stream = await resp.Content.ReadAsStreamAsync();
        var parser = SseParser.Create(stream);
        await foreach (var item in parser.EnumerateAsync())
        {
            if (item.EventType == "message" && item.Data == "[DONE]")
            {
                sawDone = true;
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(item.Data);
                var root = doc.RootElement;
                JsonElement usage = default;
                if (root.TryGetProperty("usage", out var direct))
                    usage = direct;
                else if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var nested))
                    usage = nested;
                if (usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("cache_creation_input_tokens", out var cc) && cc.GetInt32() > cacheCreation)
                        cacheCreation = cc.GetInt32();
                    if (usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.GetInt32() > cacheRead)
                        cacheRead = cr.GetInt32();
                }
            }
            catch (JsonException) { /* non-JSON data — ignore */ }
        }

        return (cacheCreation, cacheRead, sawDone);
    }
}
