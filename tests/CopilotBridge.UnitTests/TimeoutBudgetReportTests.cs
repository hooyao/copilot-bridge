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

        var text = string.Join("\n", events.ConvertAll(e => e.Message));
        // The bridge's own budgets are still stated...
        Assert.Contains("900", text, StringComparison.Ordinal);
        // ...but the client side must be called UNKNOWN, never folded into a
        // confident bound.
        var unknownLine = events.Find(e =>
            e.Message.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(unknownLine);
        Assert.Contains("client", unknownLine!.Message, StringComparison.OrdinalIgnoreCase);
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

        var text = string.Join("\n", events.ConvertAll(e => e.Message));
        Assert.Contains("no bound", text, StringComparison.OrdinalIgnoreCase);

        // With both bridge budgets disabled, the per-phase line must show them as
        // imposing no bound while still naming the client's own idle value.
        var phases = events.Find(e => e.Message.Contains("per silent gap", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(phases);
        Assert.Contains("no bound", phases!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client", phases.Message, StringComparison.OrdinalIgnoreCase);
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

        var phases = events.Find(e => e.Message.Contains("per silent gap", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(phases);
        Assert.Contains("60s", phases!.Message, StringComparison.Ordinal);
        Assert.Contains("600s", phases.Message, StringComparison.Ordinal);

        // No line may present a single number as THE bound.
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

        Assert.Empty(Warnings(events));

        var residual = events.Find(e =>
            e.Message.Contains("wall-clock cap", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(residual);
        Assert.Contains(
            ClaudeCodeTimeoutPolicy.RequestTimeoutKey, residual!.Message, StringComparison.Ordinal);
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

        var text = string.Join("\n", events.ConvertAll(e => e.Message));
        Assert.Contains("next start", text, StringComparison.OrdinalIgnoreCase);
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
