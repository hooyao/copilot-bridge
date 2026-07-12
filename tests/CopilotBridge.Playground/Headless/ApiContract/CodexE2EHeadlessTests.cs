using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// E1 (change 3, task 7.3) — the live end-to-end proof: the REAL
/// <c>codex.exe</c> driven through the bridge's <c>/codex/responses</c> endpoint
/// to Copilot's native <c>/responses</c>, asserting a full turn completes and
/// reaches Codex's stdout JSONL. This closes the loop between the offline
/// A-invariant fixtures and live behavior — the true end-to-end the research's
/// capture stub could not be. The bridge's four-file IO audit is saved (the
/// fixture enables tracing) so the actual client→IR→Copilot and Copilot→IR→client
/// bytes can be diffed post-hoc against the offline goldens.
/// </summary>
/// <remarks>
/// Uses an ephemeral non-8765 port (BridgeFixture) and injects the provider via
/// <c>codex exec -c</c> overrides — the user's <c>~/.codex/config.toml</c> is
/// never touched. Integration-tagged: skipped in CI (no Copilot creds / no
/// codex.exe).
/// </remarks>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CodexE2EHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public CodexE2EHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task Codex_PlainTurn_CompletesThroughBridge()
    {
        var result = await CodexProcess.RunAsync(new CodexInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: "Reply with exactly the word: pong",
            Timeout: TimeSpan.FromMinutes(3)));

        _output.WriteLine($"codex.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"--- stdout (first 2000) ---\n{Trunc(result.Stdout, 2000)}");
        if (result.ExitCode != 0)
            _output.WriteLine($"--- stderr (first 1500) ---\n{Trunc(result.Stderr, 1500)}");

        Assert.Equal(0, result.ExitCode);
        // The turn's JSONL stream must reach completion and carry the model's text.
        Assert.Contains("pong", result.Stdout, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"[audit] four-file IO traces under {_bridge.LogDirectory}");
    }

    [Fact]
    public async Task Codex_ToolTurn_CompletesThroughBridge()
    {
        // A prompt that nudges Codex to run a shell command (its default tool
        // loop). This exercises the function_call / function_call_output path
        // through T1–T4 and harvests a real tool-flow audit for the A5 fixture.
        var result = await CodexProcess.RunAsync(new CodexInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: "Run the shell command `echo codex-bridge-canary-9931` and tell me what it printed.",
            Timeout: TimeSpan.FromMinutes(4)));

        _output.WriteLine($"codex.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"--- stdout (first 3000) ---\n{Trunc(result.Stdout, 3000)}");
        if (result.ExitCode != 0)
            _output.WriteLine($"--- stderr (first 1500) ---\n{Trunc(result.Stderr, 1500)}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("codex-bridge-canary-9931", result.Stdout, StringComparison.OrdinalIgnoreCase);
        _output.WriteLine($"[audit] four-file IO traces under {_bridge.LogDirectory}");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + $"…[+{s.Length - n}]";
}
