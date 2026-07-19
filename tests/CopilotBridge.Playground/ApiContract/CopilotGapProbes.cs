using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Empirical probes for Anthropic-API surfaces that Copilot may or may not
/// implement — used to draw the gap line for the bridge. These tests do NOT
/// assert business behavior; they log status + body so the research doc can
/// cite concrete responses rather than circumstantial evidence from reference
/// implementations. Add a new probe here whenever a "does Copilot support X?"
/// question comes up.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CopilotGapProbes
{
    private readonly ITestOutputHelper _output;

    public CopilotGapProbes(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Probes <c>POST /v1/messages/count_tokens</c>. All three reference impls
    /// (copilot-api, copilot-api-anthropic, copilot2api) either estimate
    /// locally or route to Anthropic directly — so the prior assumption was
    /// "Copilot doesn't expose it." This test verifies otherwise.
    /// </summary>
    [Fact]
    public async Task CountTokens_ProbeCopilotUpstream()
    {
        const string payload = """
          {
            "model": "claude-sonnet-4.6",
            "messages": [{ "role": "user", "content": "hi" }]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostCountTokensAsync(payload);
        _output.WriteLine($"HTTP {(int)status} {status}");
        _output.WriteLine(body);
    }

    /// <summary>
    /// Probes Anthropic's <c>web_search_20250305</c> server tool against every
    /// Copilot upstream provider (Bedrock / Vertex / AnthropicDirect — see
    /// <see cref="ApiComparisonTests.CopilotShape_ObserveAcrossModels"/> for
    /// the provider mapping). If Copilot's gateway rejects the tool
    /// <i>regardless</i> of which upstream a model routes through, the rejection
    /// is gateway-level, not upstream-capability-level — which means the
    /// request never even reaches Anthropic, even on Anthropic-Direct models.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4.6")]            // → Bedrock
    [InlineData("claude-opus-4.7")]              // → Anthropic Direct
    [InlineData("claude-opus-4.7-1m-internal")]  // → Vertex
    [InlineData("claude-haiku-4.5")]             // → Bedrock
    public async Task WebSearchTool_ProbeCopilotAcceptance(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "messages": [{ "role": "user", "content": "What was the top story on Hacker News today? Use web_search." }],
            "max_tokens": 256,
            "tools": [
              { "type": "web_search_20250305", "name": "web_search", "max_uses": 1 }
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] HTTP {(int)status} {status}");
        _output.WriteLine(PlaygroundClient.PrettyJson(body));
    }

    /// <summary>
    /// Probes Anthropic's <c>web_search_20250305</c> server tool against the
    /// <b>native</b> <c>api.anthropic.com</c> endpoint. The Copilot-side probe
    /// above confirmed that Copilot rejects the tool with HTTP 400
    /// <c>unsupported_value</c> at the gateway level. This test answers the
    /// complementary question: does the feature work on the native API so we
    /// can characterise what a working response looks like (and decide whether
    /// the bridge could ever relay it)?
    ///
    /// Requires <c>AnthropicApiKey</c> in <c>appsettings.local.json</c>;
    /// skips cleanly if the key is absent.
    /// </summary>
    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-haiku-4-5")]
    public async Task WebSearchTool_ProbeNativeAnthropicAcceptance(string model)
    {
        if (LocalConfig.AnthropicApiKey is null)
        {
            _output.WriteLine("Skipped — AnthropicApiKey not set in appsettings.local.json");
            return;
        }

        var payload = $$"""
          {
            "model": "{{model}}",
            "messages": [{ "role": "user", "content": "What was the top story on Hacker News today? Use web_search." }],
            "max_tokens": 512,
            "tools": [
              { "type": "web_search_20250305", "name": "web_search", "max_uses": 1 }
            ]
          }
          """;

        using var client = new AnthropicNativeClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"[{model}] HTTP {(int)status} {status}");
        _output.WriteLine(PlaygroundClient.PrettyJson(body));
    }

    /// <summary>
    /// Probes <c>GET /v1/files</c>. Claude Code's <c>filesApi.ts</c> uses
    /// raw axios (not the SDK) to upload/download files via
    /// <c>ANTHROPIC_BASE_URL</c>, so these requests would land at the bridge
    /// when BriefTool or teleport features are active. This probe answers:
    /// does Copilot have a files API at all? (Almost certainly no — Copilot
    /// isn't a file storage service.)
    /// </summary>
    [Fact]
    public async Task FilesList_ProbeCopilotAcceptance()
    {
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryRequestAsync(HttpMethod.Get, "/v1/files");
        _output.WriteLine($"HTTP {(int)status} {status}");
        _output.WriteLine(body);
    }

    /// <summary>
    /// Dumps every Claude model Copilot exposes, with its <c>capabilities</c>
    /// block. Foundation for the dynamic model registry — we read this
    /// instead of hardcoding the EffortAware table. The list changes (new
    /// models added, old ones deprecated), so the bridge has to discover at
    /// startup.
    /// </summary>
    [Fact]
    public async Task DumpClaudeModelsAndCapabilities()
    {
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryRequestAsync(HttpMethod.Get, "/models");
        Assert.Equal(System.Net.HttpStatusCode.OK, status);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var claudeModels = new List<System.Text.Json.JsonElement>();
        foreach (var m in data.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString() ?? "";
            if (id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
                claudeModels.Add(m);
        }

        _output.WriteLine($"Claude models on this account: {claudeModels.Count}");
        _output.WriteLine("");

        foreach (var m in claudeModels)
        {
            var id = m.GetProperty("id").GetString();
            _output.WriteLine($"=== {id} ===");
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            _output.WriteLine("");
        }
    }

    /// <summary>
    /// Probes <c>POST /v1/messages/batches</c>. The Anthropic SDK exposes this
    /// but Claude Code doesn't call it (verified by grep on
    /// <c>restored-src/src/</c>) — included for completeness so the gap doc
    /// can cite a status code rather than assume.
    /// </summary>
    [Fact]
    public async Task MessageBatches_ProbeCopilotAcceptance()
    {
        const string payload = """
          {
            "requests": [
              {
                "custom_id": "probe-1",
                "params": {
                  "model": "claude-sonnet-4.6",
                  "messages": [{ "role": "user", "content": "hi" }],
                  "max_tokens": 8
                }
              }
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryRequestAsync(HttpMethod.Post, "/v1/messages/batches", payload);
        _output.WriteLine($"HTTP {(int)status} {status}");
        _output.WriteLine(body);
    }

    /// <summary>
    /// Probes the thinking-shape constraint on <c>claude-opus-4.7-1m-internal</c>.
    /// Base <c>claude-opus-4.7</c> only accepts <c>thinking.type=adaptive</c>
    /// (rule #4 in appsettings.json exists for exactly that). Open question:
    /// does the 1M variant inherit the same constraint, or accept
    /// <c>enabled</c> too? Result drives whether we need a compound routing
    /// rule when a request carries both thinking:enabled AND the 1M beta.
    ///
    /// Also probes the same matrix for <c>claude-opus-4.8</c>, since user just
    /// learned Claude Code 2.1.x ships opus-4.8 and Copilot serves it only as
    /// the base 200k model (no 1M variant, no -high/-xhigh variants per the
    /// model catalog). Routing opus-4.8 + 1M beta → 1m-internal needs the same
    /// answer.
    /// </summary>
    [Theory]
    [InlineData("claude-opus-4.7-1m-internal", "adaptive",  null)]
    [InlineData("claude-opus-4.7-1m-internal", "enabled",   8192)]
    [InlineData("claude-opus-4.7-1m-internal", "disabled",  null)]
    [InlineData("claude-opus-4.7-1m-internal", null,        null)]
    [InlineData("claude-opus-4.8",             "adaptive",  null)]
    [InlineData("claude-opus-4.8",             "enabled",   8192)]
    [InlineData("claude-opus-4.8",             null,        null)]
    public async Task ThinkingShape_ProbeAcceptance(string model, string? thinkingType, int? budgetTokens)
    {
        var thinkingBlock = thinkingType switch
        {
            null => "",
            "enabled" => $$$""","thinking":{"type":"enabled","budget_tokens":{{{budgetTokens}}}}""",
            _ => $$$""","thinking":{"type":"{{{thinkingType}}}"}""",
        };
        var payload = $$"""
          {
            "model": "{{model}}",
            "max_tokens": 16,
            "messages": [{"role":"user","content":"reply: ok"}]{{thinkingBlock}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);

        var preview = body.Length > 240 ? body[..240] + "…" : body;
        _output.WriteLine($"[{model}] thinking={thinkingType ?? "<absent>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {preview}");
    }

    /// <summary>
    /// Cross-version 1M fallback probe. Simulates the request shape Claude Code
    /// would send for <c>claude-opus-4.8</c> with the 1M-context toggle enabled,
    /// after the bridge has rewritten <c>body.model</c> to
    /// <c>claude-opus-4.7-1m-internal</c> (the closest 1M-capable model Copilot
    /// serves). Answers: does Copilot accept the substitution? Note that
    /// "substitution" is purely a bridge concern — Copilot only sees the
    /// rewritten body and doesn't care that opus-4.8 was the original choice.
    /// </summary>
    [Theory]
    // matrix: (effort, thinking_type) — opus-4.8 catalog says reasoning_effort=[medium]
    // and adaptive_thinking=true, so these are the shapes CC plausibly sends.
    [InlineData(null,     "adaptive")]
    [InlineData("medium", "adaptive")]
    [InlineData(null,     null)]
    [InlineData("medium", null)]
    public async Task CrossVersion1mFallback_ProbeUpstreamAcceptance(string? effort, string? thinkingType)
    {
        var effortBlock = effort is null
            ? ""
            : $$$""","output_config":{"effort":"{{{effort}}}"}""";
        var thinkingBlock = thinkingType switch
        {
            null => "",
            _ => $$$""","thinking":{"type":"{{{thinkingType}}}"}""",
        };
        var payload = $$"""
          {
            "model": "claude-opus-4.7-1m-internal",
            "max_tokens": 32,
            "messages": [{"role":"user","content":"reply: ok"}]{{effortBlock}}{{thinkingBlock}}
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);

        var preview = body.Length > 240 ? body[..240] + "…" : body;
        _output.WriteLine($"effort={effort ?? "<absent>"} thinking={thinkingType ?? "<absent>"} → {(int)status} {status}");
        _output.WriteLine($"  body: {preview}");
    }
}
