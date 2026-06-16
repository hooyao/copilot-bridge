using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.Codex;

/// <summary>
/// T4 — the Codex client-edge OUTBOUND translator: IR Anthropic stream/body →
/// Responses stream/body, back to Codex. The mirror of T3. Stateful for
/// streaming: the IR Anthropic event grammar
/// (<c>message_start</c> → <c>content_block_start/_delta/_stop</c> →
/// <c>message_delta</c> → <c>message_stop</c>) is translated back into the
/// Responses grammar Codex's parser consumes
/// (<c>response.created</c> → <c>output_item.added</c> →
/// <c>output_text.delta</c> / <c>function_call_arguments.delta</c> →
/// <c>output_item.done</c> → <c>response.completed</c>), with NO <c>[DONE]</c>
/// (Codex's parser requires terminal <c>response.completed</c>, §2.5/§3.3).
/// </summary>
internal sealed class IrToResponsesOutboundAdapter : IClientOutboundAdapter<MessagesRequest>
{
    private readonly ILogger<IrToResponsesOutboundAdapter> _log;

    public IrToResponsesOutboundAdapter(ILogger<IrToResponsesOutboundAdapter> log)
    {
        _log = log;
    }

    public string Name => "IrToResponsesOutbound(T4)";

    public async IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        BridgeContext<MessagesRequest> ctx,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _log.LogDebug("adapter {Name}: streaming IR→Responses", Name);
        var sm = new AnthropicToResponsesStream(ctx.Request.Body.Model, _log);

        // C2 — Codex's parser REQUIRES a terminal (response.completed/failed). The
        // happy-path Flush below only runs if the await-foreach completes; if the
        // upstream IR stream throws mid-relay (transient disconnect / premature
        // EOF, common on long streams), a plain `await foreach` would skip Flush
        // and hand Codex a headless stream that hangs. So iterate manually,
        // capture any fault, ALWAYS flush a terminal (response.failed on fault,
        // response.completed otherwise), then rethrow a non-transient fault.
        await using var e = irStream.GetAsyncEnumerator(ct);
        Exception? fault = null;
        while (true)
        {
            bool moved;
            try { moved = await e.MoveNextAsync(); }
            catch (Exception ex) { fault = ex; break; }
            if (!moved) break;
            foreach (var outEvt in sm.Translate(e.Current))
                yield return outEvt;
        }

        foreach (var tail in sm.FlushTerminal(failed: fault is not null))
            yield return tail;

        if (fault is not null)
        {
            _log.LogWarning("adapter {Name}: upstream IR stream faulted mid-relay ({Type}: {Message}); "
                + "emitted a terminal response.failed before propagating",
                Name, fault.GetType().Name, fault.Message);
            // Cancellation is expected on client disconnect — don't wrap it.
            if (fault is OperationCanceledException) throw fault;
            // A transient upstream fault is handled by the endpoint's transient
            // branch; rethrow so it's classified there. Non-transient too — the
            // terminal is already on the wire, the endpoint records the error.
            throw fault;
        }
    }

    public ValueTask<byte[]> AdaptBufferedAsync(
        byte[] irBody,
        BridgeContext<MessagesRequest> ctx,
        CancellationToken ct)
    {
        // Non-streaming path. On the Codex→/responses cell the strategy buffers
        // the raw Responses JSON (or error envelope) — it never round-tripped
        // through the Anthropic IR body, so it's already Responses-shaped and is
        // returned as-is. (Codex always streams in practice; this is the
        // error/edge path.)
        _log.LogDebug("adapter {Name}: buffered passthrough bytes={Bytes}", Name, irBody.Length);
        return ValueTask.FromResult(irBody);
    }
}

/// <summary>
/// State machine for T4 streaming: IR Anthropic events → Responses events.
/// Reassembles the content-block structure and emits Responses-shaped SSE. Tool
/// argument fragments (<c>input_json_delta</c>) become
/// <c>response.function_call_arguments.delta</c>; text deltas become
/// <c>response.output_text.delta</c>; the terminal becomes
/// <c>response.completed</c>.
/// </summary>
internal sealed class AnthropicToResponsesStream
{
    private readonly string _model;
    private readonly ILogger? _log;
    private int _seq;
    private bool _created;
    private int _outputIndex = -1;
    private string? _currentBlockType; // "text" | "tool_use"
    private string _itemId = "";
    private string _toolCallId = "";
    private string _toolName = "";
    private string _argsBuffer = "";
    private string _textBuffer = "";
    private string _stopReason = "end_turn";
    private long _inputTokens;
    private long _outputTokens;
    // Accumulated completed output items, for the response.completed `output[]`.
    private readonly List<string> _completedItems = [];

    public AnthropicToResponsesStream(string model, ILogger? log = null)
    {
        _model = model;
        _log = log;
    }

