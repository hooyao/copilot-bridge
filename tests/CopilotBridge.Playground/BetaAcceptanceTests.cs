using System.Net;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// Probes Copilot's <c>/v1/messages</c> with various <c>anthropic-beta</c>
/// header values to find the actual acceptance set. Research §15.4 lists 3
/// known-good values from <c>chatEndpoint.ts:182-215</c>; this test verifies
/// each empirically and additionally probes the betas Claude Code 2.1.131 ships
/// (<c>claude-code-20250219</c>, <c>prompt-caching-scope-2026-01-05</c>) which
/// were not previously tested.
///
/// The test does NOT assert acceptance — it logs status codes for human review,
/// and only fails if the request errors at the transport layer (network blip,
/// auth failure, etc.). Run with <c>dotnet test --filter BetaAcceptance --logger
/// "console;verbosity=detailed"</c> to see the matrix.
/// </summary>
public class BetaAcceptanceTests
{
    private const string MinimalPayload = """
      {
        "model": "claude-sonnet-4.6",
        "messages": [{"role":"user","content":"hi"}],
        "max_tokens": 16
      }
      """;

    private readonly ITestOutputHelper _output;

    public BetaAcceptanceTests(ITestOutputHelper output) => _output = output;

    [Theory]
    // Documented as accepted by research §15.4:
    [InlineData("interleaved-thinking-2025-05-14")]
    [InlineData("context-management-2025-06-27")]
    [InlineData("advanced-tool-use-2025-11-20")]
    // Claude Code 2.1.131 sends these — acceptance unknown:
    [InlineData("claude-code-20250219")]
    [InlineData("prompt-caching-scope-2026-01-05")]
    // Control: a clearly-fake value to see whether Copilot validates at all:
    [InlineData("bogus-nonexistent-beta-99999999")]
    public async Task BetaHeaderAcceptanceProbe(string beta)
    {
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostMessagesAsync(MinimalPayload, anthropicBeta: beta);

        var bodyPreview = body.Length > 200 ? body[..200] + "..." : body;
        _output.WriteLine($"beta=\"{beta}\" → {(int)status} {status}");
        _output.WriteLine($"body: {bodyPreview}");

        // Don't fail on rejections — we want to observe the matrix, not assert.
        // But if we got something other than 200/400 (e.g. 5xx or 401), that's
        // a transport/auth problem worth surfacing.
        Assert.True(
            status == HttpStatusCode.OK || status == HttpStatusCode.BadRequest,
            $"unexpected status {(int)status} {status}: {bodyPreview}");
    }
}
