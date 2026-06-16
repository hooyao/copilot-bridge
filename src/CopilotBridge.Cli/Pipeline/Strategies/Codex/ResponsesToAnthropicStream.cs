using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// T3 state machine — Copilot <c>/responses</c> SSE → IR Anthropic stream events.
/// Consumes the Responses event grammar and emits the Anthropic grammar the IR
/// (and the response stages) expect:
/// <code>
///  response.created                → message_start
///  output_item.added (message)     → content_block_start (text, index n)
///  output_text.delta               → content_block_delta (text_delta)
///  output_item.added (function_call)→ content_block_start (tool_use, index n)
///  function_call_arguments.delta   → content_block_delta (input_json_delta)
///  output_item.done                → content_block_stop (index n)
///  response.completed              → message_delta (stop_reason) + message_stop
/// </code>
/// Stateful: tracks the current content-block index and which output items are
/// open. Emits raw <see cref="SseItem{T}"/> with the Anthropic event name + JSON
/// data, exactly like the Anthropic passthrough path produces.
/// </summary>
internal sealed class ResponsesToAnthropicStream
{
    /// <summary>
    /// IR-internal stop reason used ONLY between T3 and T4 to carry an upstream
    /// failure honestly: Anthropic's grammar has no "failed" stop, so T3 latches
    /// this private marker and T4 maps it to a real Responses <c>response.failed</c>
    /// terminal instead of a fake <c>response.completed</c>. Never reaches a
    /// Claude Code client (it only exists on the Codex T3→T4 hop).
    /// </summary>
    internal const string ErrorStopReason = "error";

    private readonly string _model;
    private readonly ILogger? _log;
    private bool _messageStarted;
    private int _blockIndex = -1;
    private bool _blockOpen;
    private string _stopReason = "end_turn";
    // Usage carried off the upstream terminal (response.completed/incomplete), so
    // the IR message_delta reports Copilot's real counts instead of zeros.
    private long _inputTokens;
    private long _outputTokens;

    public ResponsesToAnthropicStream(string model, ILogger? log = null)
    {
        _model = model;
        _log = log;
    }

