using System.Text.Json.Nodes;
using CopilotBridge.Cli.Hosting;
using CopilotBridge.Cli.Hosting.ClientConfig;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotBridge.UnitTests;

[CollectionDefinition(ClientConfigCollection.Name, DisableParallelization = true)]
public sealed class ClientConfigCollection
{
    public const string Name = "ClientConfig (mutates process paths)";
}

/// <summary>Contract tests for connection-only, behavior-preserving client configuration.</summary>
[Collection(ClientConfigCollection.Name)]
public class ClientConfigTests
{
    [Fact]
    public void Connection_uses_appsettings_port_and_cli_override()
    {
        var config = Config("""{ "Server": { "Port": 9000 } }""");
        Assert.Equal(9000, BridgeConnectionFactory.Create(config).Port);
        Assert.Equal("http://localhost:9000/cc", BridgeConnectionFactory.Create(config).ClaudeCodeBaseUrl);
        Assert.Equal("http://localhost:18765/codex", BridgeConnectionFactory.Create(config, 18765).CodexBaseUrl);
    }

    [Fact]
    public void Claude_new_config_writes_only_connection_and_required_placeholder()
    {
        var (content, summary) = ClaudeCodeConfigurator.BuildContent(null, Conn());
        var env = JsonNode.Parse(content)!["env"]!;

        Assert.Equal("http://localhost:8765/cc", (string?)env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("copilot-bridge", (string?)env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Null(env[ClaudeCodeTimeoutPolicy.StreamIdleKey]);
        Assert.Null(env[ClaudeCodeTimeoutPolicy.ByteIdleKey]);
        Assert.Null(env[ClaudeCodeTimeoutPolicy.RequestTimeoutKey]);
        Assert.Null(env["_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"]);
        Assert.Null(env["DISABLE_ERROR_REPORTING"]);
        Assert.Contains(summary, line => line.Contains("preserved client-owned", StringComparison.Ordinal));
    }

    [Fact]
    public void Claude_preserves_every_behavioral_value_and_existing_token()
    {
        var original = """
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://old.example/cc",
                "ANTHROPIC_AUTH_TOKEN": "mine",
                "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "123456",
                "CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS": "654321",
                "API_TIMEOUT_MS": "777777",
                "CLAUDE_CODE_MAX_RETRIES": "0",
                "CLAUDE_ENABLE_STREAM_WATCHDOG": "false",
                "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "0",
                "DISABLE_ERROR_REPORTING": "false",
                "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": "1"
              },
              "statusLine": { "command": "echo a && b > c" },
              "greeting": "你好世界"
            }
            """;

        var (content, _) = ClaudeCodeConfigurator.BuildContent(original, Conn(9000));
        var root = JsonNode.Parse(content)!;
        var env = root["env"]!;

        Assert.Equal("http://localhost:9000/cc", (string?)env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("mine", (string?)env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("123456", (string?)env[ClaudeCodeTimeoutPolicy.StreamIdleKey]);
        Assert.Equal("654321", (string?)env[ClaudeCodeTimeoutPolicy.ByteIdleKey]);
        Assert.Equal("777777", (string?)env[ClaudeCodeTimeoutPolicy.RequestTimeoutKey]);
        Assert.Equal("0", (string?)env[ClaudeCodeTimeoutPolicy.RetryKey]);
        Assert.Equal("false", (string?)env[ClaudeCodeTimeoutPolicy.StreamWatchdogKey]);
        Assert.Equal("0", (string?)env["_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"]);
        Assert.Equal("false", (string?)env["DISABLE_ERROR_REPORTING"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK"]);
        Assert.Contains("echo a && b > c", content);
        Assert.Contains("你好世界", content);
        Assert.DoesNotContain("\\u4F60", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Claude_behavioral_absence_stays_absent_and_second_run_is_identical()
    {
        var original = """{ "env": { "ANTHROPIC_AUTH_TOKEN": "mine" } }""";
        var (first, _) = ClaudeCodeConfigurator.BuildContent(original, Conn());
        var (second, _) = ClaudeCodeConfigurator.BuildContent(first, Conn());

        Assert.Equal(first, second);
        var env = JsonNode.Parse(first)!["env"]!;
        Assert.Null(env[ClaudeCodeTimeoutPolicy.StreamIdleKey]);
        Assert.Null(env[ClaudeCodeTimeoutPolicy.RequestTimeoutKey]);
        Assert.Null(env["_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"]);
    }

    [Theory]
    [InlineData("// comment\n{}")]
    [InlineData("[]")]
    public void Claude_refuses_unmergeable_json(string original) =>
        Assert.Throws<ClientConfigException>(() => ClaudeCodeConfigurator.BuildContent(original, Conn()));

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"not-an-object\"")]
    public void Claude_refuses_non_object_env_instead_of_discarding_it(string envLiteral)
    {
        var original = $$"""{ "env": {{envLiteral}}, "unrelated": "keep" }""";

        var error = Assert.Throws<ClientConfigException>(
            () => ClaudeCodeConfigurator.BuildContent(original, Conn()));

        Assert.Contains("env value is not a JSON object", error.Message);
    }

    [Fact]
    public void Claude_preserves_values_written_by_an_older_bridge_instead_of_migrating_them()
    {
        var original = """
            { "env": {
              "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
              "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "540000",
              "API_TIMEOUT_MS": "3600000",
              "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "1",
              "DISABLE_ERROR_REPORTING": "1",
              "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": "1"
            } }
            """;

        var (content, _) = ClaudeCodeConfigurator.BuildContent(original, Conn(9000));
        var env = JsonNode.Parse(content)!["env"]!;

        Assert.Equal("540000", (string?)env[ClaudeCodeTimeoutPolicy.StreamIdleKey]);
        Assert.Equal("3600000", (string?)env[ClaudeCodeTimeoutPolicy.RequestTimeoutKey]);
        Assert.Equal("1", (string?)env["_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"]);
        Assert.Equal("1", (string?)env["DISABLE_ERROR_REPORTING"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK"]);
    }

    [Fact]
    public void Claude_status_treats_behavioral_values_as_observations_not_drift()
    {
        var directory = TempDirectory();
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            Directory.CreateDirectory(Path.Combine(directory, ".claude"));
            File.WriteAllText(
                Path.Combine(directory, ".claude", "settings.local.json"),
                """
                { "env": {
                  "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                  "ANTHROPIC_AUTH_TOKEN": "mine",
                  "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "1",
                  "CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS": 2,
                  "API_TIMEOUT_MS": "3",
                  "CLAUDE_ENABLE_STREAM_WATCHDOG": false,
                  "CLAUDE_ENABLE_BYTE_WATCHDOG": true,
                  "CLAUDE_CODE_MAX_RETRIES": 4,
                  "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": 0,
                  "DISABLE_ERROR_REPORTING": false,
                  "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": 1
                } }
                """);

            var state = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            Assert.True(state.ConfiguredForBridge);
            Assert.False(state.Drifted);
            Assert.Contains(state.Details, line => line.Contains("CLAUDE_STREAM_IDLE_TIMEOUT_MS: 1"));
            Assert.Contains(state.Details, line => line == "CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS: 2");
            Assert.Contains(state.Details, line => line == "API_TIMEOUT_MS: 3");
            Assert.Contains(state.Details, line => line == "CLAUDE_ENABLE_STREAM_WATCHDOG: false");
            Assert.Contains(state.Details, line => line == "CLAUDE_ENABLE_BYTE_WATCHDOG: true");
            Assert.Contains(state.Details, line => line == "CLAUDE_CODE_MAX_RETRIES: 4");
            Assert.Contains(state.Details, line => line == "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL: 0");
            Assert.Contains(state.Details, line => line == "DISABLE_ERROR_REPORTING: false");
            Assert.Contains(state.Details, line => line == "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK: 1");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(directory);
        }
    }

    [Fact]
    public void Claude_status_marks_only_a_missing_required_token_as_auth_drift()
    {
        var directory = TempDirectory();
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            var claudeDirectory = Path.Combine(directory, ".claude");
            Directory.CreateDirectory(claudeDirectory);
            var path = Path.Combine(claudeDirectory, "settings.local.json");
            File.WriteAllText(path, """
                { "env": {
                  "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                  "API_TIMEOUT_MS": "1"
                } }
                """);

            var missing = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            Assert.True(missing.Drifted);
            Assert.Contains(missing.AdditionalDriftFacts!, fact => fact.Contains("ANTHROPIC_AUTH_TOKEN"));

            File.WriteAllText(path, """
                { "env": {
                  "ANTHROPIC_BASE_URL": "http://localhost:8765/cc",
                  "ANTHROPIC_AUTH_TOKEN": "user-selected",
                  "API_TIMEOUT_MS": "1"
                } }
                """);
            var present = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            Assert.False(present.Drifted);
            Assert.Contains(present.Details, line => line == "ANTHROPIC_AUTH_TOKEN: present");
            Assert.DoesNotContain(present.Details, line => line.Contains("user-selected"));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(directory);
        }
    }

    [Fact]
    public void Claude_status_distinguishes_a_bridge_on_another_port_from_a_non_bridge_endpoint()
    {
        var directory = TempDirectory();
        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = directory;
            var claudeDirectory = Path.Combine(directory, ".claude");
            Directory.CreateDirectory(claudeDirectory);
            var path = Path.Combine(claudeDirectory, "settings.local.json");
            File.WriteAllText(path, """
                { "env": {
                  "ANTHROPIC_BASE_URL": "http://localhost:9999/cc",
                  "ANTHROPIC_AUTH_TOKEN": "present"
                } }
                """);
            var otherPort = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            Assert.True(otherPort.ConfiguredForBridge);
            Assert.True(otherPort.Drifted);

            File.WriteAllText(path, """
                { "env": {
                  "ANTHROPIC_BASE_URL": "https://api.example.test",
                  "ANTHROPIC_AUTH_TOKEN": "present"
                } }
                """);
            var otherEndpoint = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            Assert.False(otherEndpoint.ConfiguredForBridge);
            Assert.False(otherEndpoint.Drifted);
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            TryDelete(directory);
        }
    }

    [Fact]
    public void Codex_new_config_contains_connection_and_auth_without_timeout_policy()
    {
        var invocation = new CodexProviderAuthInvocation("bridge.exe", ["auth", "provider-token"]);
        var (content, summary) = CodexConfigurator.BuildContent(null, Conn(), invocation);

        Assert.Contains("model_provider = \"copilot-bridge\"", content);
        Assert.Contains("[model_providers.copilot-bridge]", content);
        Assert.Contains("base_url = \"http://localhost:8765/codex\"", content);
        Assert.Contains("wire_api = \"responses\"", content);
        Assert.Contains("[model_providers.copilot-bridge.auth]", content);
        Assert.DoesNotContain("stream_idle_timeout_ms", content);
        Assert.DoesNotContain("request_max_retries", content);
        Assert.Contains(summary, line => line.Contains("preserved client-owned provider", StringComparison.Ordinal));
        Assert.Contains(summary, line => line.Contains(".command = \"bridge.exe\"", StringComparison.Ordinal));
        Assert.Contains(summary, line => line.Contains(".args = [ \"auth\", \"provider-token\" ]", StringComparison.Ordinal));
        Assert.Contains(summary, line => line.EndsWith(".timeout_ms = 5000", StringComparison.Ordinal));
        Assert.Contains(summary, line => line.EndsWith(".refresh_interval_ms = 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Codex_surgical_merge_preserves_behavioral_fields_comments_and_rival_tables()
    {
        var original = """
            model_provider = "old"

            [model_providers.copilot-bridge]
            name = "old-name"
            base_url = "http://old.example/codex"
            wire_api = "chat"
            stream_idle_timeout_ms = 0 # explicit immediate timeout
            request_max_retries = 0
            stream_max_retries = 7
            websocket_connect_timeout_ms = 1234
            supports_websockets = true
            query_params = { region = "mine" }
            http_headers = { X-Custom = "keep" }
            env_http_headers = { Authorization = "KEEP_ENV" }

            [model_providers.copilot-bridge.auth]
            command = "old.exe"
            args = [ "old" ]
            timeout_ms = 1
            refresh_interval_ms = 9

            [model_providers.rival]
            name = 'leave exactly'
            base_url = 'https://rival.example'
            """;
        var invocation = new CodexProviderAuthInvocation("bridge.exe", ["auth", "provider-token"]);

        var (content, _) = CodexConfigurator.BuildContent(original, Conn(9000), invocation);

        Assert.Contains("base_url = \"http://localhost:9000/codex\"", content);
        Assert.Contains("stream_idle_timeout_ms = 0 # explicit immediate timeout", content);
        Assert.Contains("request_max_retries = 0", content);
        Assert.Contains("stream_max_retries = 7", content);
        Assert.Contains("websocket_connect_timeout_ms = 1234", content);
        Assert.Contains("supports_websockets = true", content);
        Assert.Contains("query_params = { region = \"mine\" }", content);
        Assert.Contains("http_headers = { X-Custom = \"keep\" }", content);
        Assert.Contains("env_http_headers = { Authorization = \"KEEP_ENV\" }", content);
        Assert.Contains("[model_providers.rival]\nname = 'leave exactly'", content.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Codex_surgical_merge_is_idempotent()
    {
        var invocation = new CodexProviderAuthInvocation("bridge.exe", ["auth", "provider-token"]);
        var original = """
            [model_providers.copilot-bridge]
            stream_idle_timeout_ms = 300000
            request_max_retries = 4
            """;
        var (first, _) = CodexConfigurator.BuildContent(original, Conn(), invocation);
        var (second, _) = CodexConfigurator.BuildContent(first, Conn(), invocation);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Codex_appends_to_a_file_without_a_trailing_newline_and_preserves_unmanaged_bytes()
    {
        const string original = "model = 'gpt-custom' # keep quote and comment";
        var invocation = new CodexProviderAuthInvocation("bridge.exe", ["auth", "provider-token"]);

        var (content, _) = CodexConfigurator.BuildContent(original, Conn(), invocation);

        Assert.StartsWith(original + "\n", content.ReplaceLineEndings("\n"));
        Assert.Contains("[model_providers.copilot-bridge]", content);
        Assert.EndsWith("\n", content);
    }

    [Fact]
    public void Codex_connection_command_plan_becomes_a_true_noop_after_apply()
    {
        var directory = TempDirectory();
        var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            var configurator = new CodexConfigurator();
            var first = configurator.Plan(Conn(), ConfigScope.Global);
            Assert.False(first.IsNoOp);
            configurator.Apply(first);

            var second = configurator.Plan(Conn(), ConfigScope.Global);
            Assert.True(second.IsNoOp);
            Assert.Equal(first.NewContent, second.NewContent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
            TryDelete(directory);
        }
    }

    [Fact]
    public void Codex_refuses_malformed_toml() =>
        Assert.Throws<ClientConfigException>(() =>
            CodexConfigurator.BuildContent("[bad\nkey =", Conn()));

    [Fact]
    public void Codex_status_observes_timeout_and_retry_without_drift()
    {
        var directory = TempDirectory();
        var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            var invocation = CodexProviderAuthInvocation.ResolveCurrent();
            var (content, _) = CodexConfigurator.BuildContent("""
                [model_providers.copilot-bridge]
                stream_idle_timeout_ms = 1
                request_max_retries = 0
                stream_max_retries = 0
                """, Conn(), invocation);
            File.WriteAllText(Path.Combine(directory, "config.toml"), content);

            var state = new CodexConfigurator().Read(Conn(), ConfigScope.Global);
            Assert.True(state.ConfiguredForBridge);
            Assert.False(state.Drifted);
            Assert.Contains(state.Details, line => line.EndsWith("stream_idle_timeout_ms = 1", StringComparison.Ordinal));
            Assert.Contains(state.Details, line => line.EndsWith("request_max_retries = 0", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
            TryDelete(directory);
        }
    }

    [Fact]
    public void Codex_status_retains_present_oddly_typed_behavior_values_instead_of_calling_them_unset()
    {
        var directory = TempDirectory();
        var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            var invocation = CodexProviderAuthInvocation.ResolveCurrent();
            var (content, _) = CodexConfigurator.BuildContent("""
                [model_providers.copilot-bridge]
                stream_idle_timeout_ms = "five minutes"
                request_max_retries = true
                stream_max_retries = { count = 2 }
                """, Conn(), invocation);
            File.WriteAllText(Path.Combine(directory, "config.toml"), content);

            var state = new CodexConfigurator().Read(Conn(), ConfigScope.Global);

            Assert.False(state.Drifted);
            Assert.Contains(state.Details, line => line.EndsWith(
                "stream_idle_timeout_ms = \"five minutes\"", StringComparison.Ordinal));
            Assert.Contains(state.Details, line => line.EndsWith(
                "request_max_retries = true", StringComparison.Ordinal));
            Assert.Contains(state.Details, line => line.Contains(
                "stream_max_retries = { count = 2 }", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
            TryDelete(directory);
        }
    }

    [Fact]
    public void Codex_status_reports_missing_managed_auth_but_not_behavioral_values_as_drift()
    {
        var directory = TempDirectory();
        var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            File.WriteAllText(Path.Combine(directory, "config.toml"), """
                model_provider = "copilot-bridge"
                [model_providers.copilot-bridge]
                name = "copilot-bridge"
                base_url = "http://localhost:8765/codex"
                wire_api = "responses"
                stream_idle_timeout_ms = 1
                request_max_retries = 0
                stream_max_retries = 0
                """);

            var state = new CodexConfigurator().Read(Conn(), ConfigScope.Global);

            Assert.True(state.ConfiguredForBridge);
            Assert.True(state.Drifted);
            Assert.Contains(state.AdditionalDriftFacts!, fact => fact.Contains("discovery-auth"));
            Assert.DoesNotContain(state.AdditionalDriftFacts!, fact => fact.Contains("retry", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(state.AdditionalDriftFacts!, fact => fact.Contains("idle", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", oldHome);
            TryDelete(directory);
        }
    }

    [Fact]
    public void Config_state_has_no_behavioral_expected_values_and_drifts_only_on_owned_facts()
    {
        var baseline = new ConfigState(
            "claude-code", ConfigScope.Global, "x", true, true,
            "http://localhost:8765/cc", "http://localhost:8765/cc",
            ["CLAUDE_STREAM_IDLE_TIMEOUT_MS: 1", "API_TIMEOUT_MS: 2"]);
        Assert.False(baseline.Drifted);
        Assert.True((baseline with { CurrentBaseUrl = "http://localhost:9999/cc" }).Drifted);
        Assert.True((baseline with { AdditionalDriftFacts = ["required auth token is missing"] }).Drifted);
        Assert.False((baseline with { Details = ["arbitrary user timeout: anything"] }).Drifted);
    }

    [Fact]
    public void Config_file_writer_creates_backup_and_noops_identical_content()
    {
        var directory = TempDirectory();
        var path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, "old");
        var changed = new ConfigPlan("x", ConfigScope.Global, path, "new", "old", []);
        var backup = ConfigFileWriter.Write(changed);
        Assert.NotNull(backup);
        Assert.Equal("old", File.ReadAllText(backup!));
        Assert.Equal("new", File.ReadAllText(path));

        var noOp = new ConfigPlan("x", ConfigScope.Global, path, "new", "new", []);
        Assert.Null(ConfigFileWriter.Write(noOp));
        TryDelete(directory);
    }

    [Fact]
    public void Config_composition_root_contains_only_configurators()
    {
        using var services = ClientConfigServices.Build();
        var configurators = services.GetServices<IClientConfigurator>().Select(c => c.ClientId).ToArray();
        Assert.Equal(["claude-code", "codex"], configurators);
        Assert.Null(services.GetService<CopilotBridge.Cli.Copilot.ICopilotClient>());
    }

    [Fact]
    public void Cli_help_states_connection_only_ownership_and_codex_global_visibility()
    {
        var config = RootCli.Build().Subcommands.Single(command => command.Name == "config");
        var claude = config.Subcommands.Single(command => command.Name == "claude-code");
        var codex = config.Subcommands.Single(command => command.Name == "codex");
        var status = config.Subcommands.Single(command => command.Name == "status");

        Assert.Contains("connection/auth only", claude.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("global", codex.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project/profile/CLI", codex.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(codex.Options, option => option.Name == "--scope" || option.Name == "scope");
        Assert.Contains("without rewriting", status.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static IConfiguration Config(string json)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static BridgeConnection Conn(int port = 8765) => new(port);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cb-client-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
