using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response;

/// <summary>
/// Buffers and repairs malformed tool calls emitted by the Copilot backend.
/// Copilot occasionally omits required fields, serializes arrays as strings,
/// and breaks Unicode escapes (e.g. \u7 ecf). This stage buffers streaming
/// tool calls and repairs the JSON based on the client-provided schema.
/// </summary>
internal sealed partial class ToolCallRepairStage : IResponseStage<MessagesRequest>
{
    private readonly ILogger<ToolCallRepairStage> _log;
    private readonly ToolCallRepairOptions _opts;

    public ToolCallRepairStage(
        ILogger<ToolCallRepairStage> log,
        IOptions<ToolCallRepairOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    public string Name => "ToolCallRepair";

    public Task ApplyAsync(BridgeContext<MessagesRequest> ctx)
    {
        if (!_opts.Enabled) return Task.CompletedTask;

        if (ctx.Response.Mode == ResponseMode.Streaming && ctx.Response.EventStream is not null)
        {
            var source = ctx.Response.EventStream;
            ctx.Response.EventStream = WrapStream(source, ctx.Request.Body.Tools, _log, ctx.Ct);
            _log.LogDebug("stage {Name}: streaming wrapper installed (will buffer tool_use blocks)", Name);
        }
        else if (ctx.Response.Mode == ResponseMode.Buffered && ctx.Response.BufferedBody is { Length: > 0 } body)
        {
            // Buffered mode isn't typically used for tool calls in Claude Code, 
            // but we implement it for completeness by rewriting the whole response.
            var rewritten = TryRewriteBufferedBody(body, ctx.Request.Body.Tools, _log);
            if (rewritten is not null)
            {
                ctx.Response.BufferedBody = rewritten;
            }
        }

        return Task.CompletedTask;
    }

    private static byte[]? TryRewriteBufferedBody(byte[] body, IReadOnlyList<Tool>? tools, ILogger log)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not JsonObject root || root["content"] is not JsonArray contentArr)
            {
                return null;
            }

            var toolDict = tools?.ToDictionary(t => t.Name, StringComparer.Ordinal) ?? new Dictionary<string, Tool>();
            bool modified = false;

