using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Validates real <c>tool_use</c> blocks after their streamed
/// <c>input_json_delta</c> fragments have closed. It catches malformed upstream
/// tool arguments before Claude Code turns them into an unrecoverable
/// "Invalid tool parameters" client-side failure.
/// </summary>
internal sealed class ToolInputValidationDetector : AbstractOrderAwareDetector<ToolInputValidationDetector>
{
    private readonly ToolInputValidationOptions _opts;
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger _log;

    private Dictionary<string, Tool>? _toolsByName;
    private StringBuilder? _currentInput;
    private int _currentIndex = -1;
    private string? _currentToolName;
    private bool _currentBlockIsTool;

    public ToolInputValidationDetector(
        DetectorOrder<ToolInputValidationDetector> order,
        IOptionsSnapshot<ToolInputValidationOptions> opts,
        BridgeContext<MessagesRequest> ctx,
        ILogger<ToolInputValidationDetector> log) : base(order)
    {
        _opts = opts.Value;
        _ctx = ctx;
        _log = log;
    }

    public override string Name => "ToolInputValidation";

    public override bool Enabled => _opts.Enabled;

    public override bool RequiresBuffering => !_opts.PreserveStream;

    public override void Begin()
    {
        _toolsByName = new Dictionary<string, Tool>(StringComparer.Ordinal);
        if (_ctx.Request.Body.Tools is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
            {
                if (string.IsNullOrEmpty(tool.Name) || _toolsByName.ContainsKey(tool.Name))
                {
                    continue;
                }
                _toolsByName.Add(tool.Name, tool);
            }
        }
        _currentInput = null;
        _currentIndex = -1;
        _currentToolName = null;
        _currentBlockIsTool = false;
    }

    public override DetectionAction InspectEvent(in SseItem<string> evt)
    {
        switch (evt.EventType)
        {
            case "content_block_start":
                StartBlock(evt.Data);
                break;

            case "content_block_delta":
                AppendInputDelta(evt.Data);
                break;

            case "content_block_stop":
                return StopBlock(evt.Data);
        }

        return DetectionAction.None;
    }

    public override DetectionAction InspectBuffered(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return DetectionAction.None;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                return DetectionAction.None;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object
                    || !block.TryGetProperty("type", out var type)
                    || type.GetString() != "tool_use"
                    || !block.TryGetProperty("input", out var input))
                {
                    continue;
                }

