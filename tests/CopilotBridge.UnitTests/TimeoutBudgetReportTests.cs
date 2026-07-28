using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for the timeout-budget-report capability. Asserted from the
/// spec: the report names its bounds and their source; a client bound that would
/// fire first warns and names BOTH remedies; a missing client key warns exactly
/// like a too-short one (it is known-bad, not unknown); a disabled bridge budget
/// reads as "no bound" and is excluded from the minimum; and an unreadable client
/// file never fails startup.
/// </summary>
public class TimeoutBudgetReportTests
{
    private static UpstreamTimeoutOptions Budgets(int firstByteSeconds, int streamIdleSeconds) =>
        new() { FirstByteTimeoutSeconds = firstByteSeconds, StreamIdleTimeoutSeconds = streamIdleSeconds };

    // ---- Requirement: keepalive injection is visible in the report ----

    /// <summary>
    /// Contract: the report states whether the bridge is injecting keepalives. With
    /// injection on, the client's idle watchdogs effectively never fire — which
    /// changes how the idle-gap row is to be read — and pings will appear in traces.
    /// An operator who cannot see this from startup would misread both.
    /// </summary>
    [Fact]
    public void Report_states_that_keepalive_injection_is_active()
    {
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        var budgets = Budgets(firstByteSeconds: 900, streamIdleSeconds: 600);
        budgets.KeepAliveIntervalSeconds = 15;
        TimeoutBudgetReport.Emit(budgets, log, path);

        var table = Table(events);
        Assert.Contains("keepalive", table, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15s", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contract: a POSITIVE interval does not mean pings will flow. When it is not
    /// shorter than the stream-idle budget, StreamIdleReader deliberately lets the
    /// budget fire first and no ping is ever due — so promising pings here would make
    /// the report confidently wrong about the one thing an operator reads it for.
    /// </summary>
    [Fact]
    public void Report_states_keepalive_inactive_when_interval_is_not_shorter_than_budget()
    {
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        var budgets = Budgets(firstByteSeconds: 900, streamIdleSeconds: 30);
        budgets.KeepAliveIntervalSeconds = 30; // equal → the budget always wins
        TimeoutBudgetReport.Emit(budgets, log, path);

        var table = Table(events);
        Assert.Contains("keepalive: INACTIVE", table, StringComparison.Ordinal);
        Assert.DoesNotContain("sends ping every", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contract: whole-response buffering (ResponseLeakGuard.PreserveStream=false)
    /// drains the response and delivers it as one body, so there is no live stream to
    /// inject into and no keepalive can reach the client mid-turn. The report must say
    /// so rather than promise protection the configuration cannot deliver.
    /// </summary>
    [Fact]
    public void Report_states_keepalive_inactive_under_whole_response_buffering()
    {
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        var budgets = Budgets(firstByteSeconds: 900, streamIdleSeconds: 600);
        budgets.KeepAliveIntervalSeconds = 15; // would otherwise be active
        TimeoutBudgetReport.Emit(budgets, log, path, wholeResponseBuffering: true);

        var table = Table(events);
        Assert.Contains("keepalive: INACTIVE", table, StringComparison.Ordinal);
        Assert.Contains("PreserveStream=false", table, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contract: with injection disabled the report must NOT imply the client is
    /// protected — the client's own watchdog is then the only thing standing between
    /// a healthy long-thinking turn and an abort, and the report has to say so.
    /// </summary>
    [Fact]
    public void Report_states_when_keepalive_injection_is_off()
    {
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        var budgets = Budgets(firstByteSeconds: 900, streamIdleSeconds: 600);
        budgets.KeepAliveIntervalSeconds = 0;
        TimeoutBudgetReport.Emit(budgets, log, path);

        var table = Table(events);
        Assert.Contains("keepalive: OFF", table, StringComparison.Ordinal);
    }

    private static (List<RecordedEvent> Events, ILogger Log) Recorder()
    {
        var provider = new RecordingLoggerProvider();
        return (provider.Events, provider.CreateLogger("test"));
    }

    private static string WriteSettings(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string BridgePointedSettings(string? streamIdleMs, string? requestMs)
    {
        var keys = new List<string> { "\"ANTHROPIC_BASE_URL\": \"http://localhost:8765/cc\"" };
        if (streamIdleMs is not null)
        {
            keys.Add($"\"{ClaudeCodeTimeoutPolicy.StreamIdleKey}\": \"{streamIdleMs}\"");
        }
        if (requestMs is not null)
        {
            keys.Add($"\"{ClaudeCodeTimeoutPolicy.RequestTimeoutKey}\": \"{requestMs}\"");
        }
        return "{ \"env\": { " + string.Join(", ", keys) + " } }";
    }

    private static List<RecordedEvent> Warnings(List<RecordedEvent> events) =>
        events.FindAll(e => e.Level == LogLevel.Warning);

    /// <summary>The report is a single Information event holding the whole table.</summary>
    private static string Table(List<RecordedEvent> events) =>
        events.Find(e => e.Message.Contains("what ends a turn", StringComparison.OrdinalIgnoreCase))?.Message
        ?? throw new Xunit.Sdk.XunitException(
            "no timeout table was emitted; got: "
            + string.Join(" | ", events.ConvertAll(e => e.Message)));

    /// <summary>
    /// One row of the table by its phase label. Assertions about a phase must be
    /// scoped to its own row: the table repeats units across rows, so a
    /// table-wide substring check can pass on a value that came from a different
    /// phase entirely.
    /// </summary>
    private static string Row(string table, string phase) =>
        Array.Find(
            table.Split('\n'),
            l => l.TrimStart().StartsWith(phase, StringComparison.Ordinal))
        ?? throw new Xunit.Sdk.XunitException($"no '{phase}' row in table:\n{table}");

    // ---- Requirement: effective end-to-end timeout is reported at startup ----

    [Fact]
    public void Client_outlasting_the_bridge_produces_a_report_and_no_warning()
    {
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        Assert.NotEmpty(events);
        Assert.Empty(Warnings(events));
    }

    [Fact]
    public void An_unreadable_client_file_still_reports_and_never_warns()
    {
        // A bridge is perfectly usable by a client configured some other way; that
        // is unknown, not misconfigured, so it must not produce a warning.
        var missing = Path.Combine(
            Path.GetTempPath(), "cb-absent-" + Guid.NewGuid().ToString("N"), "settings.json");
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(900, 600), log, missing);

        Assert.NotEmpty(events);
        Assert.Empty(Warnings(events));
        Assert.Contains(events, e => e.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unreadable_client_file_makes_the_effective_bound_unknown_not_the_bridge_budgets()
    {
        // Contract: with the client's bound unknown, the EFFECTIVE bound is unknown
        // too. Claiming it equals the bridge's budgets would falsely reassure an
        // operator whose separately-configured client holds a shorter watchdog —
        // precisely the person this report exists to protect.
        var missing = Path.Combine(
            Path.GetTempPath(), "cb-absent-" + Guid.NewGuid().ToString("N"), "settings.json");
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(900, 600), log, missing);

        var table = Table(events);
        // The bridge's own budgets are still stated (900 s renders as 15m)...
        Assert.Contains("15m", table, StringComparison.Ordinal);
        // ...but the client side must read as unknown, never folded into a
        // confident bound.
        Assert.Contains("unknown", table, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_budget_larger_than_the_client_can_honor_warns_that_config_cannot_fix_it()
    {
        // Contract: above the client's ceiling the derived value is capped BELOW the
        // budget, so the client fires first and re-running the config command writes
        // the same insufficient value. The operator must be told to lower the budget
        // rather than handed a remedy that provably cannot work.
        var overCeilingSeconds = (ClaudeCodeTimeoutPolicy.StreamIdleMaxMs / 1000) + 600;
        var path = WriteSettings(BridgePointedSettings(
            ClaudeCodeTimeoutPolicy.StreamIdleMsFor(overCeilingSeconds).ToString(),
            ClaudeCodeTimeoutPolicy.RequestTimeoutMs().ToString()));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(firstByteSeconds: 900, streamIdleSeconds: overCeilingSeconds), log, path);

        var warnings = Warnings(events);
        Assert.NotEmpty(warnings);
        var text = string.Join("\n", warnings.ConvertAll(w => w.Message));
        Assert.Contains("lower the budget", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unsatisfiable_budget_produces_one_actionable_warning_not_two()
    {
        // Above the client ceiling the undercut is unavoidable, so emitting the
        // generic "raise the client value / re-run config" advice alongside the
        // "lower the budget" advice would hand the operator two contradictory
        // instructions — and the first one is impossible to carry out.
        var overCeilingSeconds = (ClaudeCodeTimeoutPolicy.StreamIdleMaxMs / 1000) + 600;
        var path = WriteSettings(BridgePointedSettings(
            ClaudeCodeTimeoutPolicy.StreamIdleMsFor(overCeilingSeconds).ToString(),
            ClaudeCodeTimeoutPolicy.RequestTimeoutMs().ToString()));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(firstByteSeconds: 900, streamIdleSeconds: overCeilingSeconds), log, path);

        var warning = Assert.Single(Warnings(events));
        Assert.Contains("lower the budget", warning.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void A_budget_within_the_client_ceiling_does_not_warn_about_the_ceiling()
    {
        var path = WriteSettings(BridgePointedSettings(
            ClaudeCodeTimeoutPolicy.StreamIdleMsFor(600).ToString(),
            ClaudeCodeTimeoutPolicy.RequestTimeoutMs().ToString()));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        Assert.Empty(Warnings(events));
    }

    [Fact]
    public void A_disabled_budget_is_reported_as_no_bound_and_is_not_the_minimum()
    {
        // Zero means "no bound", not "zero milliseconds" — reporting it as the
        // shortest bound would be exactly backwards.
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 0, streamIdleSeconds: 0), log, path);

        // Scoped to the idle row's WINNER, not a substring of the whole table: both
        // "no bound" and the client's 15m appear in other cells regardless, so a
        // table-wide Contains would stay green even if the arrow wrongly reported
        // 0s or "no bound" — i.e. it would assert nothing about the exclusion.
        var idle = Row(Table(events), "idle gap");
        Assert.EndsWith("-> 15m", idle.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("no bound", idle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bounds_are_reported_per_phase_not_as_one_global_minimum()
    {
        // These bounds do not compete over the same interval: headers arriving
        // disarm the first-byte timer, and the stream-idle budgets then govern each
        // silent gap. A single minimum would report 60s here even though the real
        // exposure after headers is 600s — confidently wrong.
        var path = WriteSettings(BridgePointedSettings("900000", "3600000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 60, streamIdleSeconds: 600), log, path);

        // Each phase keeps its own row, asserted on that row's WINNER: 1m ends the
        // header wait, 10m ends a silent gap. Collapsing them would lose the 10m
        // exposure. Scoped per row because "1m" is also a substring of "15m" and
        // "10m", so a table-wide check would pass on values from other phases.
        var table = Table(events);
        Assert.EndsWith("-> 1m", Row(table, "first byte").TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("-> 10m", Row(table, "idle gap").TrimEnd(), StringComparison.Ordinal);

        // No line may present a single number as THE bound for the whole turn.
        Assert.DoesNotContain(
            events,
            e => e.Message.Contains("effective end-to-end bound", StringComparison.OrdinalIgnoreCase)
                 && !e.Message.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void A_disabled_budget_cannot_be_undercut()
    {
        // Nothing can fire "before" a bound that does not exist.
        var path = WriteSettings(BridgePointedSettings("1000", "1000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 0, streamIdleSeconds: 0), log, path);

        Assert.Empty(Warnings(events));
    }

    // ---- Requirement: a client watchdog that would fire first is warned about ----

    [Fact]
    public void A_client_stream_idle_shorter_than_the_bridge_budget_warns()
    {
        var path = WriteSettings(BridgePointedSettings("60000", "1200000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        var warning = Assert.Single(Warnings(events));
        Assert.Contains(ClaudeCodeTimeoutPolicy.StreamIdleKey, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_client_request_timeout_is_reported_as_a_residual_bound_not_a_warning()
    {
        // API_TIMEOUT_MS is a wall-clock cap while the bridge's budgets bound
        // INACTIVITY, so it can always be crossed first — warning on that would fire
        // on correct configurations and there is no value that would silence it.
        // The honest surface is to state it as a residual bound the bridge cannot
        // out-wait, which is what this asserts.
        var path = WriteSettings(BridgePointedSettings("900000", "60000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        // No warning: it cannot be "fixed", so warning would be noise on a correct
        // configuration. It still gets its own table row, so the operator can see
        // the cap that will end the turn.
        Assert.Empty(Warnings(events));

        var table = Table(events);
        Assert.Contains("whole request", table, StringComparison.Ordinal);
        Assert.Contains("1m", table, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_client_key_warns_rather_than_being_passed_over_as_unknown()
    {
        // The corrected design: absence is the DEFAULT state of every install
        // configured before this feature, and the bridge's own 1M-context key is
        // what selects the shorter client default. Staying quiet here would leave
        // the common case unprotected.
        var path = WriteSettings(BridgePointedSettings(streamIdleMs: null, requestMs: "1200000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        var warning = Assert.Single(Warnings(events));
        Assert.Contains(ClaudeCodeTimeoutPolicy.StreamIdleKey, warning.Message, StringComparison.Ordinal);
        // Names the bound that actually applies in the key's absence.
        Assert.Contains(
            ClaudeCodeTimeoutPolicy.AbsentStreamIdleDefaultMs.ToString(),
            warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warning_states_both_remedies()
    {
        // Some operators manage settings.json by other means (dotfiles, MDM, a
        // shared team config), so naming only the bridge's own command is not
        // enough — the env var and its minimum value must be actionable too.
        var path = WriteSettings(BridgePointedSettings("60000", "1200000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        var warning = Assert.Single(Warnings(events));
        Assert.Contains("config claude-code", warning.Message, StringComparison.Ordinal);
        Assert.Contains(ClaudeCodeTimeoutPolicy.StreamIdleKey, warning.Message, StringComparison.Ordinal);
        // The minimum the operator must set it to (the bridge budget, in ms).
        Assert.Contains("600000", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_equal_client_value_does_not_warn()
    {
        // Equal is not undercutting — it merely has no margin.
        var path = WriteSettings(BridgePointedSettings("600000", "900000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(firstByteSeconds: 900, streamIdleSeconds: 600), log, path);

        Assert.Empty(Warnings(events));
    }

    [Fact]
    public void The_report_states_that_client_values_apply_on_next_start()
    {
        // Claude Code reads env at process start, so a freshly-written value does
        // nothing for the session already running. The report must not imply it did.
        var path = WriteSettings(BridgePointedSettings("900000", "1200000"));
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(Budgets(900, 600), log, path);

        Assert.Contains("next restart", Table(events), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_values_the_config_command_writes_never_trip_the_warning()
    {
        // End-to-end consistency between the two halves of the change: whatever the
        // budgets are, what `config claude-code` writes must satisfy this report.
        foreach (var (firstByte, streamIdle) in new[] { (240, 60), (900, 600), (60, 30), (3600, 1800) })
        {
            var path = WriteSettings(BridgePointedSettings(
                ClaudeCodeTimeoutPolicy.StreamIdleMsFor(streamIdle).ToString(),
                ClaudeCodeTimeoutPolicy.RequestTimeoutMs().ToString()));
            var (events, log) = Recorder();

            TimeoutBudgetReport.Emit(Budgets(firstByte, streamIdle), log, path);

            Assert.Empty(Warnings(events));
        }
    }
}
