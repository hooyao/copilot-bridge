using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>Drives the real Codex client through its imagegen CLI and the bridge.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ClientBehavior")]
public sealed class CodexImagesBehaviorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Codex_native_image_generation_ProducesClientEvidence_ForVerdict()
    {
        using var credential = await ImagesSdkTests.StageDirectCredentialAsync();
        using var work = ClientBehaviorSupport.NewWorkDir("codex-native-images");
        using var codexHome = ClientBehaviorSupport.NewWorkDir("codex-native-images-home");
        var expectedVersion = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_CODEX_VERSION")
            ?? ClientBehaviorSupport.CodexVersion;
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            CredentialSourceDirectory: credential.Path,
            CredentialStagingMode: CredentialStagingMode.PurposeBuiltDirectVersionTwo,
            BridgeBinaryDirectory: Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_NATIVE_BINARY_DIRECTORY")));
        const string prompt = "Use your native built-in image generation tool to create an image of a red circle on a white background. "
            + "Then use that same native tool to edit the generated image, changing the circle to blue. "
            + "Do not use the CLI/API fallback, scripts, or shell commands to generate or edit either image. "
            + "Save both results to the workspace and report what happened.";
        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: "gpt-5.6-sol",
            Timeout: TimeSpan.FromMinutes(8),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: expectedVersion,
            ModelReasoningEffort: "high",
            RequiredListedModel: "gpt-5.6-sol",
            UseSyntheticChatGptAuth: true));
        var evidenceDirectory = Path.Combine(bridge.TraceDir, "native-images");
        Directory.CreateDirectory(evidenceDirectory);
        foreach (var file in Directory.EnumerateFiles(work.Path, "*", SearchOption.TopDirectoryOnly))
            File.Copy(file, Path.Combine(evidenceDirectory, Path.GetFileName(file)));
        var manifest = BehaviorRun.Write(new BehaviorManifest(
            CaseId: "codex-images-native",
            Client: "codex",
            Route: "/codex",
            Model: "gpt-5.6-sol",
            Scenario: ServeScenario.Passthrough,
            ClientExitCode: result.ExitCode,
            DurationSeconds: result.Duration.TotalSeconds,
            TraceDir: bridge.TraceDir,
            DispatchLogPath: result.DispatchLogPath,
            DispatchSinceUnix: result.StartedUnixSeconds,
            DispatchUntilUnix: result.EndedUnixSeconds,
            Prompt: prompt,
            DispatchThreadId: result.ThreadId,
            ResolvedModel: "gpt-5.6-sol"),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(), out _, out _);
        output.WriteLine($"[manifest] {manifest}");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifest);
    }

    [Fact]
    public async Task Codex_uses_imagegen_cli_to_generate_and_edit_ForVerdict()
    {
        using var credential = await ImagesSdkTests.StageDirectCredentialAsync();
        using var work = ClientBehaviorSupport.NewWorkDir("codex-images");
        using var codexHome = ClientBehaviorSupport.NewWorkDir("codex-images-home");
        var python = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_OPENAI_PYTHON")
            ?? throw new InvalidOperationException("Set COPILOT_BRIDGE_TEST_OPENAI_PYTHON to a Python with the OpenAI SDK and Pillow.");
        var script = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_IMAGEGEN_SCRIPT")
            ?? throw new InvalidOperationException("Set COPILOT_BRIDGE_TEST_IMAGEGEN_SCRIPT to Codex's imagegen.py.");
        var pythonPath = Environment.GetEnvironmentVariable("PYTHONPATH")
            ?? throw new InvalidOperationException("Set PYTHONPATH to the OpenAI SDK directory.");
        var expectedVersion = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_CODEX_VERSION")
            ?? ClientBehaviorSupport.CodexVersion;
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            CredentialSourceDirectory: credential.Path,
            CredentialStagingMode: CredentialStagingMode.PurposeBuiltDirectVersionTwo));

        var environment = $"$env:OPENAI_BASE_URL='{bridge.BaseUrl}/v1'; $env:OPENAI_API_KEY='bridge'; $env:PYTHONPATH='{pythonPath}';";
        var prompt =
            "Use separate real shell tool calls in this order; do not fabricate results. "
            + "Run these exact PowerShell commands in your workspace:\n"
            + $"1. `{environment} & '{python}' '{script}' generate --model gpt-image-2.5-flare --prompt 'A simple red circle on a plain white background.' --size 1024x1024 --out generated.png --no-augment`\n"
            + $"2. `{environment} & '{python}' '{script}' edit --model gpt-image-2.5-flare --image generated.png --prompt 'Change the red circle to blue.' --size 1024x1024 --out edited.png --no-augment`\n"
            + $"3. `{environment} & '{python}' -c \"from PIL import Image; [Image.open(p).load() for p in ['generated.png','edited.png']]; print('CODEX_IMAGES_OK')\"`\n"
            + "Report the actual command output and stop.";

        var result = await CodexAppServerProcess.RunAsync(new CodexAppServerInvocation(
            BridgeBaseUrl: bridge.BaseUrl,
            Prompt: prompt,
            Model: "gpt-5.6-sol",
            Timeout: TimeSpan.FromMinutes(8),
            CodexHome: codexHome.Path,
            WorkingDirectory: work.Path,
            ExpectedCodexVersion: expectedVersion,
            ModelReasoningEffort: "high"));
        var evidenceDirectory = Path.Combine(bridge.TraceDir, "images");
        Directory.CreateDirectory(evidenceDirectory);
        foreach (var name in new[] { "generated.png", "edited.png" })
        {
            var file = Path.Combine(work.Path, name);
            if (File.Exists(file)) File.Copy(file, Path.Combine(evidenceDirectory, name));
        }
        var manifest = BehaviorRun.Write(new BehaviorManifest(
            CaseId: "codex-images-cli",
            Client: "codex",
            Route: "/codex",
            Model: "gpt-5.6-sol",
            Scenario: ServeScenario.Passthrough,
            ClientExitCode: result.ExitCode,
            DurationSeconds: result.Duration.TotalSeconds,
            TraceDir: bridge.TraceDir,
            DispatchLogPath: result.DispatchLogPath,
            DispatchSinceUnix: result.StartedUnixSeconds,
            DispatchUntilUnix: result.EndedUnixSeconds,
            Prompt: prompt,
            DispatchThreadId: result.ThreadId,
            ResolvedModel: "gpt-5.6-sol"),
            result.Stdout, result.Stderr, ClientBehaviorSupport.Stamp(), out _, out _);
        output.WriteLine($"[manifest] {manifest}");
        ClientBehaviorSupport.AssertHarnessProducedEvidence(result.ExitCode, bridge.TraceDir, manifest);
    }
}
