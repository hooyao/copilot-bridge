using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Drives both native Codex image-delivery paths through the stock Astra route.
/// xUnit proves harness health; real-client-verify owns the visual and dispatch
/// verdict from the saved image, client output, bridge trace, and isolated SQLite.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ClientBehavior")]
public class CodexVisionBehaviorTests(ITestOutputHelper output)
{
    [Fact]
    public Task Codex_Gpt56RoutedToAstra_AttachedImage_ProducesVisionEvidenceForVerdict() =>
        DriveVisionAsync("codex-astra-attached-image", attachImage: true);

    [Fact]
    public Task Codex_Gpt56RoutedToAstra_ViewImageTool_ProducesVisionEvidenceForVerdict() =>
        DriveVisionAsync("codex-astra-view-image-tool", attachImage: false);

    private async Task DriveVisionAsync(string caseId, bool attachImage)
    {
        var credentialSource = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_PLUGIN_CREDENTIAL_SOURCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(credentialSource))
            throw new InvalidOperationException(
                "Set COPILOT_BRIDGE_TEST_PLUGIN_CREDENTIAL_SOURCE_DIRECTORY to a scratch "
                + "directory containing a freshly authorized version-3 credential.");

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Gpt56ToAstra,
            CredentialSourceDirectory: credentialSource,
            CredentialStagingMode: CredentialStagingMode.CopilotPluginVersionThree));
        using var work = ClientBehaviorSupport.NewWorkDir(caseId);
        using var codexHome = ClientBehaviorSupport.NewWorkDir(caseId + "-home");
        var challenge = CodexVisionChallenge.Create();
        var imagePath = Path.Combine(work.Path, "image.png");
        var answerPath = Path.Combine(work.Path, "observed.txt");
        await File.WriteAllBytesAsync(imagePath, challenge.Png);

        var prompt = (attachImage
                ? "Inspect the image attached to this message. "
                : $"Use the view_image tool to open the local image at {imagePath}. ")
            + "Read the six-digit code at the top and identify the colors of the four large "
            + "rectangles by their positions. Determine the contents visually; do not use "
            + "scripts, OCR services, or raw file-byte inspection to derive them. "
            + "Then call the native apply_patch tool to create observed.txt with exactly five "
            + "key=value lines, using these keys in order: code, top-left, top-right, "
            + "bottom-left, bottom-right. Use lowercase English color names. "
            + "Call apply_patch directly, not through a shell command. "
            + "Use a separate shell command tool call to read observed.txt. "
            + "Repeat its exact five lines in your final answer, then stop.";

        output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: ClientBehaviorSupport.LatestGpt,
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: ClientBehaviorSupport.CodexVersion,
            Timeout: TimeSpan.FromMinutes(6),
            ModelReasoningEffort: "none",
            LocalImagePaths: attachImage ? [imagePath] : null));

        // Persist the answer only after Codex has exited, so it cannot discover
        // the expected reading in a sidecar while doing the visual task.
        var stamp = ClientBehaviorSupport.Stamp();
        var evidenceDir = Path.Combine(ServeProcess.EvidenceRoot(), "vision", $"{caseId}-{stamp}");
        Directory.CreateDirectory(evidenceDir);
        var savedImagePath = Path.Combine(evidenceDir, "image.png");
        await File.WriteAllBytesAsync(savedImagePath, challenge.Png);
        var observedAnswer = File.Exists(answerPath) ? await File.ReadAllTextAsync(answerPath) : null;
        string? savedAnswerPath = null;
        if (observedAnswer is not null)
        {
            savedAnswerPath = Path.Combine(evidenceDir, "observed.txt");
            await File.WriteAllTextAsync(savedAnswerPath, observedAnswer);
        }
        var visionEvidencePath = Path.Combine(evidenceDir, "evidence.json");
        var visualEvidence = new JsonObject
        {
            ["imageDelivery"] = attachImage ? "localImage" : "view_image",
            ["imagePath"] = savedImagePath,
            ["sourceImagePath"] = imagePath,
            ["imageSha256"] = Convert.ToHexStringLower(SHA256.HashData(challenge.Png)),
            ["expectedAnswer"] = challenge.ExpectedAnswer,
            ["observedAnswer"] = observedAnswer,
            ["observedAnswerPath"] = savedAnswerPath,
            ["clientTurnStatus"] = result.TurnStatus,
        };
        await File.WriteAllTextAsync(visionEvidencePath,
            visualEvidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        var manifestPath = BehaviorRun.Write(
            new BehaviorManifest(
                CaseId: caseId,
                Client: "codex",
                Route: "/codex",
                Model: ClientBehaviorSupport.LatestGpt,
                Scenario: ServeScenario.Gpt56ToAstra,
                ClientExitCode: result.ExitCode,
                DurationSeconds: result.Duration.TotalSeconds,
                TraceDir: bridge.TraceDir,
                DispatchLogPath: result.DispatchLogPath,
                DispatchSinceUnix: result.StartedUnixSeconds,
                DispatchUntilUnix: result.EndedUnixSeconds,
                Prompt: prompt,
                DispatchThreadId: result.ThreadId,
                ResolvedModel: ClientBehaviorSupport.LatestGptBackend,
                VisionEvidencePath: visionEvidencePath),
            result.Stdout, result.Stderr, stamp, out _, out _);

        output.WriteLine($"codex.exe exit={result.ExitCode} duration={result.Duration}");
        output.WriteLine($"[manifest] {manifestPath}");
        output.WriteLine($"[vision evidence] {visionEvidencePath}");
        output.WriteLine(
            "[verdict] Require the intended image path, correct code and all four color positions "
            + "in the saved output and final answer, separate write/read tool round-trips, "
            + "Astra/low upstream with the vision header, no abort, and a nonempty client "
            + "SQLite window with zero router/dispatch fatals. A green actuator is not a visual PASS.");

        Assert.True(File.Exists(visionEvidencePath));
        Assert.True(File.Exists(savedImagePath));
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifestPath);
    }
}
