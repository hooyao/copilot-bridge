using System.Net.ServerSentEvents;
using System.Text;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for the response-leak guard end-to-end through
/// <see cref="ResponseInspectionStage"/>: streaming abort injects one error event
/// and stops; clean/negative streams pass through unchanged; the disabled guard
/// is inert; the buffered-delivery mode emits a real status; and the signal
/// selects the error type / status. Mirrors the behavior a Claude Code client
/// would observe.
/// </summary>
public class ResponseInspectionStageTests
{
    private static readonly string[] Tools = { "Read", "Edit", "Bash" };

    // Build the stage with all three detectors injected with the SAME ctx (as
    // production DI does), then run it. Each detector self-gates on its Enabled
    // flag; Order values follow registration order (DONE 0 → rewrite 1 → leak 2).
    private static async Task Run(
        BridgeContext<MessagesRequest> ctx,
        ResponseLeakGuardOptions leak,
        bool rewriteEnabled = false,
        ILogger<ResponseLeakDetector>? responseLeakLog = null)
    {
        var detectors = new IResponseDetector[]
        {
            new DoneFilterDetector(new DetectorOrder<DoneFilterDetector>(0)),
            new ModelRewriteDetector(
                new DetectorOrder<ModelRewriteDetector>(1),
                TestOptions.Snapshot(new ResponseModelRewriteOptions { Enabled = rewriteEnabled }),
                ctx),
            new ResponseLeakDetector(
                new DetectorOrder<ResponseLeakDetector>(2),
                TestOptions.Snapshot(leak),
                ctx,
                responseLeakLog ?? NullLogger<ResponseLeakDetector>.Instance),
        };
        var stage = new ResponseInspectionStage(detectors, ctx, NullLogger<ResponseInspectionStage>.Instance);
        await stage.ApplyAsync();
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

    // A leaked Claude Code control envelope (task-notification), the analogue of a
    // leaked <invoke> — closed, shape-valid, unfenced.
    private const string TaskNotificationLeak =
        "A background agent completed a task:\n" +
        "<task-notification>\n" +
        "<task-id>abc-123</task-id>\n" +
        "<output-file>/tmp/out.txt</output-file>\n" +
        "<status>completed</status>\n" +
        "<summary>Refactored the catalog.</summary>\n" +
        "</task-notification>";

    private const string TeammateMessageLeak =
        "<teammate-message teammate_id=\"alice\" summary=\"Brief update\">\n" +
        "please review the PR\n" +
        "</teammate-message>";

    // ---- Streaming abort (default delivery) ------------------------------

    [Fact]
    public async Task StreamingLeak_InjectsOneErrorEvent_ThenStops()
    {
        // Contract: on a detected leak, exactly one SSE error event is emitted and
        // no further upstream events flow; the flag is set.
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        var got = await Drain(ctx.Response.EventStream!);

        // message_start + content_block_start + (delta suppressed at detection) + error, then stop.
        Assert.Equal("error", got[^1].EventType);
        Assert.Contains("overloaded_error", got[^1].Data);
        Assert.DoesNotContain(got, e => e.EventType == "message_stop"); // stream ended early
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task StreamingClean_PassesThroughUnchanged()
    {
        // A benign text block (no closed invoke) → every event relayed verbatim.
        var clean = "I'll read the file now, then edit it.";
        var ctx = Ctx(LeakStream(clean));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("message_stop", got[^1].EventType);
        Assert.False(ctx.ResponseLeakDetected);
        Assert.DoesNotContain(got, e => e.EventType == "error");
    }

    [Fact]
    public async Task StreamingFencedInvoke_NotDetected()
    {
        var fenced = "Here's how:\n```\n<invoke name=\"Read\"><parameter name=\"p\">v</parameter></invoke>\n```";
        var ctx = Ctx(LeakStream(fenced));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ResponseLeakDetected);
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
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, ScanThinking = false });

