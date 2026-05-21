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
    /// Probes Anthropic's <c>web_search_20250305</c> server tool. Claude Code's
    /// <c>WebSearch</c> built-in is wired through this — if Copilot rejects the
    /// tool, that's a real Claude-Code-visible gap. See
    /// <c>references/vscode-copilot-chat-snippets/anthropicProvider.ts:209</c>
    /// for the upstream pattern.
    /// </summary>
    [Fact]
    public async Task WebSearchTool_ProbeCopilotAcceptance()
    {
        const string payload = """
          {
            "model": "claude-sonnet-4.6",
            "messages": [{ "role": "user", "content": "What was the top story on Hacker News today? Use web_search." }],
            "max_tokens": 256,
            "tools": [
              { "type": "web_search_20250305", "name": "web_search", "max_uses": 1 }
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(payload);
        _output.WriteLine($"HTTP {(int)status} {status}");
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
}
