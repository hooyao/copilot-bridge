using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// End-to-end MCP tool calling through the bridge. Spawns a tiny stdio MCP
/// echo server (<c>mcp-echo-server.py</c>) and configures Claude Code to load
/// it via <c>--mcp-config</c>. The prompt asks the model to invoke the MCP
/// tool with a canary string; assertions:
/// <list type="number">
///   <item>The bridge's upstream <c>tools[]</c> contains an entry named
///         <c>mcp__echo__echo</c> — confirms the MCP loader registered the
///         tool and Claude Code forwarded it to the API.</item>
///   <item>Bridge upstream bodies show <c>tool_use</c> + <c>tool_result</c>
///         content blocks across the multi-turn exchange.</item>
///   <item>Claude Code's stdout includes the canary so we know the round-trip
///         actually ran (model decided to call → MCP echoed → model used it).</item>
/// </list>
/// Documents WebSearch's replacement path: when users want web search, they
/// configure a custom MCP server (e.g. <c>api.microsoft.ai/v3/mcp</c>) instead
/// of the unsupported <c>web_search_*</c> built-in.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class McpToolUseTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public McpToolUseTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Fact]
    public async Task McpEchoServer_FullToolRoundTrip()
    {
        const string canary = "bridge-mcp-canary-99421";
        var prompt = $"Use the `echo` tool with text \"{canary}\" and then tell me exactly what the tool returned.";

        // Write a temp MCP config pointing at the bundled echo server script.
        // The server script is copied to the test output directory at build time
        // (see CopilotBridge.Playground.csproj).
        var serverScript = Path.Combine(AppContext.BaseDirectory, "Headless", "mcp-echo-server.py");
        Assert.True(File.Exists(serverScript), $"MCP echo server script missing: {serverScript}");

        var mcpConfig = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["echo"] = new JsonObject
                {
                    ["command"] = "python",
                    ["args"] = new JsonArray { serverScript },
                },
            },
        };
        var configPath = Path.Combine(Path.GetTempPath(), $"mcp-cfg-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(configPath, mcpConfig.ToJsonString());

        var reader = new BridgeLogReader(_bridge.LogDirectory);

        try
        {
            var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
                BridgeBaseUrl: _bridge.BaseUrl,
                Prompt: prompt,
                Model: "claude-sonnet-4-6",
                Effort: null,
                OutputFormat: "json",
                AllowedTools: "mcp__echo__echo",
                McpConfigPath: configPath));

            _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");

            var entries = reader.ReadNew()
                .Where(e => e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal))
                .ToList();

            var sawMcpToolDecl = false;
            var sawToolUse = false;
            var sawToolResult = false;
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var toolNames = ExtractToolNames(e.UpstreamBody);
                var blockTypes = ExtractContentBlockTypes(e.UpstreamBody);
                _output.WriteLine($"  [{i}] status={e.UpstreamStatus} tools=[{string.Join(",", toolNames)}] blocks=[{string.Join(",", blockTypes)}]");
                if (toolNames.Any(n => n.StartsWith("mcp__echo__"))) sawMcpToolDecl = true;
                if (blockTypes.Contains("tool_use")) sawToolUse = true;
                if (blockTypes.Contains("tool_result")) sawToolResult = true;
            }

            if (result.ExitCode != 0)
            {
                _output.WriteLine("=== stdout ===");
                _output.WriteLine(result.Stdout);
                _output.WriteLine("=== stderr ===");
                _output.WriteLine(result.Stderr);
            }

            Assert.Equal(0, result.ExitCode);
            Assert.True(sawMcpToolDecl, "Expected at least one upstream body to declare an mcp__echo__* tool — Claude Code didn't surface MCP tools to the API.");
            Assert.True(sawToolUse, "Expected at least one upstream body to contain a tool_use block — model never invoked the MCP tool.");
            Assert.True(sawToolResult, "Expected at least one upstream body to contain a tool_result block — claude.exe never forwarded the MCP server's response.");
            Assert.Contains(canary, result.Stdout);
        }
        finally
        {
            try { File.Delete(configPath); } catch { /* best effort */ }
        }
    }

    private static IReadOnlyList<string> ExtractToolNames(JsonNode? body)
    {
        if (body is not JsonObject obj || obj["tools"] is not JsonArray tools)
            return Array.Empty<string>();
        var names = new List<string>(tools.Count);
        foreach (var t in tools)
        {
            if (t is JsonObject to && to["name"]?.GetValue<string>() is { } n) names.Add(n);
        }
        return names;
    }

    private static IReadOnlyList<string> ExtractContentBlockTypes(JsonNode? body)
    {
        if (body is not JsonObject obj || obj["messages"] is not JsonArray msgs)
            return Array.Empty<string>();
        var types = new List<string>();
        foreach (var m in msgs)
        {
            if (m is not JsonObject mo || mo["content"] is not JsonArray content) continue;
            foreach (var c in content)
            {
                if (c is JsonObject co && co["type"]?.GetValue<string>() is { } t) types.Add(t);
            }
        }
        return types;
    }
}
