using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// <b>Codex CLI behavior flywheel</b> — drives the real <c>codex.exe</c> through a
/// real bridge SUBPROCESS (<see cref="ServeProcess"/>, passthrough scenario, native
/// <c>/codex</c>) on tasks chosen to exercise the code paths where the shipped
/// gpt-5.6 bugs actually lived — then records a manifest pointing at codex's OWN
/// dispatch log so the <c>real-client-verify</c> skill can render the verdict.
/// </summary>
/// <remarks>
/// <para><b>Why this exists / what the old smokes missed.</b> The previous
/// <c>CodexLoadTaskSmokeTests</c> ran a trivial <c>echo … &gt; f; cat f</c> task
/// (default-namespace shell tools) and asserted on the bridge trace + exit code +
/// stdout canary. That is doubly blind: (1) a trivial task never reaches the
/// namespaced-collaboration / multi-agent / custom-<c>exec</c> paths where the three
/// gpt-5.6 bugs lived, and (2) codex's <c>incompatible payload</c> tool-router fatal
/// is written ONLY to <c>logs_2.sqlite</c> — the bridge stays 200 with
/// <c>function_call</c> on the wire, so every signal the smoke asserted stayed green
/// while exec was 100% broken. This suite fixes both: a load task that drives the
/// real tool loop, and a manifest that hands the verdict agent the path to codex's
/// dispatch log in the real <c>~/.codex/logs_2.sqlite</c> (windowed to this run's
/// start — codex logs there regardless of <c>CODEX_HOME</c>).</para>
/// <para><b>Thin by design.</b> The xUnit assertions here only prove the harness
/// produced evidence (bridge up, client ran, trace + log captured). Whether the tool
/// actually executed — output present, no <c>ERROR codex_core::tools::router</c> /
/// <c>incompatible payload</c> — is judged by the skill from the client's own log,
/// NOT asserted here. See <see cref="ClientBehaviorSupport"/>.</para>
/// <para><b>Path coverage note.</b> <c>codex exec</c> (headless CLI) exercises the
/// function-tool loop and second-turn echoes; the namespaced-collaboration and
/// multi-agent <c>agent_message</c> shapes are emitted by the desktop Codex app's
/// multi-agent mode, which this CLI does not drive. Those shapes have direct
/// captured-byte coverage in the <c>ApiContract</c> suite
/// (<c>CodexNamespaceEchoHeadlessTests</c>, <c>CodexAgentMessageHeadlessTests</c>).
/// This behavior test drives what the CLI CAN drive — the real multi-call tool loop
/// and its dispatch outcome — which is precisely the signal the old smoke read from
/// the wrong source.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ClientBehavior")]
public class CodexBehaviorTests
{
    private readonly ITestOutputHelper _output;

    public CodexBehaviorTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A genuinely multi-step tool task that forces several <c>function_call</c> /
    /// <c>function_call_output</c> round-trips (two writes then a read-back), so the
    /// real Codex tool loop — and its dispatch outcome in <c>logs_2.sqlite</c> —
    /// actually happens for the latest gpt id.
    /// </summary>
    [Fact]
    public async Task Codex_MultiStepToolChain_ProducesDispatchLogForVerdict()
    {
        const string canary = "codex-behavior-canary-51742";
        var prompt =
            "Do these steps in order, actually running the shell commands (do not fabricate output). "
            + "As soon as step 3 is done, give your final answer and stop:\n"
            + "1. Run `echo first-line > codex_probe.txt`.\n"
            + $"2. Run `echo {canary} >> codex_probe.txt`.\n"
            + "3. Run `cat codex_probe.txt` and tell me the exact second line, verbatim.";

        await DriveAndRecordAsync("codex-multistep-toolchain", prompt);
    }

    /// <summary>
    /// A task shaped to drive codex's CODE-EXECUTION grammar tool (the custom
    /// <c>exec</c>) rather than a plain shell <c>function_call</c>: it asks for an
    /// in-process computation, which codex services via its code-mode <c>exec</c> tool.
    /// That is the exact path the 0.4.13 fix targets — codex 0.144.1 fatals
    /// <c>incompatible payload</c> if the bridge emits it as a <c>function_call</c>
    /// instead of a <c>custom_tool_call</c>. The plain shell task above tends toward
    /// <c>function_call</c> (which never exercises the exec fix), so this case exists to
    /// bias toward the <c>custom_tool_call</c> path. Codex still chooses its tool per
    /// run, so the verdict must confirm from the trace WHICH path ran before treating a
    /// clean log as proof of the exec fix (see references/evidence.md).
    /// </summary>
    [Fact]
    public async Task Codex_CodeComputation_DrivesCustomExecPath_ForVerdict()
    {
        const string canary = "codex-exec-canary-88431";
        var prompt =
            "Using your code-execution tool (run actual code, do not compute by hand or use a shell "
            + "echo), compute the following and then STOP:\n"
            + "1. Sum the integers from 1 to 100 inclusive.\n"
            + "2. Take that sum, convert it to a string, and append the exact suffix "
            + $"\"-{canary}\".\n"
            + "3. Report the resulting string verbatim as your final answer.";

        await DriveAndRecordAsync("codex-code-exec", prompt);
    }

    /// <summary>
    /// Shared driver: boot a real bridge subprocess (passthrough), run real codex on the
    /// prompt at the latest gpt id, and write the run manifest pointing the verdict agent
    /// at codex's own dispatch log. The xUnit layer asserts ONLY the harness contract —
    /// the "did the tool execute / any incompatible-payload fatal" verdict is the skill's
    /// job from <c>~/.codex/logs_2.sqlite</c>.
    /// </summary>
    private async Task DriveAndRecordAsync(string caseId, string prompt)
    {
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(ServeScenario.Passthrough));
        _output.WriteLine($"bridge up at {bridge.BaseUrl} (trace: {bridge.TraceDir})");

        // Disposable work dir so codex's file-writing tools mutate a throwaway dir, never
        // the test runner's checkout.
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);

        var result = await CodexProcess.RunAsync(new CodexInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(6),
            WorkingDirectory: work.Path));

        _output.WriteLine($"codex.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"dispatch log (real ~/.codex)={result.DispatchLogPath} window=[{result.StartedUnixSeconds},{result.EndedUnixSeconds}]");

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "codex",
                Route: "/codex",
                Model: ClientBehaviorSupport.LatestGpt,
                Scenario: ServeScenario.Passthrough,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: result.DispatchLogPath,
                DispatchSinceUnix: result.StartedUnixSeconds,
                DispatchUntilUnix: result.EndedUnixSeconds,
                Prompt: prompt),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _);

        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine("[verdict] run `/real-client-verify` — it reads ~/.codex/logs_2.sqlite (windowed) for the dispatch outcome.");

        // Harness contract only. The dispatch verdict (did exec run? any router fatal?)
        // is the skill agent's job, read from logs_2.sqlite the manifest points at.
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }
}
