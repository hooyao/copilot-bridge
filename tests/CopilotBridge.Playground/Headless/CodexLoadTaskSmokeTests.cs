using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Codex <b>load-task</b> smoke for the copilot-model-sync skill: drives the real
/// <c>codex.exe</c> through the bridge on a genuinely multi-step tool task (not a
/// plain one-word turn) so the FULL Codex client wire shape — including the
/// harness tool-registration preamble (<c>input[0]</c> <c>additional_tools</c>),
/// multi-call <c>function_call</c>/<c>function_call_output</c> round-trips, and
/// reasoning echoes — actually crosses the bridge for the model under test.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> A plain "reply pong" turn (see
/// <see cref="CodexE2EHeadlessTests.Codex_PlainTurn_CompletesThroughBridge"/>) does
/// NOT make Codex emit its full tool suite, so it cannot catch a new inbound wire
/// shape. That is exactly how the gpt-5.6 <c>additional_tools</c> item shipped a
/// 400: the model was added to the catalog and the plain/tool turns passed, but no
/// load task ever exercised the harness preamble the newer client sends. This
/// smoke closes that gap — the skill requires it for every added/reconciled Codex
/// model.</para>
/// <para>The model is taken from <c>CODEX_SMOKE_MODEL</c> (default
/// <c>gpt-5.3-codex</c>) so a model-sync run can target the id it just added,
/// e.g. <c>CODEX_SMOKE_MODEL=gpt-5.6-sol dotnet test … --filter CodexLoadTaskSmoke</c>.
/// Integration-tagged: needs live Copilot + <c>codex.exe</c>; skipped in CI. Uses
/// the ephemeral-port <see cref="BridgeFixture"/> (never 8765) and injects the
/// provider via <c>-c</c> overrides so <c>~/.codex/config.toml</c> is untouched.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class CodexLoadTaskSmokeTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexLoadTaskSmokeTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    private static string Model =>
        Environment.GetEnvironmentVariable("CODEX_SMOKE_MODEL") is { Length: > 0 } m
            ? m
            : "gpt-5.3-codex";

    [Fact]
    public async Task CodexLoadTaskSmoke_MultiStepToolChain_CompletesThroughBridge()
    {
        var model = Model;
        const string canary = "codex-loadtask-canary-51742";
        // A genuinely multi-step task: two shell writes then a read-back, forcing
        // several tool calls in sequence with tool_result round-trips — the shape
        // most likely to surface a Codex-client wire regression (a new input-item
        // type, a tool-arg reassembly bug, a reasoning-echo mishandling) on a
        // freshly added model.
        var prompt =
            "Do these steps in order, actually running the shell commands (do not fabricate output). "
            + "As soon as step 3 is done, give your final answer and stop:\n"
            + "1. Run `echo first-line > codex_probe.txt`.\n"
            + $"2. Run `echo {canary} >> codex_probe.txt`.\n"
            + "3. Run `cat codex_probe.txt` and tell me the exact second line, verbatim.";

        var result = await CodexProcess.RunAsync(new CodexInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: prompt,
            Model: model,
            Timeout: TimeSpan.FromMinutes(6)));

        _output.WriteLine($"[{model}] codex.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"--- stdout (first 3000) ---\n{Trunc(result.Stdout, 3000)}");
        if (result.ExitCode != 0)
            _output.WriteLine($"--- stderr (first 1500) ---\n{Trunc(result.Stderr, 1500)}");

        // Contract: the full load task closes the agentic loop for THIS model —
        // codex.exe exits clean and the canary the model could only obtain by
        // actually running the tools reaches its output.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(canary, result.Stdout, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"[audit] four-file IO traces under {_bridge.LogDirectory}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + $"…[+{s.Length - n}]";
}
