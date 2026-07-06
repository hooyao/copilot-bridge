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