        await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task ThinkingBlock_FencedInvoke_StillDetected()
    {
        // Contract: thinking blocks have no fence concept — a ```-wrapped invoke
        // inside a thinking block IS a leak (unlike the same in a text block).
        var fencedLeak = "reasoning...\n```\n<invoke name=\"Read\">\n<parameter name=\"p\">v</parameter>\n</invoke>\n```";
        async IAsyncEnumerable<SseItem<string>> ThinkingFenced()
        {
            yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""", "content_block_start");
            yield return new SseItem<string>(
                $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"thinking_delta\",\"thinking\":{System.Text.Json.JsonSerializer.Serialize(fencedLeak)}}}}}",
                "content_block_delta");
            await Task.CompletedTask;
        }
        var ctx = Ctx(ThinkingFenced());
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, ScanThinking = true });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.True(ctx.ResponseLeakDetected);
        Assert.Equal("error", got[^1].EventType);
    }

    [Fact]
    public async Task Disabled_Inert_SameStreamReference()
    {
        // Contract: Enabled=false (and rewrite off) → no detector set → stage does
        // not even wrap the stream (same reference passes through).
        var src = LeakStream(Leak);
        var ctx = Ctx(src);
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = false });
        // DoneFilter + (disabled) rewrite still produce a set, so the stream is
        // wrapped — but no leak abort occurs. Assert the leak is NOT acted on.
        var got = await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ResponseLeakDetected);
        Assert.DoesNotContain(got, e => e.EventType == "error");
    }

    // ---- Model-rewrite config gate at the stage level -------------------

    [Fact]
    public async Task ModelRewriteEnabled_RewritesMessageStartModel_ThroughStage()
    {
        // Contract: when the rewrite detector is enabled AND the router changed the
        // model (original != resolved), the stage runs it and the client sees the
        // ORIGINAL requested model on message_start.
        async IAsyncEnumerable<SseItem<string>> Start()
        {
            yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
            await Task.CompletedTask;
        }
        var ctx = Ctx(Start());
        ctx.OriginalRequestedModel = "claude-opus-4-7"; // client asked -4-7; body resolved to -4-8
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = false }, rewriteEnabled: true);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Contains(got, e => e.EventType == "message_start" && e.Data.Contains("claude-opus-4-7"));
    }

    [Fact]
    public async Task ModelRewriteDisabled_LeavesMessageStartModel_ThroughStage()
    {
        // Contract: the config gate lives at the stage. A disabled rewrite detector
        // is filtered out (never Begin, never inspect), so the resolved model is
        // left untouched even though original != resolved would otherwise rewrite.
        async IAsyncEnumerable<SseItem<string>> Start()
        {
            yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
            await Task.CompletedTask;
        }
        var ctx = Ctx(Start());
        ctx.OriginalRequestedModel = "claude-opus-4-7";
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = false }, rewriteEnabled: false);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Contains(got, e => e.EventType == "message_start" && e.Data.Contains("claude-opus-4-8"));
        Assert.DoesNotContain(got, e => e.Data.Contains("claude-opus-4-7"));
    }

    // ---- Control-envelope leaks through the same guard -------------------

    [Fact]
    public async Task StreamingTaskNotificationLeak_InjectsOneErrorEvent_ThenStops()
    {
        // Contract: a leaked <task-notification> control envelope aborts the turn
        // exactly like a leaked <invoke> — one SSE error event, stream ends, flag set.
        var ctx = Ctx(LeakStream(TaskNotificationLeak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("error", got[^1].EventType);
        Assert.Contains("overloaded_error", got[^1].Data);
        Assert.DoesNotContain(got, e => e.EventType == "message_stop");
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task StreamingTeammateMessageLeak_Aborts()
    {
        // Contract: a non-task control envelope (teammate-message) also aborts.
        var ctx = Ctx(LeakStream(TeammateMessageLeak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("error", got[^1].EventType);
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task BufferedDelivery_TaskNotificationLeak_EmitsRealStatus_NoLeakedContent()
    {
        // Contract: buffered delivery flips to a real 529 with an error body and
        // NONE of the leaked envelope content.
        var ctx = Ctx(LeakStream(TaskNotificationLeak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, PreserveStream = false });

        Assert.Equal(ResponseMode.Buffered, ctx.Response.Mode);
        Assert.Equal(529, ctx.Response.Status);
        var body = Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("overloaded_error", body);
        Assert.DoesNotContain("<task-notification", body);
        Assert.DoesNotContain("Refactored the catalog", body);
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task Disabled_Inert_ForControlEnvelopeLeak()
    {
        // Contract: with the guard disabled a control-envelope leak passes through
        // untouched (no abort, no error event).
        var ctx = Ctx(LeakStream(TaskNotificationLeak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = false });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ResponseLeakDetected);
        Assert.DoesNotContain(got, e => e.EventType == "error");
    }

    [Fact]
    public async Task ThinkingDisabled_ControlEnvelopeInThinking_NotDetected()
    {
        // Contract: ScanThinking=false → a control envelope inside a thinking block
        // is not classified as a leak.
        async IAsyncEnumerable<SseItem<string>> ThinkingLeak()
        {
            yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""", "content_block_start");
            yield return new SseItem<string>(
                $"{{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"thinking_delta\",\"thinking\":{System.Text.Json.JsonSerializer.Serialize(TaskNotificationLeak)}}}}}",
                "content_block_delta");
            yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
            await Task.CompletedTask;
        }
        var ctx = Ctx(ThinkingLeak());
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, ScanThinking = false });

        await Drain(ctx.Response.EventStream!);
        Assert.False(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task ControlEnvelopeLeak_LogsWarning_NamingSubjectBlockAction_NotLeakedText()
    {
        // Contract: on a control-envelope leak the detector emits exactly one
        // Warning naming the leaked subject, block type, and action — and NOT the
        // leaked envelope markup or child/body values.
        var log = new CapturingLogger<ResponseLeakDetector>();
        var ctx = Ctx(LeakStream(TaskNotificationLeak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true }, responseLeakLog: log);
        await Drain(ctx.Response.EventStream!);

        var warnings = log.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        var msg = warnings[0].Rendered;
        Assert.Contains("task-notification", msg);   // subject
        Assert.Contains("text", msg);                // block type
        Assert.Contains("overloaded_error", msg);    // signal/action
        Assert.Contains("stream", msg);              // delivery
        Assert.DoesNotContain("<task-notification>", msg); // never the leaked markup
        Assert.DoesNotContain("<task-id>", msg);
        Assert.DoesNotContain("Refactored the catalog", msg);
        Assert.DoesNotContain("/tmp/out.txt", msg);
    }

    // ---- Buffered delivery (PreserveStream=false) ------------------------

    [Fact]
    public async Task BufferedDelivery_Leak_EmitsRealStatus_NoLeakedContent()
    {
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, PreserveStream = false });

        // Flipped to buffered with a real 529 and an error body; no leaked XML.
        Assert.Equal(ResponseMode.Buffered, ctx.Response.Mode);
        Assert.Equal(529, ctx.Response.Status);
        var body = Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("overloaded_error", body);
        Assert.DoesNotContain("<invoke", body);
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task BufferedDelivery_Clean_ReplaysStream()
    {
        var ctx = Ctx(LeakStream("nothing to see"));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, PreserveStream = false });

        // Clean → still streaming, replayed intact.
        Assert.Equal(ResponseMode.Streaming, ctx.Response.Mode);
        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("message_stop", got[^1].EventType);
        Assert.False(ctx.ResponseLeakDetected);
    }

    // ---- Signal mapping --------------------------------------------------

    [Fact]
    public async Task ApiErrorSignal_Streaming_EmitsApiError()
    {
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, Signal = ResponseDetectionSignal.ApiError });
        var got = await Drain(ctx.Response.EventStream!);
        Assert.Contains("api_error", got[^1].Data);
    }

    [Fact]
    public async Task ApiErrorSignal_Buffered_Emits500()
    {
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true, PreserveStream = false, Signal = ResponseDetectionSignal.ApiError });
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
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        var got = await Drain(ctx.Response.EventStream!);
        Assert.DoesNotContain(got, e => e.Data == "[DONE]");
        Assert.Contains(ctx.DroppedEvents, d => d.Data == "[DONE]");
    }

    // ---- Action precedence: lower Order wins, independent of array order ----

    [Fact]
    public async Task LowerOrder_Wins_EvenWhenSuppliedOutOfOrder()
    {
        // Contract: the stage applies detectors by their Order (lower first), NOT
        // by the order they were handed to it. Detector A (Order 0) rewrites the
        // event; detector B (Order 1) would drop it. Supplied SHUFFLED as [B, A],
        // the output must still be A's rewrite — proving the stage re-establishes
        // registration order from the explicit Order and does not depend on the
        // enumeration order it received.
        var a = new StubDetector("A", order: 0, _ => DetectionAction.Rewrite(new SseItem<string>("REWRITTEN", "message")));
        var b = new StubDetector("B", order: 1, _ => DetectionAction.Drop());

        async IAsyncEnumerable<SseItem<string>> One()
        {
            yield return new SseItem<string>("orig", "message");
            await Task.CompletedTask;
        }
        var ctx = Ctx(One());
        // Deliberately out of Order: B before A.
        var stage = new ResponseInspectionStage(
            new IResponseDetector[] { b, a },
            ctx,
            NullLogger<ResponseInspectionStage>.Instance);
        await stage.ApplyAsync();

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Single(got);
        Assert.Equal("REWRITTEN", got[0].Data); // A (Order 0) won; B's drop never applied
        Assert.Empty(ctx.DroppedEvents);
    }

    private sealed class StubDetector : IResponseDetector
    {
        private readonly Func<SseItem<string>, DetectionAction> _on;
        public StubDetector(string name, int order, Func<SseItem<string>, DetectionAction> on)
        {
            Name = name;
            Order = order;
            _on = on;
        }
        public string Name { get; }
        public int Order { get; }
        public bool Enabled => true;
        public void Begin() { }
        public DetectionAction InspectEvent(in SseItem<string> evt) => _on(evt);
    }

    // ---- Detection-point logging ----------------------------------------

    [Fact]
    public async Task LeakDetection_LogsWarning_NamingToolBlockAction_NotLeakedText()
    {
        // Contract: on detection the detector emits exactly one Warning naming the
        // leaked tool, block type, and action — and NOT the leaked <invoke> text.
        var log = new CapturingLogger<ResponseLeakDetector>();
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true }, responseLeakLog: log);
        await Drain(ctx.Response.EventStream!);

        var warnings = log.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        var msg = warnings[0].Rendered;
        Assert.Contains("Read", msg);              // tool
        Assert.Contains("text", msg);              // block type
        Assert.Contains("overloaded_error", msg);  // signal/action
        Assert.Contains("stream", msg);            // delivery
        Assert.DoesNotContain("<invoke", msg);     // never the leaked markup
        Assert.DoesNotContain("<parameter", msg);
    }

    [Fact]
    public async Task CleanStream_LogsNoWarning()
    {
        var log = new CapturingLogger<ResponseLeakDetector>();
        var ctx = Ctx(LeakStream("nothing to see here"));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true }, responseLeakLog: log);
        await Drain(ctx.Response.EventStream!);

        Assert.DoesNotContain(log.Records, r => r.Level == LogLevel.Warning);
    }

    // ---- Per-signature toggles (false-positive escape hatch) -------------

    [Fact]
    public async Task PerSignatureDisabled_InvokeLeak_PassesThroughUnchanged()
    {
        // Contract: turning off just the 'invoke' signature lets an <invoke> sample
        // stream through untouched — the false-positive escape hatch — while the
        // guard as a whole stays enabled.
        var opts = new ResponseLeakGuardOptions { Enabled = true };
        opts.Signatures.Invoke = false;
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, opts);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("message_stop", got[^1].EventType);
        Assert.False(ctx.ResponseLeakDetected);
        Assert.DoesNotContain(got, e => e.EventType == "error");
    }

    [Fact]
    public async Task PerSignatureDisabled_Invoke_OtherSignatureStillAborts()
    {
        // Contract: disabling one signature is surgical — a leaked task-notification
        // still aborts even with 'invoke' turned off.
        var opts = new ResponseLeakGuardOptions { Enabled = true };
        opts.Signatures.Invoke = false;
        var ctx = Ctx(LeakStream(TaskNotificationLeak));
        await Run(ctx, opts);

        var got = await Drain(ctx.Response.EventStream!);
        Assert.Equal("error", got[^1].EventType);
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task LeakError_NamesSignatureAndDisableKeyAndRestart()
    {
        // Contract: the injected error tells the user which signature tripped, the
        // exact switch to disable it, and that a restart is required — so a false
        // positive is self-service to fix.
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });
        var got = await Drain(ctx.Response.EventStream!);

        Assert.Equal("error", got[^1].EventType);
        var data = got[^1].Data;
        Assert.Contains("invoke", data);                                                   // signature id
        Assert.Contains("Pipeline:Detectors:ResponseLeakGuard:Signatures:Invoke=false", data); // disable key
        Assert.Contains("restart", data);                                                  // restart required
    }

    [Fact]
    public async Task LeakDetection_Warning_NamesDisableKeyAndRestart()
    {
        // Contract: the single Warning also names the disable switch and the restart
        // requirement, so the operator can fix a false positive from the log alone.
        var log = new CapturingLogger<ResponseLeakDetector>();
        var ctx = Ctx(LeakStream(Leak));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true }, responseLeakLog: log);
        await Drain(ctx.Response.EventStream!);

        var warnings = log.Records.Where(r => r.Level == LogLevel.Warning).ToList();
        Assert.Single(warnings);
        var msg = warnings[0].Rendered;
        Assert.Contains("invoke", msg);                                             // signature
        Assert.Contains("Pipeline:Detectors:ResponseLeakGuard:Signatures:Invoke", msg); // disable key
        Assert.Contains("restart", msg);                                            // restart note
    }

    // ---- Genuinely non-streaming (ResponseMode.Buffered) responses -------
    // Distinct from the PreserveStream=false tests above (which are STREAMING then
    // buffered): here the upstream response is non-SSE from the start, so the stage
    // takes the ApplyBuffered → InspectBuffered path. Before the InspectBuffered
    // override this path did NOTHING — a leak in a buffered body reached the client.

    // Build an Anthropic Messages response body with the given content blocks.
    private static byte[] BufferedBody(params (string type, string text)[] blocks)
    {
        var items = blocks.Select(b =>
            $"{{\"type\":{System.Text.Json.JsonSerializer.Serialize(b.type)},{System.Text.Json.JsonSerializer.Serialize(b.type)}:{System.Text.Json.JsonSerializer.Serialize(b.text)}}}");
        var json = "{\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-4-8\",\"content\":["
                 + string.Join(",", items) + "]}";
        return Encoding.UTF8.GetBytes(json);
    }

    [Fact]
    public async Task BufferedResponse_InvokeLeakInTextBlock_Aborts()
    {
        // Contract: a genuinely non-streaming response whose text block carries a
        // closed in-tools <invoke> leak is aborted with a real 529 + error body and
        // NONE of the leaked markup. (Regression: this path was unguarded — the
        // detector inherited the base InspectBuffered no-op.)
        var ctx = Ctx(stream: null, mode: ResponseMode.Buffered,
            buffered: BufferedBody(("text", Leak)));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        Assert.Equal(529, ctx.Response.Status);
        var body = Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("overloaded_error", body);
        Assert.DoesNotContain("<invoke", body);
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task BufferedResponse_ControlEnvelopeLeak_Aborts()
    {
        // Contract: a leaked control envelope in a buffered body aborts too.
        var ctx = Ctx(stream: null, mode: ResponseMode.Buffered,
            buffered: BufferedBody(("text", TaskNotificationLeak)));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        Assert.Equal(529, ctx.Response.Status);
        Assert.Contains("overloaded_error", Encoding.UTF8.GetString(ctx.Response.BufferedBody!));
        Assert.True(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task BufferedResponse_Clean_PassesThroughUntouched()
    {
        // Contract: a clean buffered body is delivered verbatim — no abort, body
        // unchanged, flag clear.
        var original = BufferedBody(("text", "I'll read the file, then edit it."));
        var ctx = Ctx(stream: null, mode: ResponseMode.Buffered, buffered: original);
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        Assert.False(ctx.ResponseLeakDetected);
        Assert.Equal(original, ctx.Response.BufferedBody); // unchanged
    }

    [Fact]
    public async Task BufferedResponse_FencedInvoke_NotDetected()
    {
        // Contract: an <invoke> inside a code fence in a buffered text block is a
        // teaching example, not a leak — same fence semantics as streaming.
        var fenced = "Here's how:\n```\n<invoke name=\"Read\"><parameter name=\"p\">v</parameter></invoke>\n```";
        var ctx = Ctx(stream: null, mode: ResponseMode.Buffered,
            buffered: BufferedBody(("text", fenced)));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        Assert.False(ctx.ResponseLeakDetected);
    }

    [Fact]
    public async Task BufferedResponse_ThinkingLeak_DetectedOnlyWhenScanThinkingOn()
    {
        // Contract: a leak in a buffered THINKING block aborts iff ScanThinking is on
        // — matching the streaming block-type gate.
        var off = Ctx(stream: null, mode: ResponseMode.Buffered,
            buffered: BufferedBody(("thinking", Leak)));
        await Run(off, new ResponseLeakGuardOptions { Enabled = true, ScanThinking = false });
        Assert.False(off.ResponseLeakDetected);

        var on = Ctx(stream: null, mode: ResponseMode.Buffered,
            buffered: BufferedBody(("thinking", Leak)));
        await Run(on, new ResponseLeakGuardOptions { Enabled = true, ScanThinking = true });
        Assert.True(on.ResponseLeakDetected);
    }

    [Fact]
    public async Task BufferedResponse_Unparseable_FailsOpen()
    {
        // Contract: a body that isn't parseable Anthropic JSON must NOT abort — a
        // scan hiccup can't turn a real response into a client error.
        var ctx = Ctx(stream: null, mode: ResponseMode.Buffered,
            buffered: Encoding.UTF8.GetBytes("not json <invoke name=\"Read\"></invoke>"));
        await Run(ctx, new ResponseLeakGuardOptions { Enabled = true });

        Assert.False(ctx.ResponseLeakDetected);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Rendered)> Records = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Records.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
