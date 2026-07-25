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
/// - sonnet-4.6 / opus-4.6: <c>[low, medium, high, max]</c> — pass-through (they reject `xhigh`)
/// - opus-4.7 / opus-4.8 / opus-5: <c>[low, medium, high, xhigh, max]</c> — pass-through
/// - haiku-4.5: no reasoning_effort capability — strip
///
/// Each test verifies:
/// 1. claude.exe exits 0
/// 2. Bridge sees the expected inbound (model, effort) from Claude Code
/// 3. Bridge's outgoing upstream body has the expected (model, effort handling)
/// 4. Copilot returns 2xx
/// </summary>
/// <remarks>
/// <para><b>The variant-rewrite and dedicated-1M cases were deleted in the 2026-07
/// reconciliation.</b> They drove <c>claude-opus-4.7-high</c>, <c>-xhigh</c>,
/// <c>claude-opus-4.7-1m-internal</c>, and <c>claude-opus-4.6-1m</c> — ids Copilot
/// has since retired (all 400; <see cref="ModelProfileProbe.RetiredCandidate_LivenessProbe"/>)
/// — and asserted an <c>EffortHandling.RouteToVariant</c> rewrite that no profile
/// performs any more: the opus-4.7 base was widened to accept every effort tier
/// directly, so there is no sibling to route to. Both the target ids and the
/// behavior under test are gone, which is why these are deleted rather than
/// retargeted.</para>
/// <para>The coverage they provided — "opus-4.7 + a high effort reaches Copilot
/// intact" — now lives in <see cref="PassThrough_NativelySupportedEffort"/> as
/// ordinary pass-through, which is what the contract actually is today.</para>
/// </remarks>
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
    // opus-4.7 base: the 2026-06-05 re-probe widened it from [medium] to every
    // tier, so a high/xhigh effort now passes through on the base id instead of
    // being rewritten to a (since-retired) -high / -xhigh sibling. This is the
    // replacement coverage for the deleted VariantRewrite_Opus47 cases.
    [InlineData("claude-opus-4-7",   "medium", "claude-opus-4.7",   "medium", false)]
    [InlineData("claude-opus-4-7",   "high",   "claude-opus-4.7",   "high",   false)]
    [InlineData("claude-opus-4-7",   "xhigh",  "claude-opus-4.7",   "xhigh",  false)]
    public Task PassThrough_NativelySupportedEffort(
        string claudeModel,
        string effort,
        string expectedUpstreamModel,
        string expectedUpstreamEffort,
        bool _) =>
        RunMatrixCase(claudeModel, effort, expectedUpstreamModel, expectedUpstreamEffort);

    // ─── Strip path: model lacks reasoning_effort capability; bridge drops the field ───

    [Theory]
    [InlineData("claude-haiku-4-5",  "medium", "claude-haiku-4.5",  null)]
    public Task Strip_ModelsWithoutReasoningEffort(
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
