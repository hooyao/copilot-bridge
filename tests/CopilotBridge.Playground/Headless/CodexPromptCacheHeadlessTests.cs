using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Codex/Responses prompt-cache verification THROUGH THE BRIDGE — the live
/// integration analog of the unit-level
/// <c>CodexStreamRoundTripTests.A6c_*</c> contract tests. Drives the bridge's
/// <c>/codex/responses</c> endpoint (so the full client→T1→IR→Copilot and
/// Copilot→T3→IR→T4→client round trip runs against real Copilot) with the SAME
/// large gpt-5.5 body twice, then asserts the SECOND response's
/// <c>usage.input_tokens_details.cached_tokens</c> is positive.
///
/// Regression guard for the telemetry bug where the Responses→IR→Responses hub
/// round trip dropped Copilot's <c>cached_tokens</c> (T3 never captured it, T4
/// hard-coded 0), so Codex showed 0% prompt cache even on a near-total cache hit.
/// The unit tests prove the translators carry the count; this proves it survives
/// end-to-end against the real backend (the layer the unit tests can't reach:
/// routing, the live strategy, the endpoint's SSE relay).
/// </summary>
/// <remarks>
/// In-process bridge on an ephemeral non-8765 port (<see cref="BridgeFixture"/>),
/// talking to live Copilot. Integration-tagged: skipped in CI (no Copilot creds).
/// Mirrors <c>StreamingPromptCacheTests</c>'s two-shot shape; same inherent
/// dependency on the backend actually serving the prefix from cache.
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexPromptCacheHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexPromptCacheHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    // ~6000+ tokens of stable prefix — comfortably above the prompt-cache minimum
    // so the second identical request is eligible for a cache hit.
    private static readonly string LongInstructions = string.Concat(Enumerable.Repeat(
        "You are a precise coding assistant running inside a reverse-proxy integration test. " +
        "Answer tersely with exactly what is asked. Do not paraphrase. Do not restate the question. ", 120));

    [Fact]
    public async Task SameCodexBody_SurfacesCacheHitToCodexOnSecondRequest()
    {
        var body = BuildBody();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        var u1 = await SendAndReadUsageAsync(client, body);
        _output.WriteLine($"req1: input={u1.Input} cached={u1.Cached} output={u1.Output} reasoning={u1.Reasoning} total={u1.Total}");

        // Brief gap so the server-side cache from req1 is in place (mirrors
        // StreamingPromptCacheTests). The cache key is stable across both shots.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var u2 = await SendAndReadUsageAsync(client, body);
        _output.WriteLine($"req2: input={u2.Input} cached={u2.Cached} output={u2.Output} reasoning={u2.Reasoning} total={u2.Total}");

        // The real assertion: Copilot served the prefix from cache on req2, and
        // the bridge surfaced that to Codex. Pre-fix this was always 0 regardless
        // of the actual cache hit.
        Assert.True(u2.Cached > 0,
            $"Expected req2 to report a cache hit through the bridge; got cached={u2.Cached}. "
            + "If Copilot cached the prefix but this is 0, the IR round trip is dropping cached_tokens (the bug).");

        // Surfaced usage must be internally consistent: cached is a SUBSET of
        // input_tokens, and total = input + output (cached/reasoning are subsets,
        // never added into the total).
        Assert.True(u2.Cached <= u2.Input, $"cached ({u2.Cached}) must not exceed input ({u2.Input})");
        Assert.True(u2.Reasoning <= u2.Output, $"reasoning ({u2.Reasoning}) must not exceed output ({u2.Output})");
        Assert.Equal(u2.Input + u2.Output, u2.Total);
    }

    private static string BuildBody() => new JsonObject
    {
        ["model"] = "gpt-5.5",
        ["stream"] = true,
        ["store"] = false,
        // Stable cache key across both shots — what Codex Desktop sends per session.
        ["prompt_cache_key"] = "bridge-codex-cache-regression-test",
        ["max_output_tokens"] = 128,
        ["reasoning"] = new JsonObject { ["effort"] = "low" },
        ["instructions"] = LongInstructions,
        ["input"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = "Reply with exactly: ok" },
                },
            },
        },
    }.ToJsonString();

    /// <summary>
    /// POST the body to the bridge's <c>/codex/responses</c>, stream the SSE, and
    /// return the usage off the terminal response.completed/incomplete event.
    /// </summary>
    private async Task<Usage> SendAndReadUsageAsync(HttpClient client, string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/codex/responses");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new Xunit.Sdk.XunitException(
                $"bridge /codex/responses returned {(int)resp.StatusCode}: {Trunc(err, 800)}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        var parser = SseParser.Create(stream);
        Usage? usage = null;
        await foreach (var item in parser.EnumerateAsync())
        {
            if (TryReadTerminalUsage(item.Data, out var u)) usage = u;
        }
        return usage ?? throw new Xunit.Sdk.XunitException(
            "no terminal response.completed/incomplete with usage in the bridge stream");
    }

    private static bool TryReadTerminalUsage(string data, out Usage usage)
    {
        usage = default;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is not ("response.completed" or "response.incomplete")) return false;
            if (!root.TryGetProperty("response", out var resp)
                || !resp.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return false;
            usage = new Usage(
                Scalar(u, "input_tokens"),
                Nested(u, "input_tokens_details", "cached_tokens"),
                Scalar(u, "output_tokens"),
                Nested(u, "output_tokens_details", "reasoning_tokens"),
                Scalar(u, "total_tokens"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long Scalar(JsonElement o, string p) =>
        o.TryGetProperty(p, out var v) && v.TryGetInt64(out var n) ? n : 0;

    private static long Nested(JsonElement o, string p, string c) =>
        o.TryGetProperty(p, out var d) && d.ValueKind == JsonValueKind.Object ? Scalar(d, c) : 0;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private readonly record struct Usage(long Input, long Cached, long Output, long Reasoning, long Total);
}
