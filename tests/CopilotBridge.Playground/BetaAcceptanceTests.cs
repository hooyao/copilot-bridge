using System.Net;
using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Probes <c>/v1/messages</c> with various <c>anthropic-beta</c> header values
/// against <b>both</b> Copilot and Anthropic Direct so we can tell apart:
/// <list type="bullet">
///   <item>200 on both — safe to forward.</item>
///   <item>200 native / 400 Copilot — must be stripped before forwarding.</item>
///   <item>400 on both — fake or obsolete token; harmless to forward.</item>
///   <item>200 / silently-ignored — header is accepted but unused; treat as 200.</item>
/// </list>
/// Drives the forward-whitelist policy in <c>docs/pipeline-design.md §7.5</c>.
/// The test does NOT assert acceptance — it logs status codes for human review,
/// and only fails on transport-layer failure. Run with
/// <c>dotnet test --filter BetaAcceptance --logger "console;verbosity=detailed"</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public class BetaAcceptanceTests
{
    // sonnet-4.6 exists on both sides; the probe is about header acceptance,
    // not feature semantics, so the model choice barely matters.
    private const string CopilotPayload = """
      {
        "model": "claude-sonnet-4.6",
        "messages": [{"role":"user","content":"hi"}],
        "max_tokens": 16
      }
      """;

    private const string NativePayload = """
      {
        "model": "claude-sonnet-4-6",
        "messages": [{"role":"user","content":"hi"}],
        "max_tokens": 16
      }
      """;

    private readonly ITestOutputHelper _output;

    public BetaAcceptanceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    // ── Documented as accepted by research §15.4 (chatEndpoint.ts:182-215): ──
    [InlineData("interleaved-thinking-2025-05-14")]
    [InlineData("context-management-2025-06-27")]
    [InlineData("advanced-tool-use-2025-11-20")]
    // ── Claude Code 2.1.131 sends these — Copilot acceptance unknown: ──
    [InlineData("claude-code-20250219")]
    [InlineData("prompt-caching-scope-2026-01-05")]
    // ── NEXT.md Step 1 set — Anthropic SDK / Claude Code 1M toggle: ──
    [InlineData("context-1m-2025-08-07")]
    [InlineData("extended-cache-ttl-2025-04-11")]
    [InlineData("output-128k-2025-02-19")]
    [InlineData("fine-grained-tool-streaming-2025-05-14")]
    [InlineData("token-efficient-tools-2025-02-19")]
    // ── Control: a clearly-fake token to see whether Copilot validates at all: ──
    [InlineData("bogus-nonexistent-beta-99999999")]
    public async Task BetaHeaderAcceptanceProbe(string beta)
    {
        // ── Copilot side ──
        using var copilotClient = new PlaygroundClient();
        var (copilotStatus, copilotBody) = await copilotClient.TryPostMessagesAsync(
            CopilotPayload, anthropicBeta: beta);

        var copilotPreview = Truncate(copilotBody, 200);
        _output.WriteLine($"[Copilot] beta=\"{beta}\" → {(int)copilotStatus} {copilotStatus}");
        _output.WriteLine($"          body: {copilotPreview}");

        // ── Anthropic native side ──
        if (LocalConfig.AnthropicApiKey is null)
        {
            _output.WriteLine($"[Native]  skipped — AnthropicApiKey missing in appsettings.local.json");
        }
        else
        {
            using var nativeClient = new AnthropicNativeClient();
            var (nativeStatus, nativeBody) = await nativeClient.TryPostMessagesAsync(
                NativePayload, anthropicBeta: new[] { beta });

            var nativePreview = Truncate(nativeBody, 200);
            _output.WriteLine($"[Native]  beta=\"{beta}\" → {(int)nativeStatus} {nativeStatus}");
            _output.WriteLine($"          body: {nativePreview}");
        }

        // We only fail on transport/auth-level surprises — the whole point of
        // the test is to OBSERVE per-token acceptance, not to assert it.
        Assert.True(
            copilotStatus == HttpStatusCode.OK || copilotStatus == HttpStatusCode.BadRequest,
            $"unexpected Copilot status {(int)copilotStatus} {copilotStatus}: {copilotPreview}");
    }

    private static string Truncate(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
