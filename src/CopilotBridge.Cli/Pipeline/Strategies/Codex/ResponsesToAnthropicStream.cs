using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;

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
    private readonly string _model;
    private bool _messageStarted;
    private int _blockIndex = -1;
    private bool _blockOpen;
    private string _stopReason = "end_turn";

    public ResponsesToAnthropicStream(string model)
    {
        _model = model;
    }

    public IEnumerable<SseItem<string>> Translate(SseItem<string> evt)
    {
        // The Responses SSE carries the event type as the SSE `event:` field AND
        // a `type` inside the data JSON; use the data type (authoritative).
        JsonDocument doc;
        try { doc = JsonDocument.Parse(evt.Data); }
        catch (JsonException) { yield break; }
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
                    // Don't clobber a tool_use stop already latched when a
                    // function_call item opened — only a max_tokens/incomplete
                    // signal overrides it; otherwise keep what we have.
                    var completedStop = StopReasonFor(type, root);
                    if (_stopReason != "tool_use" || completedStop == "max_tokens")
                        _stopReason = completedStop;
                    foreach (var s in Flush()) yield return s;
                    break;

                case "response.failed":
                    // Surface as a message_delta with an error-ish stop then stop.
                    _stopReason = "end_turn";
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
                $"{{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":{JsonEncode(_stopReason)},\"stop_sequence\":null}},\"usage\":{{\"output_tokens\":0}}}}");
            yield return Sse("message_stop", "{\"type\":\"message_stop\"}");
            _stopReason = null!;
            _messageStarted = false;
        }
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

    private static SseItem<string> Sse(string eventType, string data) =>
        new(data, eventType);

    private static string JsonEncode(string s) => CodexJson.EncodeString(s);
}
