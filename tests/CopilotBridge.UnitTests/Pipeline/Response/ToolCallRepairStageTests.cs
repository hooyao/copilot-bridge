using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Response;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CopilotBridge.UnitTests.Pipeline.Response;

public class ToolCallRepairStageTests
{
    private static ToolCallRepairStage CreateStage(bool enabled = true)
    {
        var opts = Options.Create(new ToolCallRepairOptions { Enabled = enabled });
        return new ToolCallRepairStage(NullLogger<ToolCallRepairStage>.Instance, opts);
    }

    [Fact]
    public async Task Disabled_BypassesStream()
    {
        var stage = CreateStage(enabled: false);
        var ctx = CreateContext([]);
        var originalStream = ctx.Response.EventStream;

        await stage.ApplyAsync(ctx);

        Assert.Same(originalStream, ctx.Response.EventStream);
    }

    [Fact]
    public async Task NormalText_PassesThrough()
    {
        var stage = CreateStage();
        var ctx = CreateContext([]);

        var inputEvents = new[]
        {
            new SseItem<string>("""{"type":"message_start"}""", "message_start"),
            new SseItem<string>("""{"type":"content_block_start","index":0,"start":{"type":"text","text":""}}""", "content_block_start"),
            new SseItem<string>("""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hello"}}""", "content_block_delta"),
            new SseItem<string>("""{"type":"content_block_stop","index":0}""", "content_block_stop")
        };

        ctx.Response.EventStream = AsyncEnumerable(inputEvents);
        await stage.ApplyAsync(ctx);

        var outputEvents = await ToListAsync(ctx.Response.EventStream!);
        
        Assert.Equal(4, outputEvents.Count);
        Assert.Equal("message_start", outputEvents[0].EventType);
        Assert.Equal("content_block_delta", outputEvents[2].EventType);
        Assert.Contains("hello", outputEvents[2].Data);
    }

    [Fact]
    public async Task ToolCall_MissingRequiredField_InjectsDummy()
    {
        var stage = CreateStage();
        
        var tools = new[]
        {
            new Tool
            {
                Name = "AskQuestion",
                InputSchema = new InputSchema
                {
                    Type = "object",
                    Properties = JsonSerializer.Deserialize<JsonElement>("""{"question":{"type":"string"},"options":{"type":"array"}}"""),
                    Required = ["question", "options"]
                }
            }
        };
        
        var ctx = CreateContext(tools);

        // Input JSON is missing 'options'
        var inputEvents = new[]
        {
            new SseItem<string>("""{"type":"content_block_start","index":1,"start":{"type":"tool_use","id":"123","name":"AskQuestion"}}""", "content_block_start"),
            new SseItem<string>("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"question\":\"hi\"}"}}""", "content_block_delta"),
            new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop")
        };

        ctx.Response.EventStream = AsyncEnumerable(inputEvents);
        await stage.ApplyAsync(ctx);

        var outputEvents = await ToListAsync(ctx.Response.EventStream!);
        
        Assert.Equal(3, outputEvents.Count);
        Assert.Equal("content_block_start", outputEvents[0].EventType);
        Assert.Equal("content_block_delta", outputEvents[1].EventType);
        Assert.Equal("content_block_stop", outputEvents[2].EventType);

        var deltaNode = JsonNode.Parse(outputEvents[1].Data)!;
        var partialJson = deltaNode["delta"]?["partial_json"]?.GetValue<string>();
        
        Assert.NotNull(partialJson);
        var repairedObj = JsonNode.Parse(partialJson!) as JsonObject;
        
        // Assert question is intact and options was injected as empty array
        Assert.Equal("hi", repairedObj?["question"]?.GetValue<string>());
        Assert.True(repairedObj?.ContainsKey("options"));
        Assert.IsType<JsonArray>(repairedObj?["options"]);
    }

    [Fact]
    public async Task ToolCall_StringifiedArray_RestoresArray()
    {
        var stage = CreateStage();
        
        var tools = new[]
        {
            new Tool
            {
                Name = "AskQuestion",
                InputSchema = new InputSchema
                {
                    Type = "object",
                    Properties = JsonSerializer.Deserialize<JsonElement>("""{"items":{"type":"array"}}"""),
                    Required = ["items"]
                }
            }
        };
        
        var ctx = CreateContext(tools);

        // items is a stringified array
        var inputEvents = new[]
        {
            new SseItem<string>("""{"type":"content_block_start","index":1,"start":{"type":"tool_use","id":"123","name":"AskQuestion"}}""", "content_block_start"),
            new SseItem<string>("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"items\":\"[1,2,3]\"}"}}""", "content_block_delta"),
            new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop")
        };

        ctx.Response.EventStream = AsyncEnumerable(inputEvents);
        await stage.ApplyAsync(ctx);

        var outputEvents = await ToListAsync(ctx.Response.EventStream!);
        var deltaNode = JsonNode.Parse(outputEvents[1].Data)!;
        var partialJson = deltaNode["delta"]?["partial_json"]?.GetValue<string>();
        
        var repairedObj = JsonNode.Parse(partialJson!) as JsonObject;
        
        Assert.IsType<JsonArray>(repairedObj?["items"]);
        Assert.Equal(3, repairedObj?["items"]?.AsArray().Count);
    }

    [Fact]
    public async Task ToolCall_BrokenUnicodeEscape_FixesSpace()
    {
        var stage = CreateStage();
        var tools = new[] { new Tool { Name = "T", InputSchema = new InputSchema { Type = "object" } } };
        var ctx = CreateContext(tools);

        // contains \u7 ecf
        var inputEvents = new[]
        {
            new SseItem<string>("""{"type":"content_block_start","index":1,"start":{"type":"tool_use","id":"123","name":"T"}}""", "content_block_start"),
            new SseItem<string>("""{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"text\":\"\\u7 ecf\"}"}}""", "content_block_delta"),
            new SseItem<string>("""{"type":"content_block_stop","index":1}""", "content_block_stop")
        };

        ctx.Response.EventStream = AsyncEnumerable(inputEvents);
        await stage.ApplyAsync(ctx);

        var outputEvents = await ToListAsync(ctx.Response.EventStream!);
        var deltaNode = JsonNode.Parse(outputEvents[1].Data)!;
        var partialJson = deltaNode["delta"]?["partial_json"]?.GetValue<string>();
        
        // Assert the space was removed
        Assert.Contains(@"\u7ECF", partialJson, StringComparison.OrdinalIgnoreCase);
    }

    private static BridgeContext<MessagesRequest> CreateContext(Tool[] tools)
    {
        return new BridgeContext<MessagesRequest>
        {
            Request = new BridgeRequest<MessagesRequest>
            {
                Method = "POST",
                Path = "/v1/messages",
                Body = new MessagesRequest
                {
                    Model = "claude-opus-4.8",
                    Messages = [],
                    Tools = tools.Length > 0 ? tools : null
                }
            },
            Response = new BridgeResponse
            {
                Mode = ResponseMode.Streaming,
                EventStream = AsyncEnumerable(Array.Empty<SseItem<string>>())
            },
            Ct = CancellationToken.None
        };
    }

    private static async IAsyncEnumerable<T> AsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> enumerable)
    {
        var list = new List<T>();
        await foreach (var item in enumerable)
        {
            list.Add(item);
        }
        return list;
    }
}
