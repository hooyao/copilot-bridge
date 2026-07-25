using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// <b>Claude Code behavior flywheel</b> — drives the real <c>claude.exe</c> through a
/// real bridge SUBPROCESS (<see cref="ServeProcess"/>) on a multi-tool task, covering
/// two routes with the SAME task so their client-side behavior is comparable:
/// <list type="bullet">
///   <item><b>Native <c>/cc</c></b> (passthrough scenario) at the latest Claude id.</item>
///   <item><b>CC→gpt</b> (cc-to-gpt scenario) — Claude Code's <c>claude-opus-5</c>
///   traffic routed to Copilot's <c>gpt-5.6-sol</c> <c>/responses</c> backend. This
///   is the leg left UNVERIFIED at 0.4.13: T3 stamps bridge-internal markers
///   (<c>bridge_tool_namespace</c> / <c>bridge_input_is_grammar_text</c>) on the
///   CC→gpt path and <c>ClaudeCodeOutboundAdapter</c> must scrub them so they never
///   reach the Claude client.</item>
/// </list>
/// </summary>
/// <remarks>
/// Thin by design: the xUnit layer proves the harness produced evidence; the
/// <c>real-client-verify</c> skill renders the semantic verdict from the client's own
/// transcript + the bridge trace (for the CC→gpt case, that the markers are absent in
/// what the client received). See <see cref="ClientBehaviorSupport"/>.
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ClientBehavior")]
public class ClaudeCodeBehaviorTests
{
    private readonly ITestOutputHelper _output;

    public ClaudeCodeBehaviorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ClaudeCode_NativeCc_MultiToolChain_ProducesTranscriptForVerdict()
    {
        const string caseId = "cc-native-multitool";
        await RunMultiToolCaseAsync(
            caseId,
            scenario: ServeScenario.Passthrough,
            model: ClientBehaviorSupport.LatestClaude,
            route: "/cc",
            canary: "cc-native-canary-88213");
    }

    /// <summary>
    /// CC→gpt: the client still speaks <c>claude-opus-5</c>; the cc-to-gpt scenario's
    /// routing sends it to <c>gpt-5.6-sol</c>. The verdict agent additionally checks
    /// the bridge trace to confirm the internal markers did NOT leak to the client.
    /// </summary>
    [Fact]
    public async Task ClaudeCode_RoutedToGpt_MultiToolChain_NoMarkerLeak()
    {
        const string caseId = "cc-to-gpt-multitool";
        await RunMultiToolCaseAsync(
            caseId,
            scenario: ServeScenario.CcToGpt,
            model: ClientBehaviorSupport.LatestClaude, // client id; routing rewrites to gpt-5.6-sol
            route: "/cc->gpt",
            canary: "cc-to-gpt-canary-33917");
    }

