using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// End-to-end regression for the gpt-5.6 <b>namespaced-tool</b> round-trip bug: a
/// follow-up Codex turn that echoes a prior <c>collaboration.list_agents</c>
/// (namespaced) <c>function_call</c>. When the namespace is dropped, Copilot 400s
/// the turn with <c>Missing namespace for function_call 'list_agents'</c> — the
/// production failure. This drives the REAL captured inbound bytes (a genuine
/// 667-message gpt-5.6-sol turn) with the namespace present through the real
/// in-process bridge (<c>POST /codex/responses</c>) to Copilot and asserts the fix:
/// no 400, upstream 200, and the audit shows the forwarded upstream request carried
/// <c>"namespace":"collaboration"</c> on the echoed function_call.
/// </summary>
/// <remarks>
/// <para>Why the fixture has namespace injected: the raw broken-session capture
/// (<c>inbound-0010-verbatim.json</c>) itself LACKS the namespace on the echo —
/// because the response side that produced it (pre-fix) dropped the namespace, so
/// Codex never learned it to echo it. That capture 400s by construction (proven by
/// <c>NamespaceRealReplayProbe.A</c>). The FIXED response side (T3→T4, covered by
/// <c>CodexNamespaceRoundTripTests</c> + the real 0009 capture) now delivers the
/// namespace to Codex, so a fixed multi-turn session echoes it back — which is
/// exactly <c>inbound-0010-with-namespace.json</c>. This test proves the REQUEST
/// side (T1→T2) then carries it upstream and Copilot accepts it.</para>
/// <para>Fixtures live under <c>tmp-namespace-repro/</c> (de-identified real bytes,
/// NOT checked in); the test asserts they exist. Tagged Integration; ephemeral port.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexNamespaceEchoHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    private static readonly string ReproDir =
        Path.Combine("Q:\\", "MyProjects", "cc-copilot-bridge", "tmp-namespace-repro");

    public CodexNamespaceEchoHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task NamespacedEcho_ThroughBridge_ForwardsNamespace_AndCopilotAccepts()
    {
        var path = Path.Combine(ReproDir, "inbound-0010-with-namespace.json");
        Assert.True(File.Exists(path), $"replay fixture absent: {path}");
        var payload = await File.ReadAllTextAsync(path);

        var reader = new BridgeLogReader(_bridge.LogDirectory);

        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_bridge.BaseUrl}/codex/responses");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);

        _output.WriteLine($"/codex/responses → {(int)resp.StatusCode} {resp.StatusCode}");
        _output.WriteLine($"  first 300: {(body.Length <= 300 ? body : body[..300])}");

        // The production failure signature must be gone, and the turn completes.
        Assert.DoesNotContain("Missing namespace", body, StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("response.completed", body, StringComparison.Ordinal);

        // The audit must show the bridge FORWARDED the namespace to Copilot on the
        // echoed list_agents function_call — the actual T1→T2 fix, on the real wire.
        var entry = await PollForUpstreamEntryAsync(reader, TimeSpan.FromSeconds(15));
        Assert.NotNull(entry);
        var input = (entry!.UpstreamBody as JsonObject)?["input"] as JsonArray;
        Assert.NotNull(input);

        JsonObject? echoed = null;
        foreach (var n in input!)
            if (n is JsonObject o
                && o["type"]?.GetValue<string>() == "function_call"
                && o["name"]?.GetValue<string>() == "list_agents")
            {
                echoed = o;
                break;
            }
        Assert.NotNull(echoed);
        Assert.Equal("collaboration", echoed!["namespace"]?.GetValue<string>());
        _output.WriteLine("[audit] upstream list_agents function_call carried namespace=collaboration.");
    }

    private static async Task<BridgeLogEntry?> PollForUpstreamEntryAsync(BridgeLogReader reader, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var entry = reader.ReadNew()
                .FirstOrDefault(e => e.InboundPath.EndsWith("/responses", StringComparison.Ordinal)
                                     && e.UpstreamBody is not null);
            if (entry is not null || DateTime.UtcNow >= deadline)
                return entry;
            await Task.Delay(250);
        }
    }
}
