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
///   <item><b>CC→gpt</b> (cc-to-gpt scenario) — Claude Code's <c>claude-opus-4.8</c>
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
    /// CC→gpt: the client still speaks <c>claude-opus-4.8</c>; the cc-to-gpt scenario's
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
            Timeout: TimeSpan.FromMinutes(8)));

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