    public IEnumerable<SseItem<string>> Translate(SseItem<string> evt)
    {
        // The Responses SSE carries the event type as the SSE `event:` field AND
        // a `type` inside the data JSON; use the data type (authoritative).
        JsonDocument doc;
        try { doc = JsonDocument.Parse(evt.Data); }
        catch (JsonException ex)
        {
            // A blank/keepalive data line is benign (SseParser can hand us empty
            // data) — skip silently. Non-empty data that fails to parse is a real
            // signal something upstream changed shape; surface it rather than
            // dropping it without a trace.
            if (!string.IsNullOrWhiteSpace(evt.Data))
                _log?.LogWarning(
                    "T3: dropped unparseable /responses SSE event (type={EventType}): {Error}; data={Data}",
                    evt.EventType, ex.Message, Truncate(evt.Data, 200));
            yield break;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : evt.EventType;

            switch (type)
            {
                case "response.created":
                    if (!_messageStarted)
                    {
                        _messageStarted = true;
                        yield return Sse("message_start", MessageStartJson());
                    }
                    break;

                case "response.output_item.added":
                    foreach (var s in OnOutputItemAdded(root)) yield return s;
                    break;

                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var td) && td.ValueKind == JsonValueKind.String)
                        yield return Sse("content_block_delta", BlockDeltaJson(_blockIndex,
                            $"{{\"type\":\"text_delta\",\"text\":{JsonEncode(td.GetString()!)}}}"));
                    break;

                case "response.function_call_arguments.delta":
                    if (root.TryGetProperty("delta", out var fd) && fd.ValueKind == JsonValueKind.String)
                        yield return Sse("content_block_delta", BlockDeltaJson(_blockIndex,
                            $"{{\"type\":\"input_json_delta\",\"partial_json\":{JsonEncode(fd.GetString()!)}}}"));
                    break;

                case "response.output_item.done":
                    if (_blockOpen)
                    {
                        _blockOpen = false;
                        yield return Sse("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{_blockIndex}}}");
                    }
                    break;

                case "response.completed":
                case "response.incomplete":
                    CaptureUsage(root);
                    // Don't clobber a tool_use stop already latched when a
                    // function_call item opened — only a max_tokens/incomplete
                    // signal overrides it; otherwise keep what we have.
                    var completedStop = StopReasonFor(type, root);
                    if (_stopReason != "tool_use" || completedStop == "max_tokens")
                        _stopReason = completedStop;
                    foreach (var s in Flush()) yield return s;
                    break;

                case "response.failed":
                    // Upstream FAILED. Carry it honestly: latch the IR-internal
                    // error stop so T4 emits a real response.failed terminal, not a
                    // fake completed. Log the upstream error so the operator can see
                    // why (the failure detail vanishes otherwise — the IR has no
                    // typed home for it).
                    _log?.LogWarning("T3: upstream /responses signalled response.failed: {Error}",
                        ExtractError(root));
                    _stopReason = ErrorStopReason;
                    foreach (var s in Flush()) yield return s;
                    break;

                // in_progress, content_part.*, reasoning_*, etc. — no IR equivalent
                // needed for the bridge's purposes; swallow.
            }
        }
    }

    /// <summary>Emit any unclosed block + the terminal message_delta/message_stop once.</summary>
    public IEnumerable<SseItem<string>> Flush()
    {
        if (!_messageStarted) yield break;
        if (_blockOpen)
        {
            _blockOpen = false;
            yield return Sse("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{_blockIndex}}}");
        }
        if (_stopReason is not null)
        {
            yield return Sse("message_delta",
                $"{{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":{JsonEncode(_stopReason)},\"stop_sequence\":null}},\"usage\":{{\"input_tokens\":{_inputTokens},\"output_tokens\":{_outputTokens}}}}}");
            yield return Sse("message_stop", "{\"type\":\"message_stop\"}");
            _stopReason = null!;
            _messageStarted = false;
        }
    }

    /// <summary>
    /// Emit a terminal even when the upstream stream ended (or threw) WITHOUT a
    /// response.completed — Codex's parser requires a terminal. Called by the
    /// strategy on the throwing/empty path. <paramref name="failed"/> latches the
    /// error stop so T4 produces response.failed. If no message_start was ever
    /// emitted (empty stream), synthesize one so the terminal is well-formed.
    /// </summary>
    public IEnumerable<SseItem<string>> FlushTerminal(bool failed)
    {
        if (failed) _stopReason = ErrorStopReason;
        if (!_messageStarted)
        {
            _messageStarted = true;
            yield return Sse("message_start", MessageStartJson());
        }
        foreach (var s in Flush()) yield return s;
    }

    private IEnumerable<SseItem<string>> OnOutputItemAdded(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item)) yield break;
        var itemType = item.TryGetProperty("type", out var it) ? it.GetString() : null;

        // A reasoning item has no Anthropic content-block equivalent in the
        // bridge's stream (its encrypted_content is carried on the request side,
        // not re-emitted as a visible delta) — skip it, don't open an empty text
        // block that would then need a matching stop.
        if (itemType == "reasoning") yield break;

        // Close a previous still-open block defensively.
        if (_blockOpen)
        {
            yield return Sse("content_block_stop", $"{{\"type\":\"content_block_stop\",\"index\":{_blockIndex}}}");
            _blockOpen = false;
        }

        _blockIndex++;
        if (itemType is "function_call" or "custom_tool_call")
        {
            _stopReason = "tool_use";
            var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
                _log?.LogWarning(
                    "T3: function_call item missing required field(s) (call_id='{CallId}', name='{Name}') — "
                    + "the tool call will be unmatchable downstream", callId, name);
            _blockOpen = true;
            yield return Sse("content_block_start",
                $"{{\"type\":\"content_block_start\",\"index\":{_blockIndex},\"content_block\":{{\"type\":\"tool_use\",\"id\":{JsonEncode(callId)},\"name\":{JsonEncode(name)},\"input\":{{}}}}}}");
        }
        else
        {
            // message / text item.
            _blockOpen = true;
            yield return Sse("content_block_start",
                $"{{\"type\":\"content_block_start\",\"index\":{_blockIndex},\"content_block\":{{\"type\":\"text\",\"text\":\"\"}}}}");
        }
    }

    /// <summary>
    /// Pull token usage off the upstream terminal. Copilot puts it on
    /// <c>response.usage</c> (input_tokens/output_tokens) of the completed event.
    /// Absent → leaves the zeros (acceptable; we never invent counts).
    /// </summary>
    private void CaptureUsage(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var resp)
            || !resp.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return;
        if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var i)) _inputTokens = i;
        if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var o)) _outputTokens = o;
    }

    private static string ExtractError(JsonElement root)
    {
        // response.failed carries the detail on response.error (or a top-level error).
        if (root.TryGetProperty("response", out var resp)
            && resp.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            return err.ToString();
        if (root.TryGetProperty("error", out var topErr) && topErr.ValueKind != JsonValueKind.Null)
            return topErr.ToString();
        return "(no error detail)";
    }

    private string MessageStartJson() =>
        $"{{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_codex\",\"type\":\"message\",\"role\":\"assistant\",\"model\":{JsonEncode(_model)},\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{{\"input_tokens\":0,\"output_tokens\":0}}}}}}";

    private static string BlockDeltaJson(int index, string deltaJson) =>
        $"{{\"type\":\"content_block_delta\",\"index\":{index},\"delta\":{deltaJson}}}";

    private static string StopReasonFor(string? eventType, JsonElement root)
    {
        if (eventType == "response.incomplete") return "max_tokens";
        // response.completed: peek at response.status / output for tool calls.
        if (root.TryGetProperty("response", out var r)
            && r.TryGetProperty("status", out var s)
            && s.GetString() == "incomplete")
            return "max_tokens";
        return "end_turn";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static SseItem<string> Sse(string eventType, string data) =>
        new(data, eventType);

    private static string JsonEncode(string s) => CodexJson.EncodeString(s);
}