    /// <summary>
    /// <b>opus-5 cross-field constraint (thinking-disabled × effort).</b> Copilot rejects
    /// <c>output_config.effort</c> of <c>xhigh</c>/<c>max</c> on opus-5 <i>when
    /// <c>thinking</c> is disabled</i> — 400 <c>"…not supported when thinking is disabled
    /// on this model. Use effort 'high' or below, or enable thinking."</c> — while
    /// accepting both fields independently
    /// (<c>ModelProfileProbe.Opus5_DisabledThinking_EffortInteraction_Probe</c>).
    /// <see cref="Routing.ProfileAdjuster"/> clamps the effort down on that path only.
    /// <para><b>Why this case exists (Gate 1).</b> The ordinary multi-tool case runs at
    /// Claude Code's default effort, where the pair never forms — its trace showed
    /// <c>thinking=disabled</c> reaching the wire only at <c>effort=high</c>, which is
    /// legal, so the clamp was never exercised. Pinning
    /// <c>CLAUDE_CODE_EFFORT_LEVEL=max</c> makes the real client emit the offending
    /// combination on the internal no-thinking requests it issues alongside the main
    /// turn. Without the clamp those requests 400 upstream.</para>
    /// <para>Verdict evidence: every upstream request must be 2xx, and any upstream body
    /// carrying <c>thinking.type=disabled</c> must NOT carry effort <c>xhigh</c>/<c>max</c>
    /// — cross-checked against the inbound body to confirm the client really did send
    /// the rejected pair (otherwise the case proves nothing and is INCONCLUSIVE).</para>
    /// </summary>
    [Fact]
    public async Task ClaudeCode_NativeCc_MaxEffort_DisabledThinkingEffortIsClamped()
    {
        const string caseId = "cc-native-opus5-disabled-thinking-max-effort";
        const string canary = "cc-opus5-clamp-canary-51724";

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(ServeScenario.Passthrough));
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);

        var prompt =
            "Do the following in order, actually calling the tools — do not fabricate any output. "
            + "As soon as step 2 is done, give your final answer and STOP (no extra verification):\n"
            + $"1. Use the Bash tool to run `echo {canary} > cbridge_probe.txt`.\n"
            + "2. Use the Read tool to read cbridge_probe.txt and tell me the exact first line, verbatim.";

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestClaude,
            // The whole point of the case: drive the client at max effort so its
            // no-thinking internal requests form the disabled+max pair Copilot rejects.
            Effort: "max",
            OutputFormat: "stream-json",
            Verbose: true,
            AllowedTools: "Bash,Read",
            Timeout: TimeSpan.FromMinutes(8),
            WorkingDirectory: work.Path));

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "claude",
                Route: "/cc",
                Model: ClientBehaviorSupport.LatestClaude,
                Scenario: ServeScenario.Passthrough,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: null,
                DispatchSinceUnix: 0,
                DispatchUntilUnix: 0,
                Prompt: prompt),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _);

        _output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine(
            "[verdict] real-client-verify: every upstream 2xx; no upstream body may pair "
            + "thinking.type=disabled with effort xhigh/max; confirm from an inbound body that "
            + "the client actually sent that pair (else INCONCLUSIVE).");

        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    /// <summary>
    /// CC→gpt recursive-delegation guard: the real root Claude Code agent must be
    /// able to execute one Agent call, but the child request's translated Responses
    /// tools must omit Agent while retaining Bash/Read. The prompt bounds the broken
    /// case to one attempted grandchild so mutation/failure cannot recreate the
    /// incident's unbounded-width storm. The semantic verdict comes from the real
    /// Claude transcript plus the root/child request traces.
    /// </summary>
    [Fact]
    public async Task ClaudeCode_RoutedToGpt_SubagentCannotDelegateRecursively()
    {
        const string caseId = "cc-to-gpt-recursive-agent-guard";
        const string canary = "cc-agent-guard-canary-73154";
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(ServeScenario.CcToGpt));
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);

        var prompt =
            "You MUST use the Agent tool exactly once yourself; do not use Bash yourself. "
            + "Launch one general-purpose child synchronously (run_in_background=false) with this exact task: "
            + "'First inspect which tools are actually available. If Agent is available, use it exactly once "
            + "synchronously to launch one grandchild whose task explicitly forbids Agent and uses Bash to write "
            + canary + " to cbridge_probe.txt. If Agent is not available, use Bash yourself to write the same exact "
            + "text to cbridge_probe.txt. Then use Read to read the file and return the exact text. Do not create more "
            + "than one child.' After your child returns, use Read yourself on cbridge_probe.txt and report the exact "
            + "text, then STOP.";

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestClaude,
            OutputFormat: "stream-json",
            Verbose: true,
            AllowedTools: "Agent,Bash,Read",
            Bare: false,
            Timeout: TimeSpan.FromMinutes(4),
            WorkingDirectory: work.Path));

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "claude",
                Route: "/cc->gpt/recursive-agent-guard",
                Model: ClientBehaviorSupport.LatestClaude,
                Scenario: ServeScenario.CcToGpt,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: null,
                DispatchSinceUnix: 0,
                DispatchUntilUnix: 0,
                Prompt: prompt),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _);

        _output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine(
            "[verdict] real-client-verify must prove root Agent execution, child Agent omission, "
            + "child Bash/Read execution, final canary, and no bridge marker leak.");

        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    /// <summary>
    /// Path-exercising acceptance for a Responses stream that emits partial
    /// commentary and then stalls after headers. The deterministic upstream makes
    /// the first bridge request time out; only a real Claude streaming retry can
    /// reach the later Bash/Read calls and final canary.
    /// </summary>
    [Fact]
    public async Task ClaudeCode_RoutedToGpt_StalledAttempt_RetriesAndExecutesTools()
    {
        const string caseId = "cc-to-gpt-stream-fault-recovery";
        const string canary = "cc-stream-recovery-canary-64129";
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        var probePath = Path.Combine(work.Path, "cbridge_probe.txt");
        await using var upstream = ResponsesFaultRecoveryServer.Start(probePath, canary);
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.CcToGptFaultRecovery,
            TestUpstreamBaseUrl: upstream.BaseUrl,
            StreamIdleTimeoutSeconds: 1,
            WholeResponseBuffering: true));

        var prompt =
            "Actually use the Bash tool to write the exact text " + canary
            + " to cbridge_probe.txt, then use Read on that file, then report the exact text. ";
        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestClaude,
            OutputFormat: "stream-json",
            Verbose: true,
            AllowedTools: "Bash,Read",
            Timeout: TimeSpan.FromMinutes(4),
            WorkingDirectory: work.Path));

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "claude",
                Route: "/cc->gpt/fault-recovery",
                Model: ClientBehaviorSupport.LatestClaude,
                Scenario: ServeScenario.CcToGptFaultRecovery,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: null,
                DispatchSinceUnix: 0,
                DispatchUntilUnix: 0,
                Prompt: prompt),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _);

        _output.WriteLine($"fault upstream={upstream.BaseUrl} requests={upstream.RequestCount}");
        _output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine("[verdict] run `/real-client-verify` against the transcript and request-id traces.");

        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    private async Task RunMultiToolCaseAsync(
        string caseId, ServeScenario scenario, string model, string route, string canary)
    {
        var prompt =
            "Do the following in order, actually calling the tools — do not fabricate any output. "
            + "As soon as step 3 is done, give your final answer and STOP (no extra verification):\n"
            + "1. Use the Bash tool to run `echo first-line > cbridge_probe.txt`.\n"
            + $"2. Use the Bash tool to run `echo {canary} >> cbridge_probe.txt`.\n"
            + "3. Use the Read tool to read cbridge_probe.txt and tell me the exact second line, verbatim.";

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(scenario));
        _output.WriteLine($"bridge up at {bridge.BaseUrl} scenario={scenario} (trace: {bridge.TraceDir})");

        // Disposable work dir so claude's Bash/Read tools mutate a throwaway dir, never
        // the test runner's checkout.
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: model,
            Effort: null,
            // stream-json + verbose so the saved stdout carries the INTERMEDIATE
            // assistant/tool_use/tool_result events, not just the final result envelope
            // (which `--output-format json` alone emits) — the verdict agent needs the
            // client-side tool round-trip, and this is where it reads it.
            OutputFormat: "stream-json",
            Verbose: true,
            AllowedTools: "Bash,Read",
            Timeout: TimeSpan.FromMinutes(8),
            WorkingDirectory: work.Path));

        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "claude",
                Route: route,
                Model: model,
                Scenario: scenario,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: null,
                DispatchSinceUnix: 0,
                DispatchUntilUnix: 0,
                Prompt: prompt),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _);

        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine("[verdict] run `/real-client-verify` — it reads the claude transcript + bridge trace.");

        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }
}
