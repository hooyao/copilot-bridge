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
/// <c>input_json_delta</c> fragments have closed, flagging tool input that is
/// malformed JSON or violates the declared tool schema.
/// </summary>
/// <remarks>
/// <para>
/// <b>Observe-by-default.</b> Claude Code recovers from an invalid tool call
/// natively: it parses the accumulated input with <c>safeParseJSON</c> (malformed
/// JSON falls back to <c>{}</c>), runs the tool's <c>zod strictObject.safeParse</c>,
/// and on failure feeds the model an <c>is_error</c> tool_result so it retries with
/// corrected input. Aborting the response was found to cut off exactly that recovery
/// (e.g. a real <c>AskUserQuestion</c> emitted without the required <c>question</c>
/// field — CC would have re-prompted the model, but a mid-stream abort surfaced
/// "Server error mid-response" instead). So the detector records the diagnosis
/// (<c>tool_input_invalid=</c> on the summary) but relays the response unchanged
/// unless a class is explicitly set to an <c>Abort*</c> action.
/// </para>
/// <para>
/// <b>When Abort is opted in</b> (per-class via
/// <c>MalformedJsonAction</c> / <c>SchemaViolationAction</c>): the detector aborts at
/// <c>content_block_stop</c>, the point where Claude Code would otherwise commit the
/// tool block into its conversation context (a content block is only pushed onto the
/// message list at <c>content_block_stop</c>). The abort replaces that
/// <c>content_block_stop</c> with an error event and ends the stream, so the bad
/// <c>tool_use</c> block is never committed. With the default <c>PreserveStream=true</c>
/// the error is injected mid-stream; <c>PreserveStream=false</c> buffers the whole
/// response and returns a real HTTP status before any byte is written.
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
    // True when the current tool_use block's input is grammar-constrained TEXT, not
    // a JSON object (a Codex custom tool like `exec` — T3 marks the content_block
    // with bridge_input_is_grammar_text). Such input must NOT be JSON-validated:
    // parsing raw JavaScript as JSON would falsely trip "malformed JSON".
    private bool _currentBlockIsGrammarText;

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

    // Only force whole-response buffering when a class is actually set to an Abort
    // action AND real-status delivery (PreserveStream=false) is wanted. Observe-only
    // (the default) never aborts, so it must not pay the buffering cost.
    private bool AnyAbort =>
        _opts.MalformedJsonAction != ToolInputAction.Observe
        || _opts.SchemaViolationAction != ToolInputAction.Observe;

    public override bool RequiresBuffering => AnyAbort && !_opts.PreserveStream;

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

                // Skip a custom-grammar tool (Codex `exec`): its input is raw text,
                // not a JSON object to validate (mirrors the streaming StopBlock skip).
                if (block.TryGetProperty("bridge_input_is_grammar_text", out var g)
                    && g.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var toolName = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                if (!ValidateToolInput(toolName, input, out var reason))
                {
                    // A buffered body's tool_use.input is already parsed JSON, so any
                    // failure here is a schema violation, never a JSON-parse failure.
                    var action = Flag(_opts.SchemaViolationAction, toolName, reason, "buffer");
                    if (action.Kind != DetectionActionKind.None)
                    {
                        return action;
                    }
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
            // A custom-grammar tool (Codex `exec`) marks its block: its input is raw
            // text, so skip JSON validation for it (see StopBlock).
            _currentBlockIsGrammarText =
                block.TryGetProperty("bridge_input_is_grammar_text", out var g)
                && g.ValueKind == JsonValueKind.True;

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
        var isGrammarText = _currentBlockIsGrammarText;
        // Deltas are authoritative; fall back to the start-carried input only when no
        // delta fragments arrived (so start-seed and deltas can never concatenate).
        var raw = _currentInput.Length > 0
            ? _currentInput.ToString()
            : _currentStartInput ?? "";
        ResetCurrentBlock();

        // A custom-grammar tool's input is raw text (JS for `exec`), not JSON — it
        // has no JSON shape to validate and no InputSchema. Skip validation entirely
        // so a valid exec call is never flagged "malformed JSON" (which, under an
        // Abort* action, would kill every exec call before T4).
        if (isGrammarText)
            return DetectionAction.None;

        JsonDocument inputDoc;
        try
        {
            inputDoc = JsonDocument.Parse(raw.Length == 0 ? "{}" : raw);
        }
        catch (JsonException ex)
        {
            return Flag(_opts.MalformedJsonAction, toolName, "malformed JSON: " + ex.Message, RequiresBuffering ? "buffer" : "stream");
        }

        using (inputDoc)
        {
            if (!ValidateToolInput(toolName, inputDoc.RootElement, out var reason))
            {
                return Flag(_opts.SchemaViolationAction, toolName, reason, RequiresBuffering ? "buffer" : "stream");
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

    /// <summary>
    /// Record the diagnosis and, per the class-specific <paramref name="action"/>,
    /// either observe-only (mark the summary flag, log, relay unchanged) or abort with
    /// the configured retryable envelope. The flag is set in BOTH cases so
    /// <c>tool_input_invalid=</c> reports the diagnosis regardless of action — an
    /// operator can see the rate of invalid tool calls without the bridge cutting off
    /// Claude Code's native self-heal.
    /// </summary>
    private DetectionAction Flag(ToolInputAction action, string? toolName, string reason, string delivery)
    {
        _ctx.ToolInputInvalidDetected = true;
        var tool = string.IsNullOrEmpty(toolName) ? "?" : toolName;

        if (action == ToolInputAction.Observe)
        {
            // Observe-only (default): Claude Code recovers from an invalid tool call
            // natively (safeParseJSON→{} for malformed JSON, then strictObject.safeParse
            // → is_error tool_result → model retries). Aborting here would cut that off,
            // so relay unchanged and just record it.
            _log.LogWarning(
                "tool-input-invalid observed: tool={Tool} reason={Reason} delivery={Delivery} - relaying (Claude Code self-heals invalid tool input; action=Observe)",
                tool, reason, delivery);
            return DetectionAction.None;
        }

        // The abort variant carries its own wire shape — no separate Signal knob to
        // diverge from the action.
        var signal = action == ToolInputAction.AbortApiError
            ? ResponseDetectionSignal.ApiError
            : ResponseDetectionSignal.OverloadedError;
        _log.LogWarning(
            "tool-input-invalid detected: tool={Tool} reason={Reason} signal={Signal} delivery={Delivery} - aborting the turn to keep it out of client context",
            tool, reason, ResponseDetectionError.ErrorType(signal), delivery);
        return DetectionAction.Abort(
            ResponseDetectionError.JsonWithMessage(signal, ToolInputInvalidMessage),
            ResponseDetectionError.HttpStatus(signal));
    }

    private void ResetCurrentBlock()
    {
        _currentInput = null;
        _currentIndex = -1;
        _currentToolName = null;
        _currentBlockIsTool = false;
        _currentStartInput = null;
        _currentBlockIsGrammarText = false;
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
    // ResponseDetectionError.Message). Describes both trip causes (malformed JSON OR schema
    // mismatch), since a malformed-JSON trip can fire even with no declared schema.
    private const string ToolInputInvalidMessage =
        "[copilot-bridge] The upstream model produced tool input that is malformed JSON or does not match the declared tool schema; the turn was aborted so it does not enter the conversation.";
}
