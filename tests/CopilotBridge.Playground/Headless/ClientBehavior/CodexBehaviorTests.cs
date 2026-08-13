using System.Runtime.Versioning;
using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// <b>Codex CLI behavior flywheel</b> — drives the real <c>codex.exe</c> through a
/// real bridge SUBPROCESS (<see cref="ServeProcess"/>, passthrough scenario, native
/// <c>/codex</c>) on tasks chosen to exercise the code paths where the shipped
/// gpt-5.6 bugs actually lived — then records a manifest pointing at codex's OWN
/// evidence so the <c>real-client-verify</c> skill can render the verdict.
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
/// real tool loop, and a manifest that hands the verdict agent an isolated client-owned
/// <c>logs_2.sqlite</c>. Every case uses headless <c>codex app-server</c>, which starts
/// the SQLite log layer under a per-run <c>CODEX_SQLITE_HOME</c>.</para>
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
    /// real Codex tool loop and its client-owned dispatch evidence actually happen for
    /// the latest gpt id.
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

    [Fact]
    public async Task Codex_OneShotCopilot403_RefreshesAndCompletesComplexToolChain_ForVerdict()
    {
        const string canary = "codex-auth-replay-canary-64017";
        var prompt =
            "Perform these steps with real tools, in order; do not fabricate output:\n"
            + "1. Use your code-execution tool (actual code, not mental arithmetic or shell echo) "
            + "to compute the sum of integers 1 through 100.\n"
            + "2. Run a shell command that writes that numeric result as the first line of "
            + "auth_replay_probe.txt.\n"
            + $"3. Run a separate shell command that appends {canary} as the second line.\n"
            + "4. Run a third shell command that reads the file, report its exact two lines, and stop.";

        await DriveAndRecordAsync(
            "codex-one-shot-auth-403-recovery",
            prompt,
            forceCapiForbiddenOnce: ForcedCapiForbiddenOperation.Responses);
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

    [Fact]
    public async Task Codex_XhighReasoningAndCustomExec_PreservesNativeResponse_ForVerdict()
    {
        const string canary = "codex-reasoning-fidelity-canary-73159";
        var prompt =
            "Use xhigh reasoning. First call the collaboration.list_agents function tool once and inspect its result. "
            + "After that, use your code-execution tool (run actual code, do not compute by hand). "
            + "Compute 37 * 41, append the exact suffix "
            + $"\"-{canary}\", report the resulting string verbatim, and then STOP.";

        await DriveAndRecordAsync(
            "codex-xhigh-reasoning-fidelity",
            prompt,
            modelReasoningEffort: "xhigh",
            modelReasoningSummary: "detailed");
    }

    [Fact]
    public async Task Codex_CommandAuthCatalog_CarriesContextBeyond272k_AndExecutesMultiToolTask()
    {
        await DriveLongContextCatalogCaseAsync(
            caseId: "codex-command-auth-long-context",
            canary: "codex-long-context-canary-1050000",
            forceModelsFailure: false);
    }

    [Fact]
    public async Task Codex_CommandAuthCatalog_RecoversFromModelsFailure_AfterSafeBaselineRun()
    {
        await DriveLongContextCatalogCaseAsync(
            caseId: "codex-command-auth-models-failure-fallback",
            canary: "codex-models-fallback-canary-372000",
            forceModelsFailure: true);

        await DriveLongContextCatalogCaseAsync(
            caseId: "codex-command-auth-models-recovery",
            canary: "codex-models-recovery-canary-1050000",
            forceModelsFailure: false);
    }

    /// <summary>
    /// The client's exact tag does not exist upstream — the real skew, since OpenAI
    /// ships Codex builds before tagging the release. A confirmed 404 must serve the
    /// bridge's compile-time bundled snapshot (still Copilot-uplifted) instead of the
    /// metadata error that previously repeated every few minutes, and the client must
    /// go on to execute a real multi-step tool task on it.
    /// </summary>
    [Fact]
    public async Task Codex_AbsentUpstreamTag_ServesBundledCatalog_AndExecutesMultiToolTask()
    {
        const string canary = "codex-bundled-fallback-canary-1050000";
        using var cache = ClientBehaviorSupport.NewWorkDir("codex-bundled-fallback-cache");
        var prompt =
            "Perform these steps with separate shell tool calls, in order. Do not fabricate output:\n" +
            "1. Run `echo first-bundled-line > codex_bundled_probe.txt`.\n" +
            $"2. Run `echo {canary} >> codex_bundled_probe.txt`.\n" +
            "3. Run `cat codex_bundled_probe.txt`, report its exact second line, and stop.";

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            // A private empty cache dir is essential: a validated entry from any
            // earlier run legitimately outranks the bundled snapshot, so sharing
            // the default cache would satisfy the request from disk and never
            // reach the confirmed-absence branch this case exists to exercise.
            CodexCatalogCacheDirectory: cache.Path,
            ForceCodexCatalogSourceAbsent: true));
        using var work = ClientBehaviorSupport.NewWorkDir("codex-bundled-fallback");
        using var codexHome = ClientBehaviorSupport.NewWorkDir("codex-bundled-fallback-home");
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(8),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: ClientBehaviorSupport.CodexVersion));

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: "codex-bundled-catalog-fallback",
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
                Prompt: $"forced_source_absent=true, canary={canary}",
                DispatchThreadId: result.ThreadId),
            result.Stdout,
            result.Stderr,
            ClientBehaviorSupport.Stamp(),
            out _,
            out _);

        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine($"dispatch thread={result.ThreadId}, turn status={result.TurnStatus}");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    [Fact]
    public async Task Codex_ExactCatalog_OnlineThenOfflineRestart_UsesPersistentStaleCache()    {
        using var cache = ClientBehaviorSupport.NewWorkDir("codex-source-cache-persistent");
        using var onlineHome = ClientBehaviorSupport.NewWorkDir("codex-source-cache-online-home");
        using var offlineHome = ClientBehaviorSupport.NewWorkDir("codex-source-cache-offline-home");

        await DrivePersistentCatalogRunAsync(
            "codex-exact-catalog-online",
            "codex-exact-online-canary-1050000",
            cache.Path,
            onlineHome.Path,
            forceSourceFailure: false);

        var record = Assert.Single(Directory.EnumerateFiles(cache.Path, "catalog-*.cache"));
        AgeDiskRecordBeyondSourceTtl(record);

        await DrivePersistentCatalogRunAsync(
            "codex-exact-catalog-offline-restart",
            "codex-exact-offline-canary-1050000",
            cache.Path,
            offlineHome.Path,
            forceSourceFailure: true);
    }

    /// <summary>
    /// Native Codex keepalive behavior: the deterministic Responses upstream emits
    /// <c>response.created</c>, stays byte-silent for longer than this run's isolated
    /// Codex parsed-event watchdog, then resumes with a custom <c>exec</c> call. The
    /// exec performs two nested shell operations and the upstream returns final text
    /// only after Codex echoes the actual tool output on the next request.
    /// </summary>
    [Fact]
    public async Task Codex_SilentResponsesTurn_SurvivesViaSharedKeepaliveDeadline_ForVerdict()
    {
        const string caseId = "codex-native-keepalive-survives-silence";
        const string canary = "codex-keepalive-canary-86317";
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var codexHome = ClientBehaviorSupport.NewWorkDir(caseId + "-home");

        // 7s exceeds Codex's isolated 3s parsed-event watchdog. The bridge pings at
        // 1s and retains authority with a 20s upstream-idle deadline.
        var silence = TimeSpan.FromSeconds(7);
        await using var upstream = SilentResponsesUpstreamServer.Start(work.Path, canary, silence);
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.PassthroughTestUpstream,
            TestUpstreamBaseUrl: upstream.BaseUrl,
            StreamIdleTimeoutSeconds: 20,
            KeepAliveIntervalSeconds: 1));

        var prompt =
            "Execute the requested multi-step tool task: write the supplied canary to a file, "
            + "read the file back with a separate tool operation, then report the exact canary and stop.";
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(3),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: ClientBehaviorSupport.CodexVersion,
            StreamIdleTimeoutMs: 3_000));

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "codex",
                Route: "/codex",
                Model: ClientBehaviorSupport.LatestGpt,
                Scenario: ServeScenario.PassthroughTestUpstream,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: result.DispatchLogPath,
                DispatchSinceUnix: result.StartedUnixSeconds,
                DispatchUntilUnix: result.EndedUnixSeconds,
                Prompt: prompt,
                DispatchThreadId: result.ThreadId),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _);

        _output.WriteLine(
            $"silent responses upstream={upstream.BaseUrl} silence={silence} "
            + $"sampling={upstream.SamplingRequests} tool-output={upstream.ToolOutputRequests}");
        _output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        _output.WriteLine(
            $"dispatch log={result.DispatchLogPath} thread={result.ThreadId} "
            + $"window=[{result.StartedUnixSeconds},{result.EndedUnixSeconds}]");
        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine(
            "[verdict] require injected:true ping events across the 7s silence, a native "
            + "custom_tool_call plus matching output on the next request, the exact canary, "
            + "and zero idle-timeout/router-fatal rows in Codex's own SQLite log.");

        ClientBehaviorSupport.AssertHarnessProducedEvidence(
            result.ExitCode, bridge.TraceDir, manifestPath);
    }

    /// <summary>
    /// Real Codex retry scope: the bridge ends the first sampling stream after one
    /// silent second and emits a retryable <c>response.failed</c>. With isolated
    /// provider values (request retries 1, stream retries 2), Codex must begin a new
    /// sampling attempt, execute the returned custom exec tool, echo its output, and
    /// finish. Client-owned SQLite is the semantic verdict.
    /// </summary>
    [Fact]
    public async Task Codex_RetryableBridgeStreamTimeout_UsesConfiguredStreamRetries_ForVerdict()
    {
        const string caseId = "codex-native-retryable-stream-timeout";
        const string canary = "codex-stream-retry-canary-47291";
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var codexHome = ClientBehaviorSupport.NewWorkDir(caseId + "-home");
        var observedCodexConfig = Path.Combine(codexHome.Path, "config.toml");
        File.WriteAllText(observedCodexConfig, """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "copilot-bridge"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            stream_idle_timeout_ms = 5000
            request_max_retries = 1
            stream_max_retries = 2
            """);
        await using var upstream = SilentResponsesUpstreamServer.Start(
            work.Path,
            canary,
            silence: TimeSpan.Zero,
            failFirstSampling: true,
            failFirstHttpRequest: true,
            firstFailureSilence: TimeSpan.FromSeconds(10));
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.PassthroughTestUpstream,
            TestUpstreamBaseUrl: upstream.BaseUrl,
            StreamIdleTimeoutSeconds: 1,
            KeepAliveIntervalSeconds: 0,
            ClaudeSettingsPath: Path.Combine(work.Path, "missing-claude-settings.json"),
            CodexConfigPath: observedCodexConfig,
            ClaudeVersion: "2.1.221"));

        var prompt =
            "Execute the requested multi-step tool task: write the supplied canary to a file, "
            + "read it back with a separate nested operation, report the exact canary, and stop.";
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(3),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: ClientBehaviorSupport.CodexVersion,
            StreamIdleTimeoutMs: 5_000,
            RequestMaxRetries: 1,
            StreamMaxRetries: 2));

        // Gate 1: this run must actually have crossed the retry boundary before its
        // later custom-tool loop; otherwise clean client logs would be inconclusive.
        Assert.Equal(1, upstream.HttpFailedRequests);
        Assert.Equal(1, upstream.TimedOutSamplingRequests);
        Assert.True(upstream.SamplingRequests >= 3,
            $"Codex did not cross both retry boundaries (sampling={upstream.SamplingRequests}).");
        Assert.True(upstream.ToolOutputRequests >= 1,
            "Codex never echoed the custom exec output to the deterministic upstream.");

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "codex",
                Route: "/codex",
                Model: ClientBehaviorSupport.LatestGpt,
                Scenario: ServeScenario.PassthroughTestUpstream,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: result.DispatchLogPath,
                DispatchSinceUnix: result.StartedUnixSeconds,
                DispatchUntilUnix: result.EndedUnixSeconds,
                Prompt: prompt,
                DispatchThreadId: result.ThreadId),
            result.Stdout,
            result.Stderr,
            ClientBehaviorSupport.Stamp(),
            out _,
            out _);

        _output.WriteLine(
            $"retry upstream={upstream.BaseUrl} http-failed={upstream.HttpFailedRequests} "
            + $"timed-out={upstream.TimedOutSamplingRequests} "
            + $"sampling={upstream.SamplingRequests} tool-output={upstream.ToolOutputRequests}");
        _output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        _output.WriteLine(bridge.StderrAll);
        _output.WriteLine(
            $"dispatch log={result.DispatchLogPath} thread={result.ThreadId} "
            + $"window=[{result.StartedUnixSeconds},{result.EndedUnixSeconds}]");
        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine(
            "[verdict] require one bridge stream_idle failure followed by a new sampling request, "
            + "custom_tool_call + matching output, final canary, no abort, and zero router fatals "
            + "in Codex's own SQLite window (request retries=1, stream retries=2).");

        ClientBehaviorSupport.AssertHarnessProducedEvidence(
            result.ExitCode, bridge.TraceDir, manifestPath);
    }

    /// <summary>
    /// Shared driver: boot a real bridge subprocess (passthrough), run real Codex
    /// app-server on the prompt at the latest gpt id, and write its isolated SQLite
    /// dispatch evidence to the run manifest. The xUnit layer asserts ONLY the harness
    /// contract; the verdict skill reads the per-run database and exact thread id.
    /// </summary>
    private async Task DriveAndRecordAsync(
        string caseId,
        string prompt,
        string? modelReasoningEffort = null,
        string? modelReasoningSummary = null,
        string expectedCodexVersion = ClientBehaviorSupport.CodexVersion,
        ForcedCapiForbiddenOperation? forceCapiForbiddenOnce = null)
    {
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            ForceCapiForbiddenOnce: forceCapiForbiddenOnce));
        _output.WriteLine($"bridge up at {bridge.BaseUrl} (trace: {bridge.TraceDir})");

        // Disposable work dir so codex's file-writing tools mutate a throwaway dir, never
        // the test runner's checkout.
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var codexHome = ClientBehaviorSupport.NewWorkDir(caseId + "-home");

        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(6),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: expectedCodexVersion,
            ModelReasoningEffort: modelReasoningEffort,
            ModelReasoningSummary: modelReasoningSummary));

        _output.WriteLine($"codex.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"dispatch log={result.DispatchLogPath} thread={result.ThreadId} window=[{result.StartedUnixSeconds},{result.EndedUnixSeconds}]");

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
                Prompt: prompt,
                DispatchThreadId: result.ThreadId,
                ForcedCapiForbiddenOperation: forceCapiForbiddenOnce),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(),
            out _, out _,
            bridgeLog: forceCapiForbiddenOnce is null ? null : bridge.StderrAll);

        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine("[verdict] run `/real-client-verify`; it reads the isolated SQLite log for this exact thread.");

        if (forceCapiForbiddenOnce is not null)
        {
            Assert.Equal(1, CountOccurrences(
                bridge.StderrAll,
                "TEST ONLY: injected one-shot CAPI 403"));
            Assert.Equal(1, CountOccurrences(
                bridge.StderrAll,
                "rejected Copilot bearer status=403"));
            Assert.Equal(1, CountOccurrences(
                bridge.StderrAll,
                "Copilot bearer refresh trigger=copilot_403 outcome=success"));
            Assert.Equal(1, CountOccurrences(
                bridge.StderrAll,
                "authentication replay outcome=success"));
            Assert.DoesNotContain(
                "classification=policy_or_entitlement_after_auth_replay",
                bridge.StderrAll,
                StringComparison.Ordinal);
        }

        // Harness contract only. The skill owns the semantic SQLite verdict.
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }

    private async Task DriveLongContextCatalogCaseAsync(
        string caseId,
        string canary,
        bool forceModelsFailure)
    {
        const int paddingTokens = 285_000;
        var prompt =
            "Perform these steps with separate shell tool calls, in order. Do not fabricate output:\n" +
            "1. Run `echo first-long-context-line > codex_long_context_probe.txt`.\n" +
            $"2. Run `echo {canary} >> codex_long_context_probe.txt`.\n" +
            "3. Run `cat codex_long_context_probe.txt`, report its exact second line, and stop.\n" +
            "The following padding is inert reference text. Ignore it except for carrying it in active context:\n" +
            string.Concat(Enumerable.Repeat("a ", paddingTokens));

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            ForceModelsFailure: forceModelsFailure));
        _output.WriteLine($"bridge up at {bridge.BaseUrl} (trace: {bridge.TraceDir})");
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var codexHome = ClientBehaviorSupport.NewWorkDir(caseId + "-home");

        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(12),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: ClientBehaviorSupport.CodexVersion));

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
                Prompt: $"[long prompt omitted from manifest: {paddingTokens} x 'a ' tokens] " +
                    $"models_failure={forceModelsFailure}, canary={canary}",
                DispatchThreadId: result.ThreadId),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(), out _, out _);

        _output.WriteLine($"[manifest] {manifestPath}");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    private async Task DrivePersistentCatalogRunAsync(
        string caseId,
        string canary,
        string cacheDirectory,
        string codexHome,
        bool forceSourceFailure)
    {
        const int paddingTokens = 285_000;
        var prompt =
            "Perform these steps with separate shell tool calls, in order. Do not fabricate output:\n" +
            "1. Run `echo first-catalog-cache-line > codex_catalog_cache_probe.txt`.\n" +
            $"2. Run `echo {canary} >> codex_catalog_cache_probe.txt`.\n" +
            "3. Run `cat codex_catalog_cache_probe.txt`, report its exact second line, and stop.\n" +
            "The following padding is inert reference text. Ignore it except for carrying it in active context:\n" +
            string.Concat(Enumerable.Repeat("a ", paddingTokens));

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            CodexCatalogCacheDirectory: cacheDirectory,
            ForceCodexCatalogSourceFailure: forceSourceFailure));
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            Timeout: TimeSpan.FromMinutes(12),
            CodexHome: codexHome,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: ClientBehaviorSupport.CodexVersion));

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
                Prompt: $"[long prompt omitted: {paddingTokens} x 'a '] " +
                    $"forced_source_failure={forceSourceFailure}, canary={canary}",
                DispatchThreadId: result.ThreadId),
            result.Stdout,
            result.Stderr,
            ClientBehaviorSupport.Stamp(),
            out _,
            out _);

        _output.WriteLine($"[manifest] {manifestPath}");
        _output.WriteLine($"app-server user-agent={result.UserAgent}");
        _output.WriteLine($"dispatch thread={result.ThreadId}, turn status={result.TurnStatus}");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }

    private static void AgeDiskRecordBeyondSourceTtl(string path)
    {
        var record = File.ReadAllBytes(path);
        Assert.True(record.AsSpan(0, 8).SequenceEqual("CBCAT001"u8));
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8, 4));
        var sourceLength = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(12, 4));
        var metadata = JsonNode.Parse(record.AsSpan(16, metadataLength))!.AsObject();
        var stale = DateTimeOffset.UtcNow.AddDays(-30).ToString("O");
        metadata["fetched_at_utc"] = stale;
        metadata["validated_at_utc"] = stale;
        var metadataBytes = System.Text.Encoding.UTF8.GetBytes(metadata.ToJsonString());
        var rewritten = new byte[16 + metadataBytes.Length + sourceLength];
        "CBCAT001"u8.CopyTo(rewritten);
        BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(8, 4), metadataBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(rewritten.AsSpan(12, 4), sourceLength);
        metadataBytes.CopyTo(rewritten.AsSpan(16));
        record.AsSpan(16 + metadataLength, sourceLength)
            .CopyTo(rewritten.AsSpan(16 + metadataBytes.Length));
        File.WriteAllBytes(path, rewritten);
    }

}
