using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Reports, once at startup, which timeout bounds apply to a long-thinking turn —
/// per phase, from both the bridge and the client — and warns when Claude Code's
/// configuration would abort before the bridge's own budget applies.
/// </summary>
/// <remarks>
/// <para>The operator otherwise has to derive this from two separate config files,
/// one of which is the client's. Worse, the binding bound is usually the client's
/// and is invisible from the bridge's logs: the bridge stays 200 while the client
/// kills the turn. See <c>docs/timeout-chain.md</c>.</para>
/// <para><b>Bounds are reported per phase, never as one number.</b> They do not
/// compete over the same interval: the first-byte budget is disarmed the moment
/// headers arrive, the stream-idle budgets then govern each silent gap, and the
/// client's whole-request cap is wall-clock across everything. A single "effective"
/// minimum would be wrong — with first-byte 60 s and stream-idle 600 s it would
/// report 60 s for a turn whose real exposure after headers is 600 s.</para>
/// <para><b>It reports what it can see.</b> Only the GLOBAL client settings are
/// readable from startup — a project-scoped <c>settings.local.json</c> belongs to
/// the Claude session's directory, which a bridge serving many repositories cannot
/// identify.</para>
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
            // The client's bounds are unknown, so what actually ends a stalled turn
            // is unknown too — it is not "the bridge's budgets". A
            // separately-configured client may hold a much shorter watchdog, and
            // claiming otherwise would falsely reassure exactly the operator most
            // likely to be bitten.
            log.LogInformation(
                "Timeouts:  Claude Code client bounds UNKNOWN ({Reason}: {Path}) — the bridge's own "
                + "budgets above are the only ones it can vouch for; a shorter client watchdog "
                + "could end a turn before any of them",
                snapshot.Reason, snapshot.SettingsPath);
            WarnIfBudgetExceedsClientMaximum(log, budgets);
            return;
        }

        log.LogInformation(
            "Timeouts:  Claude Code stream-idle {StreamIdle}, request {Request} "
            + "(global client env — applies on Claude Code's next start; a project's own "
            + ".claude/settings.local.json would override these and is not visible from here)",
            DescribeClient(snapshot.StreamIdle),
            DescribeClient(snapshot.RequestTimeout));

        // Per PHASE, not a single minimum: these bounds do not compete over the same
        // interval, so a global min is meaningless. With first-byte 60 s and
        // stream-idle 600 s, headers arriving immediately DISARM the 60 s timer and
        // a later silence is bounded at 600 s — reporting "60 s" would be wrong.
        log.LogInformation(
            "Timeouts:  waiting for headers — bridge {FirstByte}; then per silent gap — "
            + "bridge {StreamIdle} vs client {ClientIdle} (whichever is shorter ends the turn)",
            Describe(budgets.FirstByteTimeoutSeconds),
            Describe(budgets.StreamIdleTimeoutSeconds),
            DescribeClient(snapshot.StreamIdle));

        // An undercut means the client aborts a healthy in-progress turn and the
        // bridge's budget never gets to decide — the failure this report exists for.
        // Skipped when the budget exceeds what the client can honor: there the
        // undercut is unavoidable, and WarnIfBudgetExceedsClientMaximum gives the
        // only advice that works (lower the budget). Emitting both would hand the
        // operator two contradictory instructions, one of them impossible.
        if (!ClaudeCodeTimeoutPolicy.StreamIdleBudgetExceedsClientMaximum(
                budgets.StreamIdleTimeoutSeconds))
        {
            WarnIfUndercut(
                log, snapshot.StreamIdle, budgets.StreamIdleTimeoutSeconds,
                "stream-idle", nameof(UpstreamTimeoutOptions.StreamIdleTimeoutSeconds));
        }
        // API_TIMEOUT_MS is deliberately NOT compared against a budget. It is a
        // wall-clock cap while the budgets bound inactivity, so no finite value can
        // outlast them — a healthy turn that keeps emitting has no total duration,
        // and a stalled one can legally spend first-byte + stream-idle + … before
        // any bridge timer fires. Reporting it as a residual bound is honest;
        // "warning when it undercuts" would fire on correct configurations.
        ReportResidualWallClockBound(log, snapshot.RequestTimeout);
        WarnIfBudgetExceedsClientMaximum(log, budgets);
    }

    /// <summary>
    /// State the client's whole-request cap as what it is: a bound the bridge cannot
    /// out-wait, only raise. It exists mainly for Claude Code's non-streaming
    /// recovery request (client default 300 s, far too short for a deep-thinking
    /// turn); for a streaming turn it is a ceiling that can in principle be crossed
    /// before a bridge budget fires.
    /// </summary>
    private static void ReportResidualWallClockBound(ILogger log, ClientTimeoutValue requestTimeout)
    {
        if (requestTimeout.EffectiveMs is not { } ms)
        {
            return;
        }

        log.LogInformation(
            "Timeouts:  {Key} = {Value} is a wall-clock cap on the whole request, not an "
            + "inactivity budget — the bridge cannot out-wait it, so a turn running longer than "
            + "this ends at the client regardless of the budgets above",
            requestTimeout.Key,
            requestTimeout.IsExplicit
                ? FormatMs(ms)
                : $"{FormatMs(ms)} primary / "
                  + $"{FormatMs(ClaudeCodeTimeoutPolicy.AbsentFallbackRequestTimeoutDefaultMs)} "
                  + "per non-streaming recovery attempt (unset — client defaults)");
    }

    /// <summary>
    /// Warn when a bridge budget is larger than any value the client will honor. In
    /// that state the derived client value is necessarily SHORTER than the budget,
    /// so the client fires first no matter how often the operator re-runs the config
    /// command — the only fix is lowering the budget, which the warning must say
    /// instead of pointing at a remedy that cannot work.
    /// </summary>
    private static void WarnIfBudgetExceedsClientMaximum(ILogger log, UpstreamTimeoutOptions budgets)
    {
        if (ClaudeCodeTimeoutPolicy.StreamIdleBudgetExceedsClientMaximum(
                budgets.StreamIdleTimeoutSeconds))
        {
            log.LogWarning(
                "Timeouts:  StreamIdleTimeoutSeconds ({Budget}s) exceeds the largest value Claude Code "
                + "honors for {Key} ({MaxMs}ms). The written client value is capped there, so the client "
                + "will abort BEFORE this budget and re-running `{Command}` cannot fix it — lower the "
                + "budget to at most {MaxSeconds}s.",
                budgets.StreamIdleTimeoutSeconds, ClaudeCodeTimeoutPolicy.StreamIdleKey,
                ClaudeCodeTimeoutPolicy.StreamIdleMaxMs, ConfigCommand,
                ClaudeCodeTimeoutPolicy.StreamIdleMaxMs / 1000);
        }

        // Only the stream-idle key is checked here: it is the one whose written value
        // is derived from a budget, so it is the one a too-large budget can render
        // unsatisfiable. API_TIMEOUT_MS is a fixed maximum by design (see
        // ClaudeCodeTimeoutPolicy.RequestTimeoutMs).
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

    private static string Describe(int budgetSeconds) =>
        budgetSeconds <= 0 ? "no bound (disabled)" : $"{budgetSeconds}s";

    private static string DescribeClient(ClientTimeoutValue value) =>
        value.EffectiveMs is { } ms
            ? value.IsExplicit ? FormatMs(ms) : $"{FormatMs(ms)} (unset — client default)"
            : "unknown";

    private static string FormatMs(long ms) =>
        ms % 60_000 == 0 ? $"{ms / 60_000}m" : $"{ms / 1000.0:0.#}s";
}
