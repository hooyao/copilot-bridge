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
        var sm = new AnthropicToResponsesStream(ctx.Request.Body.Model);
        await foreach (var irEvt in irStream.WithCancellation(ct))
            foreach (var outEvt in sm.Translate(irEvt))
                yield return outEvt;
        foreach (var tail in sm.Flush())
            yield return tail;
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
    // Accumulated completed output items, for the response.completed `output[]`.
    private readonly List<string> _completedItems = [];

    public AnthropicToResponsesStream(string model)
    {
        _model = model;
    }

    public IEnumerable<SseItem<string>> Translate(SseItem<string> irEvt)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(irEvt.Data); }
        catch (JsonException) { yield break; }
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
                    // Carry the stop_reason for the terminal status.
                    if (root.TryGetProperty("delta", out var md)
                        && md.TryGetProperty("stop_reason", out var sr)
                        && sr.ValueKind == JsonValueKind.String)
                        _stopReason = sr.GetString()!;
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
        yield return Ev("response.completed", CompletedEnvelope());
        _created = false;
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
        return $"{{\"type\":\"response.completed\",\"sequence_number\":{_seq++},\"response\":{{\"id\":\"resp_bridge\",\"object\":\"response\",\"status\":{Enc(MapStatus())},\"model\":{Enc(_model)},\"output\":{output}}}}}";
    }

    private string MapStatus() => _stopReason == "max_tokens" ? "incomplete" : "completed";

    private static SseItem<string> Ev(string eventType, string data) => new(data, eventType);

    private static string Enc(string s) => Strategies.Codex.CodexJson.EncodeString(s);
}
