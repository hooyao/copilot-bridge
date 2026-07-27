using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Reports, once at startup, the effective end-to-end timeout for a long-thinking
/// turn — the shortest bound that will actually fire across the chain — and warns
/// when Claude Code's configuration would abort before the bridge's own budget
/// applies.
/// </summary>
/// <remarks>
/// The operator otherwise has to derive this from two separate config files, one
/// of which is the client's. Worse, the binding bound is usually the client's and
/// is invisible from the bridge's logs: the bridge stays 200 while the client
/// kills the turn. See <c>docs/timeout-chain.md</c>.
/// </remarks>
internal static class TimeoutBudgetReport
{
    /// <summary>Command the operator runs to have the bridge rewrite the client keys.</summary>
    private const string ConfigCommand = "copilot-bridge config claude-code";

    /// <summary>
    /// Emit the report. Never throws: reading the client's settings is best-effort,
    /// because a bridge is perfectly usable by a client configured some other way.
    /// </summary>
    public static void Emit(
        UpstreamTimeoutOptions budgets, ILogger log, string? settingsPathOverride = null)
    {
        var snapshot = ClaudeCodeTimeoutReader.Read(settingsPathOverride);

        var firstByte = Describe(budgets.FirstByteTimeoutSeconds);
        var streamIdle = Describe(budgets.StreamIdleTimeoutSeconds);

        log.LogInformation(
            "Timeouts:  bridge first-byte {FirstByte}, stream-idle {StreamIdle} "
            + "(Pipeline:UpstreamTimeout — idle budgets, not total caps)",
            firstByte, streamIdle);

        if (!snapshot.Readable)
        {
            log.LogInformation(
                "Timeouts:  Claude Code client bounds unknown ({Reason}: {Path}) — "
                + "effective end-to-end timeout is the bridge's own budgets",
                snapshot.Reason, snapshot.SettingsPath);
            return;
        }

        log.LogInformation(
            "Timeouts:  Claude Code stream-idle {StreamIdle}, request {Request} "
            + "(client env — applies on Claude Code's next start)",
            DescribeClient(snapshot.StreamIdle),
            DescribeClient(snapshot.RequestTimeout));

        var effective = Effective(budgets, snapshot);
        log.LogInformation(
            "Timeouts:  effective end-to-end bound {Effective}", effective);

        // An undercut means the client aborts a healthy in-progress turn and the
        // bridge's budget never gets to decide — the failure this report exists for.
        WarnIfUndercut(
            log, snapshot.StreamIdle, budgets.StreamIdleTimeoutSeconds,
            "stream-idle", nameof(UpstreamTimeoutOptions.StreamIdleTimeoutSeconds));
        WarnIfUndercut(
            log, snapshot.RequestTimeout, budgets.FirstByteTimeoutSeconds,
            "first-byte", nameof(UpstreamTimeoutOptions.FirstByteTimeoutSeconds));
    }

    /// <summary>
    /// Warn when this client bound would fire before the bridge budget it is meant
    /// to outlast. A MISSING key warns exactly like a too-short one: absence is not
    /// benign, because the bridge's own 1M-context key makes the client fall back to
    /// a shorter first-party default — a state the bridge itself creates, and the
    /// default state of every install configured before this feature existed.
    /// </summary>
    private static void WarnIfUndercut(
        ILogger log,
        ClientTimeoutValue client,
        int bridgeBudgetSeconds,
        string phaseLabel,
        string bridgeKeyName)
    {
        if (client.EffectiveMs is not { } clientMs)
        {
            return;
        }

        // A disabled bridge budget imposes no bound, so nothing can undercut it.
        if (bridgeBudgetSeconds <= 0)
        {
            return;
        }

        var bridgeMs = (long)bridgeBudgetSeconds * 1000L;
        if (clientMs >= bridgeMs)
        {
            return;
        }

        var source = client.IsExplicit
            ? $"set to {clientMs}ms"
            : $"unset — Claude Code falls back to {clientMs}ms";

        log.LogWarning(
            "Timeouts:  {Key} ({Source}) fires BEFORE the bridge {Phase} budget "
            + "({BridgeSeconds}s / Pipeline:UpstreamTimeout:{BridgeKey}). Claude Code will abort a "
            + "healthy long-thinking turn before the bridge's budget applies. Fix either way: "
            + "run `{Command}` to have the bridge write the derived values, or set {Key} "
            + "yourself to at least {Minimum}ms.",
            client.Key, source, phaseLabel, bridgeBudgetSeconds, bridgeKeyName,
            ConfigCommand, client.Key, bridgeMs);
    }

    /// <summary>
    /// The shortest bound that will actually fire. A disabled bridge budget imposes
    /// no bound and is excluded rather than counted as zero.
    /// </summary>
    private static string Effective(UpstreamTimeoutOptions budgets, ClientTimeoutSnapshot snapshot)
    {
        var candidates = new List<(long Ms, string Source)>();

        if (budgets.StreamIdleTimeoutSeconds > 0)
        {
            candidates.Add(((long)budgets.StreamIdleTimeoutSeconds * 1000L, "bridge stream-idle"));
        }
        if (budgets.FirstByteTimeoutSeconds > 0)
        {
            candidates.Add(((long)budgets.FirstByteTimeoutSeconds * 1000L, "bridge first-byte"));
        }
        if (snapshot.StreamIdle.EffectiveMs is { } si)
        {
            candidates.Add((si, $"client {snapshot.StreamIdle.Key}"));
        }
        if (snapshot.RequestTimeout.EffectiveMs is { } rt)
        {
            candidates.Add((rt, $"client {snapshot.RequestTimeout.Key}"));
        }

        if (candidates.Count == 0)
        {
            return "none — no bound on either side";
        }

        var min = candidates[0];
        foreach (var c in candidates)
        {
            if (c.Ms < min.Ms)
            {
                min = c;
            }
        }

        return $"{FormatMs(min.Ms)} ({min.Source})";
    }

    private static string Describe(int budgetSeconds) =>
        budgetSeconds <= 0 ? "no bound (disabled)" : $"{budgetSeconds}s";

    private static string DescribeClient(ClientTimeoutValue value) =>
        value.EffectiveMs is { } ms
            ? value.IsExplicit ? FormatMs(ms) : $"{FormatMs(ms)} (unset — client default)"
            : "unknown";

    private static string FormatMs(long ms) =>
        ms % 60_000 == 0 ? $"{ms / 60_000}m" : $"{ms / 1000.0:0.#}s";
}
