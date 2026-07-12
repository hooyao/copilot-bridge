using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// The live acceptance test for the reported failure: <b>routing Claude Code to
/// gpt-5.5</b> (Copilot's <c>/responses</c> backend) must let a real headless
/// <c>claude.exe</c> run a multi-tool task without erroring. Drives the real
/// client at <c>--model gpt-5.5</c> against an in-process bridge with live
/// Copilot auth, then asserts the bridge actually exercised the tool
/// round-trip on the <c>/responses</c> upstream (not a bare chat).
/// </summary>
/// <remarks>
/// <para>This is the end of the harness loop: the offline
/// <c>TraceReplayResponsesTests</c> proves T2 emits the tools; this proves the
/// whole pipeline — real client, real Copilot gpt-5.5, real tool execution —
/// works end to end. Before the T2 tool-translation fix the model received zero
/// tools, so it could never call Bash/Read and the task failed; the asserts
/// below (a real <c>function_call</c> on the <c>/responses</c> upstream + the
/// canary in the final answer) are exactly what that bug made impossible.</para>
/// <para>Tagged Integration — needs live Copilot (DPAPI token or the
/// <c>~/github_token.dat</c> dev fallback) and <c>claude.exe</c>. Skipped in CI.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CcOnGpt5HeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CcOnGpt5HeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    /// <summary>
    /// A genuinely multi-step task: create a file via Bash, read it back with the
    /// Read tool, then report the canary. Needs at least two tool calls and a
    /// tool_result round-trip — the shape that failed when tools were dropped.
    /// </summary>
    [Fact]
    public async Task ComplexMultiToolTask_OnGpt55_ReachesFinalAnswer()
    {
        const string canary = "gpt55-cc-canary-90317";
        var prompt =
            $"Use the Bash tool to run `echo {canary}`. Then tell me exactly what it printed. "
            + "Do not guess — you must actually call the Bash tool and read its output.";
        await DriveAndAssert(prompt, canary, "Bash", TimeSpan.FromMinutes(4));
    }

    /// <summary>
    /// A harder, multi-step chain: write a file with one Bash call, read it back
    /// with another, and report a canary embedded in it. Exercises several tool
    /// calls in sequence (not a single echo) — the "slightly complex task" shape
    /// the user reported as failing on gpt-5.5.
    /// </summary>
    [Fact]
    public async Task MultiStepToolChain_OnGpt55_ReachesFinalAnswer()
    {
        const string canary = "gpt55-chain-canary-55182";
        var prompt =
            $"Do this in order using the Bash tool: (1) run `echo {canary} > cbridge_probe.txt` to write a file, "
            + "(2) run `cat cbridge_probe.txt` to read it back, (3) tell me the exact contents you read. "
            + "You must actually run both commands — do not fabricate the output.";
        await DriveAndAssert(prompt, canary, "Bash", TimeSpan.FromMinutes(4));
    }

    /// <summary>
    /// Shared driver: run claude.exe headless at gpt-5.5, then assert the tool
    /// round-trip reached the /responses upstream intact and the canary made it
    /// into the final answer. Every assertion is the contract of "Claude Code can
    /// run a tool task on gpt-5.5", not an implementation detail.
    /// </summary>
    private async Task DriveAndAssert(string prompt, string canary, string allowedTools, TimeSpan timeout)
    {
        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: prompt,
            Model: "gpt-5.5",
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
        var badUpstream = new List<string>();
        var droppedTools = new List<string>();
        var badStream = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var (types, target) = InspectUpstream(e.UpstreamBody);
            var inboundTools = CountInboundTools(e.InboundBody);
            var upstreamTools = CountTools(e.UpstreamBody);
            _output.WriteLine(
                $"  [{i}] upstreamStatus={e.UpstreamStatus} inboundTools={inboundTools} "
                + $"upstreamTools={upstreamTools} inputItemTypes={string.Join(",", types)}");
            if (types.Contains("function_call")) sawFunctionCall = true;
            if (types.Contains("function_call_output")) sawFunctionCallOutput = true;
            // Any non-2xx upstream on this path is the failure we're hunting.
            if (e.UpstreamStatus is not (>= 200 and < 300) && e.UpstreamStatus != 0)
                badUpstream.Add($"[{i}] status={e.UpstreamStatus} body={Trunc(e.InboundResponseBody?.ToJsonString() ?? "", 300)}");

            // The core contract: when Claude Code SENT tools, they must survive to
            // the /responses wire. A request that sent no tools (Claude Code's
            // title/quota preflight) legitimately has none upstream — do NOT
            // require tools on every request, only that they're never DROPPED.
            if (target && inboundTools > 0)
            {
                if (upstreamTools > 0) sawToolsReachWire = true;
                else droppedTools.Add($"[{i}] inbound had {inboundTools} tools but /responses body had 0");
            }

            // Stream integrity: the client-facing (T3-translated) SSE for each
            // streamed response must carry EXACTLY ONE message_start. A second,
            // dangling message_start (the FlushTerminal double-terminal bug)
            // corrupts Claude Code's parse — the "replies look weird" symptom.
            if (e.InboundStatus is >= 200 and < 300)
            {
                var starts = CountClientEvents(e.Events, "message_start");
                var stops = CountClientEvents(e.Events, "message_stop");
                if (starts > 1 || stops > 1)
                    badStream.Add($"[{i}] client SSE had message_start={starts} message_stop={stops} (expected 1/1)");
            }
        }

        // 0. Client-facing stream must be well-formed (one message_start/stop each).
        Assert.True(badStream.Count == 0,
            "Client-facing SSE was malformed (double terminal):\n" + string.Join("\n", badStream));

        // 1. Every upstream call that ran must be a success (no 400s from a
        //    malformed Responses body).
        Assert.True(badUpstream.Count == 0,
            "Upstream /responses returned non-2xx:\n" + string.Join("\n", badUpstream));

        // 2. Tools must NEVER be dropped between inbound and the /responses wire —
        //    this is the exact bug (T2 ignored ir.Tools). At least one request must
        //    have carried tools through, and none may have lost them.
        Assert.True(droppedTools.Count == 0,
            "Tools were dropped on the /responses wire (the reported bug):\n" + string.Join("\n", droppedTools));
        Assert.True(sawToolsReachWire,
            "No request carried tools to the /responses upstream — the harness never exercised the tool path.");

        // 3. The client must have exited cleanly.
        Assert.Equal(0, result.ExitCode);

        // 4. The model actually called a tool and got a result back — proof the
        //    tools reached gpt-5.5 and the tool_use→tool_result loop worked.
        Assert.True(sawFunctionCall,
            "No function_call reached the /responses upstream — the model never invoked a tool "
            + "(tools were dropped, or gpt-5.5 chose not to call one).");
        Assert.True(sawFunctionCallOutput,
            "No function_call_output reached the /responses upstream — the tool result was never fed back.");

        // 5. The final answer echoes the canary the shell printed — end-to-end proof
        //    the agentic round-trip closed on gpt-5.5.
        Assert.Contains(canary, result.Stdout);
    }

    /// <summary>Return the input[] item types + whether this went to /responses.</summary>
    private static (IReadOnlyList<string> Types, bool IsResponses) InspectUpstream(JsonNode? upstreamBody)
    {
        if (upstreamBody is not JsonObject obj) return (Array.Empty<string>(), false);
        var isResponses = obj["input"] is JsonArray; // Responses bodies have input[], Anthropic has messages[]
        var types = new List<string>();
        if (obj["input"] is JsonArray input)
        {
            foreach (var item in input)
            {
                if (item is JsonObject io && io["type"]?.GetValue<string>() is { } t) types.Add(t);
            }
        }
        return (types, isResponses);
    }

    private static int CountTools(JsonNode? upstreamBody) =>
        upstreamBody is JsonObject obj && obj["tools"] is JsonArray tools ? tools.Count : 0;

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
