using CopilotBridge.Cli.Hosting.ClientConfig;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for the timeout-budget-report capability's derivation and
/// reader seams. Asserted from the spec — "the client bound is not shorter than
/// the bridge's", "a disabled budget yields the clamped maximum", "unknown
/// applies only when the file could not be read or does not concern this
/// bridge" — not read back from the implementation.
/// </summary>
public class ClaudeCodeTimeoutPolicyTests
{
    // ---- Derivation (spec: Claude Code long-thinking timeout environment) ----

    [Fact]
    public void Written_stream_idle_outlasts_the_bridge_stream_idle_budget()
    {
        // The whole point of writing the key: the client must not fire first.
        foreach (var budgetSeconds in new[] { 1, 60, 240, 600, 900 })
        {
            var writtenMs = ClaudeCodeTimeoutPolicy.StreamIdleMsFor(budgetSeconds);
            Assert.True(
                writtenMs > budgetSeconds * 1000,
                $"budget {budgetSeconds}s -> written {writtenMs}ms must exceed the budget");
        }
    }

    [Fact]
    public void Written_request_timeout_outlasts_the_bridge_first_byte_budget()
    {
        foreach (var budgetSeconds in new[] { 1, 60, 240, 900, 1800 })
        {
            var writtenMs = ClaudeCodeTimeoutPolicy.RequestTimeoutMsFor(budgetSeconds);
            Assert.True(
                writtenMs > budgetSeconds * 1000,
                $"budget {budgetSeconds}s -> written {writtenMs}ms must exceed the budget");
        }
    }

