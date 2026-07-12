using System.Runtime.Versioning;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Playground.Contract;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground;

/// <summary>
/// B3 (change 3, task 7.1) — coercion-vs-live. Ties each Codex T2 coercion to the
/// LIVE Copilot <c>/responses</c> behavior, so a coercion that is no longer
/// necessary (Copilot widened acceptance) shows up as a red test rather than a
/// silent unnecessary mutation. The complement of change-2's B2 drift detection:
/// B2 alarms when the snapshot moves; B3 asserts the bridge's baked-in coercions
/// still match what the live backend does.
///
/// Live, Integration-tagged: skipped in CI. Uses the same minimal-payload probe
/// discipline as the contract sweeps, with transient-retry.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CodexCoercionContractTests
{
    private readonly ITestOutputHelper _output;
    public CodexCoercionContractTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// BECAUSE the live backend still 400s <c>service_tier</c>, the T2 strip is
    /// still needed. If Copilot starts accepting it, this goes red → reconsider
    /// the strip.
    /// </summary>
    [Fact]
    public async Task ServiceTier_StillRejected_StripStillNeeded()
    {
        using var client = new PlaygroundClient();
        var payload =
            "{\"model\":\"gpt-5.3-codex\","
            + "\"instructions\":\"Reply with exactly: ok\","
            + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"reply: ok\"}]}],"
            + "\"stream\":false,\"store\":false,\"service_tier\":\"default\"}";
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload), "service_tier probe");
        var accepted = WireAcceptance.IsAccepted(status, body, "service_tier");
        _output.WriteLine($"service_tier → {(int)status} accepted={accepted}");
        Assert.False(accepted,
            "Copilot now ACCEPTS service_tier — the T2 strip coercion may be unnecessary; reconcile.");
    }

    /// <summary>
    /// BECAUSE the large-profile models still reject <c>minimal</c> effort, T2's
    /// clamp (minimal→low) is still needed. Probes gpt-5.3-codex.
    /// </summary>
    [Fact]
    public async Task LargeModel_StillRejectsMinimalEffort_ClampStillNeeded()
    {
        using var client = new PlaygroundClient();
        var accepted = await ProbeEffort(client, "gpt-5.3-codex", "minimal");
        _output.WriteLine($"gpt-5.3-codex effort=minimal accepted={accepted}");
        Assert.False(accepted,
            "gpt-5.3-codex now ACCEPTS minimal — the large-profile clamp may be unnecessary; reconcile CodexModelProfileCatalog.");

        // And the clamp target (low) IS accepted — so clamping there is valid.
        Assert.True(await ProbeEffort(client, "gpt-5.3-codex", "low"),
            "gpt-5.3-codex no longer accepts 'low' — the clamp target is wrong.");
    }

    /// <summary>
    /// BECAUSE gpt-5-mini (small profile) still rejects <c>xhigh</c> and
    /// <c>none</c>, T2's clamps (xhigh→high, none→drop) are still needed.
    /// </summary>
    [Fact]
    public async Task SmallModel_StillRejectsXhighAndNone_ClampsStillNeeded()
    {
        using var client = new PlaygroundClient();
        Assert.False(await ProbeEffort(client, "gpt-5-mini", "xhigh"),
            "gpt-5-mini now ACCEPTS xhigh — the small-profile clamp may be unnecessary; reconcile.");
        Assert.False(await ProbeEffort(client, "gpt-5-mini", "none"),
            "gpt-5-mini now ACCEPTS none — reconcile the small-profile coercion.");
        // The clamp target (high) IS accepted.
        Assert.True(await ProbeEffort(client, "gpt-5-mini", "high"),
            "gpt-5-mini no longer accepts 'high' — the xhigh clamp target is wrong.");
    }

    /// <summary>
    /// Sanity: the catalog's coercion decisions match the live behavior probed
    /// above. Cross-checks the bridge code (CodexModelProfileCatalog) against live
    /// — the catalog says gpt-5.3-codex accepts 'low' but not 'minimal', etc.
    /// </summary>
    [Fact]
    public void Catalog_CoercionTargets_AreSelfConsistent()
    {
        var catalog = new CodexModelProfileCatalog();
        var large = catalog.Get("gpt-5.3-codex")!;
        Assert.Contains("low", large.AcceptedEfforts);    // clamp target for minimal
        Assert.DoesNotContain("minimal", large.AcceptedEfforts);

        var small = catalog.Get("gpt-5-mini")!;
        Assert.Contains("high", small.AcceptedEfforts);   // clamp target for xhigh
        Assert.DoesNotContain("xhigh", small.AcceptedEfforts);
        Assert.DoesNotContain("none", small.AcceptedEfforts);
    }

    private async Task<bool> ProbeEffort(PlaygroundClient client, string model, string effort)
    {
        var payload =
            "{\"model\":\"" + model + "\","
            + "\"instructions\":\"Reply with exactly: ok\","
            + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"reply: ok\"}]}],"
            + "\"stream\":false,\"store\":false,"
            + "\"reasoning\":{\"effort\":\"" + effort + "\"},\"include\":[\"reasoning.encrypted_content\"]}";
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(payload), $"{model} effort={effort}");
        return WireAcceptance.IsAccepted(status, body, $"{model} effort={effort}");
    }
}
