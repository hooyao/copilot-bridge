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
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<IrToResponsesOutboundAdapter> _log;

    public IrToResponsesOutboundAdapter(
        BridgeContext<MessagesRequest> ctx,
        ILogger<IrToResponsesOutboundAdapter> log)
    {
        _ctx = ctx;
        _log = log;
    }

    public string Name => "IrToResponsesOutbound(T4)";

    public async IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _log.LogDebug("adapter {Name}: streaming IR→Responses", Name);
        var sm = new AnthropicToResponsesStream(_ctx.Request.Body.Model, _log);

        // C2 — Codex's parser REQUIRES a terminal (response.completed/failed). The
        // happy-path Flush below only runs if the await-foreach completes; if the
        // upstream IR stream throws mid-relay (transient disconnect / premature
        // EOF, common on long streams), a plain `await foreach` would skip Flush
        // and hand Codex a headless stream that hangs. So iterate manually,
        // capture any fault, ALWAYS flush a terminal (response.failed on fault,
        // response.completed otherwise), then rethrow it for endpoint accounting.
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
            _log.LogDebug("adapter {Name}: upstream IR stream faulted mid-relay ({Type}: {Message}); "
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
        CancellationToken ct)
    {
        if (ReferenceEquals(irBody, _ctx.Response.InitialBufferedIrBody)
            && _ctx.Response.BufferedResponsesWireBody is { } original)
        {
            // The byte-preserving shortcut sends the ORIGINAL upstream bytes back to
            // Codex verbatim — LOSSLESS, which is its whole point (a buffered response
            // can carry reasoning/encrypted_content and fields the IR does not model).
            // The one exception: a custom_tool_call whose id is not `ctc`-prefixed would,
            // passed through unchanged, be echoed next turn and 400 ("Expected an ID
            // that begins with 'ctc'"). So SURGICALLY rewrite only that id in place —
            // every other item, property, and unknown field is copied byte-for-byte.
            // The far more common no-custom-tool / already-`ctc` case returns the exact
            // original bytes (no rewrite).
            var patched = RewriteNonCtcCustomToolCallIds(original);
            _log.LogDebug("adapter {Name}: buffered Responses byte-preserving passthrough bytes={Bytes}{Note}",
                Name, patched.Length, ReferenceEquals(patched, original) ? "" : " (custom_tool_call id rewritten to ctc)");
            return ValueTask.FromResult(patched);
        }

        var translated = BufferedAnthropicToResponses.TryTranslate(irBody);
        if (translated is not null)
        {
            _log.LogDebug("adapter {Name}: buffered IR→Responses in_bytes={InBytes} out_bytes={OutBytes}",
                Name, irBody.Length, translated.Length);
            return ValueTask.FromResult(translated);
        }

        // Upstream non-success envelopes and detector-generated error bodies do
        // not represent successful response IR; preserve their existing surface.
        _log.LogDebug("adapter {Name}: buffered non-message passthrough bytes={Bytes}", Name, irBody.Length);
        return ValueTask.FromResult(irBody);
    }

    /// <summary>
    /// Return <paramref name="original"/> unchanged unless it carries a
    /// <c>custom_tool_call</c> output item whose <c>id</c> is not <c>ctc</c>-prefixed;
    /// in that case return a copy with ONLY those ids rewritten to a deterministic
    /// <c>ctc_</c> id (synthesized from the item's <c>call_id</c>), every other item and
    /// property preserved byte-faithfully. This keeps the buffered byte-shortcut
    /// lossless (reasoning/encrypted_content and unknown fields survive) while ensuring
    /// the id Codex echoes next turn begins with <c>ctc</c>. Cheap substring pre-filter
    /// makes the common (no custom tool call) case a pure passthrough with no JSON work.
    /// </summary>
    private static byte[] RewriteNonCtcCustomToolCallIds(byte[] original)
    {
        if (original.AsSpan().IndexOf("custom_tool_call"u8) < 0)
            return original;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(original); }
        catch (JsonException) { return original; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
                return original;

            // First pass: is any rewrite actually needed? If not, return the exact
            // original bytes (byte-identical passthrough contract holds).
            var needsRewrite = false;
            foreach (var item in output.EnumerateArray())
            {
                if (IsNonCtcCustomToolCall(item)) { needsRewrite = true; break; }
            }
            if (!needsRewrite) return original;

            using var buffer = new MemoryStream(original.Length + 64);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (!prop.NameEquals("output"))
                    {
                        prop.WriteTo(w);   // verbatim: id/model/usage/status/unknowns
                        continue;
                    }
                    w.WritePropertyName("output");
                    w.WriteStartArray();
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (!IsNonCtcCustomToolCall(item))
                        {
                            item.WriteTo(w);   // verbatim: reasoning, messages, ctc-ok tools
                            continue;
                        }
                        // Copy the offending custom_tool_call, replacing ONLY its id.
                        var callId = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String
                            ? cid.GetString() ?? "" : "";
                        w.WriteStartObject();
                        foreach (var f in item.EnumerateObject())
                        {
                            if (f.NameEquals("id"))
                                w.WriteString("id", SynthesizeCtcId(callId));
                            else
                                f.WriteTo(w);
                        }
                        // If the item had no id property at all, add a conforming one.
                        if (!item.TryGetProperty("id", out _))
                            w.WriteString("id", SynthesizeCtcId(callId));
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            return buffer.ToArray();
        }
    }

    private static bool IsNonCtcCustomToolCall(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("type", out var t)
            || t.ValueKind != JsonValueKind.String
            || t.GetString() != "custom_tool_call")
            return false;
        var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() : null;
        return id is null || !id.StartsWith("ctc", StringComparison.Ordinal);
    }

    /// <summary>
    /// Deterministically synthesize a <c>ctc_</c>-prefixed id from a call_id (unique per
    /// call → stable, collision-free, no RNG). Mirrors the streaming T4 fallback; Copilot
    /// only checks the <c>ctc</c> prefix on the echoed id, not the exact value.
    /// </summary>
    private static string SynthesizeCtcId(string callId)
    {
        var suffix = callId.StartsWith("call_", StringComparison.Ordinal)
            ? callId["call_".Length..]
            : callId;
        return suffix.Length > 0 ? "ctc_" + suffix : "ctc_bridge";
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
    private const string FailedStopReason = "error";
    private readonly string _model;
    private readonly ILogger? _log;
    private int _seq;
    private bool _created;
    private int _outputIndex = -1;
    private string? _currentBlockType; // "text" | "tool_use"
    private string _itemId = "";
    private string _toolCallId = "";
    private string _toolName = "";
    // The tool's non-default namespace (gpt-5.6 collaboration/MCP), lifted off the
    // tool_use content_block's bridge-internal marker (bridge_tool_namespace) that
    // T3 stamped from Copilot's function_call item. Re-emitted on the Codex-facing
    // function_call output item so Codex can round-trip it next turn. "" for a plain
    // default-namespace tool → the namespace field is omitted (byte-identical).
    private string _toolNamespace = "";
    // True when the current tool_use block is a CUSTOM (grammar) tool — carried on
    // T3's bridge_input_is_grammar_text marker. gpt-5.6's `exec` is a custom tool, and
    // codex 0.144.1's exec handler ONLY accepts ToolPayload::Custom{input} (a
    // custom_tool_call item + custom_tool_call_input.* events). Emitting it as a
    // function_call (arguments) makes codex construct ToolPayload::Function and fatal
    // with "tool exec invoked with incompatible payload" — every exec aborts at
    // dispatch. So a marked block must re-emit the native custom_tool_call shape, NOT
    // function_call. Reset per block in OnBlockStart/OnBlockStop.
    private bool _toolIsCustom;
    // The REAL upstream custom_tool_call id (a `ctc_`-prefixed Responses item id)
    // that T3 captured and rode out on the content_block_stop marker
    // bridge_custom_tool_call_id. Stamped on the COMPLETED custom_tool_call item
    // (output_item.done + response.completed output[]) — the id Codex stores and
    // echoes next turn, which Copilot REQUIRES to begin with `ctc`. Empty when T3
    // never observed one (older upstream / non-streaming edge); T4 then keeps the
    // deterministic `ctc_`-synthesized _itemId, still prefix-conformant. Set at
    // block-stop, reset per block.
    private string _customToolCallId = "";
    private string _argsBuffer = "";
    private string _textBuffer = "";
    private string _stopReason = "end_turn";
    private long _inputTokens;
    private long _outputTokens;
    // Cache + reasoning sub-counts T3 carried off Copilot's terminal (cached
    // SUBSET of _inputTokens, reasoning SUBSET of _outputTokens). Restored into
    // the Responses usage *_details so Codex's prompt-cache telemetry is honest.
    private long _cachedInputTokens;
    private long _reasoningOutputTokens;
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
                    foreach (var s in OnBlockStop(root)) yield return s;
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
                        // T3 stashes Copilot's real cache/reasoning sub-counts here; restore
                        // them on the terminal (UsageJson) instead of emitting zeros, else
                        // Codex shows 0% cache even on a near-total prompt-cache hit.
                        if (u.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var c)) _cachedInputTokens = c;
                        if (u.TryGetProperty("reasoning_output_tokens", out var rr) && rr.TryGetInt64(out var r)) _reasoningOutputTokens = r;
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
        if (failed && _stopReason != FailedStopReason)
            _stopReason = FailedStopReason;
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
            // Lift the tool namespace off T3's bridge-internal marker (present only
            // for a non-default-namespace gpt-5.6 collaboration/MCP tool) so the
            // Codex-facing function_call carries it — required for the echo round-trip.
            _toolNamespace = cb.TryGetProperty("bridge_tool_namespace", out var ns)
                && ns.ValueKind == JsonValueKind.String
                ? ns.GetString() ?? "" : "";
            // A CUSTOM (grammar) tool — exec — must be re-emitted as a custom_tool_call
            // item (codex 0.144.1's exec handler rejects a function_call as an
            // "incompatible payload"). T3 stamped bridge_input_is_grammar_text on the block.
            _toolIsCustom = cb.TryGetProperty("bridge_input_is_grammar_text", out var gt)
                && gt.ValueKind == JsonValueKind.True;
            // Observability tripwire: `exec` is a custom (grammar) tool and MUST be
            // emitted as a custom_tool_call. If it ever arrives WITHOUT the grammar
            // marker, T4 would emit a function_call → codex fatals "incompatible payload"
            // and aborts every exec — a failure invisible in HTTP status (bridge stays
            // 200). This warning surfaces that mis-shape at the bridge instead of only at
            // the downstream codex abort. (The original exec bug had no such signal.)
            if (!_toolIsCustom && string.Equals(_toolName, "exec", StringComparison.Ordinal))
                _log?.LogWarning(
                    "T4: tool 'exec' arrived WITHOUT the grammar marker — emitting it as a "
                    + "function_call, which codex rejects as an incompatible payload. Upstream "
                    + "may have changed the exec item shape (call_id={CallId}).", _toolCallId);
            // A CUSTOM tool's item id MUST begin with `ctc` — Copilot's /responses
            // 400s otherwise once Codex echoes the item back next turn ("Expected an
            // ID that begins with 'ctc'"). The real upstream ctc_ id only arrives on
            // the content_block_stop marker (T3 captures it late, from the input.*
            // events), so the added item + input deltas use a DETERMINISTIC ctc_
            // synthesis from the call_id; block-stop then overrides _itemId with the
            // real captured id for the completed item. Both are ctc-prefixed, so the
            // echo is accepted whichever id Codex keys off. A plain function/message
            // item is unaffected — it keeps the item_N id (T2 rebuilds it without an
            // id, so it never leaks upstream).
            _itemId = _toolIsCustom ? SynthesizeCtcId(_toolCallId) : "item_" + _outputIndex;
            _customToolCallId = "";
            yield return Ev("response.output_item.added",
                $"{{\"type\":\"response.output_item.added\",\"sequence_number\":{_seq++},\"output_index\":{_outputIndex},\"item\":{ToolCallItem(status: "in_progress", itemId: _itemId)}}}");
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
                // Custom (grammar) tool → stream via custom_tool_call_input.delta (field
                // `delta`); a plain function tool → function_call_arguments.delta. codex
                // routes the input by the event family, so exec MUST use the custom one.
                yield return _toolIsCustom
                    ? Ev("response.custom_tool_call_input.delta",
                        $"{{\"type\":\"response.custom_tool_call_input.delta\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"delta\":{Enc(pj)}}}")
                    : Ev("response.function_call_arguments.delta",
                        $"{{\"type\":\"response.function_call_arguments.delta\",\"sequence_number\":{_seq++},\"item_id\":{Enc(_itemId)},\"output_index\":{_outputIndex},\"delta\":{Enc(pj)}}}");
                break;
        }
    }

    private IEnumerable<SseItem<string>> OnBlockStop(JsonElement root = default)
    {
        if (_currentBlockType is null) yield break;
        if (_currentBlockType == "tool_use")
        {
            // Lift the real upstream ctc_ id T3 captured (rode out on the
            // content_block_stop marker). It becomes the COMPLETED item's id — the id
            // Codex echoes next turn, which Copilot requires to begin with `ctc`.
            // Enforce the `ctc` prefix HERE, at the consumption boundary, not just at
            // T3's capture: this adapter's invariant is that it never emits a non-`ctc`
            // custom-tool id, so a malformed/replayed IR event carrying a non-conforming
            // marker (e.g. `item_1`) must NOT override the safe synthesized id and
            // reintroduce the follow-up 400. Non-conforming or absent → keep the
            // deterministic ctc_ synthesis already in _itemId.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("bridge_custom_tool_call_id", out var ctc)
                && ctc.ValueKind == JsonValueKind.String
                && ctc.GetString() is { Length: > 0 } realId
                && realId.StartsWith("ctc", StringComparison.Ordinal))
                _customToolCallId = realId;
            var completedItemId = _customToolCallId.Length > 0 ? _customToolCallId : _itemId;
            if (_toolIsCustom)
            {
                // Custom (grammar) tool — exec: close with custom_tool_call_input.done
                // (field `input`) + a custom_tool_call output item (codex requires the
                // Custom payload shape, else "incompatible payload").
                yield return Ev("response.custom_tool_call_input.done",
                    $"{{\"type\":\"response.custom_tool_call_input.done\",\"sequence_number\":{_seq++},\"item_id\":{Enc(completedItemId)},\"output_index\":{_outputIndex},\"input\":{Enc(_argsBuffer)}}}");
            }
            else
            {
                yield return Ev("response.function_call_arguments.done",
                    $"{{\"type\":\"response.function_call_arguments.done\",\"sequence_number\":{_seq++},\"item_id\":{Enc(completedItemId)},\"output_index\":{_outputIndex},\"arguments\":{Enc(_argsBuffer)}}}");
            }
            var toolItem = ToolCallItem(status: "completed", itemId: completedItemId);
            _completedItems.Add(toolItem);
            yield return Ev("response.output_item.done",
                $"{{\"type\":\"response.output_item.done\",\"sequence_number\":{_seq++},\"output_index\":{_outputIndex},\"item\":{toolItem}}}");
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
        _toolNamespace = "";
        _toolIsCustom = false;
        _customToolCallId = "";
    }

    private string MessageItem(string status, string? text)
    {
        var content = text is null
            ? "[]"
            : $"[{{\"type\":\"output_text\",\"text\":{Enc(text)},\"annotations\":[]}}]";
        return $"{{\"type\":\"message\",\"id\":{Enc(_itemId)},\"role\":\"assistant\",\"status\":{Enc(status)},\"content\":{content}}}";
    }

    /// <summary>
    /// Build the Codex-facing tool-call output item. A CUSTOM (grammar) tool — exec —
    /// is emitted as <c>{type:"custom_tool_call", call_id, [namespace,] name, input}</c>
    /// (codex 0.144.1's exec handler requires the Custom payload; a function_call is
    /// rejected as an "incompatible payload"). A plain function tool is
    /// <c>{type:"function_call", call_id, [namespace,] name, arguments}</c>. Both forms
    /// carry <c>namespace</c> when the block has one — per openai/codex
    /// <c>ResponseItem.ts</c>, <c>custom_tool_call</c> also has a <c>namespace?</c> field.
    /// Today exec is never namespaced, so the custom+namespace combination doesn't arise;
    /// but emitting it on both forms means a future namespaced custom tool round-trips its
    /// namespace instead of silently dropping it (which would 400 the echo turn).
    /// <paramref name="itemId"/> is the item's <c>id</c>: a deterministic <c>ctc_</c>
    /// synthesis on the in-progress event (the real id isn't known until block-stop) and
    /// the REAL captured upstream <c>ctc_</c> id on the completed event — the id Codex
    /// stores and echoes next turn, which Copilot requires to begin with <c>ctc</c>.
    /// </summary>
    private string ToolCallItem(string status, string itemId)
    {
        var nsField = _toolNamespace.Length > 0 ? $"\"namespace\":{Enc(_toolNamespace)}," : "";
        return _toolIsCustom
            ? $"{{\"type\":\"custom_tool_call\",\"id\":{Enc(itemId)},\"call_id\":{Enc(_toolCallId)},{nsField}\"name\":{Enc(_toolName)},\"input\":{Enc(_argsBuffer)},\"status\":{Enc(status)}}}"
            : $"{{\"type\":\"function_call\",\"id\":{Enc(itemId)},\"call_id\":{Enc(_toolCallId)},{nsField}\"name\":{Enc(_toolName)},\"arguments\":{Enc(_argsBuffer)},\"status\":{Enc(status)}}}";
    }

    /// <summary>
    /// Deterministically synthesize a `ctc_`-prefixed item id for a custom_tool_call
    /// when T3 captured no real upstream id. Derived from the call_id (already unique
    /// per tool call) so the value is stable and collision-free without any RNG
    /// (which AOT + resumable determinism forbid). Copilot's /responses only checks
    /// the `ctc` PREFIX on the echoed id, not that it equals the original, so a
    /// synthesized id is accepted. The `call_` prefix (if present) is stripped to
    /// avoid a redundant token; a bare or empty call_id still yields a `ctc_`-prefixed
    /// value.
    /// </summary>
    private static string SynthesizeCtcId(string callId)
    {
        var suffix = callId.StartsWith("call_", StringComparison.Ordinal)
            ? callId["call_".Length..]
            : callId;
        return suffix.Length > 0 ? "ctc_" + suffix : "ctc_bridge";
    }

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
        // Codex's ResponseCompleted parser REQUIRES total_tokens (and tolerates the
        // *_details sub-objects); omitting total_tokens makes it reject the terminal
        // with "missing field `total_tokens`" and reconnect-loop. Mirror the real
        // Copilot /responses usage shape (responses-sse fixtures), restoring the
        // cached/reasoning sub-counts T3 carried — without them Codex reports 0%
        // prompt cache even when Copilot served the prefix from cache. cached is a
        // subset of input_tokens, reasoning a subset of output_tokens, so neither
        // affects total_tokens (= input + output).
        $"{{\"input_tokens\":{_inputTokens},\"input_tokens_details\":{{\"cached_tokens\":{_cachedInputTokens}}},"
        + $"\"output_tokens\":{_outputTokens},\"output_tokens_details\":{{\"reasoning_tokens\":{_reasoningOutputTokens}}},"
        + $"\"total_tokens\":{_inputTokens + _outputTokens}}}";

    private bool IsFailed() => _stopReason == FailedStopReason;

    private string MapStatus() => _stopReason == "max_tokens" ? "incomplete" : "completed";

    private static SseItem<string> Ev(string eventType, string data) => new(data, eventType);

    private static string Enc(string s) => Strategies.Codex.CodexJson.EncodeString(s);
}
