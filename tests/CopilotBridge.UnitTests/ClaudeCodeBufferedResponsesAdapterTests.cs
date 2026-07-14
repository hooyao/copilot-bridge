using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract for Claude Code's recovery request after a streaming error. Claude
/// reissues the same `/cc` turn with `stream:false`; when routing selected a
/// Responses backend, buffered T3 must produce Anthropic response IR before the
/// response pipeline runs; the Claude edge then remains identity.
/// </summary>
public class ClaudeCodeBufferedResponsesAdapterTests
{
    private static readonly ClaudeCodeOutboundAdapter Adapter =
        new(NullLogger<ClaudeCodeOutboundAdapter>.Instance);

    private static byte[] Adapt(string json) =>
        BufferedResponsesToAnthropic.TryTranslate(Encoding.UTF8.GetBytes(json))
        ?? throw new Xunit.Sdk.XunitException("expected a successful Responses object");

    [Fact]
    public void CompletedTextResponse_BecomesAnthropicMessage()
    {
        var output = Adapt("""
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
    public void FunctionCallResponse_BecomesExecutableAnthropicToolUse()
    {
        var output = Adapt("""
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
    public async Task NonSuccessfulResponsesBodies_AreNotMisclassified_AndClaudeIrIsIdentity(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        Assert.Null(BufferedResponsesToAnthropic.TryTranslate(bytes));
        var output = await Adapter.AdaptBufferedAsync(bytes, default);
        Assert.Same(bytes, output);
    }

    [Fact]
    public void BufferedResponseFailed_RemainsExceptionalUntilClientBoundary()
    {
        var bytes = Encoding.UTF8.GetBytes(
            "{\"id\":\"resp_failed\",\"object\":\"response\",\"status\":\"failed\",\"error\":{\"code\":\"server_error\"}}");

        var ex = Assert.Throws<UpstreamResponseFailedException>(() =>
            BufferedResponsesToAnthropic.TryTranslate(bytes));

        Assert.Equal("server_error", ex.Code);
    }

    [Fact]
    public void MalformedSuccessfulToolCall_FailsClosed_InsteadOfReturningRawResponses()
    {
        var bytes = Encoding.UTF8.GetBytes("""
        {"id":"resp_bad","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"function_call","call_id":"call_bad","name":"Bash","arguments":"{not-json"}]}
        """);

        var ex = Assert.Throws<UpstreamResponseFailedException>(() =>
            BufferedResponsesToAnthropic.TryTranslate(bytes));

        Assert.Equal("invalid_buffered_response", ex.Code);
    }

    [Fact]
    public void IncompleteResponse_OverridesToolUseStopReason()
    {
        var output = Adapt("""
        {"id":"resp_limit","object":"response","status":"incomplete","model":"gpt-5.6-sol",
         "output":[{"type":"function_call","call_id":"call_partial","name":"Bash","arguments":"{}"}]}
        """);

        using var doc = JsonDocument.Parse(output);
        Assert.Equal("max_tokens", doc.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task BufferedCustomToolMarkers_AreRemovedAtClaudeEdge()
    {
        var ir = Adapt("""
        {"id":"resp_custom","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"custom_tool_call","call_id":"call_exec","name":"exec","input":"return 1;","namespace":"collaboration"}]}
        """);

        var output = await Adapter.AdaptBufferedAsync(ir, default);
        var text = Encoding.UTF8.GetString(output);
        Assert.DoesNotContain("bridge_input_is_grammar_text", text);
        Assert.DoesNotContain("bridge_tool_namespace", text);
        using var doc = JsonDocument.Parse(output);
        var tool = Assert.Single(doc.RootElement.GetProperty("content").EnumerateArray());
        Assert.Equal("return 1;", tool.GetProperty("input").GetString());
    }

    // ── custom_tool_call id round-trip (buffered) ────────────────────────────────
    // Contract: Copilot's /responses requires the echoed custom_tool_call id to begin
    // with `ctc`. Buffered T3 must carry the upstream `ctc_` id through the IR, and
    // buffered T4 must re-emit an id that begins with `ctc` (never the old item_N).

    private static byte[] BufferedT4(byte[] ir) =>
        BufferedAnthropicToResponses.TryTranslate(ir)
        ?? throw new Xunit.Sdk.XunitException("expected a successful IR message");

    [Fact]
    public void BufferedRoundTrip_RealCtcId_SurvivesToTheCodexFacingItem()
    {
        // Upstream custom_tool_call carries its real ctc_ id. Buffered T3 (into IR) then
        // buffered T4 (back to Responses) MUST re-emit that exact id on the item — the
        // value Copilot requires Codex to echo next turn.
        var ir = Adapt("""
        {"id":"resp_custom","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"custom_tool_call","id":"ctc_0679bd5b187491ee","call_id":"call_exec","name":"exec","input":"return 1;"}]}
        """);

        var responses = BufferedT4(ir);
        using var doc = JsonDocument.Parse(responses);
        var item = Assert.Single(doc.RootElement.GetProperty("output").EnumerateArray());
        Assert.Equal("custom_tool_call", item.GetProperty("type").GetString());
        Assert.Equal("ctc_0679bd5b187491ee", item.GetProperty("id").GetString());
    }

    [Fact]
    public void BufferedRoundTrip_NoCtcId_SynthesizesCtcPrefixedId()
    {
        // Upstream item id is an opaque non-ctc blob (or absent). Buffered T3 drops it;
        // buffered T4 MUST synthesize a ctc-prefixed id, never emit item_N.
        var ir = Adapt("""
        {"id":"resp_custom","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"custom_tool_call","id":"OPAQUEBLOB","call_id":"call_exec","name":"exec","input":"return 1;"}]}
        """);

        var responses = BufferedT4(ir);
        using var doc = JsonDocument.Parse(responses);
        var item = Assert.Single(doc.RootElement.GetProperty("output").EnumerateArray());
        var id = item.GetProperty("id").GetString()!;
        Assert.StartsWith("ctc", id, StringComparison.Ordinal);
        Assert.DoesNotContain("item_", id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BufferedCustomToolCallIdMarker_IsRemovedAtClaudeEdge()
    {
        // The bridge_custom_tool_call_id marker T3 stamps on the tool_use block must be
        // scrubbed before reaching claude.exe on the CC→gpt buffered route.
        var ir = Adapt("""
        {"id":"resp_custom","object":"response","status":"completed","model":"gpt-5.6-sol",
         "output":[{"type":"custom_tool_call","id":"ctc_0679bd5b187491ee","call_id":"call_exec","name":"exec","input":"return 1;"}]}
        """);
        Assert.Contains("bridge_custom_tool_call_id", Encoding.UTF8.GetString(ir));

        var output = await Adapter.AdaptBufferedAsync(ir, default);
        Assert.DoesNotContain("bridge_custom_tool_call_id", Encoding.UTF8.GetString(output));
    }
}
