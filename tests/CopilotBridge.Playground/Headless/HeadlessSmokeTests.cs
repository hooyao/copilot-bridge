using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Smoke tests for the headless harness: boot bridge in-process, drive
/// <c>claude.exe -p</c> against it, verify the bridge logs show what we
/// expected. This is the foundation for the (model × effort × stream × tools)
/// matrix; once the smoke case is solid we add cases.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class HeadlessSmokeTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public HeadlessSmokeTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Theory]
    [InlineData("claude-sonnet-4-6")]
    [InlineData("claude-sonnet-5")]   // 2026 reconciliation: end-to-end through Normalize→route→profile-adjust
    public async Task ClaudeP_MinimalPrompt_ReachesCopilotAnd2xx(string model)
    {
        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: "Reply with the single word: ok",
            Model: model,
            Effort: null,
            OutputFormat: "json",
            AllowedTools: ""));

        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine("=== stdout ===");
        _output.WriteLine(result.Stdout);
        if (!string.IsNullOrEmpty(result.Stderr))
        {
            _output.WriteLine("=== stderr ===");
            _output.WriteLine(result.Stderr);
        }

        var entries = reader.ReadNew();
        _output.WriteLine($"=== bridge logs ({entries.Count}) ===");
        foreach (var e in entries)
        {
            _output.WriteLine($"{e.InboundMethod} {e.InboundPath} -> {e.UpstreamStatus}");
        }

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(entries);
        // Contract: the conversation reaches Copilot's /v1/messages and gets a 2xx.
        // Assert on PRESENCE of a 2xx entry, not the first one: Claude Code fires
        // background requests (e.g. a non-streaming structured-output title/probe
        // carrying structured-outputs-* — which the bridge deliberately 400s via
        // Pipeline.OutboundBeta.GlobalStrip, and Claude Code self-heals from), so
        // the first /v1/messages entry can legitimately be a 400 the client recovers
        // from. What matters is that the real prompt turn succeeded.
        var messagesEntries = entries
            .Where(e => e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(messagesEntries);
        Assert.Contains(messagesEntries, e => e.UpstreamStatus is >= 200 and <= 299);
    }
}
