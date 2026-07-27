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
/// <remarks>
/// Joins the client-config collection because the scope-precedence test mutates
/// the process-wide <see cref="System.Environment.CurrentDirectory"/> (repo-scoped
/// settings resolve relative to it), which would redirect any test running in
/// parallel that resolves a relative path.
/// </remarks>
[Collection(ClientConfigCollection.Name)]
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
            var writtenMs = ClaudeCodeTimeoutPolicy.RequestTimeoutMs();
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
        // stops being the binding bound. Only the stream-idle key is derived from a
        // budget — API_TIMEOUT_MS is a fixed maximum by design, since no finite
        // wall-clock value can outlast an inactivity budget.
        Assert.True(
            ClaudeCodeTimeoutPolicy.StreamIdleMsFor(600) > ClaudeCodeTimeoutPolicy.StreamIdleMsFor(300));
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
            ClaudeCodeTimeoutPolicy.RequestTimeoutMs());
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
        Assert.True(ClaudeCodeTimeoutPolicy.RequestTimeoutMs() > 0);
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
        // The heart of the corrected design: absence is NOT unknown — the bridge knows
        // which default applies. This file has no first-party flag, so the 300 s
        // non-first-party floor is the real bound (see the first-party test below).
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

    [Fact]
    public void Repo_scoped_settings_are_reported_as_outside_what_the_bridge_can_see()
    {
        // Contract: repo-scoped settings live in the CLAUDE SESSION's project dir,
        // which the bridge cannot know — one long-running bridge serves sessions in
        // many repos. Resolving "./.claude/settings.local.json" against the BRIDGE's
        // own working directory would claim an unrelated file is authoritative, so
        // the reader must NOT do it. It reads global only; the report discloses the
        // gap.
        var dir = Path.Combine(Path.GetTempPath(), "cb-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(
            Path.Combine(dir, ".claude", "settings.local.json"),
            """
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "1000"
              }
            }
            """);

        var old = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;

            var snap = ClaudeCodeTimeoutReader.Read();

            // Whatever it read, it must not be the CWD-relative repo file.
            Assert.NotEqual(
                Path.Combine(dir, ".claude", "settings.local.json"),
                snap.SettingsPath);
            Assert.Equal(ClaudeCodeTimeoutReader.DefaultSettingsPath, snap.SettingsPath);
        }
        finally
        {
            Environment.CurrentDirectory = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- Absent-key default depends on the first-party flag ----

    [Fact]
    public void Absent_key_uses_the_first_party_default_only_when_that_flag_is_set()
    {
        // The 180 s bound is selected BY _CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL,
        // which the bridge writes for the 1M window. Applying it unconditionally
        // would report a bound that does not exist for a hand-managed config.
        var firstParty = WriteSettings("""
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "1"
              }
            }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(firstParty);

        Assert.True(snap.Readable);
        Assert.False(snap.StreamIdle.IsExplicit);
        Assert.Equal(
            ClaudeCodeTimeoutPolicy.AbsentStreamIdleFirstPartyDefaultMs,
            snap.StreamIdle.EffectiveMs);
    }

    [Fact]
    public void Absent_key_without_the_first_party_flag_uses_the_longer_default()
    {
        var handManaged = WriteSettings("""
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:8765/cc" } }
            """);

        var snap = ClaudeCodeTimeoutReader.Read(handManaged);

        Assert.True(snap.Readable);
        Assert.False(snap.StreamIdle.IsExplicit);
        Assert.Equal(
            ClaudeCodeTimeoutPolicy.AbsentStreamIdleDefaultMs, snap.StreamIdle.EffectiveMs);
        Assert.True(
            ClaudeCodeTimeoutPolicy.AbsentStreamIdleDefaultMs
            > ClaudeCodeTimeoutPolicy.AbsentStreamIdleFirstPartyDefaultMs);
    }

    // ---- API_TIMEOUT_MS is a residual wall-clock bound, not a derived one ----

    [Fact]
    public void Request_timeout_is_the_fixed_maximum_not_derived_from_budgets()
    {
        // No finite wall-clock value can outlast inactivity budgets: a healthy turn
        // that keeps emitting has no total duration, and a stalled one can spend
        // first-byte + stream-idle + ... before any bridge timer fires. Deriving it
        // (from first-byte, or from Math.Max of both) only moved the threshold while
        // still implying a guarantee that cannot exist — e.g. first-byte 900 +
        // stream-idle 600 derived 1200 s while the bridge could legitimately take
        // ~1500 s. So the policy writes its maximum and the report calls it residual.
        Assert.Equal(ClaudeCodeTimeoutPolicy.RequestTimeoutMaxMs, ClaudeCodeTimeoutPolicy.RequestTimeoutMs());
    }

    [Fact]
    public void Request_timeout_still_far_exceeds_the_clients_own_fallback_default()
    {
        // It is written for a real reason: it also bounds each attempt of Claude
        // Code's non-streaming recovery request, whose 300 s client default was half
        // of the original failure. That path IS a bounded single response, so raising
        // the ceiling genuinely helps even though it guarantees nothing for a stream.
        Assert.True(
            ClaudeCodeTimeoutPolicy.RequestTimeoutMs()
            > ClaudeCodeTimeoutPolicy.AbsentRequestTimeoutDefaultMs);
    }
}