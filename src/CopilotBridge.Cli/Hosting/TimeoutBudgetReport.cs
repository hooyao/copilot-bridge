using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Hosting;

/// <summary>
/// Reports, once at startup, which timeout bounds apply to a long-thinking turn —
/// per phase, from the bridge, Claude Code, and Codex — and warns when an
/// unprotected client watchdog would abort before the bridge's own budget applies.
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
/// <para><b>It reports what it can see.</b> Only GLOBAL client settings are readable
/// from startup: Claude Code's project-scoped <c>settings.local.json</c> belongs to
/// the session's directory, while Codex's active global provider lives in
/// <c>config.toml</c>.</para>
/// </remarks>
internal static class TimeoutBudgetReport
{
    /// <summary>Command the operator runs to have the bridge rewrite the client keys.</summary>
    private const string ConfigCommand = "copilot-bridge config claude-code";

    /// <summary>
    /// Emit the report. Never throws: reading the client's settings is best-effort,
    /// because a bridge is perfectly usable by a client configured some other way.
    /// </summary>
    /// <param name="wholeResponseBuffering">
    /// True when the response leak guard buffers the entire response
    /// (<c>PreserveStream=false</c>). In that mode there is no live stream to inject
    /// keepalives into, so the report must not claim they are active.
    /// </param>
    public static void Emit(
        UpstreamTimeoutOptions budgets, ILogger log, string? settingsPathOverride = null,
        bool wholeResponseBuffering = false,
        string? codexConfigPathOverride = null)
    {
        var snapshot = ClaudeCodeTimeoutReader.Read(settingsPathOverride);
        var codex = CodexTimeoutReader.Read(codexConfigPathOverride);
        var keepAliveCanReach = KeepAliveCanReach(budgets, wholeResponseBuffering);
        var claudeKeepAliveEffective = KeepAliveEffectiveFor(
            budgets, keepAliveCanReach, snapshot.StreamIdle);
        var codexKeepAliveEffective = KeepAliveEffectiveFor(
            budgets, keepAliveCanReach, codex.StreamIdle);

        // One table, not a paragraph per bound. Each row is a phase, and the row
        // says which side ends the turn in that phase — the question an operator
        // actually has. Rationale and caveats live in docs/timeout-chain.md; a
        // startup log is the wrong place to explain them.
        log.LogInformation(
            "Timeouts (what ends a turn):\n"
            + "  idle gap (Claude) bridge {BridgeIdle,-9} client {ClientIdle,-9} -> {ClaudeIdleWinner}\n"
            + "  Codex idle gap   bridge {BridgeIdle,-9} client {CodexIdle,-9} -> {CodexIdleWinner}\n"
            + "  first byte      bridge {BridgeFirstByte,-9} client {ClientFirstByte,-9} -> {FirstByteWinner}\n"
            + "  whole request (Claude) bridge {BridgeWhole,-9} client {ClientWhole,-9} -> {WholeWinner}"
            + "{Notes}",
            Describe(budgets.StreamIdleTimeoutSeconds),
            DescribeClient(snapshot.StreamIdle),
            IdleWinner(
                budgets.StreamIdleTimeoutSeconds,
                snapshot.StreamIdle,
                claudeKeepAliveEffective),
            Describe(budgets.StreamIdleTimeoutSeconds),
            DescribeClient(codex.StreamIdle),
            IdleWinner(
                budgets.StreamIdleTimeoutSeconds,
                codex.StreamIdle,
                codexKeepAliveEffective),
            Describe(budgets.FirstByteTimeoutSeconds),
            None,
            Describe(budgets.FirstByteTimeoutSeconds),
            None,
            DescribeClient(snapshot.RequestTimeout),
            DescribeClient(snapshot.RequestTimeout),
            BuildNotes(
                snapshot,
                codex,
                budgets,
                wholeResponseBuffering,
                keepAliveCanReach,
                claudeKeepAliveEffective,
                codexKeepAliveEffective));

        // Warnings stay separate: they are the only lines an operator must act on,
        // so they must not be buried in the table above.
        if (!snapshot.Readable)
        {
            WarnIfBudgetExceedsClientMaximum(log, budgets, claudeKeepAliveEffective);
            return;
        }

        // An undercut means the client aborts a healthy in-progress turn and the
        // bridge's budget never gets to decide — the failure this report exists for.
        // Skipped when the budget exceeds what the client can honor: there the
        // undercut is unavoidable, and WarnIfBudgetExceedsClientMaximum gives the
        // only advice that works (lower the budget). Emitting both would hand the
        // operator two contradictory instructions, one of them impossible.
        if (!claudeKeepAliveEffective
            && !ClaudeCodeTimeoutPolicy.StreamIdleBudgetExceedsClientMaximum(
                budgets.StreamIdleTimeoutSeconds))
        {
            WarnIfUndercut(
                log, snapshot.StreamIdle, budgets.StreamIdleTimeoutSeconds,
                "stream-idle", nameof(UpstreamTimeoutOptions.StreamIdleTimeoutSeconds));
        }
        WarnIfBudgetExceedsClientMaximum(log, budgets, claudeKeepAliveEffective);
    }