    [Fact]
    public void Raising_a_budget_raises_the_written_value()
    {
        // Drives the drift contract: an operator who raises a budget and re-runs
        // config must get a larger client value, otherwise the bridge silently
        // stops being the binding bound.
        Assert.True(
            ClaudeCodeTimeoutPolicy.StreamIdleMsFor(600) > ClaudeCodeTimeoutPolicy.StreamIdleMsFor(300));
        Assert.True(
            ClaudeCodeTimeoutPolicy.RequestTimeoutMsFor(900) > ClaudeCodeTimeoutPolicy.RequestTimeoutMsFor(300));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_disabled_budget_yields_the_clamped_maximum(int disabledBudget)
    {
        // "No bound" cannot be outlasted by any finite client value, so the
        // policy writes the largest value that actually takes effect.
        Assert.Equal(
            ClaudeCodeTimeoutPolicy.StreamIdleMaxMs,
            ClaudeCodeTimeoutPolicy.StreamIdleMsFor(disabledBudget));
        Assert.Equal(
            ClaudeCodeTimeoutPolicy.RequestTimeoutMaxMs,
            ClaudeCodeTimeoutPolicy.RequestTimeoutMsFor(disabledBudget));
    }

    [Fact]
    public void Written_stream_idle_never_exceeds_the_clients_hard_cap()
    {
        // Above the cap the client silently reduces the value, so what the bridge
        // writes would not be what applies — and the report would compare against
        // a number the client never honored.
        foreach (var budgetSeconds in new[] { 1_500, 3_600, 86_400, int.MaxValue })
        {
            Assert.Equal(
                ClaudeCodeTimeoutPolicy.StreamIdleMaxMs,
                ClaudeCodeTimeoutPolicy.StreamIdleMsFor(budgetSeconds));
        }
    }

    [Fact]
    public void A_huge_budget_does_not_overflow_into_a_short_timeout()
    {
        // int arithmetic on (budget + margin) * 1000 would wrap negative and
        // produce a *shorter* client bound than the bridge's — the exact failure
        // this capability exists to prevent.
        Assert.True(ClaudeCodeTimeoutPolicy.StreamIdleMsFor(int.MaxValue) > 0);
        Assert.True(ClaudeCodeTimeoutPolicy.RequestTimeoutMsFor(int.MaxValue) > 0);
        Assert.True(ClaudeCodeTimeoutPolicy.StreamIdleMsFor(int.MaxValue - 1) > 0);
    }

    // ---- Reader (spec: best-effort, non-fatal; "unknown" is narrow) ----

    private static string WriteSettings(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Missing_settings_file_reads_as_unknown_and_does_not_throw()
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-absent-" + Guid.NewGuid().ToString("N"), "settings.json");

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.False(snap.Readable);
        Assert.True(snap.StreamIdle.IsUnknown);
        Assert.True(snap.RequestTimeout.IsUnknown);
        Assert.NotNull(snap.Reason);
    }

    [Fact]
    public void Malformed_settings_file_reads_as_unknown_and_does_not_throw()
    {
        var path = WriteSettings("{ this is not json ");

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.False(snap.Readable);
        Assert.True(snap.StreamIdle.IsUnknown);
    }

    [Fact]
    public void Settings_not_pointed_at_this_bridge_read_as_unknown()
    {
        // A config aimed at some other Anthropic-compatible endpoint is unrelated,
        // not a misconfigured bridge client — warning about it would be noise.
        var path = WriteSettings("""
            { "env": { "ANTHROPIC_BASE_URL": "https://api.anthropic.com" } }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.False(snap.Readable);
        Assert.True(snap.StreamIdle.IsUnknown);
    }

    [Fact]
    public void A_bridge_pointed_file_missing_a_key_is_known_bad_not_unknown()
    {
        // The heart of the corrected design: absence is NOT unknown. The bridge's
        // own _CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL makes the client fall back
        // to its 180 s first-party bound, so the bridge knows the number.
        var path = WriteSettings("""
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:8765/cc" } }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.True(snap.Readable);
        Assert.False(snap.StreamIdle.IsUnknown);
        Assert.False(snap.StreamIdle.IsExplicit);
        Assert.Equal(ClaudeCodeTimeoutPolicy.AbsentStreamIdleDefaultMs, snap.StreamIdle.EffectiveMs);
        Assert.False(snap.RequestTimeout.IsExplicit);
        Assert.Equal(ClaudeCodeTimeoutPolicy.AbsentRequestTimeoutDefaultMs, snap.RequestTimeout.EffectiveMs);
    }

    [Fact]
    public void Stored_values_are_read_as_explicit()
    {
        var path = WriteSettings("""
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "900000",
                "API_TIMEOUT_MS": "1200000"
              }
            }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.True(snap.Readable);
        Assert.True(snap.StreamIdle.IsExplicit);
        Assert.Equal(900_000, snap.StreamIdle.EffectiveMs);
        Assert.True(snap.RequestTimeout.IsExplicit);
        Assert.Equal(1_200_000, snap.RequestTimeout.EffectiveMs);
    }

    [Theory]
    [InlineData("\"not-a-number\"")]
    [InlineData("\"\"")]
    [InlineData("\"0\"")]
    [InlineData("\"-5\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void An_unusable_stored_value_falls_back_to_the_clients_default(string literal)
    {
        // Claude Code's own parse yields no usable number from these, so its
        // built-in default is what actually applies — the report must say so
        // rather than quoting a value that has no effect.
        var path = WriteSettings($$"""
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                "CLAUDE_STREAM_IDLE_TIMEOUT_MS": {{literal}}
              }
            }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.True(snap.Readable);
        Assert.False(snap.StreamIdle.IsExplicit);
        Assert.Equal(ClaudeCodeTimeoutPolicy.AbsentStreamIdleDefaultMs, snap.StreamIdle.EffectiveMs);
    }

    [Fact]
    public void A_numeric_json_value_is_tolerated()
    {
        // Hand-edited files sometimes hold a number where Claude Code expects a
        // string; the reader must not crash or silently discard it.
        var path = WriteSettings("""
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                "CLAUDE_STREAM_IDLE_TIMEOUT_MS": 900000
              }
            }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.True(snap.StreamIdle.IsExplicit);
        Assert.Equal(900_000, snap.StreamIdle.EffectiveMs);
    }

    [Fact]
    public void A_bridge_on_another_port_still_counts_as_this_bridges_client()
    {
        var path = WriteSettings("""
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:18765/cc" } }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(path);

        Assert.True(snap.Readable);
    }
}
