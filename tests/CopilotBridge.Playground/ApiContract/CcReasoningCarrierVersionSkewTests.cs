using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Wire-level proof that a reasoning carrier this build cannot read never reaches
/// Copilot. The carrier is durable CLIENT data — Claude Code persists transcripts and
/// replays them across <c>--resume</c>, compaction, and a bridge downgrade — so a build
/// WILL meet one written by a newer build. The only outcome that is silently wrong is
/// forwarding it as if it were provider-native encrypted content: the request still
/// returns 200 while the backend receives another build's private JSON in place of its
/// own reasoning blob. A unit test can assert the decision; only this can assert that
/// the bytes never left.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CcReasoningCarrierVersionSkewTests
{
    private readonly ITestOutputHelper _output;

    public CcReasoningCarrierVersionSkewTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task UnreadableCarrierVersion_IsRejected_AndNeverReachesTheUpstream()
    {
        // A well-formed envelope bearing a version this build does not implement,
        // exactly as a newer bridge would have written into the transcript.
        const string marker = "FUTURE-VERSION-CARRIER-PAYLOAD";
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "{\"v\":999,\"item\":{\"type\":\"reasoning\",\"encrypted_content\":\"" + marker + "\","
                + "\"summary\":[],\"field_from_a_later_build\":true}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var carrier = "cbridge_rr_7f3a9d2c:999:" + payload;

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(ServeScenario.CcToGpt));
        var reader = new BridgeLogReader(bridge.TraceDir);
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        var body = new JsonObject
        {
            ["model"] = "claude-opus-5",
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Say ok." },
                    },
                },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "redacted_thinking", ["data"] = carrier },
                        new JsonObject { ["type"] = "text", ["text"] = "Working." },
                    },
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "Now reply with exactly: ok" },
                    },
                },
            },
            ["stream"] = true,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{bridge.BaseUrl}/cc/v1/messages");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);

        _output.WriteLine($"status={(int)response.StatusCode} trace={bridge.TraceDir}");
        _output.WriteLine(responseBody.Length <= 600 ? responseBody : responseBody[..600]);

        // Client-side fault, reported as such — not a 502 blaming the backend.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request_error", responseBody, StringComparison.Ordinal);

        // The point of the whole change: the payload never left the bridge. The audit
        // sink writes asynchronously, so give it a bounded moment to land anything it
        // was going to write, then assert on every upstream body it recorded.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (reader.ReadNew().Any(e => e.UpstreamBody is not null)) break;
            await Task.Delay(250, cts.Token);
        }
        foreach (var entry in reader.ReadNew())
        {
            var upstream = entry.UpstreamBody?.ToJsonString() ?? "";
            Assert.DoesNotContain(marker, upstream, StringComparison.Ordinal);
            Assert.DoesNotContain("cbridge_rr_", upstream, StringComparison.Ordinal);
        }
    }
}
