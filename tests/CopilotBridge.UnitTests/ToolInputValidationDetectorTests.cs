using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CopilotBridge.UnitTests;

public class ToolInputValidationDetectorTests
{
    [Fact]
    public async Task ValidToolInput_PassesThroughUnchanged()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"question":"Which database?","header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task MissingNestedRequiredField_AbortsAndMarksContext()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);
        var abort = actions.Single(a => a.Kind == DetectionActionKind.Abort);

        Assert.True(ctx.ToolInputInvalidDetected);
        Assert.Equal(529, abort.HttpStatus);
        Assert.Contains("overloaded_error", abort.ErrorJson);
    }

    [Fact]
    public async Task MalformedJson_AbortsAndMarksContext()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":[{"question":"oops"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task StringifiedArrayForArrayProperty_AbortsInsteadOfRepairing()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"questions":"[]"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task LegacyStartField_IsAcceptedForToolBlockStart()
    {
        var ctx = Context(ToolSchema(), LegacyStartToolStream("AskUserQuestion", """{"questions":"[]"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task DuplicateToolNames_DoNotCrashDetector()
    {
        var tools = ToolSchema().Concat(ToolSchema()).ToArray();
        var ctx = Context(tools, ToolStream("AskUserQuestion", """{"questions":[{"question":"Which database?","header":"DB","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }


    [Fact]
    public async Task UnknownToolSchema_OnlyRequiresInputObject()
    {
        var ctx = Context(ToolSchema(), ToolStream("OtherTool", """{"anything":"goes"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public void BufferedInvalidToolInput_AbortsWithRealStatus()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("""
        {"content":[{"type":"tool_use","id":"toolu_1","name":"AskUserQuestion","input":{"questions":[{"header":"DB","options":[],"multiSelect":false}]}}]}
        """);
        var ctx = Context(ToolSchema(), AsyncEnumerable(Array.Empty<SseItem<string>>()));
        ctx.Response.Mode = ResponseMode.Buffered;
        ctx.Response.BufferedBody = body;
        var detector = Detector(ctx, preserveStream: false);
        detector.Begin();

        var action = detector.InspectBuffered(body);

        Assert.Equal(DetectionActionKind.Abort, action.Kind);
        Assert.Equal(529, action.HttpStatus);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    // --- End-to-end through ResponseInspectionStage: the contract the user actually
    // depends on is that a malformed tool block does NOT enter Claude Code's context.
    // Claude Code commits a content block into its message list only when it receives
    // content_block_stop; the stage's Abort replaces that stop with an `error` event
    // and ends the stream. So the delivered sequence must carry the error and must
    // NOT carry the bad block's content_block_stop (nor message_stop) — that is what
    // keeps the block out of context. These drive the real stage, not the detector
    // in isolation, so an inverted Enabled/RequiresBuffering or a broken stage-render
    // would be caught here (the direct-call tests above would not catch those).

    [Fact]
    public async Task Streaming_MalformedTool_StageDropsStop_AndInjectsError_NoContextCommit()
    {
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: true);

        var delivered = await Drain(ctx.Response.EventStream!);

        // The bad block is kept out of context: its content_block_stop is never
        // delivered (CC only commits at stop), and neither is message_stop.
        Assert.DoesNotContain(delivered, e => e.EventType == "content_block_stop");
        Assert.DoesNotContain(delivered, e => e.EventType == "message_stop");
        // Exactly one terminal error event, and it is last (the stream ended there).
        Assert.Equal("error", delivered[^1].EventType);
        Assert.Contains("overloaded_error", delivered[^1].Data);
        Assert.Single(delivered, e => e.EventType == "error");
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task Streaming_ValidTool_StageDeliversWholeStream_Untouched()
    {
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"question":"Which database?","header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: true);

        var delivered = await Drain(ctx.Response.EventStream!);

        Assert.DoesNotContain(delivered, e => e.EventType == "error");
        Assert.Equal("message_stop", delivered[^1].EventType);
        Assert.Contains(delivered, e => e.EventType == "content_block_stop");
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task Streaming_Disabled_IsInert_MalformedToolPassesThrough()
    {
        // Contract: Enabled=false → the detector is never begun/run by the stage, so
        // even a malformed tool block streams through whole and the flag stays clear.
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: true, enabled: false);

        var delivered = await Drain(ctx.Response.EventStream!);

        Assert.DoesNotContain(delivered, e => e.EventType == "error");
        Assert.Equal("message_stop", delivered[^1].EventType);
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task BufferedPreserveStreamFalse_StageWithholdsMalformedContent_RealStatus()
    {
        // PreserveStream=false → RequiresBuffering=true → the stage buffers the whole
        // stream, trips on the bad stop, and flips to a real 529 whose body is the
        // error envelope only — the malformed tool text never reaches the client.
        var ctx = Context(ToolSchema(), FullToolStream("AskUserQuestion", """{"questions":[{"header":"DB","options":[],"multiSelect":false}]}"""));
        await RunStage(ctx, preserveStream: false);

        Assert.Equal(ResponseMode.Buffered, ctx.Response.Mode);
        Assert.Equal(529, ctx.Response.Status);
        Assert.True(ctx.ToolInputInvalidDetected);
        var body = System.Text.Encoding.UTF8.GetString(ctx.Response.BufferedBody!);
        Assert.Contains("overloaded_error", body);
        Assert.DoesNotContain("multiSelect", body); // no leaked tool-input content
    }

    [Fact]
    public async Task MultipleToolBlocks_ValidThenInvalid_TripsOnSecondOnly()
    {
        var ctx = Context(
            ToolSchema(),
            TwoToolStream(
                ("AskUserQuestion", """{"questions":[{"question":"ok?","header":"H","options":[],"multiSelect":false}]}"""),
                ("AskUserQuestion", """{"questions":[{"header":"H","options":[],"multiSelect":false}]}""")));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Single(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task InterleavedTextBlock_IsIgnored_ValidToolPasses()
    {
        // A text block before the tool block must not be accumulated/parsed as tool
        // input; only the tool block is validated.
        var ctx = Context(ToolSchema(), TextThenToolStream(
            "Here is a plain-text answer with { braces } that is not JSON.",
            "AskUserQuestion",
            """{"questions":[{"question":"ok?","header":"H","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task StartCarriedInput_NoDeltas_IsValidated()
    {
        // Speculative path: a backend ships the full input object on content_block_start
        // with no following deltas. An invalid one must still trip.
        var ctx = Context(ToolSchema(), StartInputToolStream(
            "AskUserQuestion", """{"questions":[{"header":"H","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task StartCarriedInput_PlusDeltas_DoesNotConcatenateIntoMalformedJson()
    {
        // Regression: when a start carries a (complete, valid) input object AND deltas
        // also stream the full input, the two must NOT be concatenated into `{...}{...}`
        // (malformed JSON) and falsely aborted. Deltas are authoritative.
        var ctx = Context(ToolSchema(), StartInputPlusDeltasToolStream(
            "AskUserQuestion", """{"questions":[{"question":"ok?","header":"H","options":[],"multiSelect":false}]}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.All(actions, a => Assert.Equal(DetectionActionKind.None, a.Kind));
        Assert.False(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task TopLevelRequiredMissing_Aborts()
    {
        // Input is a valid object but omits the top-level required `questions`.
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"other":"value"}"""));
        var detector = Detector(ctx);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);

        Assert.Contains(actions, a => a.Kind == DetectionActionKind.Abort);
        Assert.True(ctx.ToolInputInvalidDetected);
    }

    [Fact]
    public async Task ApiErrorSignal_AbortsWith500()
    {
        var ctx = Context(ToolSchema(), ToolStream("AskUserQuestion", """{"other":"value"}"""));
        var detector = new ToolInputValidationDetector(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions
            {
                Enabled = true,
                PreserveStream = true,
                Signal = ResponseLeakSignal.ApiError,
            }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);
        detector.Begin();

        var actions = await Feed(detector, ctx.Response.EventStream!);
        var abort = actions.Single(a => a.Kind == DetectionActionKind.Abort);

        Assert.Equal(500, abort.HttpStatus);
        Assert.Contains("api_error", abort.ErrorJson);
    }

    private static async Task RunStage(BridgeContext<MessagesRequest> ctx, bool preserveStream, bool enabled = true)
    {
        // Drive the REAL stage with the always-on DONE filter plus the tool-input
        // detector, sharing ctx as production DI does. This exercises Begin() gating,
        // RequiresBuffering branch selection, and the stage's Abort rendering.
        var detectors = new IResponseDetector[]
        {
            new DoneFilterDetector(new DetectorOrder<DoneFilterDetector>(0)),
            new ToolInputValidationDetector(
                new DetectorOrder<ToolInputValidationDetector>(4),
                TestOptions.Snapshot(new ToolInputValidationOptions { Enabled = enabled, PreserveStream = preserveStream }),
                ctx,
                NullLogger<ToolInputValidationDetector>.Instance),
        };
        var stage = new ResponseInspectionStage(detectors, ctx, NullLogger<ResponseInspectionStage>.Instance);
        await stage.ApplyAsync();
    }

    private static async Task<List<SseItem<string>>> Drain(IAsyncEnumerable<SseItem<string>> s)
    {
        var list = new List<SseItem<string>>();
        await foreach (var e in s) list.Add(e);
        return list;
    }

    private static ToolInputValidationDetector Detector(BridgeContext<MessagesRequest> ctx, bool preserveStream = true) =>
        new(
            new DetectorOrder<ToolInputValidationDetector>(4),
            TestOptions.Snapshot(new ToolInputValidationOptions { Enabled = true, PreserveStream = preserveStream }),
            ctx,
            NullLogger<ToolInputValidationDetector>.Instance);

    private static BridgeContext<MessagesRequest> Context(IReadOnlyList<Tool> tools, IAsyncEnumerable<SseItem<string>> stream) =>
        new()
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/cc/v1/messages",
                Body = new MessagesRequest
                {
                    Model = "claude-opus-4-8",
                    Messages = Array.Empty<MessageParam>(),
                    Tools = tools,
                },
            },
            Response = new BridgeResponse { Mode = ResponseMode.Streaming, EventStream = stream },
            Ct = default,
        };

    private static IReadOnlyList<Tool> ToolSchema() =>
    [
        new Tool
        {
            Name = "AskUserQuestion",
            InputSchema = new InputSchema
            {
                Type = "object",
                Properties = JsonDocument.Parse("""
                {
                  "questions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "question": { "type": "string" },
                        "header": { "type": "string" },
                        "options": { "type": "array" },
                        "multiSelect": { "type": "boolean" }
                      },
                      "required": ["question", "header", "options", "multiSelect"]
                    }
                  }
                }
                """).RootElement.Clone(),
                Required = ["questions"],
            },
        },
    ];

    private static async IAsyncEnumerable<SseItem<string>> ToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    // A COMPLETE Anthropic stream (message_start … content blocks … message_stop),
    // as a real client sees it — used for the end-to-end stage tests so we can assert
    // whether message_stop / content_block_stop are delivered.
    private static async IAsyncEnumerable<SseItem<string>> FullToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>("""{"type":"message_start","message":{"model":"claude-opus-4-8"}}""", "message_start");
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>("""{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""", "message_delta");
        yield return new SseItem<string>("""{"type":"message_stop"}""", "message_stop");
        await Task.CompletedTask;
    }

    // Two tool blocks at distinct indices, each with its own start/deltas/stop.
    private static async IAsyncEnumerable<SseItem<string>> TwoToolStream(
        (string tool, string input) first,
        (string tool, string input) second)
    {
        var idx = 0;
        foreach (var (tool, input) in new[] { first, second })
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_start\",\"index\":" + idx + ",\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_" + idx + "\",\"name\":" + JsonSerializer.Serialize(tool) + ",\"input\":{}}}",
                "content_block_start");
            foreach (var fragment in Split(input, 12))
            {
                yield return new SseItem<string>(
                    "{\"type\":\"content_block_delta\",\"index\":" + idx + ",\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                    "content_block_delta");
            }
            yield return new SseItem<string>("{\"type\":\"content_block_stop\",\"index\":" + idx + "}", "content_block_stop");
            idx++;
        }
        await Task.CompletedTask;
    }

    // A text block (index 0) followed by a tool block (index 1). The text deltas must
    // be ignored by the tool detector.
    private static async IAsyncEnumerable<SseItem<string>> TextThenToolStream(string text, string toolName, string inputJson)
    {
        yield return new SseItem<string>("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""", "content_block_start");
        yield return new SseItem<string>(
            "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + JsonSerializer.Serialize(text) + "}}",
            "content_block_delta");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop");
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    // A tool block whose full input rides on content_block_start, with NO deltas.
    private static async IAsyncEnumerable<SseItem<string>> StartInputToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":" + inputJson + "}}",
            "content_block_start");
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    // A tool block that carries the full input on start AND also streams the same
    // input as deltas — the regression case that must not concatenate into bad JSON.
    private static async IAsyncEnumerable<SseItem<string>> StartInputPlusDeltasToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":" + inputJson + "}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<SseItem<string>> LegacyStartToolStream(string toolName, string inputJson)
    {
        yield return new SseItem<string>(
            "{\"type\":\"content_block_start\",\"index\":1,\"start\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":" + JsonSerializer.Serialize(toolName) + ",\"input\":{}}}",
            "content_block_start");
        foreach (var fragment in Split(inputJson, 12))
        {
            yield return new SseItem<string>(
                "{\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":" + JsonSerializer.Serialize(fragment) + "}}",
                "content_block_delta");
        }
        yield return new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop");
        await Task.CompletedTask;
    }


    private static IEnumerable<string> Split(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }

    private static async Task<List<DetectionAction>> Feed(ToolInputValidationDetector detector, IAsyncEnumerable<SseItem<string>> stream)
    {
        var actions = new List<DetectionAction>();
        await foreach (var evt in stream)
        {
            actions.Add(detector.InspectEvent(evt));
        }
        return actions;
    }

    private static async IAsyncEnumerable<T> AsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.CompletedTask;
        }
    }
}
