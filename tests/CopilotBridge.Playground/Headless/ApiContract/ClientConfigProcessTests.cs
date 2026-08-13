using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>Drives the real CLI executable against isolated global client files.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class ClientConfigProcessTests
{
    private readonly ITestOutputHelper _output;

    public ClientConfigProcessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Real_config_commands_preserve_every_client_behavior_field()
    {
        var bridge = ServeProcess.LocateBridgeExecutable();
        if (!bridge.Contains(
                $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Isolated Claude global-path injection exists only in Debug; refusing to run a Release binary against the real user home.");

        using var isolated = ClientBehaviorSupport.NewWorkDir("real-client-config-preservation");
        var claudePath = Path.Combine(isolated.Path, "settings.json");
        var claudeOriginal = """
            {
              "env": {
                "ANTHROPIC_BASE_URL": "http://old.example/cc",
                "ANTHROPIC_AUTH_TOKEN": "user-token-secret",
                "CLAUDE_STREAM_IDLE_TIMEOUT_MS": "111111",
                "CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS": "222222",
                "API_TIMEOUT_MS": "333333",
                "CLAUDE_CODE_MAX_RETRIES": "7",
                "CLAUDE_ENABLE_STREAM_WATCHDOG": "false",
                "CLAUDE_ENABLE_BYTE_WATCHDOG": "true",
                "_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL": "0",
                "DISABLE_ERROR_REPORTING": "false",
                "CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK": "1"
              },
              "statusLine": { "command": "echo preserve-me" }
            }
            """;
        File.WriteAllText(claudePath, claudeOriginal);

        var claude = await RunCliAsync(
            bridge,
            ["config", "claude-code", "--scope", "global", "--port", "19001"],
            new Dictionary<string, string?>
            {
                ["COPILOT_BRIDGE_TEST_CLAUDE_SETTINGS_PATH"] = claudePath,
            });
        _output.WriteLine(claude.Stdout);
        _output.WriteLine(claude.Stderr);
        Assert.Equal(0, claude.ExitCode);

        var env = JsonNode.Parse(File.ReadAllText(claudePath))!["env"]!;
        Assert.Equal("http://localhost:19001/cc", (string?)env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("user-token-secret", (string?)env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("111111", (string?)env["CLAUDE_STREAM_IDLE_TIMEOUT_MS"]);
        Assert.Equal("222222", (string?)env["CLAUDE_BYTE_STREAM_IDLE_TIMEOUT_MS"]);
        Assert.Equal("333333", (string?)env["API_TIMEOUT_MS"]);
        Assert.Equal("7", (string?)env["CLAUDE_CODE_MAX_RETRIES"]);
        Assert.Equal("false", (string?)env["CLAUDE_ENABLE_STREAM_WATCHDOG"]);
        Assert.Equal("true", (string?)env["CLAUDE_ENABLE_BYTE_WATCHDOG"]);
        Assert.Equal("0", (string?)env["_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL"]);
        Assert.Equal("false", (string?)env["DISABLE_ERROR_REPORTING"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK"]);
        Assert.Equal(claudeOriginal, File.ReadAllText(claudePath + ".bak"));

        var codexHome = Path.Combine(isolated.Path, "codex-home");
        Directory.CreateDirectory(codexHome);
        var codexPath = Path.Combine(codexHome, "config.toml");
        var codexOriginal = """
            model = "gpt-user-choice"
            model_provider = "old"

            [model_providers.copilot-bridge]
            name = "old-name"
            base_url = "http://old.example/codex"
            wire_api = "chat"
            stream_idle_timeout_ms = 444444 # preserve idle
            request_max_retries = 1
            stream_max_retries = 2
            websocket_connect_timeout_ms = 5555
            supports_websockets = true
            query_params = { region = "user" }
            http_headers = { X-Custom = "keep" }
            env_http_headers = { Authorization = "KEEP_ENV" }

            [model_providers.rival]
            name = 'rival-preserve'
            """;
        File.WriteAllText(codexPath, codexOriginal);

        var codex = await RunCliAsync(
            bridge,
            ["config", "codex", "--port", "19001"],
            new Dictionary<string, string?> { ["CODEX_HOME"] = codexHome });
        _output.WriteLine(codex.Stdout);
        _output.WriteLine(codex.Stderr);
        Assert.Equal(0, codex.ExitCode);

        var merged = File.ReadAllText(codexPath).ReplaceLineEndings("\n");
        Assert.Contains("model = \"gpt-user-choice\"", merged);
        Assert.Contains("stream_idle_timeout_ms = 444444 # preserve idle", merged);
        Assert.Contains("request_max_retries = 1", merged);
        Assert.Contains("stream_max_retries = 2", merged);
        Assert.Contains("websocket_connect_timeout_ms = 5555", merged);
        Assert.Contains("supports_websockets = true", merged);
        Assert.Contains("query_params = { region = \"user\" }", merged);
        Assert.Contains("http_headers = { X-Custom = \"keep\" }", merged);
        Assert.Contains("env_http_headers = { Authorization = \"KEEP_ENV\" }", merged);
        Assert.Contains("[model_providers.rival]\nname = 'rival-preserve'", merged);
        Assert.Equal(codexOriginal, File.ReadAllText(codexPath + ".bak"));
    }

    private static async Task<CliResult> RunCliAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var pair in environment)
        {
            if (pair.Value is null) start.Environment.Remove(pair.Key);
            else start.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(executable)} did not exit within 30 seconds.");
        }
        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
