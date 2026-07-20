using System.Runtime.Versioning;
using System.Text;
using CopilotBridge.Cli.Auth;
using CopilotBridge.Cli.Copilot;
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

    /// <summary>
    /// Dumps Copilot's FULL <c>/models</c> id list (not just <c>claude-*</c>) and
    /// the complete capability block for any <c>copilot-search-*</c> /
    /// <c>exec-agent-*</c> entry. Motivated by the discovery (2026-07) that an
    /// invalid-model request surfaces a <c>model_not_available_for_integrator</c>
    /// error whose "Available models" list now includes
    /// <c>copilot-search-a/b/c</c> and <c>exec-agent-a/b/c</c> — candidates for
    /// GitHub's "Copilot can search the web using model native search" feature.
    /// This probe answers: are those models actually enumerated by <c>/models</c>
    /// (i.e. first-class, with a capabilities/endpoint block), or only reachable
    /// as internal orchestration targets? The capability block — if present —
    /// names the endpoint + wire shape, which the repo requires before any
    /// catalog entry (never guess a model's shape from its name).
    /// </summary>
    [Fact]
    public async Task DumpAllModelIds_AndNativeSearchCapabilities()
    {
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryRequestAsync(HttpMethod.Get, "/models");
        _output.WriteLine($"GET /models → {(int)status} {status}");
        Assert.Equal(System.Net.HttpStatusCode.OK, status);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");

        var allIds = new List<string>();
        var interesting = new List<System.Text.Json.JsonElement>();
        foreach (var m in data.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString() ?? "";
            allIds.Add(id);
            if (id.StartsWith("copilot-search", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("exec-agent", StringComparison.OrdinalIgnoreCase))
                interesting.Add(m);
        }

        _output.WriteLine($"Total models enumerated by /models: {allIds.Count}");
        _output.WriteLine("All ids: " + string.Join(" ", allIds));
        _output.WriteLine("");
        _output.WriteLine($"copilot-search-* / exec-agent-* present in /models data: {interesting.Count}");
        _output.WriteLine("");

        foreach (var m in interesting)
        {
            _output.WriteLine($"=== {m.GetProperty("id").GetString()} ===");
            _output.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                m, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            _output.WriteLine("");
        }
    }

    /// <summary>
    /// Probes which endpoint + wire shape <c>copilot-search-a</c> accepts. The
    /// integrator error list proves the id is authorized for <c>vscode-chat</c>,
    /// but not which surface serves it. Fires a minimal request at all three
    /// candidate endpoints — Anthropic <c>/v1/messages</c>, OpenAI
    /// <c>/chat/completions</c>, and <c>/responses</c> — and logs each status +
    /// body. A 200 (or even a shape-specific 400 that isn't
    /// <c>model_not_available</c>) tells us the endpoint owns the model; a
    /// <c>model_not_available_for_integrator</c> means "wrong surface." This is
    /// pure reconnaissance — no assertion beyond "we reached the gateway."
    /// </summary>
    [Fact]
    public async Task NativeSearchModel_ProbeEndpointAcceptance()
    {
        using var client = new PlaygroundClient();

        // ── Anthropic /v1/messages shape ──
        var anthropicBody = """
          {
            "model": "copilot-search-a",
            "max_tokens": 64,
            "messages": [{ "role": "user", "content": "What is the top story on Hacker News right now?" }]
          }
          """;
        var (aStatus, aBody) = await client.TryPostMessagesAsync(anthropicBody);
        _output.WriteLine($"[/v1/messages] copilot-search-a → {(int)aStatus} {aStatus}");
        _output.WriteLine(PlaygroundClient.PrettyJson(aBody));
        _output.WriteLine("");

        // ── OpenAI /chat/completions shape ──
        var openAiBody = """
          {
            "model": "copilot-search-a",
            "messages": [{ "role": "user", "content": "What is the top story on Hacker News right now?" }]
          }
          """;
        var (cStatus, cBody) = await client.TryRequestAsync(HttpMethod.Post, "/chat/completions", openAiBody);
        _output.WriteLine($"[/chat/completions] copilot-search-a → {(int)cStatus} {cStatus}");
        _output.WriteLine(PlaygroundClient.PrettyJson(cBody));
        _output.WriteLine("");

        // ── OpenAI /responses shape ──
        var responsesBody = """
          {
            "model": "copilot-search-a",
            "input": "What is the top story on Hacker News right now?"
          }
          """;
        var (rStatus, rBody) = await client.TryPostResponsesAsync(responsesBody);
        _output.WriteLine($"[/responses] copilot-search-a → {(int)rStatus} {rStatus}");
        _output.WriteLine(PlaygroundClient.PrettyJson(rBody));
    }

    /// <summary>
    /// Does <c>copilot-search-a</c> unlock under a DIFFERENT
    /// <c>Copilot-Integration-Id</c>? The default probe sends <c>vscode-chat</c>
    /// (prod VS Code Chat). Research doc §3.0.4 enumerates the other integrator
    /// ids the official package can send (<c>code-oss</c>, <c>vscode-nl</c>,
    /// <c>vscode-chat-dev</c>, plus private HMAC ids). The
    /// <c>model_not_supported</c> (as opposed to
    /// <c>model_not_available_for_integrator</c>) we saw on <c>vscode-chat</c>
    /// suggests the id is authorized but the endpoint doesn't serve it — this
    /// probe rules in/out a "wrong integrator surface" explanation by trying the
    /// public non-HMAC ids against <c>/v1/messages</c>. HMAC-gated private ids
    /// (<c>vscode-chat-dev</c> needs a <c>Request-Hmac</c> we can't forge) are
    /// expected to fail auth, logged for completeness.
    /// </summary>
    [Theory]
    [InlineData("code-oss")]
    [InlineData("vscode-nl")]
    [InlineData("vscode-chat-dev")]
    [InlineData("copilot-cli")]
    [InlineData("copilot-search")]
    public async Task NativeSearchModel_ProbeAlternateIntegrationIds(string integrationId)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        // GitHub's token-exchange endpoint 403s ("User-Agent required") without
        // one — PlaygroundClient sets this; a hand-rolled client must too.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("copilot-playground/0.1");
        var auth = new AuthService(http);
        try
        {
            var token = await auth.GetCopilotTokenAsync();
            var baseUrl = auth.CopilotApiBaseUrl
                ?? throw new InvalidOperationException("CopilotApiBaseUrl unknown after token fetch.");

            var headers = new CopilotHeaderFactory();
            var body = """
              {
                "model": "copilot-search-a",
                "max_tokens": 64,
                "messages": [{ "role": "user", "content": "What is the top story on Hacker News right now?" }]
              }
              """;

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages");
            // Override the default vscode-chat integration id with the candidate.
            headers.ApplyTo(req, token, overrides: new Dictionary<string, string?>
            {
                ["Copilot-Integration-Id"] = integrationId,
            });
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            _output.WriteLine($"[integration-id={integrationId}] copilot-search-a → {(int)resp.StatusCode} {resp.StatusCode}");
            _output.WriteLine(PlaygroundClient.PrettyJson(respBody));
        }
        finally
        {
            auth.Dispose();
        }
    }

    /// <summary>
    /// The 2026-02-25 GitHub changelog frames "model native search" as the
    /// MODEL's own built-in web search (GPT-5.1 / Codex family), not a separate
    /// search model — surfaced on github.com Copilot Chat, with an opt-out
    /// setting worded exactly "Copilot can search the web using model native
    /// search." That points at the gpt <c>/responses</c> path + a
    /// <c>web_search</c> tool (which docs record as 200 on Copilot's Responses
    /// surface, unlike Anthropic's server tool). This probe fires a gpt model at
    /// <c>/responses</c> WITH a <c>web_search</c> tool and a query that demands
    /// fresh info, then logs whether the model actually emitted a web_search
    /// call / real content. If it does, THAT is the reachable "native search"
    /// path for the bridge (via a Codex-backed model), and the copilot-search-*
    /// ids are just internal orchestration we never touch.
    /// </summary>
    [Theory]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-5.5")]
    public async Task GptResponses_WebSearchTool_ProbeNativeSearch(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "input": "What is today's top story on Hacker News? Use web search to check.",
            "tools": [{ "type": "web_search" }],
            "stream": false
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{model}] /responses + web_search tool → {(int)status} {status}");
        _output.WriteLine(PlaygroundClient.PrettyJson(body));
    }
}
