using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
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
///  output_item.added (custom_tool_call)→ content_block_start (tool_use, index n)
///  custom_tool_call_input.delta    → content_block_delta (input_json_delta)
///  output_item.done                → content_block_stop (index n)
///  response.completed              → message_delta (stop_reason) + message_stop
/// </code>
/// Custom tools (Codex's grammar-constrained `exec`) stream their input under the
/// <c>custom_tool_call_input.*</c> events, distinct from a plain function tool's
/// <c>function_call_arguments.*</c>; both map to the same IR <c>input_json_delta</c>.
/// Stateful: tracks the current content-block index and which output items are
/// open. Emits raw <see cref="SseItem{T}"/> with the Anthropic event name + JSON
/// data, exactly like the Anthropic passthrough path produces.
/// </summary>
internal sealed class ResponsesToAnthropicStream
{
    private readonly string _model;
    private readonly ILogger? _log;
    private bool _messageStarted;
    // Latched true once a terminal (message_delta + message_stop) has been emitted,
    // so a following FlushTerminal() is a genuine no-op. Without it, the normal
    // path — response.completed → Flush() emits the terminal and resets
    // _messageStarted=false, then the strategy's unconditional post-loop
    // FlushTerminal() sees !_messageStarted and synthesizes a SECOND, dangling
    // message_start — corrupting every streamed response the client parses.
    private bool _terminated;
    private int _blockIndex = -1;
    private bool _blockOpen;
    // True once the current tool-call block has received at least one argument
    // delta (function_call_arguments.delta OR custom_tool_call_input.delta). Lets
    // the matching `.done` event emit the full argument string as a fallback ONLY
    // when the stream sent no deltas — avoiding a double-emit when it did. Reset
    // each time a new output item opens.
    private bool _blockSawArgsDelta;
    private string _stopReason = "end_turn";
    // Usage carried off the upstream terminal (response.completed/incomplete), so
    // the IR message_delta reports Copilot's real counts instead of zeros.
    private long _inputTokens;
    private long _outputTokens;
    // Cache + reasoning sub-counts, carried verbatim off the upstream Responses
    // usage so T4 can restore Copilot's real input_tokens_details.cached_tokens /
    // output_tokens_details.reasoning_tokens on the Codex-facing terminal. Without
    // them Codex reports 0% prompt cache even when Copilot served ~all of the
    // prefix from cache. CONVENTION (Codex T3↔T4 hop ONLY, never a Claude Code
    // client): cache_read here is the cached SUBSET of _inputTokens (the OpenAI
    // total), and reasoning the SUBSET of _outputTokens — mirroring OpenAI's
    // *_details, NOT Anthropic's separate-bucket cache semantics.
    private long _cacheReadInputTokens;
    private long _reasoningOutputTokens;

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
                    {
                        _blockSawArgsDelta = true;
                        yield return Sse("content_block_delta", BlockDeltaJson(_blockIndex,
                            $"{{\"type\":\"input_json_delta\",\"partial_json\":{JsonEncode(fd.GetString()!)}}}"));
                    }
                    break;

                // Symmetric no-delta fallback for FUNCTION tools (mirrors the custom
                // tool `.done` case below). Real Copilot function tools stream via
                // deltas, so this is normally a no-op; but if a stream ever delivered
                // a function call's arguments only on `.done` (zero deltas), emit them
                // once so the call isn't empty — the same abort symptom custom tools
                // hit. function_call_arguments.done carries the full string under
                // `arguments` (custom tools use `input`). Guarded by _blockSawArgsDelta.
                case "response.function_call_arguments.done":
                    if (!_blockSawArgsDelta && _blockOpen
                        && root.TryGetProperty("arguments", out var fda) && fda.ValueKind == JsonValueKind.String
                        && fda.GetString() is { Length: > 0 } fullArgs)
                    {
                        yield return Sse("content_block_delta", BlockDeltaJson(_blockIndex,
                            $"{{\"type\":\"input_json_delta\",\"partial_json\":{JsonEncode(fullArgs)}}}"));
                    }
                    break;

