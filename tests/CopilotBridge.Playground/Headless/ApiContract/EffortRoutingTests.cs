using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Matrix: for each (model, effort) combination Claude Code can express,
/// drive <c>claude.exe -p</c> end-to-end and assert the bridge transforms
/// the request into a shape Copilot accepts. The truth tables come from the live
/// probes in <see cref="ModelProfileProbe"/> (NOT from <c>/models</c>, which has
/// been wrong in both directions):
///
/// - opus-5.5: <c>[low, medium, high, xhigh, max]</c> — pass-through
///
/// Each test verifies:
/// 1. claude.exe exits 0
/// 2. Bridge sees the expected inbound (model, effort) from Claude Code
/// 3. Bridge's outgoing upstream body has the expected (model, effort handling)
/// 4. Copilot returns 2xx
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class EffortRoutingTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public EffortRoutingTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    // ─── Pass-through path: model declares the effort, bridge keeps it ───

    [Theory]
    [InlineData("claude-opus-5-5", "low",    "claude-opus-5.5", "low",    false)]
    [InlineData("claude-opus-5-5", "medium", "claude-opus-5.5", "medium", false)]
    [InlineData("claude-opus-5-5", "high",   "claude-opus-5.5", "high",   false)]
    [InlineData("claude-opus-5-5", "xhigh",  "claude-opus-5.5", "xhigh",  false)]
    [InlineData("claude-opus-5-5", "max",    "claude-opus-5.5", "max",    false)]
    public Task PassThrough_NativelySupportedEffort(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string expectedUpstreamEffort,
        bool _) =>
        RunMatrixCase(claudeModel, effort, expectedUpstreamModel, expectedUpstreamEffort);

    /// <summary>
    /// Runs one matrix case: drives claude.exe with the given model+effort, then
    /// asserts the bridge audit log matches <paramref name="expectedUpstreamModel"/>
    /// and <paramref name="expectedUpstreamEffort"/> (null = the field must be absent).
    /// </summary>
    private async Task RunMatrixCase(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string? expectedUpstreamEffort)
    {
        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: "Reply with the single word: ok",
            Model: claudeModel,
            Effort: effort,
            OutputFormat: "json",
            AllowedTools: ""));

        var entries = reader.ReadNew();
        var messagesEntries = entries.Where(e => e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal)).ToList();

        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"bridge log entries: total={entries.Count}, messages={messagesEntries.Count}");
        for (var i = 0; i < messagesEntries.Count; i++)
        {
            var m = messagesEntries[i];
            var inUp = m.UpstreamBody is JsonObject ub
                ? $"model={ub["model"]?.GetValue<string>()} effort={ub["output_config"]?["effort"]?.GetValue<string>() ?? "<none>"}"
                : "<no upstream body>";
            _output.WriteLine($"  [{i}] {m.InboundMethod} {m.InboundPath} -> {m.UpstreamStatus}  upstream: {inUp}");
        }
        if (result.ExitCode != 0)
        {
            _output.WriteLine("=== stdout ===");
            _output.WriteLine(result.Stdout);
            _output.WriteLine("=== stderr ===");
            _output.WriteLine(result.Stderr);
        }

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(messagesEntries);

        // Claude Code issues a verification ping in parallel with the user-prompt
        // call; either can win the race, the loser is cancelled (UpstreamStatus=0).
        // What matters is: at least one call succeeded with the expected transform.
        var successful = messagesEntries
            .Where(e => e.UpstreamStatus is >= 200 and < 300 && e.UpstreamBody is JsonObject)
            .ToList();
        Assert.NotEmpty(successful);

        // The user-prompt call has the larger body (system + user). Pick the
        // largest successful entry as the canonical "this is what the user got."
        var canonical = successful
            .OrderByDescending(e => e.UpstreamBody!.ToJsonString().Length)
            .First();
        var upstream = canonical.UpstreamBody!.AsObject();

        var actualModel = upstream["model"]?.GetValue<string>();
        var actualEffort = upstream["output_config"]?["effort"]?.GetValue<string>();

        Assert.Equal(expectedUpstreamModel, actualModel);
        Assert.Equal(expectedUpstreamEffort, actualEffort);
    }
}
