using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Matrix: for each (model, effort) combination Claude Code can express,
/// drive <c>claude.exe -p</c> end-to-end and assert the bridge transforms
/// the request into a shape Copilot accepts. The truth tables come from
/// the live <c>/models</c> capabilities dump
/// (<see cref="CopilotGapProbes.DumpClaudeModelsAndCapabilities"/>):
///
/// - sonnet-4.6 / opus-4.6 / opus-4.6-1m: <c>reasoning_effort: [low, medium, high]</c> — pass-through
/// - opus-4.7-1m-internal: <c>[low, medium, high, xhigh]</c> — pass-through
/// - opus-4.7 (base): <c>[medium]</c> only — effort != medium ⇒ rewrite to <c>-{effort}</c> variant
/// - sonnet-4.5 / opus-4.5 / haiku-4.5: no reasoning_effort capability — strip
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
    [InlineData("claude-sonnet-4-6", "low",    "claude-sonnet-4.6", "low",    false)]
    [InlineData("claude-sonnet-4-6", "medium", "claude-sonnet-4.6", "medium", false)]
    [InlineData("claude-sonnet-4-6", "high",   "claude-sonnet-4.6", "high",   false)]
    [InlineData("claude-opus-4-6",   "low",    "claude-opus-4.6",   "low",    false)]
    [InlineData("claude-opus-4-6",   "medium", "claude-opus-4.6",   "medium", false)]
    [InlineData("claude-opus-4-6",   "high",   "claude-opus-4.6",   "high",   false)]
    public Task PassThrough_NativelySupportedEffort(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string expectedUpstreamEffort,
        bool _) =>
        RunMatrixCase(claudeModel, effort, expectedUpstreamModel, expectedUpstreamEffort);

    // ─── Variant rewrite path: opus-4.7 base only accepts medium; bridge picks a variant ───

    [Theory]
    [InlineData("claude-opus-4-7", "medium", "claude-opus-4.7",       "medium")]  // medium is in base's supports list — pass through
    [InlineData("claude-opus-4-7", "high",   "claude-opus-4.7-high",  null)]      // rewrite to -high variant, strip
    [InlineData("claude-opus-4-7", "xhigh",  "claude-opus-4.7-xhigh", null)]      // rewrite to -xhigh variant, strip
    public Task VariantRewrite_Opus47(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string? expectedUpstreamEffort) =>
        RunMatrixCase(claudeModel, effort, expectedUpstreamModel, expectedUpstreamEffort);

    // ─── Strip path: model lacks reasoning_effort capability; bridge drops the field ───

    [Theory]
    [InlineData("claude-sonnet-4-5", "high",   "claude-sonnet-4.5", null)]
    [InlineData("claude-haiku-4-5",  "medium", "claude-haiku-4.5",  null)]
    public Task Strip_ModelsWithoutReasoningEffort(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string? expectedUpstreamEffort) =>
        RunMatrixCase(claudeModel, effort, expectedUpstreamModel, expectedUpstreamEffort);

    // ─── 1M-context models: priority per goal. Both expose full effort ranges. ───

    [Theory]
    [InlineData("claude-opus-4-7-1m-internal", "low",    "claude-opus-4.7-1m-internal", "low")]
    [InlineData("claude-opus-4-7-1m-internal", "medium", "claude-opus-4.7-1m-internal", "medium")]
    [InlineData("claude-opus-4-7-1m-internal", "high",   "claude-opus-4.7-1m-internal", "high")]
    [InlineData("claude-opus-4-7-1m-internal", "xhigh",  "claude-opus-4.7-1m-internal", "xhigh")]
    [InlineData("claude-opus-4-6-1m",          "low",    "claude-opus-4.6-1m",          "low")]
    [InlineData("claude-opus-4-6-1m",          "medium", "claude-opus-4.6-1m",          "medium")]
    [InlineData("claude-opus-4-6-1m",          "high",   "claude-opus-4.6-1m",          "high")]
    public Task LongContext1mModels_PassThroughAllEfforts(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string? expectedUpstreamEffort) =>
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
