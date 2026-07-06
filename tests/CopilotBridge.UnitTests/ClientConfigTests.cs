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
            "ToolInputValidation": { "Enabled": false, "PreserveStream": false },
            "RunawayGuard": { "Enabled": false }
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
            "ToolInputValidation": { "Enabled": true, "PreserveStream": true },
            "RunawayGuard": { "Enabled": false }
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
            "ToolInputValidation": { "Enabled": false, "PreserveStream": true },
            "RunawayGuard": { "Enabled": false }
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

    // ---- Refuse-on-unparseable (PR review #1: surgical merge must not silently
    //      discard content it cannot parse) ----

    [Fact]
    public void ClaudeCode_refuses_to_merge_invalid_json_rather_than_discard()
    {
        // A JSONC-style comment makes System.Text.Json reject the file. The merge must
        // throw (so the caller aborts) rather than return an env-only file that has
        // dropped statusLine/enabledPlugins/etc.
        var withComment = "{\n  // my settings\n  \"statusLine\": { \"type\": \"command\" }\n}";

        Assert.Throws<ClientConfigException>(
            () => ClaudeCodeConfigurator.BuildContent(withComment, Conn()));
    }

    [Fact]
    public void ClaudeCode_refuses_non_object_json()
    {
        // Valid JSON, but an array — merging would discard it. Must throw.
        Assert.Throws<ClientConfigException>(
            () => ClaudeCodeConfigurator.BuildContent("[1, 2, 3]", Conn()));
    }

    [Fact]
    public void Codex_refuses_to_merge_malformed_toml_rather_than_corrupt()
    {
        // A syntactically broken TOML line. Editing an error-laden tree could drop or
        // corrupt unrelated content, so the merge must throw.
        var broken = "model = \"gpt-5.5\"\n[unclosed_table\nkey = 1\n";

        Assert.Throws<ClientConfigException>(
            () => CodexConfigurator.BuildContent(broken, Conn()));
    }

    // ---- Fallback-env drift (PR review #3: status must detect fallback drift, not
    //      only base-URL drift) ----

    [Fact]
    public void Status_reports_fallback_drift_even_when_base_url_matches()
    {
        // A ConfigState whose base URL matches but whose fallback-env does not (e.g.
        // appsettings later turned a detector off) must be reported as drifted.
        var drifted = new ConfigState("claude-code", ConfigScope.Global, "x", Exists: true,
            ConfiguredForBridge: true,
            CurrentBaseUrl: "http://localhost:8765/cc", ExpectedBaseUrl: "http://localhost:8765/cc",
            ExpectedFallback: null, CurrentFallback: "1", Details: []);
        Assert.True(drifted.Drifted);
    }

    [Fact]
    public void Status_not_drifted_when_base_url_and_fallback_both_match()
    {
        var ok = new ConfigState("claude-code", ConfigScope.Global, "x", Exists: true,
            ConfiguredForBridge: true,
            CurrentBaseUrl: "http://localhost:8765/cc", ExpectedBaseUrl: "http://localhost:8765/cc",
            ExpectedFallback: "1", CurrentFallback: "1", Details: []);
        Assert.False(ok.Drifted);
    }

    [Fact]
    public void Status_fallback_null_null_is_not_drift()
    {
        // Codex (no fallback concept) passes null for both — must never count as drift.
        var codexLike = new ConfigState("codex", ConfigScope.Global, "x", Exists: true,
            ConfiguredForBridge: true,
            CurrentBaseUrl: "http://localhost:8765/codex", ExpectedBaseUrl: "http://localhost:8765/codex",
            ExpectedFallback: null, CurrentFallback: null, Details: []);
        Assert.False(codexLike.Drifted);
    }

    // ---- Review round 2: RunawayGuard contributes to fallback need ----

    [Fact]
    public void RunawayGuard_enabled_alone_requires_fallback_disabled()
    {
        // RunawayGuard has no PreserveStream toggle — it always aborts mid-stream when
        // enabled. With the other two detectors off, its Enabled flag alone must still
        // drive the fallback env, or a runaway abort gets swallowed by Claude Code's
        // silent non-streaming re-request.
        var config = Config("""
        { "Pipeline": { "Detectors": {
            "ResponseLeakGuard": { "Enabled": false, "PreserveStream": false },
            "ToolInputValidation": { "Enabled": false, "PreserveStream": false },
            "RunawayGuard": { "Enabled": true }
        } } }
        """);
        var conn = BridgeConnectionFactory.Create(config);
        Assert.True(conn.NeedNonStreamingFallbackDisabled);
    }

    [Fact]
    public void All_detectors_off_means_no_fallback()
    {
        var config = Config("""
        { "Pipeline": { "Detectors": {
            "ResponseLeakGuard": { "Enabled": false, "PreserveStream": true },
            "ToolInputValidation": { "Enabled": false, "PreserveStream": true },
            "RunawayGuard": { "Enabled": false }
        } } }
        """);
        var conn = BridgeConnectionFactory.Create(config);
        Assert.False(conn.NeedNonStreamingFallbackDisabled);
    }

    // ---- Review round 2: byte-preservation of &<>+ and non-ASCII in unrelated keys ----

    [Fact]
    public void ClaudeCode_preserves_ampersands_and_non_ascii_verbatim()
    {
        // The default JSON encoder escapes &, <, >, +, and all non-ASCII to \uXXXX,
        // which would silently mangle preserved user values. They must survive verbatim.
        var original = """
        {
          "statusLine": { "command": "echo a && b > c" },
          "greeting": "你好世界"
        }
        """;
        var (content, _) = ClaudeCodeConfigurator.BuildContent(original, Conn());

        Assert.Contains("echo a && b > c", content);
        Assert.Contains("你好世界", content);
        Assert.DoesNotContain("\\u0026", content);
        Assert.DoesNotContain("\\u4F60", content);
    }

    [Fact]
    public void ClaudeCode_read_tolerates_non_string_env_value()
    {
        // A hand-edited file with a numeric fallback value (1 instead of "1") must not
        // crash Read — GetValue<string> on a JSON number throws; AsStringOrNull tolerates.
        var dir = Path.Combine(Path.GetTempPath(), "cbcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        File.WriteAllText(Path.Combine(dir, ".claude", "settings.local.json"),
            "{ \"env\": { \"ANTHROPIC_BASE_URL\": \"http://localhost:8765/cc\", " +
            "\"CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK\": 1 } }");
        var old = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            var state = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            // Does not throw; base URL still read, numeric fallback reported as unset.
            Assert.Equal("http://localhost:8765/cc", state.CurrentBaseUrl);
            Assert.Null(state.CurrentFallback);
        }
        finally
        {
            Environment.CurrentDirectory = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- Review round 2: TOML append never glues onto a file lacking a trailing newline ----

    [Fact]
    public void Codex_appends_cleanly_when_file_has_no_trailing_newline()
    {
        // Existing copilot-bridge block is NOT last and the file has no final newline.
        // A naive append would glue `sandbox = "x"[model_providers.copilot-bridge]`.
        var toml =
            "model_provider = \"copilot-bridge\"\n\n" +
            "[model_providers.copilot-bridge]\nbase_url = \"http://localhost:8765/codex\"\n\n" +
            "[windows]\nsandbox = \"elevated\"";  // no trailing newline
        var (content, _) = CodexConfigurator.BuildContent(toml, Conn());

        var doc = Tomlyn.Parsing.SyntaxParser.Parse(content, "x");
        Assert.False(doc.HasErrors);
        Assert.DoesNotContain("\"elevated\"[model_providers", content);
    }

    [Fact]
    public void Codex_appends_top_level_key_cleanly_without_trailing_newline()
    {
        // No model_provider yet, last top-level line has no trailing newline.
        var toml = "model = \"gpt-5.5\"";  // no newline, no model_provider
        var (content, _) = CodexConfigurator.BuildContent(toml, Conn());

        var doc = Tomlyn.Parsing.SyntaxParser.Parse(content, "x");
        Assert.False(doc.HasErrors);
        Assert.Contains("model_provider = \"copilot-bridge\"", content);
        Assert.DoesNotContain("\"gpt-5.5\"model_provider", content);
    }

    [Fact]
    public void Codex_read_reports_malformed_file()
    {
        // Codex Read must flag a syntactically broken file, not misreport it as unconfigured.
        var dir = Path.Combine(Path.GetTempPath(), "cbcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var codexHome = Path.Combine(dir, "codex");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), "model = \"x\"\n[unclosed\nkey = 1\n");
        var old = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", codexHome);
            var state = new CodexConfigurator().Read(Conn(), ConfigScope.Global);
            Assert.False(state.ConfiguredForBridge);
            Assert.Contains(state.Details, d => d.Contains("syntax error", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- Review round 3: a non-bridge base URL is "not pointed at bridge", not drift ----

    [Fact]
    public void ClaudeCode_read_non_bridge_url_is_not_configured_for_bridge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cbcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        // Points at some other Anthropic-compatible endpoint, not this bridge's /cc route.
        File.WriteAllText(Path.Combine(dir, ".claude", "settings.local.json"),
            "{ \"env\": { \"ANTHROPIC_BASE_URL\": \"https://api.anthropic.com\" } }");
        var old = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            var state = new ClaudeCodeConfigurator().Read(Conn(), ConfigScope.Repo);
            Assert.False(state.ConfiguredForBridge);  // not "configured for bridge"
            Assert.False(state.Drifted);              // and therefore not "DRIFTED"
        }
        finally
        {
            Environment.CurrentDirectory = old;
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ClaudeCode_read_bridge_url_on_other_port_is_drifted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cbcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".claude"));
        // A bridge endpoint (/cc) but a different port than the current appsettings → drift.
        File.WriteAllText(Path.Combine(dir, ".claude", "settings.local.json"),
            "{ \"env\": { \"ANTHROPIC_BASE_URL\": \"http://localhost:9999/cc\" } }");
        var old = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            var state = new ClaudeCodeConfigurator().Read(Conn(port: 8765), ConfigScope.Repo);
            Assert.True(state.ConfiguredForBridge);
            Assert.True(state.Drifted);
        }
        finally
        {
            Environment.CurrentDirectory = old;
            try { Directory.Delete(dir, true); } catch { }
        }
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

    // ---- Apply seam (PR review #2: the write must go through IClientConfigurator.Apply) ----

    [Fact]
    public void Configurator_apply_writes_and_returns_backup_path()
    {
        // Drive the write through the seam (as the dispatcher does), not the static
        // writer, so Apply is a live path and returns the backup on an overwrite.
        IClientConfigurator configurator = new ClaudeCodeConfigurator();
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ \"keep\": true }");

        var plan = new ConfigPlan(configurator.ClientId, ConfigScope.Global, path,
            "{\n  \"env\": {}\n}\n", "{ \"keep\": true }", ["change"]);
        var backup = configurator.Apply(plan);

        Assert.NotNull(backup);
        Assert.Equal("{ \"keep\": true }", File.ReadAllText(backup!));
        Assert.Equal("{\n  \"env\": {}\n}\n", File.ReadAllText(path));
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

