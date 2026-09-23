using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Live HTTP-edge checks for the currently served Claude model. The large
/// prompt capacity itself is proven by ModelProfileProbe.Opus55_LargePrompt_*;
/// these cases guard the bridge's model, beta, and thinking rewrites.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class OneMillionContextRoutingTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public OneMillionContextRoutingTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("context-1m-2025-08-07")]
    public async Task Opus55_ClientIdNormalizes_AndOneMillionBetaPassesThrough(string? beta)
    {
        var (upstream, _) = await PostAsync(beta: beta);

        Assert.Equal("claude-opus-5.5", upstream["body"]!["model"]!.GetValue<string>());
        var sentBeta = upstream["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        if (beta is null)
            Assert.DoesNotContain("context-1m-2025-08-07", sentBeta, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(beta, sentBeta, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnrecognizedBeta_PassesThroughVerbatim()
    {
        const string beta = "extended-cache-ttl-2025-04-11";
        var (upstream, _) = await PostAsync(beta: beta);

        Assert.Equal("claude-opus-5.5", upstream["body"]!["model"]!.GetValue<string>());
        Assert.Contains(beta, upstream["headers"]!["anthropic-beta"]!.GetValue<string>());
    }

    [Fact]
    public async Task Opus55_EnabledThinking_BecomesAdaptiveWithDerivedEffort()
    {
        var (upstream, _) = await PostAsync(thinking: new JsonObject
        {
            ["type"] = "enabled",
            ["budget_tokens"] = 16384,
        });

        Assert.Equal("claude-opus-5.5", upstream["body"]!["model"]!.GetValue<string>());
        Assert.Equal("adaptive", upstream["body"]!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("medium", upstream["body"]!["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Opus55_DisabledThinking_BecomesAdaptiveAtLowEffort()
    {
        var (upstream, _) = await PostAsync(
            thinking: new JsonObject { ["type"] = "disabled" },
            effort: "max");

        Assert.Equal("adaptive", upstream["body"]!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("low", upstream["body"]!["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Opus55_AdaptiveThinking_MaxEffortPassesThrough()
    {
        var (upstream, _) = await PostAsync(
            thinking: new JsonObject { ["type"] = "adaptive" },
            effort: "max");

        Assert.Equal("adaptive", upstream["body"]!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("max", upstream["body"]!["output_config"]!["effort"]!.GetValue<string>());
    }

    private async Task<(JsonObject Upstream, HttpStatusCode Status)> PostAsync(
        string? beta = null, JsonObject? thinking = null, string? effort = null)
    {
        var marker = $"probe-{Guid.NewGuid():N}";
        var body = new JsonObject
        {
            ["model"] = "claude-opus-5-5", // official Claude Code id
            ["max_tokens"] = 32768,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "reply: ok " + marker },
            },
        };
        if (thinking is not null) body["thinking"] = thinking;
        if (effort is not null)
            body["output_config"] = new JsonObject { ["effort"] = effort };

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        if (beta is not null)
            request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"bridge → client: HTTP {(int)response.StatusCode} body={Truncate(responseBody, 200)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var upstream = FindUpstreamRequestByMarker(marker);
        Assert.NotNull(upstream);
        return (upstream!, response.StatusCode);
    }

    private JsonObject? FindUpstreamRequestByMarker(string marker)
    {
        // Audits are written asynchronously into a shared trace directory.
        // Correlate by the unique user-message marker, not by file order.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var files = Directory.GetFiles(_bridge.LogDirectory, "*-upstream-req.json")
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (var file in files)
            {
                string raw;
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    raw = reader.ReadToEnd();
                }
                catch (IOException) { continue; }
                if (!raw.Contains(marker, StringComparison.Ordinal)) continue;
                try { return JsonNode.Parse(raw)?.AsObject(); }
                catch (JsonException) { /* audit may still be flushing */ }
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private static string Truncate(string value, int limit) =>
        value.Length > limit ? value[..limit] + "…" : value;
}
