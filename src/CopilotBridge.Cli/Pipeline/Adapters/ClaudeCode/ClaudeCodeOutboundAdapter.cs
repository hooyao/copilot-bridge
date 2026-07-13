using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Common;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Anthropic.Response;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Identity adapter — IR responses are already in the Anthropic shape Claude
/// Code expects, so both streaming and buffered modes return the input
/// unchanged, EXCEPT for one surgical scrub: on the CC→gpt route (Claude Code
/// pointed at a gpt-5.6 <c>/responses</c> backend) the response is produced by the
/// Codex strategy's T3 (<c>ResponsesToAnthropicStream</c>), which stamps two
/// bridge-internal markers — <c>bridge_input_is_grammar_text</c> and
/// <c>bridge_tool_namespace</c> — onto <c>tool_use</c> <c>content_block_start</c>
/// events for T4 to consume. On the Codex route T4 removes them; but the Claude
/// Code route has NO T4 (this adapter is the client edge), so without scrubbing
/// those markers would leak verbatim to <c>claude.exe</c> as bogus tool-call
/// metadata. This adapter strips them.
/// </summary>
/// <remarks>
/// The scrub is gated on a cheap substring pre-check, so the common case — a real
/// Copilot Anthropic backend, which never stamps these markers — is a pure
/// pass-through (byte-identical, no JSON parse). Only an event whose raw data
/// actually contains a marker name is re-serialized without the marker keys.
/// </remarks>
internal sealed class ClaudeCodeOutboundAdapter : IClientOutboundAdapter<MessagesRequest>
{
    // Bridge-internal markers stamped by the Codex T3 (ResponsesToAnthropicStream) on
    // tool_use content_block_start events. Must never reach a Claude Code client.
    private const string GrammarMarker = "bridge_input_is_grammar_text";
    private const string NamespaceMarker = "bridge_tool_namespace";

    private readonly ILogger<ClaudeCodeOutboundAdapter> _log;

    public ClaudeCodeOutboundAdapter(ILogger<ClaudeCodeOutboundAdapter> log)
    {
        _log = log;
    }

    public string Name => "ClaudeCodeOutbound";

    public async IAsyncEnumerable<SseItem<string>> AdaptStreamAsync(
        IAsyncEnumerable<SseItem<string>> irStream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _log.LogDebug("adapter {Name}: identity-stream (with CC-edge marker scrub)", Name);
        await foreach (var evt in irStream.WithCancellation(ct))
            yield return ScrubMarkers(evt);
    }

    public ValueTask<byte[]> AdaptBufferedAsync(
        byte[] irBody,
        CancellationToken ct)
    {
        var translated = TryTranslateResponsesBody(irBody);
        if (translated is null)
        {
            _log.LogDebug("adapter {Name}: identity-buffered  bytes={Bytes}", Name, irBody.Length);
            return ValueTask.FromResult(irBody);
        }

        _log.LogDebug(
            "adapter {Name}: Responses-buffered→Anthropic  in_bytes={InBytes} out_bytes={OutBytes}",
            Name, irBody.Length, translated.Length);
        return ValueTask.FromResult(translated);
    }