    /// <summary>Placeholder for a cell where that side imposes no bound on the phase.</summary>
    private const string None = "-";

    /// <summary>
    /// Which side ends the turn in the idle-gap phase. With effective keepalive the
    /// client timer is repeatedly refreshed and the bridge budget owns the decision;
    /// without it, the shorter active numeric bound wins.
    /// </summary>
    private static string IdleWinner(
        int bridgeSeconds,
        ClientTimeoutValue client,
        bool keepAliveEffective)
    {
        var bridgeMs = bridgeSeconds > 0 ? (long)bridgeSeconds * 1000L : (long?)null;
        var clientMs = client.EffectiveMs;

        if (keepAliveEffective)
        {
            return bridgeMs is { } bounded
                ? $"bridge {FormatMs(bounded)} (keepalive)"
                : "no bound (keepalive)";
        }

        return (bridgeMs, clientMs) switch
        {
            (null, null) => client.IsUnknown ? "unknown" : "no bound",
            (not null, null) => client.IsUnknown
                ? "unknown"
                : $"bridge {FormatMs(bridgeMs.Value)}",
            (null, not null) => $"client {FormatMs(clientMs!.Value)}",
            _ when bridgeMs!.Value <= clientMs!.Value => $"bridge {FormatMs(bridgeMs.Value)}",
            _ => $"client {FormatMs(clientMs!.Value)}",
        };
    }

    /// <summary>
    /// The two caveats that change how the table should be read, appended only when
    /// they apply. Anything an operator does not need at startup stays in the docs.
    /// </summary>
    private static string BuildNotes(
        ClientTimeoutSnapshot snapshot,
        CodexTimeoutSnapshot codex,
        UpstreamTimeoutOptions budgets,
        bool wholeResponseBuffering,
        bool keepAliveCanReach,
        bool claudeKeepAliveEffective,
        bool codexKeepAliveEffective)
    {
        var keepAlive = DescribeKeepAlive(
            budgets,
            wholeResponseBuffering,
            keepAliveCanReach,
            snapshot.StreamIdle,
            claudeKeepAliveEffective,
            codex.StreamIdle,
            codexKeepAliveEffective);
        var codexNote = codex.Readable
            ? $"\n  Codex client value from {codex.ConfigPath}"
            : $"\n  Codex client value unknown ({codex.Reason})";

        if (!snapshot.Readable)
        {
            return keepAlive + codexNote
                   + $"\n  Claude client values unknown ({snapshot.Reason}) — a shorter client watchdog"
                   + "\n  could end a turn before any bridge budget";
        }

        var notes = keepAlive + codexNote
                    + "\n  Claude client values take effect on Claude Code's next restart";
        if (!snapshot.StreamIdle.IsExplicit
            || !snapshot.RequestTimeout.IsExplicit
            || (codex.Readable && !codex.StreamIdle.IsExplicit))
        {
            notes += "\n  * = built-in client default (not explicitly configured)";
        }
        if (!snapshot.StreamIdle.IsExplicit || !snapshot.RequestTimeout.IsExplicit)
        {
            notes += "\n  run `" + ConfigCommand + "` to write Claude Code's managed values";
        }
        return notes;
    }

