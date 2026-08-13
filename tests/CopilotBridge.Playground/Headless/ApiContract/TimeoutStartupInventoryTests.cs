using System.Runtime.Versioning;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>Real-process contract for the operator-visible startup inventory.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class TimeoutStartupInventoryTests
{
    private readonly ITestOutputHelper _output;

    public TimeoutStartupInventoryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Scratch_bridge_prints_the_approved_dynamic_inventory_exactly()
    {
        using var configs = ClientBehaviorSupport.NewWorkDir("timeout-startup-inventory");
        var claudePath = Path.Combine(configs.Path, "settings.json");
        var codexPath = Path.Combine(configs.Path, "config.toml");
        File.WriteAllText(claudePath, """
            { "env": { "ANTHROPIC_BASE_URL": "http://localhost:8765/cc" } }
            """);
        File.WriteAllText(codexPath, """
            model_provider = "copilot-bridge"
            [model_providers.copilot-bridge]
            name = "copilot-bridge"
            base_url = "http://localhost:8765/codex"
            wire_api = "responses"
            """);
        var projectDirectory = Path.Combine(configs.Path, "project");
        var projectCodexDirectory = Path.Combine(projectDirectory, ".codex");
        Directory.CreateDirectory(projectCodexDirectory);
        File.WriteAllText(Path.Combine(projectCodexDirectory, "config.toml"), """
            [model_providers.copilot-bridge]
            stream_idle_timeout_ms = 1000
            request_max_retries = 0
            stream_max_retries = 0
            """);

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.PassthroughTestUpstream,
            TestUpstreamBaseUrl: "http://127.0.0.1:1",
            ClaudeSettingsPath: claudePath,
            CodexConfigPath: codexPath,
            WorkingDirectory: projectDirectory));

        const string marker = "Timeouts (observed configuration; startup does not rewrite values):";
        const string lastLine = "  * = client built-in default";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string stderr;
        do
        {
            stderr = bridge.StderrAll;
            if (stderr.Contains(lastLine, StringComparison.Ordinal)) break;
            await Task.Delay(50);
        } while (DateTime.UtcNow < deadline);

        var start = stderr.IndexOf(marker, StringComparison.Ordinal);
        var last = stderr.IndexOf(lastLine, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && last >= start, $"timeout inventory missing from real stderr:\n{stderr}");
        var actual = stderr[start..(last + lastLine.Length)].ReplaceLineEndings();
        _output.WriteLine($"scratch bridge: {bridge.BaseUrl}");
        _output.WriteLine(actual);

        var expected = $$"""
            Timeouts (observed configuration; startup does not rewrite values):
              Bridge — appsettings.json
                upstream response headers  4m / send attempt
                upstream SSE event gap     4m / parsed event gap
                downstream keepalive       15s, after first upstream event
                network retries            2
                buffered body              no limit after headers
              Claude Code — {{claudePath}} (global only)
                SSE event idle             unset -> 5m*
                SSE byte idle              unset -> 5m*
                request timeout            unset -> normal 10m*; after stream error 5m*
                retries                    not visible at bridge startup
              Codex — {{codexPath}} (global only)
                SSE event idle             unset -> 5m* / parsed event
                request retries            unset -> 4*
                stream retries             unset -> 5*
                whole request              no limit
              note: timeouts apply per attempt; a retry starts a new attempt, so there is no fixed whole-turn limit
              scope: global client configs only; project/profile/CLI/env overrides are not included
              * = client built-in default
            """.ReplaceLineEndings();

        Assert.Equal(expected, actual);
        Assert.NotEqual(8765, new Uri(bridge.BaseUrl).Port);
    }
}
