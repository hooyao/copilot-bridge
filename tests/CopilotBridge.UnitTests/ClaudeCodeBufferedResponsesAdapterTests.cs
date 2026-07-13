using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for Claude Code's recovery request after a streaming error. Claude
/// reissues the same `/cc` turn with `stream:false`; when routing selected a
/// Responses backend, the Claude client edge must return Anthropic Messages JSON,
/// never the upstream Responses object.
/// </summary>
public class ClaudeCodeBufferedResponsesAdapterTests
{
    private static readonly ClaudeCodeOutboundAdapter Adapter =
        new(NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    private static async Task<byte[]> Adapt(string json) =>
        await Adapter.AdaptBufferedAsync(Encoding.UTF8.GetBytes(json), default);

    [Fact]
    public async Task CompletedTextResponse_BecomesAnthropicMessage()
    {
        var output = await Adapt("""
        {"id":"resp_text","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"message","id":"msg_1","role":"assistant","status":"completed",
                    "content":[{"type":"output_text","text":"recovered answer","annotations":[]}]}],
         "usage":{"input_tokens":11,"output_tokens":7,"total_tokens":18}}
        """);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("gpt-5.6-sol", root.GetProperty("model").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.Equal("text", root.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("recovered answer", root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(11, root.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.Equal(7, root.GetProperty("usage").GetProperty("output_tokens").GetInt64());
        Assert.False(root.TryGetProperty("object", out _));
        Assert.False(root.TryGetProperty("output", out _));
    }

    [Fact]
    public async Task FunctionCallResponse_BecomesExecutableAnthropicToolUse()
    {
        var output = await Adapt("""
        {"id":"resp_tool","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"function_call","id":"item_1","call_id":"call_bash","name":"Bash",
                    "arguments":"{\"command\":\"echo recovered\",\"description\":\"Write recovery marker\"}","status":"completed"}],
         "usage":{"input_tokens":13,"output_tokens":9,"total_tokens":22}}
        """);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var tool = Assert.Single(root.GetProperty("content").EnumerateArray());
        Assert.Equal("tool_use", tool.GetProperty("type").GetString());
        Assert.Equal("call_bash", tool.GetProperty("id").GetString());
        Assert.Equal("Bash", tool.GetProperty("name").GetString());
        Assert.Equal("echo recovered", tool.GetProperty("input").GetProperty("command").GetString());
        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
    }

    [Theory]
    [InlineData("{\"type\":\"message\",\"role\":\"assistant\",\"content\":[],\"model\":\"claude-opus-4.8\"}")]
    [InlineData("{\"error\":{\"type\":\"api_error\",\"message\":\"upstream failed\"}}")]
    [InlineData("{\"id\":\"resp_failed\",\"object\":\"response\",\"status\":\"failed\",\"error\":{\"code\":\"server_error\"}}")]
    public async Task NonSuccessfulResponsesBodies_PassThroughByteIdentical(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var output = await Adapter.AdaptBufferedAsync(bytes, default);
        Assert.Same(bytes, output);
    }
}
