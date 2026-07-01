using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Regression gate for the model-rewrite behavior after its migration from the
/// standalone <c>ResponseModelRewriteStage</c> into <see cref="ModelRewriteDetector"/>.
/// Same observable contract: buffered bodies get the top-level model flipped back
/// to the original requested id; the first (only the first) streaming
/// message_start event's message.model is rewritten; every other shape passes
/// through untouched; and the disabled / original==resolved / original-null cases
/// are no-ops. Assertions mirror the pre-migration tests byte-for-byte.
/// </summary>
public class ResponseModelRewriteStageTests
{
    private static ModelRewriteDetector NewDetector(
        string? original, string resolved, bool enabled = true) =>
        new(enabled, original, resolved);

    // ---- Buffered path ---------------------------------------------------

    [Fact]
    public void BufferedBody_RewritesTopLevelModelField()
    {
        const string body = """
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "model": "claude-opus-4-7",
          "content": [{"type":"text","text":"hi"}],
          "usage": {"input_tokens": 10, "output_tokens": 2}
        }
        """;
        var d = NewDetector("claude-opus-4-8", "claude-opus-4.7-1m-internal");

        var action = d.InspectBuffered(Encoding.UTF8.GetBytes(body));

        Assert.Equal(DetectionActionKind.RewriteEvent, action.Kind);
        var node = JsonNode.Parse(action.Event.Data)!;
        Assert.Equal("claude-opus-4-8", node["model"]!.GetValue<string>());
        Assert.Equal("msg_01", node["id"]!.GetValue<string>());
        Assert.Equal(10, node["usage"]!["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void BufferedBody_OriginalEqualsResolved_NoOp()
    {
        var d = NewDetector("claude-opus-4-7", "claude-opus-4-7");
        var action = d.InspectBuffered(Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-7"}"""));
        Assert.Equal(DetectionActionKind.None, action.Kind);
    }

    [Fact]
    public void BufferedBody_MalformedJson_LeavesBytesUntouched()
    {
        var d = NewDetector("claude-opus-4-8", "claude-opus-4-7");
        var action = d.InspectBuffered(Encoding.UTF8.GetBytes("not json"));
        Assert.Equal(DetectionActionKind.None, action.Kind);
    }

    [Fact]
    public void BufferedBody_NoModelField_LeavesBytesUntouched()
    {
        var d = NewDetector("claude-opus-4-8", "claude-opus-4-7");
        var action = d.InspectBuffered(Encoding.UTF8.GetBytes("""{"id":"msg_01"}"""));
        Assert.Equal(DetectionActionKind.None, action.Kind);
    }

    [Fact]
    public void BufferedBody_UpstreamPromptTooLongError_RelayedVerbatim()
    {
        // Graceful degradation for sonnet/haiku "1M": an error body has no
        // top-level "model" key, so the rewrite must NEVER touch it (else CC's
        // "prompt is too long" self-heal breaks). original != resolved forces the
        // detector to be active, proving the guard holds even on the rewrite path.
        var errorBody = Encoding.UTF8.GetBytes(
            """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 206217 tokens > 200000 maximum"},"request_id":"req_vrtx_x"}""");
        var d = NewDetector("claude-opus-4-8", "claude-opus-4.7-1m-internal");

        var action = d.InspectBuffered(errorBody);

        Assert.Equal(DetectionActionKind.None, action.Kind);
    }

    [Fact]
    public void BufferedBody_OriginalNull_NoOp()
    {
        // Router never ran (e.g. parse-error short circuit) — leave the body alone.
        var d = NewDetector(original: null, resolved: "claude-opus-4-7");
        var action = d.InspectBuffered(Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-7"}"""));
        Assert.Equal(DetectionActionKind.None, action.Kind);
    }

    [Fact]
    public void Enabled_False_BufferedBody_NoOp()
    {
        var d = NewDetector("claude-opus-4-8", "claude-opus-4-7", enabled: false);
        var action = d.InspectBuffered(Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-7"}"""));
        Assert.Equal(DetectionActionKind.None, action.Kind);
    }

    // ---- Streaming path --------------------------------------------------

    [Fact]
    public void Enabled_False_Streaming_NoRewrite()
    {
        var d = NewDetector("claude-opus-4-8", "claude-opus-4-7", enabled: false);
        var evt = new SseItem<string>(
            """{"type":"message_start","message":{"model":"claude-opus-4-7"}}""",
            "message_start");
        Assert.Equal(DetectionActionKind.None, d.InspectEvent(evt).Kind);
    }

    [Fact]
    public void Streaming_RewritesFirstMessageStart_OnlyOnce()
    {
        var d = NewDetector("claude-opus-4-8", "claude-opus-4.7-1m-internal");

        var first = d.InspectEvent(new SseItem<string>(
            """{"type":"message_start","message":{"id":"msg_1","model":"claude-opus-4-7","usage":{"input_tokens":1,"output_tokens":0}}}""",
            "message_start"));
        Assert.Equal(DetectionActionKind.RewriteEvent, first.Kind);
        Assert.Contains("\"model\":\"claude-opus-4-8\"", first.Event.Data);

        var delta = d.InspectEvent(new SseItem<string>(
            """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""",
            "content_block_delta"));
        Assert.Equal(DetectionActionKind.None, delta.Kind);

        // A second message_start should NOT be rewritten — only the first.
        var second = d.InspectEvent(new SseItem<string>(
            """{"type":"message_start","message":{"id":"msg_2","model":"claude-opus-4-7"}}""",
            "message_start"));
        Assert.Equal(DetectionActionKind.None, second.Kind);
    }

    [Fact]
    public void Streaming_MessageStartMissingModelField_PassesThrough()
    {
        var d = NewDetector("claude-opus-4-8", "claude-opus-4-7");
        var action = d.InspectEvent(new SseItem<string>(
            """{"type":"message_start","message":{"id":"msg_1"}}""",
            "message_start"));
        Assert.Equal(DetectionActionKind.None, action.Kind);
    }
}
