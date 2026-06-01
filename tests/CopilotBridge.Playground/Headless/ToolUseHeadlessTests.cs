using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// End-to-end tool-use round-trip via claude.exe headless. The prompt forces
/// the model to invoke <c>Bash</c>; claude.exe handles the tool execution
/// locally and feeds the result back. Asserts the bridge sees the full
/// tool_use → tool_result protocol exchange and Claude Code's final answer
/// contains the canary string the shell command emitted.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
public class ToolUseHeadlessTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public ToolUseHeadlessTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Theory]
    [InlineData("claude-sonnet-4-6", null)]
    [InlineData("claude-opus-4-7",   "high")]
    public async Task BashToolRoundTrip_ReachesFinalAnswer(string claudeModel, string? effort)
    {
        const string canary = "bridge-tool-canary-7421";
        var prompt = $"Run the shell command `echo {canary}` using the Bash tool, then tell me what it printed.";

        var reader = new BridgeLogReader(_bridge.LogDirectory);

        var result = await ClaudeProcess.RunAsync(new ClaudeInvocation(
            BridgeBaseUrl: _bridge.BaseUrl,
            Prompt: prompt,
            Model: claudeModel,
            Effort: effort,
            OutputFormat: "json",
            AllowedTools: "Bash"));

        var entries = reader.ReadNew()
            .Where(e => e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"claude.exe exit={result.ExitCode} duration={result.Duration}");
        _output.WriteLine($"bridge /v1/messages entries: {entries.Count}");

        // Surface what tool blocks each upstream body carried — invaluable when
        // a regression hides in protocol shape rather than status codes.
        var hadToolUse = false;
        var hadToolResult = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var roles = ExtractMessageRoles(e.UpstreamBody);
            var blocks = ExtractContentBlockTypes(e.UpstreamBody);
            _output.WriteLine($"  [{i}] status={e.UpstreamStatus} roles={string.Join(",", roles)} blocks={string.Join(",", blocks)}");
            if (blocks.Contains("tool_use")) hadToolUse = true;
            if (blocks.Contains("tool_result")) hadToolResult = true;
        }

        Assert.Equal(0, result.ExitCode);

        // Tool round-trip needs at least two successful API calls: one returning
        // tool_use, the next carrying tool_result back to the model.
        var successful = entries.Where(e => e.UpstreamStatus is >= 200 and < 300).ToList();
        Assert.True(successful.Count >= 2,
            $"Expected at least 2 successful /v1/messages calls for a tool round-trip; got {successful.Count}.");

        // At least one inbound upstream body should carry the assistant's tool_use
        // block (the request that comes AFTER the model decided to call Bash).
        Assert.True(hadToolUse, "Expected at least one upstream body to contain a tool_use content block — model never invoked the tool.");
        Assert.True(hadToolResult, "Expected at least one upstream body to contain a tool_result content block — claude.exe never forwarded the tool's stdout.");

        // The final user-facing output must echo the canary that the shell printed.
        Assert.Contains(canary, result.Stdout);
    }

    private static IReadOnlyList<string> ExtractMessageRoles(JsonNode? upstreamBody)
    {
        if (upstreamBody is not JsonObject obj || obj["messages"] is not JsonArray msgs)
            return Array.Empty<string>();
        var roles = new List<string>(msgs.Count);
        foreach (var m in msgs)
        {
            if (m is JsonObject mo && mo["role"]?.GetValue<string>() is { } r) roles.Add(r);
        }
        return roles;
    }

    private static IReadOnlyList<string> ExtractContentBlockTypes(JsonNode? upstreamBody)
    {
        if (upstreamBody is not JsonObject obj || obj["messages"] is not JsonArray msgs)
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