                var toolName = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (!ValidateToolInput(toolName, input, out var reason))
                {
                    return Trip(toolName, reason, "buffer");
                }
            }
        }

        return DetectionAction.None;
    }

    private void StartBlock(string data)
    {
        ResetCurrentBlock();

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            var block = TryGetObject(root, "content_block", out var contentBlock)
                ? contentBlock
                : TryGetObject(root, "start", out var start)
                    ? start
                    : default;

            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type)
                || type.GetString() != "tool_use")
            {
                return;
            }

            _currentBlockIsTool = true;
            _currentIndex = root.TryGetProperty("index", out var index) && index.TryGetInt32(out var i) ? i : -1;
            _currentToolName = block.TryGetProperty("name", out var name) ? name.GetString() : null;
            _currentInput = new StringBuilder();

            if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
            {
                // Some starts carry an initial empty object. Preserve non-empty input
                // if a backend ever sends it here instead of deltas.
                var initial = input.GetRawText();
                if (!string.Equals(initial, "{}", StringComparison.Ordinal))
                {
                    _currentInput.Append(initial);
                }
            }
        }
        catch (JsonException)
        {
            ResetCurrentBlock();
        }
    }

    private void AppendInputDelta(string data)
    {
        if (!_currentBlockIsTool || _currentInput is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (_currentIndex >= 0
                && root.TryGetProperty("index", out var index)
                && index.TryGetInt32(out var i)
                && i != _currentIndex)
            {
                return;
            }

            if (!root.TryGetProperty("delta", out var delta)
                || delta.ValueKind != JsonValueKind.Object
                || !delta.TryGetProperty("type", out var type)
                || type.GetString() != "input_json_delta")
            {
                return;
            }

            if (delta.TryGetProperty("partial_json", out var partial)
                && partial.ValueKind == JsonValueKind.String)
            {
                _currentInput.Append(partial.GetString());
            }
        }
        catch (JsonException)
        {
            // A malformed event frame is not the same as malformed tool input; let
            // the rest of the pipeline/client see it unchanged.
        }
    }

    private DetectionAction StopBlock(string data)
    {
        if (!_currentBlockIsTool || _currentInput is null)
        {
            return DetectionAction.None;
        }

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (_currentIndex >= 0
                && doc.RootElement.TryGetProperty("index", out var index)
                && index.TryGetInt32(out var i)
                && i != _currentIndex)
            {
                return DetectionAction.None;
            }
        }
        catch (JsonException)
        {
            return DetectionAction.None;
        }

        var toolName = _currentToolName;
        var raw = _currentInput.ToString();
        ResetCurrentBlock();

        JsonDocument inputDoc;
        try
        {
            inputDoc = JsonDocument.Parse(raw.Length == 0 ? "{}" : raw);
        }
        catch (JsonException ex)
        {
            return Trip(toolName, "malformed JSON: " + ex.Message, RequiresBuffering ? "buffer" : "stream");
        }

        using (inputDoc)
        {
            if (!ValidateToolInput(toolName, inputDoc.RootElement, out var reason))
            {
                return Trip(toolName, reason, RequiresBuffering ? "buffer" : "stream");
            }
        }

        return DetectionAction.None;
    }

    private bool ValidateToolInput(string? toolName, JsonElement input, out string reason)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            reason = "tool input must be a JSON object";
            return false;
        }

        if (string.IsNullOrEmpty(toolName)
            || _toolsByName is null
            || !_toolsByName.TryGetValue(toolName, out var tool)
            || tool.InputSchema is null)
        {
            reason = "";
            return true;
        }

        return JsonSchemaSubsetValidator.Validate(tool.InputSchema, input, out reason);
    }

    private DetectionAction Trip(string? toolName, string reason, string delivery)
    {
        var signal = _opts.Signal;
        _ctx.ToolInputInvalidDetected = true;
        _log.LogWarning(
            "tool-input-invalid detected: tool={Tool} reason={Reason} signal={Signal} delivery={Delivery} - forcing client retry",
            string.IsNullOrEmpty(toolName) ? "?" : toolName,
            reason,
            ResponseLeakError.ErrorType(signal),
            delivery);
        return DetectionAction.Abort(
            ResponseLeakError.JsonWithMessage(signal, ToolInputInvalidMessage),
            ResponseLeakError.HttpStatus(signal));
    }

    private void ResetCurrentBlock()
    {
        _currentInput = null;
        _currentIndex = -1;
        _currentToolName = null;
        _currentBlockIsTool = false;
    }

    private static bool TryGetObject(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        value = default;
        return false;
    }

    private const string ToolInputInvalidMessage =
        "[copilot-bridge] The upstream model produced malformed tool input that does not match the declared tool schema; forcing a clean retry.";

    private static class JsonSchemaSubsetValidator
    {
        public static bool Validate(InputSchema schema, JsonElement value, out string reason)
        {
            using var schemaDoc = JsonDocument.Parse(SchemaToJson(schema));
            return ValidateAgainstSchema(schemaDoc.RootElement, value, "$", out reason);
        }

        private static string SchemaToJson(InputSchema schema)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", schema.Type);
                if (schema.Properties is { } properties)
                {
                    writer.WritePropertyName("properties");
                    properties.WriteTo(writer);
                }
                if (schema.Required is { Count: > 0 } required)
                {
                    writer.WritePropertyName("required");
                    writer.WriteStartArray();
                    foreach (var name in required)
                    {
                        writer.WriteStringValue(name);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static bool ValidateAgainstSchema(JsonElement schema, JsonElement value, string path, out string reason)
        {
            var expectedType = TryGetType(schema);
            if (expectedType is not null && !MatchesType(value, expectedType))
            {
                reason = $"{path} expected {expectedType} but got {KindName(value.ValueKind)}";
                return false;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
                {
                    foreach (var req in required.EnumerateArray())
                    {
                        if (req.ValueKind != JsonValueKind.String) continue;
                        var name = req.GetString();
                        if (string.IsNullOrEmpty(name)) continue;
                        if (!value.TryGetProperty(name, out _))
                        {
                            reason = $"{path}.{name} is required";
                            return false;
                        }
                    }
                }

                if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in properties.EnumerateObject())
                    {
                        if (value.TryGetProperty(prop.Name, out var child)
                            && !ValidateAgainstSchema(prop.Value, child, path + "." + prop.Name, out reason))
                        {
                            return false;
                        }
                    }
                }
            }
            else if (value.ValueKind == JsonValueKind.Array
                     && schema.TryGetProperty("items", out var items)
                     && items.ValueKind == JsonValueKind.Object)
            {
                var idx = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (!ValidateAgainstSchema(items, item, path + "[" + idx + "]", out reason))
                    {
                        return false;
                    }
                    idx++;
                }
            }

            reason = "";
            return true;
        }

        private static string? TryGetType(JsonElement schema)
        {
            if (!schema.TryGetProperty("type", out var type)) return null;
            return type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        }

        private static bool MatchesType(JsonElement value, string expected) => expected switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string KindName(JsonValueKind kind) => kind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => kind.ToString(),
        };
    }
}
