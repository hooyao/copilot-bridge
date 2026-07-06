using CopilotBridge.Cli.Hosting.ClientConfig;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for the <c>config</c> command family. Each asserts a required
/// behavior from the client-autoconfiguration spec — derived from the spec, not read
/// back from the implementation.
/// </summary>
public class ClientConfigTests
{
    // ---- Connection derivation (spec: "Connection facts derived from appsettings") ----

    private static IConfiguration Config(string json)
    {
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    [Fact]
    public void Port_defaults_to_appsettings_server_port()
    {
        var config = Config("""{ "Server": { "Port": 8765 } }""");
        var conn = BridgeConnectionFactory.Create(config, cliPort: null);

        Assert.Equal(8765, conn.Port);
        Assert.Equal("http://localhost:8765/cc", conn.ClaudeCodeBaseUrl);
        Assert.Equal("http://localhost:8765/codex", conn.CodexBaseUrl);
    }

    [Fact]
    public void Cli_port_overrides_appsettings()
    {
        var config = Config("""{ "Server": { "Port": 8765 } }""");
        var conn = BridgeConnectionFactory.Create(config, cliPort: 18765);

        Assert.Equal(18765, conn.Port);
        Assert.Equal("http://localhost:18765/cc", conn.ClaudeCodeBaseUrl);
    }

    [Fact]
    public void Non_default_appsettings_port_is_honored()
    {
        var config = Config("""{ "Server": { "Port": 9000 } }""");
        var conn = BridgeConnectionFactory.Create(config, cliPort: null);

        Assert.Equal(9000, conn.Port);
        Assert.Equal("http://localhost:9000/cc", conn.ClaudeCodeBaseUrl);
    }

    [Fact]
    public void Missing_server_section_falls_back_to_default_8765()
    {
        var conn = BridgeConnectionFactory.Create(Config("{}"), cliPort: null);
        Assert.Equal(8765, conn.Port);
    }

    // ---- Fallback env derivation (spec: "Non-streaming fallback env derived from detector options") ----

    [Fact]
    public void PreserveStream_on_leak_guard_requires_fallback_disabled()
    {
        var config = Config("""
        { "Pipeline": { "Detectors": {
            "ResponseLeakGuard": { "Enabled": true, "PreserveStream": true },
            "ToolInputValidation": { "Enabled": false, "PreserveStream": false }
        } } }
        """);
        var conn = BridgeConnectionFactory.Create(config);
        Assert.True(conn.NeedNonStreamingFallbackDisabled);
    }

    [Fact]
    public void PreserveStream_on_tool_input_validation_requires_fallback_disabled()
    {
        var config = Config("""
        { "Pipeline": { "Detectors": {
            "ResponseLeakGuard": { "Enabled": false, "PreserveStream": false },
            "ToolInputValidation": { "Enabled": true, "PreserveStream": true }
        } } }
        """);
        var conn = BridgeConnectionFactory.Create(config);
        Assert.True(conn.NeedNonStreamingFallbackDisabled);
    }

    [Fact]
    public void No_detector_preserving_stream_means_no_fallback()
    {
        var config = Config("""
        { "Pipeline": { "Detectors": {
            "ResponseLeakGuard": { "Enabled": true, "PreserveStream": false },
            "ToolInputValidation": { "Enabled": false, "PreserveStream": true }
        } } }
        """);
        var conn = BridgeConnectionFactory.Create(config);
        Assert.False(conn.NeedNonStreamingFallbackDisabled);
    }

    [Fact]
    public void Detector_defaults_true_true_means_fallback_needed()
    {
        // The shipped appsettings has both detectors Enabled+PreserveStream=true by
        // default; a bound-with-defaults config must reflect that.
        var conn = BridgeConnectionFactory.Create(Config("{}"));
        Assert.True(conn.NeedNonStreamingFallbackDisabled);
    }

    // ---- Claude Code JSON merge (spec: "Non-streaming fallback env", "Overwrite policy") ----

    private static BridgeConnection Conn(int port = 8765, bool fallback = true) => new(port, fallback);

    [Fact]
    public void ClaudeCode_sets_base_url_and_fallback_when_needed()
    {
        var (content, _) = ClaudeCodeConfigurator.BuildContent(null, Conn(fallback: true));
        var root = System.Text.Json.Nodes.JsonNode.Parse(content)!;
        var env = root["env"]!;

        Assert.Equal("http://localhost:8765/cc", (string?)env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK"]);
    }

    [Fact]
    public void ClaudeCode_fills_auth_token_with_copilot_bridge_when_absent()
    {
        var (content, _) = ClaudeCodeConfigurator.BuildContent(null, Conn());
        var env = System.Text.Json.Nodes.JsonNode.Parse(content)!["env"]!;

        var token = (string?)env["ANTHROPIC_AUTH_TOKEN"];
        Assert.Equal("copilot-bridge", token);
        // No competitor branding.
        Assert.DoesNotContain("Maestro", token, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClaudeCode_preserves_existing_auth_token()
    {
        var original = """{ "env": { "ANTHROPIC_AUTH_TOKEN": "my-existing-token" } }""";
        var (content, _) = ClaudeCodeConfigurator.BuildContent(original, Conn());
        var env = System.Text.Json.Nodes.JsonNode.Parse(content)!["env"]!;

        Assert.Equal("my-existing-token", (string?)env["ANTHROPIC_AUTH_TOKEN"]);
    }

    [Fact]
    public void ClaudeCode_removes_fallback_env_when_not_needed()
    {
        var original = """
        { "env": { "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": "1", "ANTHROPIC_AUTH_TOKEN": "x" } }
        """;
        var (content, _) = ClaudeCodeConfigurator.BuildContent(original, Conn(fallback: false));
        var env = System.Text.Json.Nodes.JsonNode.Parse(content)!["env"]!.AsObject();

        Assert.False(env.ContainsKey("CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK"));
    }

    [Fact]
    public void ClaudeCode_preserves_unrelated_settings()
    {
        var original = """
        {
          "statusLine": { "type": "command", "command": "x.sh" },
          "enabledPlugins": { "foo": true },
          "effortLevel": "xhigh",
          "env": { "SOMETHING_ELSE": "keep-me" }
        }
        """;
        var (content, _) = ClaudeCodeConfigurator.BuildContent(original, Conn());
        var root = System.Text.Json.Nodes.JsonNode.Parse(content)!;

        Assert.NotNull(root["statusLine"]);
        Assert.Equal("command", (string?)root["statusLine"]!["type"]);
        Assert.Equal("xhigh", (string?)root["effortLevel"]);
        Assert.True((bool?)root["enabledPlugins"]!["foo"]);
        Assert.Equal("keep-me", (string?)root["env"]!["SOMETHING_ELSE"]);
    }

    [Fact]
    public void ClaudeCode_is_idempotent()
    {
        var (first, _) = ClaudeCodeConfigurator.BuildContent(null, Conn());
        var (second, _) = ClaudeCodeConfigurator.BuildContent(first, Conn());
        Assert.Equal(first, second);
    }

    // ---- Codex TOML merge (spec: "Surgical merge preserves all unrelated content", "Overwrite policy") ----

    private const string DenseCodexToml = """
        model = "gpt-5.5"
        model_provider = "agent-maestro"
        model_context_window = 921793
        notify = [ "C:\\Users\\HuYao\\bin\\notify.exe", "turn-ended" ]
        model_reasoning_effort = "xhigh"

        [model_providers.agent-maestro]
        name = "Agent Maestro"
        base_url = "http://127.0.0.1:23333/api/openai/v1"
        wire_api = "responses"

        [mcp_servers.node_repl.env]
        NODE_REPL_NODE_PATH = 'C:\Users\HuYao\bin\node.exe'
        CODEX_HOME = 'C:\Users\HuYao\.codex'

        [windows]
        sandbox = "elevated"
        """;

    [Fact]
    public void Codex_repoints_model_provider_to_copilot_bridge()
    {
        var (content, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn());

        Assert.Contains("model_provider = \"copilot-bridge\"", content);
        Assert.DoesNotContain("model_provider = \"agent-maestro\"", content);
    }

    [Fact]
    public void Codex_writes_provider_block_with_base_url_and_wire_api()
    {
        var (content, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn(port: 8765));

        Assert.Contains("[model_providers.copilot-bridge]", content);
        Assert.Contains("http://localhost:8765/codex", content);
        Assert.Contains("wire_api = \"responses\"", content);
    }

    [Fact]
    public void Codex_preserves_model_and_effort()
    {
        var (content, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn());

        Assert.Contains("model = \"gpt-5.5\"", content);
        Assert.Contains("model_reasoning_effort = \"xhigh\"", content);
    }

    [Fact]
    public void Codex_keeps_prior_provider_block_for_switch_back()
    {
        var (content, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn());

        Assert.Contains("[model_providers.agent-maestro]", content);
        Assert.Contains("http://127.0.0.1:23333/api/openai/v1", content);
    }

    [Fact]
    public void Codex_preserves_unrelated_tables_comments_and_literals()
    {
        var (content, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn());

        // Unrelated tables intact.
        Assert.Contains("[mcp_servers.node_repl.env]", content);
        Assert.Contains("[windows]", content);
        Assert.Contains("sandbox = \"elevated\"", content);
        // Single-quoted (literal) Windows paths preserved verbatim — a model-DOM
        // round-trip would rewrite these.
        Assert.Contains("""NODE_REPL_NODE_PATH = 'C:\Users\HuYao\bin\node.exe'""", content);
        Assert.Contains("""CODEX_HOME = 'C:\Users\HuYao\.codex'""", content);
        // The multi-line notify array and its double-backslash escapes preserved.
        Assert.Contains("""notify = [ "C:\\Users\\HuYao\\bin\\notify.exe", "turn-ended" ]""", content);
        Assert.Contains("model_context_window = 921793", content);
    }

    [Fact]
    public void Codex_output_parses_back_cleanly()
    {
        var (content, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn());
        var doc = Tomlyn.Parsing.SyntaxParser.Parse(content, "roundtrip.toml");
        Assert.False(doc.HasErrors);
    }

    [Fact]
    public void Codex_is_idempotent()
    {
        var (first, _) = CodexConfigurator.BuildContent(DenseCodexToml, Conn());
        var (second, _) = CodexConfigurator.BuildContent(first, Conn());
        Assert.Equal(first, second);
    }

    [Fact]
    public void Codex_creates_valid_file_from_empty()
    {
        var (content, _) = CodexConfigurator.BuildContent(null, Conn());
        var doc = Tomlyn.Parsing.SyntaxParser.Parse(content, "new.toml");

        Assert.False(doc.HasErrors);
        Assert.Contains("model_provider = \"copilot-bridge\"", content);
        Assert.Contains("[model_providers.copilot-bridge]", content);
    }
}

/// <summary>
/// Effectful-edge contract tests: safe-write plumbing (backup, atomic write,
/// idempotence on disk), the isolated composition root, and the writer's no-op path.
/// </summary>
public class ClientConfigWriteTests : IDisposable
{
    private readonly string _dir;

    public ClientConfigWriteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cbcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ConfigPlan PlanFor(string fileName, string newContent, string? original)
    {
        var path = Path.Combine(_dir, fileName);
        if (original is not null)
        {
            File.WriteAllText(path, original);
        }
        return new ConfigPlan("test", ConfigScope.Global, path, newContent, original, ["change"]);
    }

    // ---- Safe write (spec: "Safe and idempotent writes") ----

    [Fact]
    public void Write_backs_up_existing_file_before_overwriting()
    {
        var plan = PlanFor("settings.json", "NEW", original: "OLD");
        var backup = ConfigFileWriter.Write(plan);

        Assert.NotNull(backup);
        Assert.Equal("OLD", File.ReadAllText(backup!));
        Assert.Equal("NEW", File.ReadAllText(plan.TargetPath));
    }

    [Fact]
    public void Write_creates_new_file_without_backup()
    {
        var plan = PlanFor("settings.json", "NEW", original: null);
        var backup = ConfigFileWriter.Write(plan);

        Assert.Null(backup);
        Assert.Equal("NEW", File.ReadAllText(plan.TargetPath));
        Assert.False(File.Exists(plan.TargetPath + ".bak"));
    }

    [Fact]
    public void Write_is_noop_when_content_identical()
    {
        var plan = PlanFor("settings.json", "SAME", original: "SAME");
        // Pre-create a backup to prove a no-op does not touch anything.
        var backup = ConfigFileWriter.Write(plan);

        Assert.Null(backup);
        Assert.False(File.Exists(plan.TargetPath + ".bak"));
        Assert.Equal("SAME", File.ReadAllText(plan.TargetPath));
    }

    [Fact]
    public void Write_twice_yields_identical_file()
    {
        var plan = PlanFor("settings.json", "CONTENT", original: "ORIGINAL");
        ConfigFileWriter.Write(plan);
        var afterFirst = File.ReadAllText(plan.TargetPath);

        // A second application of the SAME plan (now a no-op vs disk) leaves the file
        // byte-identical.
        var plan2 = new ConfigPlan("test", ConfigScope.Global, plan.TargetPath, "CONTENT",
            File.ReadAllText(plan.TargetPath), ["change"]);
        ConfigFileWriter.Write(plan2);

        Assert.Equal(afterFirst, File.ReadAllText(plan.TargetPath));
    }

    // ---- Isolation (spec: "Isolation from the proxy server startup path") ----

    [Fact]
    public void Composition_root_resolves_configurators()
    {
        using var provider = ClientConfigServices.Build();
        var configurators = provider.GetServices<IClientConfigurator>().ToList();

        Assert.Contains(configurators, c => c.ClientId == "claude-code");
        Assert.Contains(configurators, c => c.ClientId == "codex");
    }

    [Fact]
    public void Composition_root_excludes_runtime_services()
    {
        using var provider = ClientConfigServices.Build();

        // The config graph must not carry any proxy runtime service or hosted service.
        Assert.Null(provider.GetService<CopilotBridge.Cli.Auth.AuthService>());
        Assert.Null(provider.GetService<CopilotBridge.Cli.Copilot.ICopilotClient>());
        Assert.Empty(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
    }
}

