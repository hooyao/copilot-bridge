using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Catalogs.Codex;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// ApiContract E2E: the REAL <c>codex.exe</c> driven through the bridge's
/// <c>/codex/responses</c> endpoint on a task that forces Copilot's HOSTED web
/// search, asserting from the bridge's four-file audit that the bridge RELAYED the
/// server-side <c>web_search_call</c> lifecycle back to codex (T3/T4 carrier). This
/// is the wire-contract half of the native-web-search change; the flip side is the
/// offline <c>CodexWebSearchRoundTripTests</c> fixture round-trip.
/// </summary>
/// <remarks>
/// <para><b>Why ApiContract, not ClientBehavior.</b> Per the repo's Kind rule
/// (CLAUDE.md/AGENTS.md): a test that <i>drives a client then asserts the upstream/
/// relayed wire shape in-xUnit</i> is <c>Kind=ApiContract</c> — same bucket as
/// <c>CodexLoadTaskSmokeTests</c>/<c>CodexE2EHeadlessTests</c>. ClientBehavior is
/// reserved for thin actuators whose verdict is the <c>real-client-verify</c> skill
/// reading the client's OWN log. This test's verdict IS an in-test trace assertion,
/// so it belongs here.</para>
/// <para><b>Model.</b> Exact gpt-6.2-sol with an isolated, test-only catalog
/// override of <c>use_responses_lite=false</c>. Its official Lite metadata
/// suppresses hosted tools client-side, which would prevent this transport
/// regression from reaching hosted search. Production metadata stays pinned.
/// The bridge fix is model-agnostic. The shell
/// is forbidden in the prompt so hosted web_search is the ONLY path to the answer.</para>
/// <para>Ephemeral non-8765 port (BridgeFixture, tracing on); provider injected via
/// <c>codex exec -c</c> overrides. Integration-tagged: skipped in CI.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CodexWebSearchHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexWebSearchHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task Codex_NativeWebSearch_BridgeRelaysWebSearchCallToClient()
    {
        const string model = "gpt-6.2-sol";
        using var scratch = ClientBehaviorSupport.NewWorkDir("codex-hosted-search-catalog");
        var resource = CodexSupplementalCatalog.Load().Models.Single(
            row => row.GetProperty("slug").GetString() == model);
        var testResource = JsonNode.Parse(resource.GetRawText())!.AsObject();
        testResource["use_responses_lite"] = false;
        var catalogPath = Path.Combine(scratch.Path, "model-catalog.json");
        await File.WriteAllTextAsync(catalogPath,
            new JsonObject { ["models"] = new JsonArray(testResource) }.ToJsonString());
        var tomlCatalogPath = catalogPath.Replace('\\', '/');
        var prompt =
            "Use ONLY your built-in web_search tool. Do NOT run any shell command, do NOT use "
            + "PowerShell, curl, Invoke-RestMethod, or any command-execution tool. Search the web "
            + "for the current stable release version of the Node.js 20.x line as published on "
            + "nodejs.org today. Reply on a single final line formatted exactly as "
            + "`WEBSEARCH_RESULT=<version> SOURCE=<url>`, then stop.";

        // Constructed BEFORE the run so it only reports this test's requests.
        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await CodexProcess.RunAsync(new CodexInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: prompt,
            Model: model,
            Timeout: TimeSpan.FromMinutes(6),
            CodexHome: scratch.Path,
            WorkingDirectory: scratch.Path,
            ExpectedCodexVersion: "0.167.0-alpha.4.1",
            // Force hosted search into live-fetch mode (default is "cached"); a
            // non-responses-lite model then emits the {type:web_search} tool.
            ExtraConfig: new[] { "web_search=live", $"model_catalog_json=\"{tomlCatalogPath}\"" }));

        // Poll (bounded) until the async BridgeIoSink flush settles on BOTH signals we
        // assert — the upstream web_search_call AND the inbound response.web_search_call.*
        // relay — or the deadline passes. Waiting only on upstream is a race: the endpoint
        // enqueues inbound-resp AFTER upstream-resp and the sink writes them asynchronously,
        // so a poll that stops at upstream can observe the relay events not-yet-flushed.
        var entries = await PollUntilRelaySettledAsync(reader, TimeSpan.FromSeconds(20));

        _output.WriteLine($"codex.exe exit={result.ExitCode} duration={result.Duration} model={model}");
        _output.WriteLine($"bridge /responses entries: {entries.Count}");

        Assert.Equal(0, result.ExitCode);

        // ① Premise: Copilot actually ran a server-side search (raw upstream-resp SSE
        // carries a web_search_call). A run where the model answered from memory is
        // INCONCLUSIVE — assert it so a no-search run fails loudly instead of green.
        var upstreamSearched = entries.Any(e =>
            RawUpstreamSse(e).Contains("web_search_call", StringComparison.Ordinal));
        Assert.True(upstreamSearched,
            "INCONCLUSIVE: no web_search_call in any upstream-resp — Copilot never searched "
            + "(codex may have answered from memory). The relay can't be judged without a real search.");

        // ② The fix: the bridge RELAYED the search lifecycle to codex. The inbound-resp
        // (what the bridge sent the client) must carry response.web_search_call.* events.
        // Before the T3/T4 carrier these were swallowed (codex saw the answer, not the
        // search) while codex still exited 0 — so stdout/exit-code can't guard this.
        var relayedToClient = entries.Any(e =>
            InboundEventNames(e).Any(n => n.StartsWith("response.web_search_call.", StringComparison.Ordinal)));
        Assert.True(relayedToClient,
            "REGRESSION: Copilot returned web_search_call upstream but the bridge sent codex NO "
            + "response.web_search_call.* events — T3/T4 swallowed the search (the pre-fix bug).");

        _output.WriteLine("[web-search] upstream searched ✓ and bridge relayed web_search_call.* to codex ✓");
        _output.WriteLine($"[audit] four-file IO traces under {_bridge.LogDirectory}");
    }

    /// <summary>Raw Copilot SSE recorded on the upstream-resp artifact (a string body).</summary>
    private static string RawUpstreamSse(BridgeLogEntry e)
    {
        var body = e.UpstreamResp?["body"];
        return body switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            not null => body.ToJsonString(),
            _ => "",
        };
    }

    /// <summary>The SSE event names the bridge sent the client (inbound-resp events[].event).</summary>
    private static IEnumerable<string> InboundEventNames(BridgeLogEntry e) =>
        e.Events is { } evs
            ? evs.Select(ev => ev?["event"]?.GetValue<string>() ?? "").Where(n => n.Length > 0)
            : Array.Empty<string>();

    private static async Task<IReadOnlyList<BridgeLogEntry>> PollUntilRelaySettledAsync(
        BridgeLogReader reader, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        IReadOnlyList<BridgeLogEntry> entries = reader.ReadNew();
        while (DateTime.UtcNow < deadline)
        {
            entries = reader.ReadNew();
            var upstreamSearched = entries.Any(e =>
                RawUpstreamSse(e).Contains("web_search_call", StringComparison.Ordinal));
            var relayed = entries.Any(e =>
                InboundEventNames(e).Any(n => n.StartsWith("response.web_search_call.", StringComparison.Ordinal)));
            // Leave only once BOTH the upstream search and its inbound relay are flushed,
            // so the assertions below never race the async sink writing inbound-resp.
            if (upstreamSearched && relayed)
                break;
            await Task.Delay(500);
        }
        return entries;
    }
}
