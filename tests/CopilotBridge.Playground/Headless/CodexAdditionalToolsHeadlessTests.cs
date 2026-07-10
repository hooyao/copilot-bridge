using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Live end-to-end regression for the gpt-5.6 <c>additional_tools</c> input item
/// (change <c>add-codex-additional-tools-item</c>). Drives the EXACT desktop
/// capture that used to 400 — a Codex <c>/responses</c> body whose <c>input[0]</c>
/// is <c>{type:"additional_tools", role:"developer", tools:[…]}</c> — through the
/// real in-process bridge on the real Codex client edge (<c>POST /codex/responses</c>),
/// and asserts the whole path now completes: T1 deserializes the item (no more
/// <c>Polymorphism_UnrecognizedTypeDiscriminator</c>), routing/T2 carry it to
/// Copilot's native <c>/responses</c> verbatim, and the upstream 200s.
/// </summary>
/// <remarks>
/// <para>This is the client-edge counterpart to <see cref="CcOnGpt56HeadlessTests"/>
/// (which drives <c>claude.exe</c> onto gpt-5.6-sol). Here the client is the Codex
/// wire shape itself, replayed verbatim — the bug was that the bridge rejected a
/// body Copilot accepts, so the test's contract is "the bridge no longer 400s a
/// shape Copilot 200s." The offline byte-fidelity of the carriage is proven by
/// <c>CodexAdditionalToolsRoundTripTests</c>; this proves the live loop closes.</para>
/// <para>Tagged Integration — needs live Copilot (DPAPI token or the
/// <c>~/github_token.dat</c> dev fallback). Skipped in CI. Uses the ephemeral-port
/// <see cref="BridgeFixture"/>, never 8765.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexAdditionalToolsHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexAdditionalToolsHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task AdditionalToolsCapture_ThroughBridge_NoLonger400s()
    {
        // Real Codex always streams (research §3.2 — stream hardcoded true); the
        // committed fixture is stored non-streaming for the direct-to-Copilot probe,
        // so flip it here to exercise the faithful streaming path end-to-end.
        var payload = (await File.ReadAllTextAsync(FindVerbatimFixture()))
            .Replace("\"stream\": false", "\"stream\": true")
            .Replace("\"stream\":false", "\"stream\":true");
        // Fail loudly if the fixture was reformatted and the flip silently no-op'd —
        // otherwise we'd quietly exercise the non-streaming path this test avoids.
        Assert.Contains("\"stream\": true", payload.Replace("\"stream\":true", "\"stream\": true"),
            StringComparison.Ordinal);

        // Explicit deadline for BOTH the send AND the body read: with
        // ResponseHeadersRead, HttpClient.Timeout stops applying once the SSE
        // headers arrive, so a stalled stream would make ReadAsStringAsync() hang
        // forever. A linked CTS bounds the whole exchange at 2 minutes.
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/codex/responses");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        _output.WriteLine($"/codex/responses → {(int)resp.StatusCode} {resp.StatusCode}");
        _output.WriteLine($"  first 400 chars: {(body.Length <= 400 ? body : body[..400])}");

        // Contract: the shape Copilot accepts natively must NOT be rejected by the
        // bridge. Specifically, the old failure signature must be gone.
        Assert.DoesNotContain("Polymorphism_UnrecognizedTypeDiscriminator", body, StringComparison.Ordinal);
        Assert.DoesNotContain("additional_tools", ExtractErrorText(body), StringComparison.Ordinal);

        // And the request completes end-to-end: 200 with a terminal Responses event.
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);
    }

    // A bridge 400 writes a short error string ("invalid request body: …") rather
    // than an SSE stream; return the body only when it's an error so the
    // additional_tools assertion above doesn't trip on the legitimate item echoed
    // inside a successful upstream stream.
    private static string ExtractErrorText(string body) =>
        body.StartsWith("invalid request body", StringComparison.Ordinal)
        || body.Contains("\"error\"", StringComparison.Ordinal)
            ? body
            : "";

    private static string FindVerbatimFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "CopilotBridge.Playground",
                "Fixtures", "codex-additional-tools-verbatim.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "codex-additional-tools-verbatim.json not found from " + AppContext.BaseDirectory);
    }
}
