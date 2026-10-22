using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Thin actuator for the real Astra plaintext task/message/follow-up lifecycle.
/// The semantic verdict comes from the saved client log, trace, and independently
/// generated input/output artifacts, not the HTTP status or this test's exit code.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ClientBehavior")]
public sealed class CodexCollaborationBehaviorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Codex_DecryptionDetailAfterEmptyFailure_ReachesClientAndToolRecovery_ForVerdict()
    {
        const string caseId = "codex-decryption-failure-detail";
        const string canary = "DECRYPTION_DIAGNOSTIC_RECOVERY_48379";
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var home = ClientBehaviorSupport.NewWorkDir(caseId + "-home");
        await using var upstream = SilentResponsesUpstreamServer.Start(
            work.Path, canary, TimeSpan.Zero, decryptionFailureOnce: true);
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.PassthroughTestUpstream, TestUpstreamBaseUrl: upstream.BaseUrl));
        const string prompt = "Execute the requested tool chain: write the supplied diagnostic marker to a file, read it back in another tool operation, and return the observed marker.";
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl, Prompt: prompt, Model: "gpt-6-astra",
            CodexHome: home.Path, WorkingDirectory: work.Path,
            ExpectedCodexVersion: Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_COLLAB_CODEX_VERSION") ?? "0.163.0-alpha.11",
            Timeout: TimeSpan.FromMinutes(3), ModelReasoningEffort: "low",
            RequestMaxRetries: 0, StreamMaxRetries: 2));
        var manifest = BehaviorRun.Write(new BehaviorManifest(
            CaseId: caseId, Client: "codex", Route: "/codex", Model: "gpt-6-astra",
            Scenario: ServeScenario.PassthroughTestUpstream, ClientExitCode: result.ExitCode,
            DurationSeconds: result.Duration.TotalSeconds, TraceDir: bridge.TraceDir,
            DispatchLogPath: result.DispatchLogPath, DispatchSinceUnix: result.StartedUnixSeconds,
            DispatchUntilUnix: result.EndedUnixSeconds, Prompt: prompt,
            DispatchThreadId: result.ThreadId), result.Stdout, result.Stderr,
            ClientBehaviorSupport.Stamp(), out _, out _);
        output.WriteLine($"[manifest] {manifest}");
        output.WriteLine($"[upstream] injected decryption failures={upstream.DecryptionFailures}; tool-output requests={upstream.ToolOutputRequests}");
        output.WriteLine("[verdict] Require the exact decryption diagnostic/code in native failure and client retry evidence, then custom exec/output recovery and the observed marker with zero router fatals.");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifest);
    }

    [Fact]
    public async Task Codex_AstraPlaintextCollaboration_ExecutesChildToolsAndFollowup_ForVerdict()
    {
        var credentialSource = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_CUSTOM_CREDENTIAL_SOURCE_DIRECTORY")
            ?? throw new InvalidOperationException("Set COPILOT_BRIDGE_TEST_CUSTOM_CREDENTIAL_SOURCE_DIRECTORY to an existing valid v4 success-test credential source.");
        var expectedVersion = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_COLLAB_CODEX_VERSION")
            ?? "0.163.0-alpha.11";
        const string caseId = "codex-astra-plaintext-collaboration";
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var home = ClientBehaviorSupport.NewWorkDir(caseId + "-home");
        var numbers = Enumerable.Range(0, 7).Select(_ => Random.Shared.Next(11, 90)).ToArray();
        var input = new JsonArray(numbers.Select(number => (JsonNode?)JsonValue.Create(number)).ToArray());
        File.WriteAllText(Path.Combine(work.Path, "numbers.json"), input.ToJsonString());
        var stamp = ClientBehaviorSupport.Stamp();
        var evidenceDir = Path.Combine(ServeProcess.EvidenceRoot(), caseId + "-" + stamp);
        Directory.CreateDirectory(evidenceDir);
        File.WriteAllText(Path.Combine(evidenceDir, "challenge.json"), new JsonObject
        {
            ["numbers"] = input.DeepClone(),
            ["expectedValue"] = numbers.Sum(number => number * number + number),
        }.ToJsonString());

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            CredentialSourceDirectory: credentialSource,
            CredentialStagingMode: CredentialStagingMode.CustomOAuthVersionFour,
            WorkingDirectory: work.Path,
            UseCustomOAuthApp: true,
            PlaintextCollaboration: true));
        const string prompt = """
            Exercise a real native collaboration lifecycle. Use collaboration.spawn_agent once,
            task_name worker, model gpt-6-astra, reasoning_effort low, fork_turns none.
            Give the child this complete task: Read numbers.json using a shell tool. Compute the
            sum of n*n+n for all numbers. Use a second tool operation to generate a fresh GUID
            nonce and write result.txt containing the numeric sum on line 1 and the nonce on
            line 2. Read result.txt back with a separate tool operation. Use
            collaboration.send_message to send both observed lines to /root, then finish with
            those observed lines. Work only in the current directory; do not spawn more agents.

            Wait for that child to complete. Then use collaboration.followup_task on the same
            worker with this task: Read result.txt, calculate its SHA-256 with a shell tool,
            write the hash to followup.txt, read that file back using a separate tool operation,
            and report the observed hash. Do not create another child.

            Wait again. Independently read numbers.json, result.txt, and followup.txt with tools,
            verify the arithmetic and file hash, and finish with the three observed values:
            numeric sum, child-generated GUID, and SHA-256. Do not guess any value or bypass
            the child. Do not edit configuration or files outside the current directory.
            """;
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: "gpt-6-astra",
            CodexHome: home.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: expectedVersion,
            Timeout: TimeSpan.FromMinutes(8),
            ModelReasoningEffort: "low",
            StreamMaxRetries: 1,
            RequestMaxRetries: 0,
            EnableMultiAgent: true));
        foreach (var name in new[] { "result.txt", "followup.txt" })
        {
            var path = Path.Combine(work.Path, name);
            if (File.Exists(path)) File.Copy(path, Path.Combine(evidenceDir, name));
        }
        var manifestPath = BehaviorRun.Write(new BehaviorManifest(
            CaseId: caseId,
            Client: "codex",
            Route: "/codex",
            Model: "gpt-6-astra",
            Scenario: ServeScenario.Passthrough,
            ClientExitCode: result.ExitCode,
            DurationSeconds: result.Duration.TotalSeconds,
            TraceDir: bridge.TraceDir,
            DispatchLogPath: result.DispatchLogPath,
            DispatchSinceUnix: result.StartedUnixSeconds,
            DispatchUntilUnix: result.EndedUnixSeconds,
            Prompt: prompt,
            DispatchThreadId: result.ThreadId), result.Stdout, result.Stderr, stamp, out _, out _);
        output.WriteLine($"[manifest] {manifestPath}");
        output.WriteLine($"[challenge] {evidenceDir}");
        output.WriteLine($"[dispatch] {result.DispatchLogPath} [{result.StartedUnixSeconds},{result.EndedUnixSeconds}] thread={result.ThreadId}");
        output.WriteLine("[verdict] Require actual child tool/output rounds, plaintext NEW_TASK/message/follow-up delivery, exact independently checked files, and zero client dispatch/router fatals.");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }
}