    /// <summary>
    /// Report whether keepalives will ACTUALLY reach the client, not merely whether the
    /// interval is positive. Three configurations look "on" but deliver nothing, and each
    /// would otherwise leave the operator believing a silent turn is protected when it is
    /// not:
    /// <list type="bullet">
    ///   <item>interval &lt;= 0 — injection disabled outright;</item>
    ///   <item>interval &gt;= a positive stream-idle budget — the budget always fires
    ///   first, so no ping is ever due (<see cref="Pipeline.Strategies.StreamIdleReader"/>
    ///   gives ties to the budget deliberately);</item>
    ///   <item>whole-response buffering (<c>ResponseLeakGuard.PreserveStream=false</c>) —
    ///   the response is drained and delivered as one buffered body, so there is no live
    ///   stream to inject into at all.</item>
    /// </list>
    /// </summary>
    private static string DescribeKeepAlive(
        UpstreamTimeoutOptions budgets,
        bool wholeResponseBuffering,
        bool keepAliveCanReach,
        ClientTimeoutValue claudeIdle,
        bool claudeKeepAliveEffective,
        ClientTimeoutValue codexIdle,
        bool codexKeepAliveEffective)
    {
        if (budgets.KeepAliveIntervalSeconds <= 0)
        {
            return "\n  keepalive: OFF — the client's own idle watchdog is the only thing"
                   + "\n  protecting a silent long-thinking turn";
        }

        if (wholeResponseBuffering)
        {
            return "\n  keepalive: INACTIVE — ResponseLeakGuard.PreserveStream=false buffers the"
                   + "\n  whole response, so no ping can reach the client mid-turn";
        }

        if (budgets.StreamIdleTimeoutSeconds > 0
            && budgets.KeepAliveIntervalSeconds >= budgets.StreamIdleTimeoutSeconds)
        {
            return $"\n  keepalive: INACTIVE — interval ({budgets.KeepAliveIntervalSeconds}s) is not shorter than the"
                   + $"\n  stream-idle budget ({budgets.StreamIdleTimeoutSeconds}s), so the budget always fires first";
        }

        if (!keepAliveCanReach)
            throw new InvalidOperationException("Keepalive reachability disagrees with its report state.");

        return $"\n  keepalive: bridge sends ping every {budgets.KeepAliveIntervalSeconds}s while upstream is silent"
               + $"\n  protection: Claude {Protection(claudeIdle, claudeKeepAliveEffective)};"
               + $" Codex {Protection(codexIdle, codexKeepAliveEffective)}";
    }

    private static bool KeepAliveCanReach(
        UpstreamTimeoutOptions budgets,
        bool wholeResponseBuffering) =>
        budgets.KeepAliveIntervalSeconds > 0
        && !wholeResponseBuffering
        && (budgets.StreamIdleTimeoutSeconds <= 0
            || budgets.KeepAliveIntervalSeconds < budgets.StreamIdleTimeoutSeconds);

    private static bool KeepAliveEffectiveFor(
        UpstreamTimeoutOptions budgets,
        bool keepAliveCanReach,
        ClientTimeoutValue client) =>
        keepAliveCanReach
        && client.EffectiveMs is { } clientMs
        && (long)budgets.KeepAliveIntervalSeconds * 1000L < clientMs;

    private static string Protection(ClientTimeoutValue client, bool effective)
    {
        if (client.IsUnknown) return "unknown";
        return effective
            ? "active"
            : $"inactive (watchdog {FormatMs(client.EffectiveMs!.Value)} precedes ping)";
    }

    /// <summary>
    /// Warn when a bridge budget is larger than any value Claude Code will honor and
    /// keepalive is not protecting the live stream. In that state the derived client
    /// value is necessarily shorter, so the only fix is lowering the budget.
    /// </summary>
    private static void WarnIfBudgetExceedsClientMaximum(
        ILogger log,
        UpstreamTimeoutOptions budgets,
        bool keepAliveEffective)
    {
        if (keepAliveEffective) return;

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

    /// <summary>A bridge budget, in the same units as every other cell.</summary>
    private static string Describe(int budgetSeconds) =>
        budgetSeconds <= 0 ? "no bound" : FormatMs((long)budgetSeconds * 1000L);

    /// <summary>A client bound. A trailing <c>*</c> marks a value the client falls
    /// back to rather than one it was configured with; the note under the table
    /// expands on it.</summary>
    private static string DescribeClient(ClientTimeoutValue value) =>
        value.EffectiveMs is { } ms
            ? value.IsExplicit ? FormatMs(ms) : $"{FormatMs(ms)}*"
            : "unknown";

    private static string FormatMs(long ms) =>
        ms % 60_000 == 0 ? $"{ms / 60_000}m" : $"{ms / 1000.0:0.#}s";
}