    public IEnumerable<SseItem<string>> Translate(SseItem<string> irEvt)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(irEvt.Data); }
        catch (JsonException ex)
        {
            if (!string.IsNullOrWhiteSpace(irEvt.Data))
                _log?.LogWarning("T4: dropped unparseable IR stream event (type={EventType}): {Error}",
                    irEvt.EventType, ex.Message);
            yield break;
        }
        using (doc)
        {
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : irEvt.EventType;

            switch (type)
            {
                case "message_start":
                    if (!_created)
                    {
                        _created = true;
                        yield return Ev("response.created", ResponseEnvelope("response.created", "in_progress"));
                        yield return Ev("response.in_progress", ResponseEnvelope("response.in_progress", "in_progress"));
                    }
                    break;

                case "content_block_start":
                    foreach (var s in OnBlockStart(root)) yield return s;
                    break;

                case "content_block_delta":
                    foreach (var s in OnBlockDelta(root)) yield return s;
                    break;

                case "content_block_stop":
                    foreach (var s in OnBlockStop()) yield return s;
                    break;

                case "message_delta":
                    // Carry the stop_reason + usage for the terminal status.
                    if (root.TryGetProperty("delta", out var md)
                        && md.TryGetProperty("stop_reason", out var sr)
                        && sr.ValueKind == JsonValueKind.String)
                        _stopReason = sr.GetString()!;
                    if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    {
                        if (u.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var i)) _inputTokens = i;
                        if (u.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var o)) _outputTokens = o;
                    }
                    break;

                case "message_stop":
                    // handled in Flush (terminal)
                    break;
            }
        }
    }

    public IEnumerable<SseItem<string>> Flush()
    {
        if (!_created) yield break;
        foreach (var s in OnBlockStop()) yield return s;
        // Honest terminal: an IR-internal error stop (T3's marker for an upstream
        // response.failed) becomes a real response.failed; everything else is
        // response.completed (with status incomplete for max_tokens).
        yield return IsFailed()
            ? Ev("response.failed", FailedEnvelope())
            : Ev("response.completed", CompletedEnvelope());
        _created = false;
    }

    /// <summary>
    /// Guarantee a terminal even when the IR stream ended/threw without a
    /// message_stop (C2). <paramref name="failed"/> forces response.failed. If no
    /// message_start was seen (empty stream), synthesize the opening envelopes so
    /// the terminal is well-formed and Codex's parser always sees a complete turn.
    /// </summary>
    public IEnumerable<SseItem<string>> FlushTerminal(bool failed)
    {
        if (failed && _stopReason != Strategies.Codex.ResponsesToAnthropicStream.ErrorStopReason)
            _stopReason = Strategies.Codex.ResponsesToAnthropicStream.ErrorStopReason;
        if (!_created)
        {
            _created = true;
            yield return Ev("response.created", ResponseEnvelope("response.created", "in_progress"));
            yield return Ev("response.in_progress", ResponseEnvelope("response.in_progress", "in_progress"));
        }
        foreach (var s in Flush()) yield return s;
    }

    private IEnumerable<SseItem<string>> OnBlockStart(JsonElement root)
    {
        var cb = root.TryGetProperty("content_block", out var c) ? c : default;
        var blockType = cb.ValueKind == JsonValueKind.Object && cb.TryGetProperty("type", out var bt)
            ? bt.GetString() : "text";
        _outputIndex++;
        _currentBlockType = blockType;
        _argsBuffer = "";
        _textBuffer = "";

        if (blockType == "tool_use")
        {
            _toolCallId = cb.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            _toolName = cb.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            _itemId = "item_" + _outputIndex;
            yield return Ev("response.output_item.added",
                $"{{\"type\":\"response.output_item.added\",\"sequence_number\":{_seq++},\"output_index\":{_outputIndex},\"item\":{FunctionCallItem(status: "in_progress")}}}");
        }
        else
        {
            _itemId = "item_" + _outputIndex;
            yield return Ev("response.output_item.added",
                $"{{\"type\":\"response.output_item.added\",\"sequence_number\":{_seq++},\"output_index\":{_outputIndex},\"item\":{MessageItem(status: "in_progress", text: null)}}}");
            // Codex expects a content part to be opened before text deltas.
            yield return Ev("response.content_part.added",
                $"{{\"type\":\"response.content_part.added\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"content_index\":0,\"part\":{{\"type\":\"output_text\",\"text\":\"\",\"annotations\":[]}}}}");
        }
    }

    private IEnumerable<SseItem<string>> OnBlockDelta(JsonElement root)
    {
        if (!root.TryGetProperty("delta", out var delta)) yield break;
        var dtype = delta.TryGetProperty("type", out var dt) ? dt.GetString() : null;
        switch (dtype)
        {
            case "text_delta":
                var text = delta.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                _textBuffer += text;
                yield return Ev("response.output_text.delta",
                    $"{{\"type\":\"response.output_text.delta\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"content_index\":0,\"delta\":{Enc(text)}}}");
                break;
            case "input_json_delta":
                var pj = delta.TryGetProperty("partial_json", out var p) ? p.GetString() ?? "" : "";
                _argsBuffer += pj;
                yield return Ev("response.function_call_arguments.delta",
                    $"{{\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"delta\":{Enc(pj)}}}");
                break;
        }
    }

    private IEnumerable<SseItem<string>> OnBlockStop()
    {
        if (_currentBlockType is null) yield break;
        if (_currentBlockType == "tool_use")
        {
            yield return Ev("response.function_call_arguments.done",
                $"{{\"type\":\"response.function_call_arguments.done\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"arguments\":{Enc(_argsBuffer)}}}");
            var fnItem = FunctionCallItem(status: "completed");
            _completedItems.Add(fnItem);
            yield return Ev("response.output_item.done",
                $"{{\"type\":\"response.output_item.done\",\"sequence_number\":{_seq++},\"output_index\":{_outputIndex},\"item\":{fnItem}}}");
        }
        else
        {
            // Close the text: output_text.done → content_part.done → output_item.done.
            yield return Ev("response.output_text.done",
                $"{{\"type\":\"response.output_text.done\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"content_index\":0,\"text\":{Enc(_textBuffer)}}}");
            yield return Ev("response.content_part.done",
                $"{{\"type\":\"response.content_part.done\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"content_index\":0,\"part\":{{\"type\":\"output_text\",\"text\":{Enc(_textBuffer)},\"annotations\":[]}}}}");
            var msgItem = MessageItem(status: "completed", text: _textBuffer);
            _completedItems.Add(msgItem);
            yield return Ev("response.output_item.done",
                $"{{\"type\":\"response.output_item.done\",\"sequence_number\":{_seq++},\"output_index\":{_outputIndex},\"item\":{msgItem}}}");
        }
        _currentBlockType = null;
        _argsBuffer = "";
        _textBuffer = "";
    }

    private string MessageItem(string status, string? text)
    {
        var content = text is null
            ? "[]"
            : $"[{{\"type\":\"output_text\",\"text\":{Enc(text)},\"annotations\":[]}}]";
        return $"{{\"type\":\"message\",\"id\":{Enc(_itemId)},\"role\":\"assistant\",\"status\":{Enc(status)},\"content\":{content}}}";
    }

    private string FunctionCallItem(string status) =>
        $"{{\"type\":\"function_call\",\"id\":{Enc(_itemId)},\"call_id\":{Enc(_toolCallId)},\"name\":{Enc(_toolName)},\"arguments\":{Enc(_argsBuffer)},\"status\":{Enc(status)}}}";

    private string ResponseEnvelope(string eventType, string status) =>
        $"{{\"type\":{Enc(eventType)},\"sequence_number\":{_seq++},\"response\":{{\"id\":\"resp_bridge\",\"object\":\"response\",\"status\":{Enc(status)},\"model\":{Enc(_model)},\"output\":[]}}}}";

    private string CompletedEnvelope()
    {
        var output = "[" + string.Join(",", _completedItems) + "]";
        return $"{{\"type\":\"response.completed\",\"sequence_number\":{_seq++},\"response\":{{\"id\":\"resp_bridge\",\"object\":\"response\",\"status\":{Enc(MapStatus())},\"model\":{Enc(_model)},\"output\":{output},\"usage\":{UsageJson()}}}}}";
    }

    private string FailedEnvelope()
    {
        // Honest failure terminal. Codex's parser models response.failed; carry
        // whatever output was assembled before the failure plus an error object.
        var output = "[" + string.Join(",", _completedItems) + "]";
        return $"{{\"type\":\"response.failed\",\"sequence_number\":{_seq++},\"response\":{{\"id\":\"resp_bridge\",\"object\":\"response\",\"status\":\"failed\",\"model\":{Enc(_model)},\"output\":{output},\"error\":{{\"code\":\"upstream_error\",\"message\":\"the upstream model backend failed mid-stream\"}},\"usage\":{UsageJson()}}}}}";
    }

    private string UsageJson() =>
        $"{{\"input_tokens\":{_inputTokens},\"output_tokens\":{_outputTokens}}}";

    private bool IsFailed() => _stopReason == Strategies.Codex.ResponsesToAnthropicStream.ErrorStopReason;

    private string MapStatus() => _stopReason == "max_tokens" ? "incomplete" : "completed";

    private static SseItem<string> Ev(string eventType, string data) => new(data, eventType);

    private static string Enc(string s) => Strategies.Codex.CodexJson.EncodeString(s);
}