                // Custom tools (Codex's `exec`/grammar tools — item.type
                // `custom_tool_call`, opened as a tool_use block in OnOutputItemAdded)
                // stream their input via custom_tool_call_input.* events, NOT
                // function_call_arguments.*. Same IR mapping: each delta is an
                // input_json_delta fragment. Without this, gpt-5.6 exec-tool arguments
                // are dropped and Codex receives arguments:"" → aborts every call.
                // The delta field is "delta" (same as function_call_arguments.delta).
                case "response.custom_tool_call_input.delta":
                    if (root.TryGetProperty("delta", out var cd) && cd.ValueKind == JsonValueKind.String)
                    {
                        _blockSawArgsDelta = true;
                        yield return Sse("content_block_delta", BlockDeltaJson(_blockIndex,
                            $"{{\"type\":\"input_json_delta\",\"partial_json\":{JsonEncode(cd.GetString()!)}}}"));
                    }
                    break;

                // custom_tool_call_input.done carries the COMPLETE input under the
                // field `input` (NOT `arguments`). Normally the deltas above already
                // streamed it, so this is a no-op; but if a stream sent the full input
                // only on `.done` (no deltas), emit it once as a single fragment so the
                // tool call is never empty. Guard on _blockSawArgsDelta to avoid a
                // double-emit on the normal delta path.
                case "response.custom_tool_call_input.done":
                    if (!_blockSawArgsDelta && _blockOpen
                        && root.TryGetProperty("input", out var ci) && ci.ValueKind == JsonValueKind.String
                        && ci.GetString() is { Length: > 0 } fullInput)
                    {
                        yield return Sse("content_block_delta", BlockDeltaJson(_blockIndex,
                            $"{{\"type\":\"input_json_delta\",\"partial_json\":{JsonEncode(fullInput)}}}"));
                    }
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
                    // A failed Responses terminal is not a successful Anthropic
                    // stop. Keep it exceptional until the downstream client edge;
                    // carry only the bounded machine code, never generated detail.
                    throw new UpstreamResponseFailedException(ExtractErrorCode(root));

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
                $"{{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":{JsonEncode(_stopReason)},\"stop_sequence\":null}},\"usage\":{{\"input_tokens\":{_inputTokens},\"output_tokens\":{_outputTokens},\"cache_read_input_tokens\":{_cacheReadInputTokens},\"reasoning_output_tokens\":{_reasoningOutputTokens}}}}}");
            yield return Sse("message_stop", "{\"type\":\"message_stop\"}");
            _stopReason = null!;
            _messageStarted = false;
            // A well-formed terminal has now been emitted; a later FlushTerminal
            // (the strategy calls it unconditionally after the loop) must NOT
            // synthesize a second message_start.
            _terminated = true;
        }
    }

    /// <summary>
    /// Emit a terminal when the upstream stream ended cleanly WITHOUT a
    /// response.completed. A throwing path never calls this method: faults remain
    /// exceptional until the downstream client boundary.
    /// </summary>
    public IEnumerable<SseItem<string>> FlushTerminal()
    {
        // Idempotent: if the stream already produced its terminal (the normal
        // response.completed path), do nothing. Otherwise a redundant call would
        // emit a dangling second message_start (empty content, no message_stop),
        // which corrupts the client's parse of an otherwise-complete response.
        if (_terminated) yield break;
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
        _blockSawArgsDelta = false;   // fresh block: no argument deltas seen yet
        if (itemType is "function_call" or "custom_tool_call")
        {
            _stopReason = "tool_use";
            var callId = item.TryGetProperty("call_id", out var cid) ? cid.GetString() ?? "" : "";
            var name = item.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name))
                _log?.LogWarning(
                    "T3: {ItemType} item missing required field(s) (call_id='{CallId}', name='{Name}') — "
                    + "the tool call will be unmatchable downstream", itemType, callId, name);
            _blockOpen = true;
            // A custom_tool_call's "input" is arbitrary grammar-constrained text
            // (Codex's `exec` streams raw JavaScript), NOT a JSON object. It still
            // rides the IR as input_json_delta (T4 re-emits it as the tool's
            // arguments verbatim), but the response-side ToolInputValidationDetector
            // must NOT try to JSON-parse it — otherwise every valid exec call trips
            // "malformed JSON" (and an Abort-configured deployment would kill it).
            // Mark the block so that detector skips JSON validation. This is a
            // bridge-internal IR marker that must never reach a client. It is removed
            // at BOTH client edges: on the Codex route T4 (AnthropicToResponsesStream)
            // rebuilds the output item from typed fields and never copies the marker;
            // on the CC→gpt route (no T4) ClaudeCodeOutboundAdapter scrubs it from the
            // content_block before the event reaches claude.exe.
            var customMarker = itemType == "custom_tool_call"
                ? ",\"bridge_input_is_grammar_text\":true"
                : "";
            // A NON-default `namespace` (gpt-5.6 collaboration/MCP tool — Copilot puts
            // it on the function_call/custom_tool_call output item, e.g.
            // "namespace":"collaboration" for list_agents) MUST reach T4 so Codex
            // learns it and echoes it back next turn; dropping it 400s the follow-up
            // ("Missing namespace for function_call", live-replayed). Carry it on the
            // tool_use content_block as a bridge-internal marker. Like the grammar
            // marker it is removed at both client edges (T4 lifts it back onto the
            // Codex-facing function_call item; ClaudeCodeOutboundAdapter scrubs it on
            // the CC→gpt route) so it never reaches a client.
            var nsMarker = item.TryGetProperty("namespace", out var nsEl)
                && nsEl.ValueKind == JsonValueKind.String
                && nsEl.GetString() is { Length: > 0 } ns
                ? $",\"bridge_tool_namespace\":{JsonEncode(ns)}"
                : "";
            yield return Sse("content_block_start",
                $"{{\"type\":\"content_block_start\",\"index\":{_blockIndex},\"content_block\":{{\"type\":\"tool_use\",\"id\":{JsonEncode(callId)},\"name\":{JsonEncode(name)},\"input\":{{}}{customMarker}{nsMarker}}}}}");
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
    /// <c>response.usage</c> of the completed event: <c>input_tokens</c> /
    /// <c>output_tokens</c> (totals) plus the <c>input_tokens_details.cached_tokens</c>
    /// and <c>output_tokens_details.reasoning_tokens</c> sub-counts. All four are
    /// carried so T4 can rebuild the exact Codex-facing usage (the cache sub-count
    /// in particular drives Codex's prompt-cache telemetry). Absent → leaves the
    /// zeros (acceptable; we never invent counts).
    /// </summary>
    private void CaptureUsage(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var resp)
            || !resp.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return;
        if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var i)) _inputTokens = i;
        if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var o)) _outputTokens = o;
        if (usage.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object
            && itd.TryGetProperty("cached_tokens", out var ct) && ct.TryGetInt64(out var c)) _cacheReadInputTokens = c;
        if (usage.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object
            && otd.TryGetProperty("reasoning_tokens", out var rt) && rt.TryGetInt64(out var r)) _reasoningOutputTokens = r;
    }

    private static string? ExtractErrorCode(JsonElement root)
    {
        // response.failed carries detail on response.error (or a top-level error).
        // Only the bounded code may leave T3; messages can contain generated text.
        if (root.TryGetProperty("response", out var resp)
            && resp.TryGetProperty("error", out var err)
            && err.ValueKind == JsonValueKind.Object
            && err.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.String)
            return code.GetString();
        if (root.TryGetProperty("error", out var topErr)
            && topErr.ValueKind == JsonValueKind.Object
            && topErr.TryGetProperty("code", out var topCode)
            && topCode.ValueKind == JsonValueKind.String)
            return topCode.GetString();
        return null;
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
