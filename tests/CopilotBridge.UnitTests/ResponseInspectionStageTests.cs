using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for the tool-leak guard end-to-end through
/// <see cref="ResponseInspectionStage"/>: streaming abort injects one error event
/// and stops; clean/negative streams pass through unchanged; the disabled guard
/// is inert; the buffered-delivery mode emits a real status; and the signal
/// selects the error type / status. Mirrors the behavior a Claude Code client
/// would observe.
/// </summary>
public class ResponseInspectionStageTests
{
    private static readonly string[] Tools = { "Read", "Edit", "Bash" };

    private static ResponseInspectionStage Stage(ToolLeakGuardOptions leak, bool rewriteEnabled = false)
    {
        var factory = new DetectorSetFactory(
            Options.Create(new ResponseModelRewriteOptions { Enabled = rewriteEnabled }),
            Options.Create(leak));
        return new ResponseInspectionStage(factory, NullLogger<ResponseInspectionStage>.Instance);
    }

    private static BridgeContext<MessagesRequest> Ctx(
        IAsyncEnumerable<SseItem<string>>? stream,
        ResponseMode mode = ResponseMode.Streaming,
        byte[]? buffered = null,
        IReadOnlyList<Tool>? tools = null)
    {
        var body = new MessagesRequest
        {
            Model = "claude-opus-4-8",
            Messages = Array.Empty<MessageParam>(),
            Tools = tools ?? Tools.Select(n => new Tool { Name = n }).ToArray(),
        };
        return new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest> { Method = "POST", Path = "/cc/v1/messages", Body = body },
            Response = new BridgeResponse { Mode = mode, EventStream = stream, BufferedBody = buffered },
            Ct = default,
        };
    }

    // Builds a streaming leak: message_start → text block (start+delta+stop) → message_delta/stop.
    private static async IAsyncEnumerable<SseItem<string>> LeakStream(string text)
    {
        yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
        yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""", "content_block_start");
        yield return new SseItem<string>(
            $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{System.Text.Json.JsonSerializer.Serialize(text)}}}}}",
            "content_block_delta");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>("""{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""", "message_delta");
        yield return new SseItem<string>("""{"type":"message_stop"}""", "message_stop");
        await Task.CompletedTask;
    }

    private static async Task<List<SseItem<string>>> Drain(IAsyncEnumerable<SseItem<string>> s)
    {
        var list = new List<SseItem<string>>();
        await foreach (var e in s) list.Add(e);
        return list;
    }

    private const string Leak =
        "Let me read the file.\ncourt\n<invoke name=\"Read\">\n<parameter name=\"file_path\">/x</parameter>\n</invoke>";

    // ---- Streaming abort (default delivery) ------------------------------

    [Fact]
    public async Task StreamingLeak_InjectsOneErrorEvent_ThenStops()
    {
        // Contract: on a detected leak, exactly one SSE error event is emitted and
        // no further upstream events flow; the flag is set.
        var ctx = Ctx(LeakStream(Leak));
        await Stage(new ToolLeakGuardOptions { Enabled = true }).ApplyAsync(ctx);

        var got = await Drain(ctx.Response.EventStream!);

        // message_start + content_block_start + (delta suppressed at detection) + error, then stop.
        Assert.Equal("error", got[^1].EventType);
        Assert.Contains("overloaded_error", got[^1].Data);
        Assert.DoesNotContain(got, e => e.EventType == "message_stop"); // stream ended early
        Assert.True(ctx.ToolLeakDetected);
    }

    [Fact]
    public async Task StreamingClean_PassesThroughUnchanged()
    {
        // A benign text block (no closed invoke) → every event relayed verbatim.
        var clean = "I'll read the file now, then edit it.";
        var ctx = Ctx(LeakStream(clean));
        await Stage(new ToolLeakGuardOptions { Enabled = true }).ApplyAsync(ctx);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("message_stop", got[^1].EventType);
        Assert.False(ctx.ToolLeakDetected);
        Assert.DoesNotContain(got, e => e.EventType == "error");
    }

    [Fact]
    public async Task StreamingFencedInvoke_NotDetected()
    {
        var fenced = "Here's how:\n```\n<invoke name=\"Read\"><parameter name=\"p\">v</parameter></invoke>\n```";
        var ctx = Ctx(LeakStream(fenced));
        await Stage(new ToolLeakGuardOptions { Enabled = true }).ApplyAsync(ctx);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ToolLeakDetected);
        Assert.Equal("message_stop", got[^1].EventType);
    }

    [Fact]
    public async Task StreamingThinkingDisabled_NotDetectedInThinking()
    {
        // Same leak but in a thinking block, with ScanThinking off.
        async IAsyncEnumerable<SseItem<string>> ThinkingLeak()
        {
            yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""", "content_block_start");
            yield return new SseItem<string>(
                $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"thinking_delta\",\"thinking\":{System.Text.Json.JsonSerializer.Serialize(Leak)}}}}}",
                "content_block_delta");
            yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
            await Task.CompletedTask;
        }
        var ctx = Ctx(ThinkingLeak());
        await Stage(new ToolLeakGuardOptions { Enabled = true, ScanThinking = false }).ApplyAsync(ctx);

        await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ToolLeakDetected);
    }

    [Fact]
    public async Task Disabled_Inert_SameStreamReference()
    {
        // Contract: Enabled=false (and rewrite off) → no detector set → stage does
        // not even wrap the stream (same reference passes through).
        var src = LeakStream(Leak);
        var ctx = Ctx(src);
        await Stage(new ToolLeakGuardOptions { Enabled = false }).ApplyAsync(ctx);
        // DoneFilter + (disabled) rewrite still produce a set, so the stream is
        // wrapped — but no leak abort occurs. Assert the leak is NOT acted on.
        var got = await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ToolLeakDetected);
        Assert.DoesNotContain(got, e => e.EventType == "error");
    }

    // ---- Buffered delivery (PreserveStream=false) ------------------------

    [Fact]
    public async Task BufferedDelivery_Leak_EmitsRealStatus_NoLeakedContent()
    {
        var ctx = Ctx(LeakStream(Leak));
        await Stage(new ToolLeakGuardOptions { Enabled = true, PreserveStream = false }).ApplyAsync(ctx);

        // Flipped to buffered with a real 529 and an error body; no leaked XML.
        Assert.Equal(ResponseMode.Buffered, ctx.Response.Mode);
        Assert.Equal(529, ctx.Response.Status);
        var body = Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("overloaded_error", body);
        Assert.DoesNotContain("<invoke", body);
        Assert.True(ctx.ToolLeakDetected);
    }

    [Fact]
    public async Task BufferedDelivery_Clean_ReplaysStream()
    {
        var ctx = Ctx(LeakStream("nothing to see"));
        await Stage(new ToolLeakGuardOptions { Enabled = true, PreserveStream = false }).ApplyAsync(ctx);

        // Clean → still streaming, replayed intact.
        Assert.Equal(ResponseMode.Streaming, ctx.Response.Mode);
        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("message_stop", got[^1].EventType);
        Assert.False(ctx.ToolLeakDetected);
    }

    // ---- Signal mapping --------------------------------------------------

    [Fact]
    public async Task ApiErrorSignal_Streaming_EmitsApiError()
    {
        var ctx = Ctx(LeakStream(Leak));
        await Stage(new ToolLeakGuardOptions { Enabled = true, Signal = ToolLeakSignal.ApiError }).ApplyAsync(ctx);
        var got = await Drain(ctx.Response.EventStream!);
        Assert.Contains("api_error", got[^1].Data);
    }

    [Fact]
    public async Task ApiErrorSignal_Buffered_Emits500()
    {
        var ctx = Ctx(LeakStream(Leak));
        await Stage(new ToolLeakGuardOptions { Enabled = true, PreserveStream = false, Signal = ToolLeakSignal.ApiError }).ApplyAsync(ctx);
        Assert.Equal(500, ctx.Response.Status);
        Assert.Contains("api_error", Encoding.UTF8.GetString(ctx.Response.BufferedBody!));
    }

    // ---- Migration regression: DONE filter still works -------------------

    [Fact]
    public async Task DoneEvent_Dropped_AndRecorded()
    {
        async IAsyncEnumerable<SseItem<string>> WithDone()
        {
            yield return new SseItem<string>("""{"type":"message_stop"}""", "message_stop");
            yield return new SseItem<string>("[DONE]", "message");
            await Task.CompletedTask;
        }
        var ctx = Ctx(WithDone());
        await Stage(new ToolLeakGuardOptions { Enabled = true }).ApplyAsync(ctx);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.DoesNotContain(got, e => e.Data == "[DONE]");
        Assert.Contains(ctx.DroppedEvents, d => d.Data == "[DONE]");
    }
}