    /// <summary>
    /// Claude Code recovers from a streaming failure with a non-streaming
    /// <c>/cc</c> request. When routing selected Copilot Responses, convert that
    /// successful buffered object at the Claude client edge. Native Anthropic
    /// messages and upstream error/failed objects pass through byte-identically.
    /// </summary>
    private static byte[]? TryTranslateResponsesBody(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("object", out var objectType)
                || objectType.GetString() != "response"
                || !root.TryGetProperty("status", out var statusElement)
                || statusElement.GetString() is not ("completed" or "incomplete")
                || !root.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
                return null;

            var blocks = new List<ContentBlock>();
            var hasToolUse = false;
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemType))
                    continue;

                switch (itemType.GetString())
                {
                    case "message":
                        if (!item.TryGetProperty("content", out var content)
                            || content.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.Object
                                && part.TryGetProperty("type", out var partType)
                                && partType.GetString() == "output_text"
                                && part.TryGetProperty("text", out var text)
                                && text.ValueKind == JsonValueKind.String)
                            {
                                blocks.Add(new TextBlock { Text = text.GetString() ?? "" });
                            }
                        }
                        break;

                    case "function_call":
                        if (!item.TryGetProperty("call_id", out var callId)
                            || callId.ValueKind != JsonValueKind.String
                            || !item.TryGetProperty("name", out var name)
                            || name.ValueKind != JsonValueKind.String
                            || !item.TryGetProperty("arguments", out var arguments)
                            || arguments.ValueKind != JsonValueKind.String)
                            return null;
                        JsonElement input;
                        try
                        {
                            using var argsDoc = JsonDocument.Parse(arguments.GetString() ?? "{}");
                            if (argsDoc.RootElement.ValueKind != JsonValueKind.Object) return null;
                            input = argsDoc.RootElement.Clone();
                        }
                        catch (JsonException)
                        {
                            return null;
                        }
                        blocks.Add(new ToolUseBlock
                        {
                            Id = callId.GetString() ?? "",
                            Name = name.GetString() ?? "",
                            Input = input,
                        });
                        hasToolUse = true;
                        break;
                }
            }

            var inputTokens = 0;
            var outputTokens = 0;
            var cacheReadTokens = 0;
            if (root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = ReadInt32(usage, "input_tokens");
                outputTokens = ReadInt32(usage, "output_tokens");
                if (usage.TryGetProperty("input_tokens_details", out var details)
                    && details.ValueKind == JsonValueKind.Object)
                    cacheReadTokens = ReadInt32(details, "cached_tokens");
            }

            var status = statusElement.GetString();
            var message = new AnthropicMessage
            {
                Id = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? "msg_bridge"
                    : "msg_bridge",
                Model = root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                    ? model.GetString() ?? "unknown"
                    : "unknown",
                Content = blocks,
                StopReason = hasToolUse ? "tool_use" : status == "incomplete" ? "max_tokens" : "end_turn",
                Usage = new Usage
                {
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    CacheReadInputTokens = cacheReadTokens,
                },
            };
            return JsonSerializer.SerializeToUtf8Bytes(message, JsonContext.Default.AnthropicMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ReadInt32(JsonElement obj, string property)
    {
        if (!obj.TryGetProperty(property, out var value) || !value.TryGetInt64(out var number))
            return 0;
        return (int)Math.Clamp(number, 0, int.MaxValue);
    }

    /// <summary>
    /// Return the event unchanged unless its data carries a bridge marker; if it does,
    /// re-emit the same event with the two marker keys removed from the tool_use
    /// content_block. Byte-identical (same instance) for every marker-free event.
    /// </summary>
    private SseItem<string> ScrubMarkers(SseItem<string> evt)
    {
        var data = evt.Data;
        // A bridge-private failure stop is never a legal Claude-facing terminal.
        // T3 now propagates typed faults, but fail closed here as a final invariant
        // in case a future translator accidentally reintroduces the old marker.
        if (IsPrivateFailureMarker(evt))
        {
            throw new UpstreamResponseFailedException("bridge_private_marker");
        }
        // Fast path: no marker name present → return the exact same item (no parse,
        // no allocation). This is every event on a real Copilot Anthropic backend, and
        // every non-content_block_start event on the CC→gpt route.
        if (data.IndexOf(GrammarMarker, StringComparison.Ordinal) < 0
            && data.IndexOf(NamespaceMarker, StringComparison.Ordinal) < 0)
            return evt;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content_block", out var cb)
                || cb.ValueKind != JsonValueKind.Object)
                return evt;

            // The substring match above is only a fast filter; it also fires when a
            // marker NAME appears as a VALUE (e.g. a tool input mentioning
            // "bridge_tool_namespace" in prose). Only rewrite when the content_block
            // actually has one of the markers as a PROPERTY — otherwise return the
            // original event so the byte-identical pass-through contract holds.
            if (!cb.TryGetProperty(GrammarMarker, out _) && !cb.TryGetProperty(NamespaceMarker, out _))
                return evt;

            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.NameEquals("content_block"))
                    {
                        w.WritePropertyName("content_block");
                        w.WriteStartObject();
                        foreach (var inner in prop.Value.EnumerateObject())
                        {
                            if (inner.NameEquals(GrammarMarker) || inner.NameEquals(NamespaceMarker))
                                continue; // drop the bridge-internal marker
                            inner.WriteTo(w);
                        }
                        w.WriteEndObject();
                    }
                    else
                    {
                        prop.WriteTo(w);
                    }
                }
                w.WriteEndObject();
            }
            var scrubbed = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            return new SseItem<string>(scrubbed, evt.EventType);
        }
        catch (JsonException)
        {
            // Non-JSON or unexpected shape — the substring matched inside some other
            // text. Leave the event untouched rather than risk corrupting it; the
            // marker names are distinctive enough that a false positive here is benign.
            return evt;
        }
    }

    private static bool IsPrivateFailureMarker(SseItem<string> evt)
    {
        if (evt.EventType != "message_delta"
            || !evt.Data.Contains("stop_reason", StringComparison.Ordinal)
            || !evt.Data.Contains("error", StringComparison.Ordinal))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(evt.Data);
            return doc.RootElement.TryGetProperty("delta", out var delta)
                && delta.ValueKind == JsonValueKind.Object
                && delta.TryGetProperty("stop_reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                && reason.GetString() == "error";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
