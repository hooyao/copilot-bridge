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

        // One table, not a paragraph per bound. Each row is a phase, and the row
        // says which side ends the turn in that phase — the question an operator
        // actually has. Rationale and caveats live in docs/timeout-chain.md; a
        // startup log is the wrong place to explain them.
        log.LogInformation(
            "Timeouts (what ends a turn):\n"
            + "  idle gap        bridge {BridgeIdle,-9} client {ClientIdle,-9} -> {IdleWinner}\n"
            + "  first byte      bridge {BridgeFirstByte,-9} client {ClientFirstByte,-9} -> {FirstByteWinner}\n"
            + "  whole request   bridge {BridgeWhole,-9} client {ClientWhole,-9} -> {WholeWinner}"
            + "{Notes}",
            Describe(budgets.StreamIdleTimeoutSeconds),
            DescribeClient(snapshot.StreamIdle),
            Winner(budgets.StreamIdleTimeoutSeconds, snapshot.StreamIdle),
            Describe(budgets.FirstByteTimeoutSeconds),
            None,
            Describe(budgets.FirstByteTimeoutSeconds),
            None,
            DescribeClient(snapshot.RequestTimeout),
            DescribeClient(snapshot.RequestTimeout),
            BuildNotes(snapshot));

        // Warnings stay separate: they are the only lines an operator must act on,
        // so they must not be buried in the table above.
        if (!snapshot.Readable)
        {
            WarnIfBudgetExceedsClientMaximum(log, budgets);
            return;
        }

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
        WarnIfBudgetExceedsClientMaximum(log, budgets);
    }

    /// <summary>Placeholder for a cell where that side imposes no bound on the phase.</summary>
    private const string None = "-";

    /// <summary>
    /// Which side ends the turn in the idle-gap phase: the shorter of the two, since
    /// both timers run over the same interval there. (The other rows have only one
    /// side, so their winner is that side.)
    /// </summary>
    private static string Winner(int bridgeSeconds, ClientTimeoutValue client)
    {
        var bridgeMs = bridgeSeconds > 0 ? (long)bridgeSeconds * 1000L : (long?)null;
        var clientMs = client.EffectiveMs;

        return (bridgeMs, clientMs) switch
        {
            (null, null) => "no bound",
            (not null, null) => client.IsUnknown ? $"{FormatMs(bridgeMs.Value)}?" : FormatMs(bridgeMs.Value),
            (null, not null) => FormatMs(clientMs!.Value),
            _ => FormatMs(Math.Min(bridgeMs!.Value, clientMs!.Value)),
        };
    }

    /// <summary>
    /// The two caveats that change how the table should be read, appended only when
    /// they apply. Anything an operator does not need at startup stays in the docs.
    /// </summary>
    private static string BuildNotes(ClientTimeoutSnapshot snapshot)
    {
        if (!snapshot.Readable)
        {
            return $"\n  client values unknown ({snapshot.Reason}) — a shorter client watchdog"
                   + "\n  could end a turn before any bridge budget";
        }

        var notes = "\n  client values take effect on Claude Code's next restart";
        if (!snapshot.StreamIdle.IsExplicit || !snapshot.RequestTimeout.IsExplicit)
        {
            notes += "\n  * = client default (not configured); run `" + ConfigCommand + "` to set it";
        }
        return notes;
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
