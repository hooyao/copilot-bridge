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
/// <c>input_json_delta</c> fragments have closed, and aborts the response when the
/// tool input is malformed JSON or obviously violates the declared tool schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Semantics: stop-loss, not repair or guaranteed retry.</b> The detector trips at
/// <c>content_block_stop</c> — the point where Claude Code would otherwise commit the
/// tool block into its conversation context (a content block is only pushed onto the
/// message list at <c>content_block_stop</c>). The abort replaces that
/// <c>content_block_stop</c> with an error event and ends the stream, so the bad
/// <c>tool_use</c> block is never committed and never pollutes the next turn's
/// context — which is the primary guarantee this detector provides.
/// </para>
/// <para>
/// Whether Claude Code then <i>retries automatically</i> depends on the client, not on
/// this detector: with the default <c>PreserveStream=true</c> the error is injected
/// mid-stream, after the tool block's deltas have already rendered, and current Claude
/// Code silently falls back to a non-streaming re-request unless
/// <c>CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK=1</c> (or the matching feature flag) is
/// set, in which case the error propagates to its <c>withRetry</c> path. Either way the
/// malformed tool block does not reach the client's context. <c>PreserveStream=false</c>
/// buffers the whole response and returns a real HTTP status before any byte is written.
/// </para>
/// </remarks>
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
    // Full input object carried on content_block_start itself (some backends may ship
    // it here instead of as deltas). Kept separate from _currentInput so a start-seed
    // and streamed deltas can never be concatenated into corrupt JSON — deltas win
    // when both are present (see StopBlock).
    private string? _currentStartInput;

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
        ResetCurrentBlock();
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
                // Some starts carry an initial empty object. Stash a non-empty input
                // separately (not appended to _currentInput) so it can seed validation
                // ONLY if no deltas follow — mixing start-input with deltas would
                // concatenate two JSON objects into malformed text (see StopBlock).
                var initial = input.GetRawText();
                if (!string.Equals(initial, "{}", StringComparison.Ordinal))
                {
                    _currentStartInput = initial;
                }
            }
        }
        catch (JsonException ex)
        {
            // A malformed content_block_start frame means we cannot know if this block
            // is a tool_use; abandon tracking (fail open) but leave a trace — a silent
            // miss here skips this detector's whole job for the block.
            _log.LogDebug(ex, "tool-input-validation: unparseable content_block_start; skipping block validation");
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
        catch (JsonException ex)
        {
            // A malformed event frame is not the same as malformed tool input; the
            // detector returns None so the frame still passes to the client unchanged.
            // But we skipped accumulating this fragment, so the reassembled input is
            // now incomplete and will most likely trip as "malformed JSON" at stop —
            // trace it so a spurious abort can be attributed to a dropped frame, not
            // the model.
            _log.LogDebug(ex, "tool-input-validation: unparseable content_block_delta; fragment dropped from accumulated input");
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
                // A stop for a different index than the block we are tracking. Under
                // Anthropic's contiguous-block ordering this should not happen; leave
                // our block open (a correctly-indexed stop may still arrive) but trace
                // it, since a wrong-index stop that never corrects would silently skip
                // validation of this block.
                _log.LogDebug(
                    "tool-input-validation: content_block_stop index {StopIndex} != tracked {TrackedIndex}; block not yet validated",
                    i, _currentIndex);
                return DetectionAction.None;
            }
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "tool-input-validation: unparseable content_block_stop; block not validated");
            return DetectionAction.None;
        }

        var toolName = _currentToolName;
        // Deltas are authoritative; fall back to the start-carried input only when no
        // delta fragments arrived (so start-seed and deltas can never concatenate).
        var raw = _currentInput.Length > 0
            ? _currentInput.ToString()
            : _currentStartInput ?? "";
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
        // "stop-loss": the abort keeps the malformed tool block out of the client's
        // context. Whether the client then retries automatically depends on it (see
        // the class remarks); the log records the trip, not a promise of retry.
        _log.LogWarning(
            "tool-input-invalid detected: tool={Tool} reason={Reason} signal={Signal} delivery={Delivery} - aborting the turn to keep it out of client context",
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
        _currentStartInput = null;
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

    // No " or \ (embedded in hand-built JSON without escaping — same constraint as
    // ResponseLeakError.Message). Describes both trip causes (malformed JSON OR schema
    // mismatch), since a malformed-JSON trip can fire even with no declared schema.
    private const string ToolInputInvalidMessage =
        "[copilot-bridge] The upstream model produced tool input that is malformed JSON or does not match the declared tool schema; the turn was aborted so it does not enter the conversation.";
}
