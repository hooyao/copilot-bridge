using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Verifies the 1M-context routing rules:
/// <list type="bullet">
///   <item>POST <c>claude-opus-4.7</c> + <c>anthropic-beta: context-1m-2025-08-07</c>
///         → upstream <c>body.model</c> rewritten to
///         <c>claude-opus-4.7-1m-internal</c> AND the <c>anthropic-beta</c>
///         header sent upstream does NOT contain a <c>context-1m-*</c> token
///         (which Copilot would reject — verified empirically in
///         <c>BetaAcceptanceTests</c>).</item>
///   <item>POST <c>claude-opus-4.7</c> without the beta → no rewrite happens,
///         upstream model stays <c>claude-opus-4.7</c>.</item>
/// </list>
/// Drives the test via direct <see cref="HttpClient"/> POST through the
/// in-process bridge (same pattern as <see cref="CacheHitHeadlessTests"/> and
/// <see cref="WebSearchRejectionTests"/>). The "1M context" toggle in Claude
/// Code's UI surfaces as the <c>anthropic-beta</c> header on the wire; we
/// inject it directly so the test isn't coupled to the CLI's settings storage.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class OneMillionContextRoutingTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public OneMillionContextRoutingTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task Opus47_With1mBeta_RewritesToInternalVariant_AndStripsContextBeta()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "context-1m-2025-08-07");
        req.Content = new StringContent(
            """{"model":"claude-opus-4-7","max_tokens":8,"messages":[{"role":"user","content":"reply: ok"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        _output.WriteLine($"bridge → client: HTTP {(int)resp.StatusCode}");
        _output.WriteLine($"body: {Truncate(respBody, 400)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore);
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} anthropic-beta={upstreamBeta}");

        Assert.Equal("claude-opus-4.7-1m-internal", upstreamModel);
        Assert.DoesNotContain("context-1m", upstreamBeta, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Opus47_WithoutBeta_DoesNotRewrite()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Content = new StringContent(
            """{"model":"claude-opus-4-7","max_tokens":8,"messages":[{"role":"user","content":"reply: ok"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore);
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        _output.WriteLine($"upstream: model={upstreamModel}");

        // No 1M beta on the way in → no rewrite. Should be the base model id.
        Assert.Equal("claude-opus-4.7", upstreamModel);
    }

    /// <summary>
    /// Inbound betas the bridge does NOT have a strip rule for should land on
    /// the upstream verbatim — that's the pass-through-by-default policy
    /// (<c>docs/pipeline-design.md §7.5</c>).
    /// </summary>
    [Fact]
    public async Task UnknownBeta_PassesThroughVerbatim()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "extended-cache-ttl-2025-04-11");
        req.Content = new StringContent(
            """{"model":"claude-haiku-4-5","max_tokens":8,"messages":[{"role":"user","content":"reply: ok"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore);
        Assert.NotNull(upstreamReq);

        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: anthropic-beta={upstreamBeta}");
        Assert.Contains("extended-cache-ttl-2025-04-11", upstreamBeta);
    }

    /// <summary>
    /// opus-4.7 + 1M beta + thinking:enabled — both 1m-internal and base
    /// opus-4.7 reject <c>thinking.type=enabled</c> ("Use thinking.type.adaptive
    /// and output_config.effort to control thinking behavior" per
    /// <c>CopilotGapProbes.ThinkingShape_ProbeAcceptance</c>). The compound
    /// rule must route the model AND rewrite the thinking shape in one step;
    /// the simple 1M rule alone would have left thinking:enabled to be rejected.
    /// </summary>
    [Fact]
    public async Task Opus47_WithThinkingEnabled_And1mBeta_RewritesModelAndThinking()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "context-1m-2025-08-07");
        req.Content = new StringContent(
            """{"model":"claude-opus-4-7","max_tokens":32,"messages":[{"role":"user","content":"reply: ok"}],"thinking":{"type":"enabled","budget_tokens":8192}}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        _output.WriteLine($"bridge → client: HTTP {(int)resp.StatusCode}");
        _output.WriteLine($"body: {Truncate(respBody, 300)}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore);
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamThinkingType = upstreamReq["body"]?["thinking"]?["type"]?.GetValue<string>();
        var upstreamEffort = upstreamReq["body"]?["output_config"]?["effort"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} thinking.type={upstreamThinkingType} effort={upstreamEffort} beta={upstreamBeta}");

        Assert.Equal("claude-opus-4.7-1m-internal", upstreamModel);
        Assert.Equal("adaptive", upstreamThinkingType);
        // 8192 budget → low (< 8192 = low, threshold is < 8192 so 8192 → medium)
        Assert.NotNull(upstreamEffort);
        Assert.DoesNotContain("context-1m", upstreamBeta, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// opus-4.8 + 1M beta — Copilot has no opus-4.8 1M variant; the bridge
    /// silently falls back to <c>claude-opus-4.7-1m-internal</c> (the closest
    /// 1M-capable model on the catalog). User gets 1M context at the cost of
    /// a silent model version downgrade. Per
    /// <c>CopilotGapProbes.CrossVersion1mFallback_ProbeUpstreamAcceptance</c>,
    /// the wire shape Claude Code sends for opus-4.8 is accepted by 1m-internal
    /// for the simple no-thinking case.
    /// </summary>
    [Fact]
    public async Task Opus48_With1mBeta_SilentlyFallsBackTo1mInternal()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "context-1m-2025-08-07");
        req.Content = new StringContent(
            """{"model":"claude-opus-4-8","max_tokens":16,"messages":[{"role":"user","content":"reply: ok"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore);
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} beta={upstreamBeta}");

        Assert.Equal("claude-opus-4.7-1m-internal", upstreamModel);
        Assert.DoesNotContain("context-1m", upstreamBeta, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// opus-4.8 + thinking:enabled (no 1M beta) — opus-4.8 only accepts
    /// adaptive thinking, same constraint as opus-4.7 base. Bridge rewrites
    /// thinking shape and derives effort from the budget. Model stays
    /// opus-4.8.
    /// </summary>
    [Fact]
    public async Task Opus48_WithThinkingEnabled_RewritesThinkingButKeepsModel()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Content = new StringContent(
            """{"model":"claude-opus-4-8","max_tokens":32,"messages":[{"role":"user","content":"reply: ok"}],"thinking":{"type":"enabled","budget_tokens":16384}}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore);
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamThinkingType = upstreamReq["body"]?["thinking"]?["type"]?.GetValue<string>();
        var upstreamEffort = upstreamReq["body"]?["output_config"]?["effort"]?.GetValue<string>();
        _output.WriteLine($"upstream: model={upstreamModel} thinking.type={upstreamThinkingType} effort={upstreamEffort}");

        Assert.Equal("claude-opus-4.8", upstreamModel);
        Assert.Equal("adaptive", upstreamThinkingType);
        Assert.NotNull(upstreamEffort);
    }

    private HashSet<string> SnapshotLogFiles() =>
        Directory.Exists(_bridge.LogDirectory)
            ? new HashSet<string>(Directory.GetFiles(_bridge.LogDirectory, "*.json"))
            : new HashSet<string>();

    /// <summary>
    /// Finds the newest <c>*-upstream-req.json</c> file written since
    /// <paramref name="seenBefore"/> was snapshotted. Returns its parsed root.
    /// Null if no such file appeared (e.g. early-fail path).
    /// </summary>
    private JsonObject? FindUpstreamRequestSince(HashSet<string> seenBefore)
    {
        // Sink writes asynchronously; the file appears a few ms after the
        // response. Poll briefly so the test doesn't flake.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var newFiles = Directory.GetFiles(_bridge.LogDirectory, "*-upstream-req.json")
                .Where(f => !seenBefore.Contains(f))
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();
            if (newFiles.Count > 0)
            {
                // Use FileShare.ReadWrite so we don't race the sink worker —
                // when several tests share the fixture and run in parallel, the
                // worker may still hold a write handle on the latest file.
                var raw = ReadFileShared(newFiles[^1]);
                if (raw is null) { Thread.Sleep(50); continue; }
                return JsonNode.Parse(raw)?.AsObject();
            }
            Thread.Sleep(50);
        }
        return null;
    }

    private static string? ReadFileShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
