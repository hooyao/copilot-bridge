using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Verifies 1M-context routing: every model Copilot still serves reaches 1M on
/// its BASE id, with the <c>context-1m-*</c> beta passed through untouched (or
/// stripped only where the backend genuinely can't honor it).
/// <list type="bullet">
///   <item>opus-4.7 / opus-4.8 / opus-5 / sonnet-4.6 + <c>context-1m-2025-08-07</c>
///         → no model swap, beta forwarded verbatim.</item>
///   <item>opus-5's cross-field thinking-disabled × effort clamp (the
///         <c>Opus5_*</c> cases below).</item>
///   <item>An unknown beta passes through verbatim (pass-through-by-default).</item>
/// </list>
/// Drives the test via direct <see cref="HttpClient"/> POST through the
/// in-process bridge (same pattern as <see cref="CacheHitHeadlessTests"/> and
/// <see cref="WebSearchRejectionTests"/>). The "1M context" toggle in Claude
/// Code's UI surfaces as the <c>anthropic-beta</c> header on the wire; we
/// inject it directly so the test isn't coupled to the CLI's settings storage.
/// </summary>
/// <remarks>
/// <b>The opus-4.7 → <c>-1m-internal</c> redirect cases were deleted in the
/// 2026-07 reconciliation.</b> They asserted that opus-4.7 + the 1M beta was
/// rewritten to <c>claude-opus-4.7-1m-internal</c> with the beta stripped.
/// Copilot has since retired that id (400 —
/// <see cref="ModelProfileProbe.RetiredCandidate_LivenessProbe"/>) and upgraded
/// the opus-4.7 BASE id to serve 1M natively
/// (<see cref="ModelProfileProbe.OpusBase_LargePrompt_ProbeOneMillionContextSupport"/>),
/// so the redirect was removed from <c>appsettings.json</c>. Both the target id
/// and the behavior are gone; <see cref="Opus47_With1mBeta_NoDowngrade_BetaPassesThrough"/>
/// replaces them by pinning the identity-passthrough contract that holds now —
/// which is also the guard against re-introducing a downgrade.
/// </remarks>
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

    /// <summary>
    /// opus-4.7 + 1M beta — identity passthrough. Replaces the deleted
    /// redirect-to-<c>-1m-internal</c> case (see the class remarks): Copilot
    /// retired that variant and upgraded the opus-4.7 base to serve 1M natively
    /// (677k-token prompt → 200), so the correct contract is now "no model swap,
    /// beta forwarded". Guards against re-introducing a downgrade.
    /// </summary>
    [Fact]
    public async Task Opus47_With1mBeta_NoDowngrade_BetaPassesThrough()
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

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-4.7");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} anthropic-beta={upstreamBeta}");

        Assert.Equal("claude-opus-4.7", upstreamModel);
        Assert.Contains("context-1m-2025-08-07", upstreamBeta, StringComparison.OrdinalIgnoreCase);
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

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-4.7");
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

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-haiku-4.5");
        Assert.NotNull(upstreamReq);

        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: anthropic-beta={upstreamBeta}");
        Assert.Contains("extended-cache-ttl-2025-04-11", upstreamBeta);
    }

    /// <summary>
    /// opus-4.7 + 1M beta + thinking:enabled — opus-4.7 rejects
    /// <c>thinking.type=enabled</c> ("Use thinking.type.adaptive and
    /// output_config.effort to control thinking behavior" per
    /// <c>CopilotGapProbes.ThinkingShape_ProbeAcceptance</c>), so the bridge must
    /// coerce the shape to adaptive and carry the reasoning depth across as an
    /// effort. The model id is NOT rewritten — this used to also assert a swap to
    /// <c>-1m-internal</c>, which Copilot retired (see the class remarks); the
    /// thinking coercion is the part that still matters and is unchanged.
    /// </summary>
    [Fact]
    public async Task Opus47_WithThinkingEnabled_And1mBeta_RewritesThinkingButKeepsModel()
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

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-4.7");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamThinkingType = upstreamReq["body"]?["thinking"]?["type"]?.GetValue<string>();
        var upstreamEffort = upstreamReq["body"]?["output_config"]?["effort"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} thinking.type={upstreamThinkingType} effort={upstreamEffort} beta={upstreamBeta}");

        Assert.Equal("claude-opus-4.7", upstreamModel);
        Assert.Equal("adaptive", upstreamThinkingType);
        Assert.NotNull(upstreamEffort);
        // 1M is native on the base id now — the beta is forwarded, not stripped.
        Assert.Contains("context-1m-2025-08-07", upstreamBeta, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// opus-4.8 + 1M beta — Copilot's <c>claude-opus-4.8</c> natively
    /// supports 1M context (probed 2026-06-05: a 260k-token prompt returns
    /// 200 with or without <c>context-1m-2025-08-07</c>), so the bridge no
    /// longer downgrades the request to <c>claude-opus-4.7-1m-internal</c>.
    /// Upstream model stays as <c>claude-opus-4.8</c>; the beta header
    /// passes through verbatim (no <c>StripBetas</c> entry on the 4.8
    /// profile — Copilot silently accepts the token).
    /// </summary>
    [Fact]
    public async Task Opus48_With1mBeta_RoutesToCopilotOpus48_NoDowngrade()
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

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-4.8");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} beta={upstreamBeta}");

        // No model swap — opus-4.8 stays opus-4.8.
        Assert.Equal("claude-opus-4.8", upstreamModel);
        // Beta passes through verbatim (no per-profile strip for opus-4.8).
        Assert.Contains("context-1m-2025-08-07", upstreamBeta, StringComparison.OrdinalIgnoreCase);
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

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-4.8");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamThinkingType = upstreamReq["body"]?["thinking"]?["type"]?.GetValue<string>();
        var upstreamEffort = upstreamReq["body"]?["output_config"]?["effort"]?.GetValue<string>();
        _output.WriteLine($"upstream: model={upstreamModel} thinking.type={upstreamThinkingType} effort={upstreamEffort}");

        Assert.Equal("claude-opus-4.8", upstreamModel);
        Assert.Equal("adaptive", upstreamThinkingType);
        Assert.NotNull(upstreamEffort);
    }

    /// <summary>
    /// sonnet-4.6 + 1M beta — the bridge has NO routing rule for sonnet
    /// (rules #1/#2 are opus-only), so the request flows through identity
    /// passthrough exactly like opus-4.8: upstream model stays
    /// <c>claude-sonnet-4.6</c>, beta header passes through verbatim. There
    /// is no "fallback to 200k" — Copilot's sonnet-4.6 natively serves 1M
    /// context (probed 2026-06-05 in
    /// <c>ModelProfileProbe.Sonnet46_LargePrompt_ProbeOneMillionContextSupport</c>:
    /// a 638k-token prompt returns 200 with and without the beta), so no
    /// model swap is needed. This test guards against accidentally adding
    /// a sonnet-4.6 → sonnet-4.6-old rule later that would silently downgrade.
    /// </summary>
    [Fact]
    public async Task Sonnet46_With1mBeta_NoDowngrade_PassthroughToCopilotSonnet46()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "context-1m-2025-08-07");
        req.Content = new StringContent(
            """{"model":"claude-sonnet-4-6","max_tokens":16,"messages":[{"role":"user","content":"reply: ok"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-sonnet-4.6");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} beta={upstreamBeta}");

        // No model swap — sonnet-4.6 stays sonnet-4.6 (no -200k fallback,
        // no -1m variant lookup).
        Assert.Equal("claude-sonnet-4.6", upstreamModel);
        // Beta passes through verbatim — no per-profile strip for sonnet-4.6.
        Assert.Contains("context-1m-2025-08-07", upstreamBeta, StringComparison.OrdinalIgnoreCase);
    }

    // ── claude-opus-5 (2026-07) ──────────────────────────────────────────────

    /// <summary>
    /// <b>Permanent regression guard for the opus-5 cross-field constraint.</b> Copilot
    /// rejects <c>output_config.effort</c> <c>max</c>/<c>xhigh</c> on opus-5 <i>when
    /// <c>thinking</c> is disabled</i> — 400 <c>"output_config.effort 'max' is not
    /// supported when thinking is disabled on this model. Use effort 'high' or below, or
    /// enable thinking."</c> — while accepting each field on its own.
    /// <see cref="Routing.ProfileAdjuster"/> must clamp the effort down on that path.
    /// <para><b>This exact request shape was proven to 400 by a real
    /// <c>claude.exe</c> run</b>: with the constraint removed from the catalog, the
    /// client's own no-thinking internal request went upstream as
    /// <c>disabled</c>+<c>max</c> and Copilot returned that 400 (behavior case
    /// <c>ClaudeCode_NativeCc_MaxEffort_DisabledThinkingEffortIsClamped</c>). Replaying
    /// it here keeps the bug found without needing a live client every run — the live
    /// case FOUND it, this replay KEEPS it found.</para>
    /// <para>Asserts the response is 200 <b>and</b> the clamped effort on the wire, so
    /// the guard can't be satisfied by the bridge merely surviving.</para>
    /// </summary>
    [Theory]
    [InlineData("max")]
    [InlineData("xhigh")]
    public async Task Opus5_DisabledThinking_RejectedEffort_IsClampedNotForwarded(string effort)
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Content = new StringContent(
            $$$"""{"model":"claude-opus-5","max_tokens":16,"messages":[{"role":"user","content":"reply: ok"}],"thinking":{"type":"disabled"},"output_config":{"effort":"{{{effort}}}"}}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        var respBody = await resp.Content.ReadAsStringAsync();
        _output.WriteLine($"bridge → client: HTTP {(int)resp.StatusCode}");
        _output.WriteLine($"body: {Truncate(respBody, 300)}");

        // Without the clamp this is the upstream 400 quoted above, surfaced to the client.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-5");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamThinking = upstreamReq["body"]?["thinking"]?["type"]?.GetValue<string>();
        var upstreamEffort = upstreamReq["body"]?["output_config"]?["effort"]?.GetValue<string>();
        _output.WriteLine($"upstream: model={upstreamModel} thinking={upstreamThinking} effort={upstreamEffort}");

        Assert.Equal("claude-opus-5", upstreamModel);
        // The user turned thinking OFF — the clamp resolves the conflict by lowering
        // effort, never by silently re-enabling reasoning.
        Assert.Equal("disabled", upstreamThinking);
        // Clamped to the highest tier opus-5 accepts with thinking disabled.
        Assert.Equal("high", upstreamEffort);
    }

    /// <summary>
    /// The other half of the contract: with thinking ON, opus-5 accepts every effort tier
    /// (<c>ModelProfileProbe.Opus5_Effort_ReProbe</c> — 200 for all five, standalone and
    /// with adaptive). So <c>max</c> must reach the wire UNCHANGED here. This is what
    /// stops the cheap-but-wrong fix of narrowing the profile's accepted-effort list,
    /// which would silently downgrade every thinking-on max request.
    /// </summary>
    [Fact]
    public async Task Opus5_AdaptiveThinking_MaxEffort_PassesThroughUnclamped()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Content = new StringContent(
            """{"model":"claude-opus-5","max_tokens":16,"messages":[{"role":"user","content":"reply: ok"}],"thinking":{"type":"adaptive"},"output_config":{"effort":"max"}}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-5");
        Assert.NotNull(upstreamReq);

        var upstreamThinking = upstreamReq["body"]?["thinking"]?["type"]?.GetValue<string>();
        var upstreamEffort = upstreamReq["body"]?["output_config"]?["effort"]?.GetValue<string>();
        _output.WriteLine($"upstream: thinking={upstreamThinking} effort={upstreamEffort}");

        Assert.Equal("adaptive", upstreamThinking);
        Assert.Equal("max", upstreamEffort);
    }

    /// <summary>
    /// opus-5 + 1M beta — identity passthrough, same as opus-4.8 / sonnet-4.6. Copilot
    /// serves opus-5 at 1M natively (a 677k-token prompt returns 200 with and without the
    /// beta — <c>ModelProfileProbe.Opus5_LargePrompt_ProbeOneMillionContextSupport</c>),
    /// so there is no model swap and no <c>StripBetas</c> entry. Guards against a future
    /// rule that would silently downgrade opus-5.
    /// </summary>
    [Fact]
    public async Task Opus5_With1mBeta_NoDowngrade_BetaPassesThrough()
    {
        var seenBefore = SnapshotLogFiles();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/cc/v1/messages");
        req.Headers.TryAddWithoutValidation("anthropic-beta", "context-1m-2025-08-07");
        req.Content = new StringContent(
            """{"model":"claude-opus-5","max_tokens":16,"messages":[{"role":"user","content":"reply: ok"}]}""",
            Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var upstreamReq = FindUpstreamRequestSince(seenBefore, "claude-opus-5");
        Assert.NotNull(upstreamReq);

        var upstreamModel = upstreamReq["body"]?["model"]?.GetValue<string>();
        var upstreamBeta = upstreamReq["headers"]?["anthropic-beta"]?.GetValue<string>() ?? "";
        _output.WriteLine($"upstream: model={upstreamModel} beta={upstreamBeta}");

        Assert.Equal("claude-opus-5", upstreamModel);
        Assert.Contains("context-1m-2025-08-07", upstreamBeta, StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<string> SnapshotLogFiles() =>        Directory.Exists(_bridge.LogDirectory)
            ? new HashSet<string>(Directory.GetFiles(_bridge.LogDirectory, "*.json"))
            : new HashSet<string>();

    /// <summary>
    /// Finds the newest <c>*-upstream-req.json</c> file written since
    /// <paramref name="seenBefore"/> was snapshotted. Returns its parsed root.
    /// Null if no such file appeared (e.g. early-fail path).
    /// <para><paramref name="expectModelPrefix"/> filters to the request whose
    /// <c>body.model</c> starts with that value — pass the <b>normalized dotted</b>
    /// id (<c>claude-opus-4.7</c>), which is what
    /// <see cref="Routing.CopilotModelRegistry.Normalize"/> puts on the upstream
    /// body, NOT the dashed form the client sends inbound.</para>
    /// <para><b>This filter is required for correctness, not just precision:</b>
    /// every test in this class shares one <see cref="BridgeFixture"/> and xUnit
    /// runs them concurrently within the class, so several tests write into the
    /// same trace dir at once. Taking the newest new file unconditionally can
    /// return a SIBLING test's request — which surfaced as
    /// <c>Opus47_WithThinkingEnabled_And1mBeta…</c> reading an opus-5 body and
    /// failing on the model assertion, while passing in isolation. Filtering by
    /// the model under test makes each lookup pick its own request regardless of
    /// interleaving.</para>
    /// </summary>
    private JsonObject? FindUpstreamRequestSince(HashSet<string> seenBefore, string? expectModelPrefix = null)
    {
        // Sink writes asynchronously; the file appears a few ms after the
        // response. Poll briefly so the test doesn't flake.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var newFiles = Directory.GetFiles(_bridge.LogDirectory, "*-upstream-req.json")
                .Where(f => !seenBefore.Contains(f))
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();

            // Walk newest-first so the most recent matching request wins.
            for (var i = newFiles.Count - 1; i >= 0; i--)
            {
                // Use FileShare.ReadWrite so we don't race the sink worker —
                // when several tests share the fixture and run in parallel, the
                // worker may still hold a write handle on the latest file.
                var raw = ReadFileShared(newFiles[i]);
                if (raw is null) continue;
                var parsed = JsonNode.Parse(raw)?.AsObject();
                if (parsed is null) continue;
                if (expectModelPrefix is null) return parsed;

                var model = parsed["body"]?["model"]?.GetValue<string>();
                if (model is not null
                    && model.StartsWith(expectModelPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return parsed;
                }
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