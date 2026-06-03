using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins down ResponseModelRewriteStage: buffered bodies have the top-level
/// model field flipped back to OriginalRequestedModel, streaming pipelines
/// rewrite the first message_start event's message.model, every other
/// shape passes through untouched, and the Enabled=false config disables
/// the whole stage as a no-op.
/// </summary>
public class ResponseModelRewriteStageTests
{
    private static BridgeContext<MessagesRequest> Ctx(
        string? originalModel,
        string resolvedModel,
        ResponseMode mode,
        byte[]? bufferedBody = null,
        IAsyncEnumerable<SseItem<string>>? eventStream = null)
    {
        var body = new MessagesRequest
        {
            Model = resolvedModel,
            Messages = Array.Empty<MessageParam>(),
        };
        return new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST", Path = "/cc/v1/messages", Body = body,
            },
            Response = new BridgeResponse
            {
                Mode = mode,
                BufferedBody = bufferedBody,
                EventStream = eventStream,
            },
            Ct = default,
            OriginalRequestedModel = originalModel,
        };
    }

    private static ResponseModelRewriteStage NewStage(bool enabled = true) =>
        new(NullLogger<ResponseModelRewriteStage>.Instance,
            Options.Create(new ResponseModelRewriteOptions { Enabled = enabled }));

    [Fact]
    public async Task BufferedBody_RewritesTopLevelModelField()
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
        var ctx = Ctx("claude-opus-4-8", "claude-opus-4.7-1m-internal",
            ResponseMode.Buffered, bufferedBody: Encoding.UTF8.GetBytes(body));

        await NewStage().ApplyAsync(ctx);

        var node = JsonNode.Parse(ctx.Response.BufferedBody!)!;
        Assert.Equal("claude-opus-4-8", node["model"]!.GetValue<string>());
        Assert.Equal("msg_01", node["id"]!.GetValue<string>());
        Assert.Equal(10, node["usage"]!["input_tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task BufferedBody_OriginalEqualsResolved_NoOp()
    {
        var original = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-7"}""");
        var ctx = Ctx("claude-opus-4-7", "claude-opus-4-7",
            ResponseMode.Buffered, bufferedBody: original);

        await NewStage().ApplyAsync(ctx);

        Assert.Same(original, ctx.Response.BufferedBody);
    }

    [Fact]
    public async Task BufferedBody_MalformedJson_LeavesBytesUntouched()
    {
        var original = Encoding.UTF8.GetBytes("not json");
        var ctx = Ctx("claude-opus-4-8", "claude-opus-4-7",
            ResponseMode.Buffered, bufferedBody: original);

        await NewStage().ApplyAsync(ctx);

        Assert.Same(original, ctx.Response.BufferedBody);
    }

    [Fact]
    public async Task BufferedBody_NoModelField_LeavesBytesUntouched()
    {
        var original = Encoding.UTF8.GetBytes("""{"id":"msg_01"}""");
        var ctx = Ctx("claude-opus-4-8", "claude-opus-4-7",
            ResponseMode.Buffered, bufferedBody: original);

        await NewStage().ApplyAsync(ctx);

        Assert.Same(original, ctx.Response.BufferedBody);
    }

    [Fact]
    public async Task BufferedBody_OriginalNull_NoOp()
    {
        // Router never ran (e.g. parse-error short circuit) — leave the body alone.
        var original = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-7"}""");
        var ctx = Ctx(originalModel: null, resolvedModel: "claude-opus-4-7",
            ResponseMode.Buffered, bufferedBody: original);

        await NewStage().ApplyAsync(ctx);

        Assert.Same(original, ctx.Response.BufferedBody);
    }

    [Fact]
    public async Task Enabled_False_BufferedBody_NoOp()
    {
        var original = Encoding.UTF8.GetBytes("""{"model":"claude-opus-4-7"}""");
        var ctx = Ctx("claude-opus-4-8", "claude-opus-4-7",
            ResponseMode.Buffered, bufferedBody: original);

        await NewStage(enabled: false).ApplyAsync(ctx);

        Assert.Same(original, ctx.Response.BufferedBody);
    }

    [Fact]
    public async Task Enabled_False_Streaming_NoWrapperInstalled()
    {
        async IAsyncEnumerable<SseItem<string>> Source()
        {
            yield return new SseItem<string>(
                """{"type":"message_start","message":{"model":"claude-opus-4-7"}}""",
                "message_start");
            await Task.CompletedTask;
        }

        var source = Source();
        var ctx = Ctx("claude-opus-4-8", "claude-opus-4-7",
            ResponseMode.Streaming, eventStream: source);

        await NewStage(enabled: false).ApplyAsync(ctx);

        Assert.Same(source, ctx.Response.EventStream);
    }

    [Fact]
    public async Task Streaming_RewritesFirstMessageStart_OnlyOnce()
    {
        async IAsyncEnumerable<SseItem<string>> Source()
        {
            yield return new SseItem<string>(
                """{"type":"message_start","message":{"id":"msg_1","model":"claude-opus-4-7","usage":{"input_tokens":1,"output_tokens":0}}}""",
                "message_start");
            yield return new SseItem<string>(
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}""",
                "content_block_delta");
            // A second message_start should NOT be rewritten — only the first.
            yield return new SseItem<string>(
                """{"type":"message_start","message":{"id":"msg_2","model":"claude-opus-4-7"}}""",
                "message_start");
            await Task.CompletedTask;
        }

        var ctx = Ctx("claude-opus-4-8", "claude-opus-4.7-1m-internal",
            ResponseMode.Streaming, eventStream: Source());

        await NewStage().ApplyAsync(ctx);

        var collected = new List<SseItem<string>>();
        await foreach (var evt in ctx.Response.EventStream!)
        {
            collected.Add(evt);
        }

        Assert.Equal(3, collected.Count);
        Assert.Contains("\"model\":\"claude-opus-4-8\"", collected[0].Data);
        Assert.Equal("hi", JsonNode.Parse(collected[1].Data)!["delta"]!["text"]!.GetValue<string>());
        // Second message_start was passed through verbatim.
        Assert.Contains("\"model\":\"claude-opus-4-7\"", collected[2].Data);
    }

    [Fact]
    public async Task Streaming_MessageStartMissingModelField_PassesThrough()
    {
        async IAsyncEnumerable<SseItem<string>> Source()
        {
            yield return new SseItem<string>(
                """{"type":"message_start","message":{"id":"msg_1"}}""",
                "message_start");
            await Task.CompletedTask;
        }

        var ctx = Ctx("claude-opus-4-8", "claude-opus-4-7",
            ResponseMode.Streaming, eventStream: Source());

        await NewStage().ApplyAsync(ctx);

        await foreach (var evt in ctx.Response.EventStream!)
        {
            // No model field → no rewrite, data unchanged.
            Assert.DoesNotContain("claude-opus-4-8", evt.Data);
        }
    }
}
