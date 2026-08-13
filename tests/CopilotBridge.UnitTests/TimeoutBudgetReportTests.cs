using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.ClientConfig;
using CopilotBridge.Cli.Hosting.Options;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>Golden and edge contracts for the compact read-only startup inventory.</summary>
public class TimeoutBudgetReportTests
{
    [Fact]
    public void Default_global_configs_render_the_approved_inventory_exactly()
    {
        var claude = Write("settings.json", """
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:8765/cc" } }
            """);
        var codex = Write("config.toml", """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "copilot-bridge"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            """);
        var (events, log) = Recorder();
        var budgets = new UpstreamTimeoutOptions
        {
            FirstByteTimeoutSeconds = 240,
            StreamIdleTimeoutSeconds = 240,
            KeepAliveIntervalSeconds = 15,
        };

        TimeoutBudgetReport.Emit(
            budgets,
            log,
            claude,
            codexConfigPathOverride: codex,
            retryOptions: new UpstreamRetryOptions { MaxRetries = 2 },
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var expected = $$"""
            Timeouts (observed configuration; startup does not rewrite values):
              Bridge — appsettings.json
                upstream response headers  4m / send attempt
                upstream SSE event gap     4m / parsed event gap
                downstream keepalive       15s, after first upstream event
                network retries            2
                buffered body              no limit after headers
              Claude Code — {{claude}} (global only)
                SSE event idle             unset -> 5m*
                SSE byte idle              unset -> 5m*
                request timeout            unset -> normal 10m*; after stream error 5m*
                retries                    not visible at bridge startup
              Codex — {{codex}} (global only)
                SSE event idle             unset -> 5m* / parsed event
                request retries            unset -> 4*
                stream retries             unset -> 5*
                whole request              no limit
              note: timeouts apply per attempt; a retry starts a new attempt, so there is no fixed whole-turn limit
              scope: global client configs only; project/profile/CLI/env overrides are not included
              * = client built-in default
            """.ReplaceLineEndings();

        Assert.Equal(expected, Inventory(events).ReplaceLineEndings());
    }

    [Fact]
    public void Explicit_values_show_human_duration_and_client_floor_without_milliseconds_noise()
    {
        var claude = Write("settings.json", """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "60000",
              "API_TIMEOUT_MS": "900000"
            } }
            """);
        var codex = Write("config.toml", """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "copilot-bridge"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            stream_idle_timeout_ms = 0
            request_max_retries = 0
            stream_max_retries = 0
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 240, 15), log, claude, codexConfigPathOverride: codex,
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var inventory = Inventory(events);
        Assert.Contains("configured 1m -> effective 5m (client floor)", inventory);
        Assert.Contains("request timeout            15m, explicit", inventory);
        Assert.Contains("SSE event idle             0s, explicit", inventory);
        Assert.Contains("request retries            0, explicit", inventory);
        Assert.DoesNotContain("300000ms", inventory);
        Assert.DoesNotContain("900000ms", inventory);
        Assert.DoesNotContain("up to", inventory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Present_invalid_client_values_are_not_rendered_as_absent_defaults()
    {
        var claude = Write("settings.json", """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": false,
              "API_TIMEOUT_MS": {}
            } }
            """);
        var codex = Write("config.toml", """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "copilot-bridge"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            stream_idle_timeout_ms = "300000"
            request_max_retries = -1
            stream_max_retries = "5"
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 240, 15),
            log,
            claude,
            codexConfigPathOverride: codex,
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var inventory = Inventory(events);
        Assert.Contains("SSE event idle             invalid (false)", inventory);
        Assert.Contains("request timeout            invalid ({})", inventory);
        Assert.Contains("SSE event idle             invalid (\"300000\") / parsed event", inventory);
        Assert.Contains("request retries            invalid (-1)", inventory);
        Assert.Contains("stream retries             invalid (\"5\")", inventory);
    }

    [Fact]
    public void Unverified_claude_version_prints_configured_value_and_unknown_effect_instead_of_guessing()
    {
        var claude = Write("settings.json", """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "60000"
            } }
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 240, 15),
            log,
            claude,
            codexConfigPathOverride: Missing("config.toml"),
            claudeVersionOverride: "2.1.999");

        var inventory = Inventory(events);
        Assert.Contains(
            "configured 1m; Claude Code 2.1.999 has not been verified; effective behavior is version-dependent",
            inventory);
        Assert.DoesNotContain("configured 1m -> effective 5m", inventory);
    }

    [Fact]
    public void Codex_provider_with_wrong_name_is_not_treated_as_the_active_bridge_baseline()
    {
        var codex = Write("config.toml", """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "lookalike"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            stream_idle_timeout_ms = 900000
            """);

        var snapshot = CodexTimeoutReader.Read(codex);

        Assert.False(snapshot.Readable);
        Assert.Equal(ClientValueSource.Unknown, snapshot.EventIdle.Source);
        Assert.Contains("name is lookalike, not copilot-bridge", snapshot.Reason);
    }

    [Fact]
    public void Equal_unprotected_deadline_warns_that_it_is_a_race()
    {
        var claude = Write("settings.json", """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "300000"
            } }
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 300, 0),
            log,
            claude,
            codexConfigPathOverride: Missing("config.toml"),
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var warning = Assert.Single(events, e => e.Level == LogLevel.Warning);
        Assert.Contains("races the bridge", warning.Message);
        Assert.Contains("CLAUDE_STREAM_IDLE_TIMEOUT_MS", warning.Message);
        Assert.Contains("Pipeline:UpstreamTimeout:StreamIdleTimeoutSeconds", warning.Message);
        Assert.DoesNotContain("config claude-code", warning.Message);
    }

    [Fact]
    public void Effective_keepalive_does_not_hide_the_unprotected_first_event_gap()
    {
        var claude = Write("settings.json", """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "300000"
            } }
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 600, 15),
            log,
            claude,
            codexConfigPathOverride: Missing("config.toml"),
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var warning = Assert.Single(events, e => e.Level == LogLevel.Warning);
        Assert.Contains("keepalive starts only after that event", warning.Message);
    }

    [Fact]
    public void Whole_response_buffering_exposes_shorter_client_watchdog()
    {
        var claude = Write("settings.json", """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "300000"
            } }
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 600, 15),
            log,
            claude,
            codexConfigPathOverride: Missing("config.toml"),
            wholeResponseBuffering: true,
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        Assert.Single(events, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Unreadable_clients_do_not_suppress_bridge_or_sibling_facts()
    {
        var codex = Write("config.toml", """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "copilot-bridge"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            """);
        var (events, log) = Recorder();

        TimeoutBudgetReport.Emit(
            Budgets(240, 240, 15),
            log,
            settingsPathOverride: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"),
            codexConfigPathOverride: codex,
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var inventory = Inventory(events);
        Assert.Contains("Bridge — appsettings.json", inventory);
        Assert.Contains($"Codex — {codex} (global only)", inventory);
        Assert.Contains("SSE event idle             unset -> 5m*", inventory);
        Assert.Contains("Claude Code —", inventory);
        Assert.Contains("unknown", inventory);
        Assert.Contains("settings file does not exist", inventory);
    }

    [Fact]
    public void Disabled_bridge_values_are_not_replaced_by_finite_fallbacks()
    {
        var (events, log) = Recorder();
        TimeoutBudgetReport.Emit(
            Budgets(0, -1, 0),
            log,
            claudeVersionOverride: ClaudeCodeTimeoutPolicy.VerifiedClientVersion);

        var inventory = Inventory(events);
        Assert.Contains("upstream response headers  disabled (0s)", inventory);
        Assert.Contains("upstream SSE event gap     disabled (-1s)", inventory);
        Assert.Contains("downstream keepalive       disabled (0s)", inventory);
    }

    [Fact]
    public void Startup_buffering_fact_matches_the_actual_active_detector_modes()
    {
        Assert.False(BridgeStartupHostedService.WholeResponseBufferingActive(
            new ResponseLeakGuardOptions { Enabled = false, PreserveStream = false },
            new ToolInputValidationOptions
            {
                Enabled = true,
                PreserveStream = false,
                MalformedJsonAction = ToolInputAction.Observe,
                SchemaViolationAction = ToolInputAction.Observe,
            }));

        Assert.True(BridgeStartupHostedService.WholeResponseBufferingActive(
            new ResponseLeakGuardOptions { Enabled = true, PreserveStream = false },
            new ToolInputValidationOptions { Enabled = false }));

        Assert.True(BridgeStartupHostedService.WholeResponseBufferingActive(
            new ResponseLeakGuardOptions { Enabled = false },
            new ToolInputValidationOptions
            {
                Enabled = true,
                PreserveStream = false,
                MalformedJsonAction = ToolInputAction.AbortOverloaded,
                SchemaViolationAction = ToolInputAction.Observe,
            }));

        Assert.False(BridgeStartupHostedService.WholeResponseBufferingActive(
            new ResponseLeakGuardOptions { Enabled = false },
            new ToolInputValidationOptions
            {
                Enabled = true,
                PreserveStream = true,
                MalformedJsonAction = ToolInputAction.AbortOverloaded,
            }));
    }

    [Theory]
    [InlineData(15_000, "15s")]
    [InlineData(300_000, "5m")]
    [InlineData(1_500, "1.5s")]
    [InlineData(long.MaxValue, "9223372036854775.807s")]
    public void Human_duration_format_is_exact(long milliseconds, string expected) =>
        Assert.Equal(expected, TimeoutBudgetReport.FormatDuration(milliseconds));

    [Fact]
    public void Human_duration_format_is_culture_invariant()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("1.5s", TimeoutBudgetReport.FormatDuration(1_500));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    private static UpstreamTimeoutOptions Budgets(int headers, int idle, int keepalive) => new()
    {
        FirstByteTimeoutSeconds = headers,
        StreamIdleTimeoutSeconds = idle,
        KeepAliveIntervalSeconds = keepalive,
    };

    private static string Inventory(IEnumerable<RecordedEvent> events) =>
        Assert.Single(events, e => e.Level == LogLevel.Information).Message;

    private static (List<RecordedEvent> Events, ILogger Log) Recorder()
    {
        var provider = new RecordingLoggerProvider();
        return (provider.Events, provider.CreateLogger("test"));
    }

    private static string Write(string fileName, string content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-timeout-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Missing(string fileName) =>
        Path.Combine(Path.GetTempPath(), "cb-timeout-report-" + Guid.NewGuid().ToString("N"), fileName);
}
