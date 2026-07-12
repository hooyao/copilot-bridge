using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Live headless acceptance test for the newest Codex codename slot,
/// <b>routing Claude Code to gpt-5.6-sol</b> (Copilot's <c>/responses</c> backend,
/// added in the 2026-07 reconciliation). A real headless <c>claude.exe</c> must
/// run a genuinely multi-step tool task through the bridge without erroring, and
/// the tool round-trip must reach the <c>/responses</c> upstream intact.
/// </summary>
/// <remarks>
/// <para>gpt-5.6-sol is one of the gpt-5.6 codenames — the first Codex models to
/// accept the <c>max</c> effort tier (probed in
/// <c>ResponsesProbe.Gpt56_Effort_ReProbe</c>). This test is the end-to-end
/// counterpart to the offline catalog + T2 tests: it proves the whole pipeline —
/// real client, real Copilot gpt-5.6-sol, real multi-tool execution — closes the
/// agentic loop. The assertions mirror <see cref="CcOnGpt5HeadlessTests"/>'s
/// contract (tools never dropped on the wire, a real
/// function_call/function_call_output round-trip, a well-formed client SSE, the
/// canary in the final answer) — the same "Claude Code can run a tool task on this
/// Codex model" guarantee, retargeted to the new id.
/// <see cref="EffortMax_OnGpt56Sol_ReachesWireVerbatim"/> additionally guards the
/// feature this id actually adds: that <c>max</c> reaches the wire unclamped.</para>
/// <para>Tagged Integration — needs live Copilot (DPAPI token or the
/// <c>~/github_token.dat</c> dev fallback) and <c>claude.exe</c>. Skipped in CI.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CcOnGpt56HeadlessTests : IClassFixture<BridgeFixture>
{
    private const string Model = "gpt-5.6-sol";

    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CcOnGpt56HeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    /// <summary>
    /// A genuinely complex, multi-step tool chain: write a file, append a second
    /// line to it across a separate Bash call, read the whole thing back with the
    /// Read tool, then report a canary embedded in it. This needs SEVERAL tool
    /// calls in sequence with tool_result round-trips (not a single echo) — the
    /// shape most likely to expose a tool-translation or streaming regression on a
    /// freshly-added model — while staying bounded enough (no explicit effort, so
    /// Copilot's server default) that the model converges instead of re-verifying
    /// forever.
    /// </summary>
    [Fact]
    public async Task ComplexMultiToolChain_OnGpt56Sol_ReachesFinalAnswer()
    {
        const string canary = "gpt56sol-canary-74213";
        var prompt =
            "Do the following in order, actually calling the tools — do not fabricate any output. "
            + "As soon as step 4 is done, give your final answer and STOP (no extra verification):\n"
            + "1. Use the Bash tool to run `echo first-line > cbridge_probe.txt`.\n"
            + $"2. Use the Bash tool to run `echo {canary} >> cbridge_probe.txt`.\n"
            + "3. Use the Read tool to read cbridge_probe.txt.\n"
            + "4. Tell me the exact second line you read, verbatim.";

        await DriveAndAssert(prompt, canary, allowedTools: "Bash,Read",
            timeout: TimeSpan.FromMinutes(8));
    }

    /// <summary>
    /// Guards the feature this id actually ADDS: gpt-5.6-sol accepts the <c>max</c>
    /// effort tier (the first Codex model to do so — <c>Gpt56_Effort_ReProbe</c>),
    /// so a Claude Code request at <c>max</c> must reach the <c>/responses</c> wire
    /// with <c>reasoning.effort == "max"</c> VERBATIM — not clamped to <c>xhigh</c>
    /// the way every "large"-profile Codex model clamps it. This is the live E2E
    /// counterpart to <c>CodexRequestBuildTests</c>'s in-process byte assertion:
    /// it proves the xlarge profile is really wired AND that Copilot really accepts
    /// <c>max</c> (2xx). A regression that reverted sol's profile to "large" would
    /// emit <c>xhigh</c> here and fail — the exact sensitivity the null-effort
    /// multi-tool smoke lacks (its default effort is accepted by both profiles).
    /// A trivial no-tool prompt keeps it fast (one upstream round-trip).
    /// </summary>
    [Fact]
    public async Task EffortMax_OnGpt56Sol_ReachesWireVerbatim()
    {
        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: "Reply with exactly the word: ok",
            Model: Model,
            Effort: "max",
            OutputFormat: "json",
            AllowedTools: "",            // no tools — a single fast turn
            Timeout: TimeSpan.FromMinutes(3)));

        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        if (result.ExitCode != 0)
        {
            _output.WriteLine("STDERR:\n" + result.Stderr);
            _output.WriteLine("STDOUT:\n" + Trunc(result.Stdout, 2000));
        }

        var solResponses = reader.ReadNew()
            .Where(e => e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal))
            .Select(e => InspectUpstream(e.UpstreamBody) is var u && u.IsResponses && u.Model == Model
                ? (Effort: ReadEffort(e.UpstreamBody), Status: e.UpstreamStatus)
                : (Effort: (string?)null, Status: 0))
            .Where(x => x.Status != 0)
            .ToList();

        foreach (var r in solResponses)
            _output.WriteLine($"  sol upstream status={r.Status} reasoning.effort={r.Effort ?? "<none>"}");

        // At least one request must have carried effort=max to gpt-5.6-sol on the
        // /responses wire, with a 2xx. Claude Code sends 'max' as the Anthropic
        // effort; the xlarge profile passes it through verbatim (large would clamp
        // to xhigh). Both the presence of "max" AND the 2xx are load-bearing:
        // "max" proves the profile accepts it; 2xx proves Copilot does too.
        var maxOnWire = solResponses.Where(r => r.Effort == "max" && r.Status is >= 200 and < 300).ToList();
        Assert.True(maxOnWire.Count > 0,
            "No /responses request to gpt-5.6-sol carried reasoning.effort=\"max\" with a 2xx. "
            + "Either the xlarge profile isn't wired (effort would be clamped to xhigh) or Copilot "
            + "rejected max. Observed: "
            + string.Join("; ", solResponses.Select(r => $"effort={r.Effort ?? "<none>"}/status={r.Status}")));
        // No sol upstream may have DOWNGRADED max to xhigh — that's the precise
        // regression (profile reverted to large). Catches the case where some
        // requests kept max but a mis-wired one clamped it.
        Assert.DoesNotContain(solResponses, r => r.Effort == "xhigh");

        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Shared driver: run claude.exe headless at gpt-5.6-sol, then assert the tool
    /// round-trip reached the /responses upstream intact and the canary made it into
    /// the final answer. Every assertion is the contract of "Claude Code can run a
    /// complex tool task on gpt-5.6-sol", not an implementation detail.
    /// </summary>
    private async Task DriveAndAssert(
        string prompt, string canary, string allowedTools, TimeSpan timeout)
    {
        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: prompt,
            Model: Model,
            Effort: null,
            OutputFormat: "json",
            AllowedTools: allowedTools,
            Timeout: timeout));

        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        if (result.ExitCode != 0)
        {
            _output.WriteLine("STDERR:\n" + result.Stderr);
            _output.WriteLine("STDOUT:\n" + Trunc(result.Stdout, 2000));
        }

        var entries = reader.ReadNew()
            .Where(e => e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"bridge /v1/messages entries: {entries.Count}");
        var sawFunctionCall = false;
        var sawFunctionCallOutput = false;
        var sawToolsReachWire = false;
        var sawSolUpstream = false;
        var badUpstream = new List<string>();
        var droppedTools = new List<string>();
        var badStream = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var (types, target, model) = InspectUpstream(e.UpstreamBody);
            var inboundTools = CountInboundTools(e.InboundBody);
            var upstreamTools = CountTools(e.UpstreamBody);
            // Is this a request that actually went to gpt-5.6-sol on the /responses
            // wire? All the sol-specific evidence below is gated on this so a tool
            // round-trip on some OTHER Responses model in the same run can't satisfy
            // the "tools reached gpt-5.6-sol" claim.
            var isSol = target && model == Model;
            _output.WriteLine(
                $"  [{i}] upstreamStatus={e.UpstreamStatus} model={model} inboundTools={inboundTools} "
                + $"upstreamTools={upstreamTools} inputItemTypes={string.Join(",", types)}");
            if (isSol && types.Contains("function_call")) sawFunctionCall = true;
            if (isSol && types.Contains("function_call_output")) sawFunctionCallOutput = true;
            // The request must actually have gone to gpt-5.6-sol on the /responses
            // wire — proves the new id routed to the Responses backend, not that a
            // fallback model silently served the task.
            if (isSol) sawSolUpstream = true;
            // Any non-2xx upstream on this path is the failure we're hunting.
            if (e.UpstreamStatus is not (>= 200 and < 300) && e.UpstreamStatus != 0)
                badUpstream.Add($"[{i}] status={e.UpstreamStatus} body={Trunc(e.InboundResponseBody?.ToJsonString() ?? "", 300)}");

            // The core contract: when Claude Code SENT tools, they must survive to
            // the /responses wire. A request that sent no tools (Claude Code's
            // title/quota preflight) legitimately has none upstream — do NOT
            // require tools on every request, only that they're never DROPPED.
            // Gated on isSol so this proves tools reached gpt-5.6-sol specifically.
            if (isSol && inboundTools > 0)
            {
                if (upstreamTools > 0) sawToolsReachWire = true;
                else droppedTools.Add($"[{i}] inbound had {inboundTools} tools but /responses body had 0");
            }

            // Stream integrity: a client-facing (T3-translated) SSE response must
            // carry EXACTLY ONE message_start and EXACTLY ONE message_stop. A
            // second dangling message_start (the FlushTerminal double-terminal bug)
            // corrupts Claude Code's parse ("replies look weird"); a MISSING
            // terminal (zero) is just as malformed. We only assert this on a
            // response that actually streamed events (starts >= 1 identifies a
            // T3 SSE response — a non-streaming preflight has no captured events
            // and is legitimately skipped, not forced to have a terminal pair).
            if (e.InboundStatus is >= 200 and < 300)
            {
                var starts = CountClientEvents(e.Events, "message_start");
                var stops = CountClientEvents(e.Events, "message_stop");
                if (starts >= 1 && (starts != 1 || stops != 1))
                    badStream.Add($"[{i}] client SSE had message_start={starts} message_stop={stops} (expected exactly 1/1)");
            }
        }

        // 0. Client-facing stream must be well-formed (one message_start/stop each).
        Assert.True(badStream.Count == 0,
            "Client-facing SSE was malformed (double terminal):\n" + string.Join("\n", badStream));

        // 1. Every upstream call that ran must be a success (no 400s from a
        //    malformed Responses body — e.g. an effort the new profile got wrong).
        Assert.True(badUpstream.Count == 0,
            "Upstream /responses returned non-2xx:\n" + string.Join("\n", badUpstream));

        // 2. The task actually ran on gpt-5.6-sol via /responses (routing worked).
        Assert.True(sawSolUpstream,
            $"No /responses request went to {Model} — the new id did not route to the Responses backend.");

        // 3. Tools must NEVER be dropped between inbound and the /responses wire.
        Assert.True(droppedTools.Count == 0,
            "Tools were dropped on the /responses wire:\n" + string.Join("\n", droppedTools));
        Assert.True(sawToolsReachWire,
            "No request carried tools to the /responses upstream — the harness never exercised the tool path.");

        // 4. The client must have exited cleanly.
        Assert.Equal(0, result.ExitCode);

        // 5. The model actually called a tool and got a result back — proof the
        //    tools reached gpt-5.6-sol and the tool_use→tool_result loop worked.
        Assert.True(sawFunctionCall,
            "No function_call reached the /responses upstream — the model never invoked a tool.");
        Assert.True(sawFunctionCallOutput,
            "No function_call_output reached the /responses upstream — the tool result was never fed back.");

        // 6. The final answer echoes the canary the shell wrote and the client read
        //    back — end-to-end proof the multi-step agentic round-trip closed on
        //    gpt-5.6-sol.
        Assert.Contains(canary, result.Stdout);
    }

    /// <summary>Return the input[] item types, whether this went to /responses, and the target model.</summary>
    private static (IReadOnlyList<string> Types, bool IsResponses, string? Model) InspectUpstream(JsonNode? upstreamBody)
    {
        if (upstreamBody is not JsonObject obj) return (Array.Empty<string>(), false, null);
        var isResponses = obj["input"] is JsonArray; // Responses bodies have input[], Anthropic has messages[]
        var model = obj["model"]?.GetValue<string>();
        var types = new List<string>();
        if (obj["input"] is JsonArray input)
        {
            foreach (var item in input)
            {
                if (item is JsonObject io && io["type"]?.GetValue<string>() is { } t) types.Add(t);
            }
        }
        return (types, isResponses, model);
    }

    private static int CountTools(JsonNode? upstreamBody) =>
        upstreamBody is JsonObject obj && obj["tools"] is JsonArray tools ? tools.Count : 0;

    /// <summary>The <c>reasoning.effort</c> written to the /responses upstream body, or null if absent.</summary>
    private static string? ReadEffort(JsonNode? upstreamBody) =>
        upstreamBody is JsonObject obj && obj["reasoning"] is JsonObject r
            ? r["effort"]?.GetValue<string>()
            : null;

    /// <summary>Tool count on the INBOUND (Anthropic-shape) request body.</summary>
    private static int CountInboundTools(JsonNode? inboundBody) =>
        inboundBody is JsonObject obj && obj["tools"] is JsonArray tools ? tools.Count : 0;

    /// <summary>
    /// Count client-facing SSE events of a given Anthropic event type in the
    /// captured inbound-resp events (the T3-translated stream Claude Code parses).
    /// The audit sink serializes each event as { "event": "&lt;type&gt;", "data": ... };
    /// fall back to the data payload's "type" when the event label is absent.
    /// </summary>
    private static int CountClientEvents(JsonArray? events, string eventType)
    {
        if (events is null) return 0;
        var n = 0;
        foreach (var ev in events)
        {
            if (ev is not JsonObject eo) continue;
            var label = eo["event"]?.GetValue<string>();
            if (label is null && eo["data"] is JsonObject data)
                label = data["type"]?.GetValue<string>();
            if (label == eventType) n++;
        }
        return n;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