            foreach (var block in contentArr)
            {
                if (block is JsonObject blockObj && 
                    blockObj["type"]?.GetValue<string>() == "tool_use" && 
                    blockObj["input"] is JsonObject inputObj)
                {
                    string toolName = blockObj["name"]?.GetValue<string>() ?? "";
                    string rawJson = inputObj.ToJsonString();
                    string repairedJson = RepairJson(rawJson, toolName, toolDict, log);

                    if (rawJson != repairedJson)
                    {
                        var repairedNode = JsonNode.Parse(repairedJson);
                        if (repairedNode != null)
                        {
                            blockObj["input"] = repairedNode;
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                return Encoding.UTF8.GetBytes(root.ToJsonString());
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async IAsyncEnumerable<SseItem<string>> WrapStream(
        IAsyncEnumerable<SseItem<string>> source,
        IReadOnlyList<Tool>? tools,
        ILogger log,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var toolDict = tools?.ToDictionary(t => t.Name, StringComparer.Ordinal) ?? new Dictionary<string, Tool>();
        
        bool isBuffering = false;
        int currentToolIndex = -1;
        string? currentToolName = null;
        StringBuilder buffer = new();
        
        await foreach (var evt in source.WithCancellation(ct))
        {
            if (evt.EventType == "content_block_start")
            {
                var node = TryParseNode(evt.Data);
                if (node?["start"] is JsonObject startObj && 
                    startObj["type"]?.GetValue<string>() == "tool_use")
                {
                    currentToolIndex = node["index"]?.GetValue<int>() ?? -1;
                    currentToolName = startObj["name"]?.GetValue<string>();
                    isBuffering = true;
                    buffer.Clear();
                    yield return evt;
                    continue;
                }
            }
            else if (isBuffering && evt.EventType == "content_block_delta")
            {
                var node = TryParseNode(evt.Data);
                if (node?["index"]?.GetValue<int>() == currentToolIndex && 
                    node?["delta"] is JsonObject deltaObj && 
                    deltaObj["type"]?.GetValue<string>() == "input_json_delta")
                {
                    var partialJson = deltaObj["partial_json"]?.GetValue<string>();
                    if (partialJson != null)
                    {
                        buffer.Append(partialJson);
                    }
                    // Suppress emitting this chunk
                    continue;
                }
            }
            else if (isBuffering && evt.EventType == "content_block_stop")
            {
                var node = TryParseNode(evt.Data);
                if (node?["index"]?.GetValue<int>() == currentToolIndex)
                {
                    // We reached the end of the tool block! Repair and emit.
                    string rawJson = buffer.ToString();
                    string repairedJson = RepairJson(rawJson, currentToolName, toolDict, log);
                    
                    // Emit a single content_block_delta with the repaired JSON
                    var syntheticDelta = new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = currentToolIndex,
                        ["delta"] = new JsonObject
                        {
                            ["type"] = "input_json_delta",
                            ["partial_json"] = repairedJson
                        }
                    };
                    
                    yield return new SseItem<string>(syntheticDelta.ToJsonString(), "content_block_delta");
                    
                    // Emit the stop event
                    yield return evt;
                    
                    // Reset state
                    isBuffering = false;
                    currentToolIndex = -1;
                    currentToolName = null;
                    buffer.Clear();
                    continue;
                }
            }
            
            // Pass through everything else
            yield return evt;
        }
    }

    private static string RepairJson(string rawJson, string? toolName, IReadOnlyDictionary<string, Tool> toolDict, ILogger log)
    {
        // 1. Fix Unicode escapes with extra spaces: \u7 ecf -> \u7ecf
        string fixedEscapes = UnicodeEscapeRegex().Replace(rawJson, @"\u$1$2");
        
        if (toolName == null || !toolDict.TryGetValue(toolName, out var tool) || tool.InputSchema == null)
        {
            return fixedEscapes;
        }
        
        try
        {
            var node = JsonNode.Parse(fixedEscapes);
            if (node is not JsonObject root) return fixedEscapes;
            
            var schema = tool.InputSchema;
            
            // 2. Type Mismatch (Stringified arrays)
            if (schema.Properties.HasValue)
            {
                var properties = JsonNode.Parse(schema.Properties.Value.GetRawText()) as JsonObject;
                if (properties != null)
                {
                    foreach (var prop in properties)
                    {
                        string propName = prop.Key;
                        string? expectedType = prop.Value?["type"]?.GetValue<string>();
                        
                        if (root.TryGetPropertyValue(propName, out var propNode) && propNode is JsonValue val)
                        {
                            if ((expectedType == "array" || expectedType == "object") && val.TryGetValue<string>(out var stringVal))
                            {
                                try
                                {
                                    var parsed = JsonNode.Parse(stringVal);
                                    if (parsed != null)
                                    {
                                        root[propName] = parsed;
                                        log.LogDebug("ToolCallRepair: Restored stringified {Type} for property '{Prop}'", expectedType, propName);
                                    }
                                }
                                catch { } // Ignore parse failures
                            }
                        }
                    }
                }
            }
            
            // 3. Missing Required Fields
            if (schema.Required != null)
            {
                var properties = schema.Properties.HasValue ? JsonNode.Parse(schema.Properties.Value.GetRawText()) as JsonObject : null;
                
                foreach (var req in schema.Required)
                {
                    if (!root.ContainsKey(req))
                    {
                        string? expectedType = properties?[req]?["type"]?.GetValue<string>();
                        JsonNode dummyValue = expectedType switch
                        {
                            "array" => new JsonArray(),
                            "object" => new JsonObject(),
                            "boolean" => JsonValue.Create(false),
                            "number" => JsonValue.Create(0),
                            "integer" => JsonValue.Create(0),
                            _ => JsonValue.Create("")
                        };
                        root[req] = dummyValue;
                        log.LogDebug("ToolCallRepair: Injected dummy value for missing required property '{Prop}'", req);
                    }
                }
            }
            
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            // If the JSON is totally busted, return the escape-fixed version
            return fixedEscapes;
        }
    }

    private static JsonNode? TryParseNode(string json)
    {
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }

    [GeneratedRegex(@"\\u([0-9a-fA-F])\s+([0-9a-fA-F]{3})")]
    private static partial Regex UnicodeEscapeRegex();
}
