using CopilotBridge.Cli.Hosting.ClientConfig;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>Contract tests for read-only Claude timeout interpretation.</summary>
[Collection(ClientConfigCollection.Name)]
public class ClaudeCodeTimeoutPolicyTests
{
    [Fact]
    public void Missing_keys_report_distinct_source_confirmed_defaults()
    {
        var snapshot = Read("""
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:8765/cc" } }
            """);

        Assert.Equal(300_000, snapshot.EventIdle.EffectiveMs);
        Assert.Equal(300_000, snapshot.ByteIdle.EffectiveMs);
        Assert.Equal(600_000, snapshot.NormalRequest.EffectiveMs);
        Assert.Equal(300_000, snapshot.AfterStreamErrorRequest.EffectiveMs);
        Assert.All(
            new[] { snapshot.EventIdle, snapshot.ByteIdle, snapshot.NormalRequest, snapshot.AfterStreamErrorRequest },
            value => Assert.Equal(ClientValueSource.BuiltIn, value.Source));
    }

    [Fact]
    public void First_party_assertion_changes_only_absent_byte_idle_default()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "1"
            } }
            """);

        Assert.Equal(300_000, snapshot.EventIdle.EffectiveMs);
        Assert.Equal(180_000, snapshot.ByteIdle.EffectiveMs);
    }

    [Fact]
    public void Explicit_stream_idle_is_not_derived_from_bridge_budget()
    {
        var snapshot = Read(Settings("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "900000"));

        Assert.Equal(900_000, snapshot.EventIdle.ConfiguredMs);
        Assert.Equal(900_000, snapshot.EventIdle.EffectiveMs);
        Assert.Equal(ClientValueSource.Explicit, snapshot.EventIdle.Source);
        Assert.Equal(900_000, snapshot.ByteIdle.EffectiveMs);
        Assert.Equal(ClientValueSource.Inherited, snapshot.ByteIdle.Source);
    }

    [Fact]
    public void Stream_idle_below_client_floor_reports_configured_and_effective()
    {
        var snapshot = Read(Settings("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "60000"));

        Assert.Equal(60_000, snapshot.EventIdle.ConfiguredMs);
        Assert.Equal(300_000, snapshot.EventIdle.EffectiveMs);
        Assert.Equal("client floor", snapshot.EventIdle.Detail);
        Assert.Equal(300_000, snapshot.ByteIdle.EffectiveMs);
    }

    [Theory]
    [InlineData("1000", 10_000, "client floor")]
    [InlineData("2400000", 1_800_000, "client cap")]
    public void Explicit_byte_idle_applies_source_confirmed_floor_or_cap(
        string raw, long expected, string adjustment)
    {
        var snapshot = Read(Settings("CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS", raw));

        Assert.Equal(long.Parse(raw), snapshot.ByteIdle.ConfiguredMs);
        Assert.Equal(expected, snapshot.ByteIdle.EffectiveMs);
        Assert.Equal(adjustment, snapshot.ByteIdle.Detail);
    }

    [Fact]
    public void Explicit_request_timeout_applies_to_both_request_modes()
    {
        var snapshot = Read(Settings("API_TIMEOUT_MS", "900000"));

        Assert.Equal(900_000, snapshot.NormalRequest.EffectiveMs);
        Assert.Equal(900_000, snapshot.AfterStreamErrorRequest.EffectiveMs);
        Assert.Equal(ClientValueSource.Explicit, snapshot.NormalRequest.Source);
    }

    [Theory]
    [InlineData("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "0")]
    [InlineData("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "not-a-number")]
    [InlineData("API_TIMEOUT_MS", "-1")]
    public void Present_invalid_value_is_not_relabelled_as_absent_default(string key, string raw)
    {
        var snapshot = Read(Settings(key, raw));
        var value = key == "API_TIMEOUT_MS" ? snapshot.NormalRequest : snapshot.EventIdle;

        Assert.Equal(ClientValueSource.Invalid, value.Source);
        Assert.Null(value.EffectiveMs);
        Assert.Equal(raw, value.RawValue);
    }

    [Theory]
    [InlineData("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "false")]
    [InlineData("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "{}")]
    [InlineData("API_TIMEOUT_MS", "false")]
    [InlineData("API_TIMEOUT_MS", "[]")]
    public void Present_non_integer_json_value_is_invalid_not_absent(string key, string literal)
    {
        var snapshot = Read($$"""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "{{key}}": {{literal}}
            } }
            """);
        var value = key == ClaudeCodeTimeoutPolicy.RequestTimeoutKey
            ? snapshot.NormalRequest
            : snapshot.EventIdle;

        Assert.Equal(ClientValueSource.Invalid, value.Source);
        Assert.Null(value.EffectiveMs);
        Assert.Equal(literal, value.RawValue);
    }

    [Fact]
    public void Numeric_json_durations_are_read_without_int_overflow_or_string_coercion()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": 21474836480,
              "API_TIMEOUT_MS": 21474836480
            } }
            """);

        Assert.Equal(21_474_836_480L, snapshot.EventIdle.EffectiveMs);
        Assert.Equal(1_800_000, snapshot.ByteIdle.EffectiveMs);
        Assert.Equal(21_474_836_480L, snapshot.NormalRequest.EffectiveMs);
        Assert.Equal(21_474_836_480L, snapshot.AfterStreamErrorRequest.EffectiveMs);
    }

    [Fact]
    public void Explicit_watchdog_disable_is_preserved_as_disabled()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_ENABLE_STREAM_WATCHDOG": "0",
              "CLAUDE_ENABLE_BYTE_WATCHDOG": "false"
            } }
            """);

        Assert.Equal(ClientValueSource.Disabled, snapshot.EventIdle.Source);
        Assert.Equal(ClientValueSource.Disabled, snapshot.ByteIdle.Source);
        Assert.Equal("0", snapshot.StreamWatchdog.RawValue);
        Assert.Equal("false", snapshot.ByteWatchdog.RawValue);
    }

    [Fact]
    public void Disabling_stream_watchdog_does_not_break_independent_byte_idle_inheritance()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "900000",
              "CLAUDE_ENABLE_STREAM_WATCHDOG": "0"
            } }
            """);

        Assert.Equal(ClientValueSource.Disabled, snapshot.EventIdle.Source);
        Assert.Equal(ClientValueSource.Inherited, snapshot.ByteIdle.Source);
        Assert.Equal(900_000, snapshot.ByteIdle.EffectiveMs);
    }

    [Fact]
    public void Invalid_first_party_value_makes_absent_byte_idle_unknown_instead_of_enabling_first_party_mode()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": { "unexpected": true }
            } }
            """);

        Assert.Equal(ClientValueSource.Unknown, snapshot.ByteIdle.Source);
        Assert.Null(snapshot.ByteIdle.EffectiveMs);
        Assert.Contains("invalid _CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL", snapshot.ByteIdle.Detail);
    }

    [Fact]
    public void Invalid_stream_idle_cannot_be_relabelled_as_an_absent_byte_idle_default()
    {
        var snapshot = Read(Settings("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "not-a-number"));

        Assert.Equal(ClientValueSource.Invalid, snapshot.EventIdle.Source);
        Assert.Equal(ClientValueSource.Unknown, snapshot.ByteIdle.Source);
        Assert.Null(snapshot.ByteIdle.EffectiveMs);
        Assert.Contains("cannot inherit invalid", snapshot.ByteIdle.Detail);
    }

    [Fact]
    public void Invalid_watchdog_is_retained_and_makes_effective_idle_unknown()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "900000",
              "CLAUDE_ENABLE_STREAM_WATCHDOG": { "unexpected": true }
            } }
            """);

        Assert.Equal(ClientValueSource.Invalid, snapshot.StreamWatchdog.Source);
        Assert.Equal("{\"unexpected\":true}", snapshot.StreamWatchdog.RawValue);
        Assert.Equal(ClientValueSource.Unknown, snapshot.EventIdle.Source);
        Assert.Contains("invalid CLAUDE_ENABLE_STREAM_WATCHDOG", snapshot.EventIdle.Detail);
    }

    [Fact]
    public void Invalid_dependency_remains_the_reason_when_the_client_version_is_unverified()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_ENABLE_STREAM_WATCHDOG": { "unexpected": true }
            } }
            """, installedVersion: "2.1.999");

        Assert.Equal(ClientValueSource.Unknown, snapshot.EventIdle.Source);
        Assert.Contains("invalid CLAUDE_ENABLE_STREAM_WATCHDOG", snapshot.EventIdle.Detail);
        Assert.DoesNotContain("version-dependent", snapshot.EventIdle.Detail);
    }

    [Fact]
    public void Retry_value_is_retained_without_claiming_effective_precedence()
    {
        var snapshot = Read(Settings("CLAUDE_CODE_MAX_RETRIES", "7"));
        Assert.Equal("7", snapshot.Retry.RawValue);
        Assert.Equal(ClientValueSource.Explicit, snapshot.Retry.Source);
    }

    [Fact]
    public void Invalid_retry_value_is_retained_instead_of_becoming_absent()
    {
        var snapshot = Read("""
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_CODE_MAX_RETRIES": { "value": 7 }
            } }
            """);

        Assert.Equal(ClientValueSource.Invalid, snapshot.Retry.Source);
        Assert.Equal("{\"value\":7}", snapshot.Retry.RawValue);
    }

    [Fact]
    public void Non_bridge_global_file_is_unknown()
    {
        var snapshot = Read("""
            { "env": { "ANTHROPIC_BASE_URL": "https://example.test" } }
            """);

        Assert.False(snapshot.Readable);
        Assert.Equal(ClientValueSource.Unknown, snapshot.EventIdle.Source);
    }

    [Fact]
    public void Missing_and_malformed_global_files_are_best_effort_unknowns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-claude-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var missing = ClaudeCodeTimeoutReader.Read(Path.Combine(directory, "missing.json"));
            File.WriteAllText(Path.Combine(directory, "bad.json"), "{ not json");
            var malformed = ClaudeCodeTimeoutReader.Read(Path.Combine(directory, "bad.json"));

            Assert.False(missing.Readable);
            Assert.False(malformed.Readable);
            Assert.Equal(ClientValueSource.Unknown, missing.EventIdle.Source);
            Assert.Equal(ClientValueSource.Unknown, malformed.EventIdle.Source);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bridge_route_on_another_port_is_still_a_readable_global_baseline()
    {
        var snapshot = Read("""
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:9999/cc" } }
            """);

        Assert.True(snapshot.Readable);
        Assert.Equal(ClientValueSource.BuiltIn, snapshot.EventIdle.Source);
    }

    [Fact]
    public void Unverified_client_version_keeps_configured_duration_but_does_not_guess_effective_rules()
    {
        var snapshot = Read(
            Settings("CLAUDE_STREAM_IDLE_TIMEOUT_MS", "60000"),
            installedVersion: "2.1.999");

        Assert.Equal(60_000, snapshot.EventIdle.ConfiguredMs);
        Assert.Null(snapshot.EventIdle.EffectiveMs);
        Assert.Equal(ClientValueSource.Explicit, snapshot.EventIdle.Source);
        Assert.Contains("has not been verified", snapshot.EventIdle.Detail);
        Assert.Equal(ClientValueSource.Unknown, snapshot.ByteIdle.Source);
        Assert.Equal(ClientValueSource.Unknown, snapshot.NormalRequest.Source);
    }

    [Fact]
    public void Version_probe_path_resolution_ignores_relative_and_current_directory_launchers()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-version-probe-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "untrusted-checkout");
        var currentTools = Path.Combine(current, "tools");
        var trusted = Path.Combine(root, "explicit-path-entry");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(currentTools);
        Directory.CreateDirectory(trusted);
        File.WriteAllText(Path.Combine(current, "claude.cmd"), "malicious");
        File.WriteAllText(Path.Combine(currentTools, "claude.cmd"), "also malicious");
        var expected = Path.Combine(trusted, "claude.cmd");
        File.WriteAllText(expected, "trusted");

        try
        {
            var path = string.Join(
                Path.PathSeparator,
                new[] { ".", current, currentTools, trusted });

            var actual = ClaudeCodeVersionProbe.FindExecutableOnPath(
                "claude", path, [".cmd"], current);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Version_probe_rejects_a_path_directory_alias_of_the_working_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-version-alias-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "untrusted-checkout");
        var alias = Path.Combine(root, "path-alias");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "claude.cmd"), "malicious");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(alias, current);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Some Windows runners do not grant symlink creation. The lexical
                // child-directory contract above still executes on every platform.
                return;
            }

            var actual = ClaudeCodeVersionProbe.FindExecutableOnPath(
                "claude", alias, [".cmd"], current);

            Assert.Null(actual);
        }
        finally
        {
            try
            {
                if (Directory.Exists(alias)
                    && (File.GetAttributes(alias) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(alias);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void Windows_version_probe_preserves_path_directory_precedence_across_shim_and_exe_layouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "cb-version-order-" + Guid.NewGuid().ToString("N"));
        var current = Path.Combine(root, "current");
        var first = Path.Combine(root, "first-npm-bin");
        var second = Path.Combine(root, "later-direct-bin");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "claude.cmd"), "shim");
        var expected = Path.Combine(
            first, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, "first installation");
        File.WriteAllText(Path.Combine(second, "claude.exe"), "later installation");

        try
        {
            var path = string.Join(Path.PathSeparator, new[] { first, second });

            var actual = ClaudeCodeVersionProbe.FindWindowsClaudeExecutable(path, current);

            Assert.Equal(expected, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Settings(string key, string value) => $$"""
        { "env": {
          "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
          "{{key}}": "{{value}}"
        } }
        """;

    private static ClientTimeoutSnapshot Read(
        string json,
        string? installedVersion = ClaudeCodeTimeoutPolicy.VerifiedClientVersion)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cb-claude-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        try
        {
            File.WriteAllText(path, json);
            return ClaudeCodeTimeoutReader.Read(path, installedVersion: installedVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
