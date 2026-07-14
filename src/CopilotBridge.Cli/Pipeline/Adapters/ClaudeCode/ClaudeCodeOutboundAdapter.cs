using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Copilot;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Identity adapter — IR responses are already in the Anthropic shape Claude
/// Code expects, so both streaming and buffered modes return the input
/// unchanged, EXCEPT for one surgical scrub: on the CC→gpt route (Claude Code
/// pointed at a gpt-5.6 <c>/responses</c> backend) the response is produced by the
/// Codex strategy's T3 (<c>ResponsesToAnthropicStream</c>), which stamps three
/// bridge-internal markers for T4 to consume — <c>bridge_input_is_grammar_text</c>
/// and <c>bridge_tool_namespace</c> onto <c>tool_use</c> <c>content_block_start</c>
/// events, and <c>bridge_custom_tool_call_id</c> onto the <c>content_block_stop</c>
/// event. On the Codex route T4 removes them; but the Claude Code route has NO T4
/// (this adapter is the client edge), so without scrubbing those markers would leak
/// verbatim to <c>claude.exe</c> as bogus tool-call metadata. This adapter strips
/// all three.
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
    // Also stamped by T3, but on the content_block_stop event (the real upstream
    // custom_tool_call `ctc_` id, captured late from the input.* events). Same rule:
    // it exists only for T4 and must never reach a Claude Code client.
    private const string CustomToolCallIdMarker = "bridge_custom_tool_call_id";

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
        var scrubbed = ScrubBufferedMarkers(irBody);
        _log.LogDebug("adapter {Name}: identity-buffered (with CC-edge marker scrub) bytes={Bytes}",
            Name, scrubbed.Length);
        return ValueTask.FromResult(scrubbed);
    }

    private static byte[] ScrubBufferedMarkers(byte[] body)
    {
        if (body.AsSpan().IndexOf(GrammarMarkerBytes) < 0
            && body.AsSpan().IndexOf(NamespaceMarkerBytes) < 0
            && body.AsSpan().IndexOf(CustomToolCallIdMarkerBytes) < 0)
            return body;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return body;

            var found = false;
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.Object
                    && (block.TryGetProperty(GrammarMarker, out _)
                        || block.TryGetProperty(NamespaceMarker, out _)
                        || block.TryGetProperty(CustomToolCallIdMarker, out _)))
                {
                    found = true;
                    break;
                }
            }
            if (!found) return body;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in root.EnumerateObject())
                {
                    if (!property.NameEquals("content"))
                    {
                        property.WriteTo(writer);
                        continue;
                    }

                    writer.WritePropertyName("content");
                    writer.WriteStartArray();
                    foreach (var block in property.Value.EnumerateArray())
                    {
                        if (block.ValueKind != JsonValueKind.Object)
                        {
                            block.WriteTo(writer);
                            continue;
                        }
                        writer.WriteStartObject();
                        foreach (var inner in block.EnumerateObject())
                        {
                            if (!inner.NameEquals(GrammarMarker)
                                && !inner.NameEquals(NamespaceMarker)
                                && !inner.NameEquals(CustomToolCallIdMarker))
                                inner.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return buffer.ToArray();
        }
        catch (JsonException)
        {
            return body;
        }
    }

    private static ReadOnlySpan<byte> GrammarMarkerBytes => "bridge_input_is_grammar_text"u8;
    private static ReadOnlySpan<byte> NamespaceMarkerBytes => "bridge_tool_namespace"u8;
    private static ReadOnlySpan<byte> CustomToolCallIdMarkerBytes => "bridge_custom_tool_call_id"u8;

    /// <summary>
    /// Return the event unchanged unless its data carries a bridge marker; if it does,
    /// re-emit the same event with the marker removed. Two shapes are scrubbed: the
    /// grammar/namespace markers nested in a <c>content_block_start</c>'s
    /// <c>tool_use</c> content_block, and the <c>bridge_custom_tool_call_id</c> marker
    /// carried as a top-level key on <c>content_block_stop</c>. Byte-identical (same
    /// instance) for every marker-free event.
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
        // every non-content_block_start/stop event on the CC→gpt route.
        if (data.IndexOf(GrammarMarker, StringComparison.Ordinal) < 0
            && data.IndexOf(NamespaceMarker, StringComparison.Ordinal) < 0
            && data.IndexOf(CustomToolCallIdMarker, StringComparison.Ordinal) < 0)
            return evt;

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return evt;

            // The bridge_custom_tool_call_id marker rides as a TOP-LEVEL property on
            // content_block_stop (the other two are nested in content_block on start).
            // Strip it there. GrammarMarker/NamespaceMarker are a substring of neither
            // name, so this branch is unambiguous.
            if (root.TryGetProperty("type", out var evType)
                && evType.ValueKind == JsonValueKind.String
                && evType.GetString() == "content_block_stop")
            {
                if (!root.TryGetProperty(CustomToolCallIdMarker, out _))
                    return evt;   // substring matched a value, not the marker property
                return new SseItem<string>(RewriteWithout(root, topLevelKey: CustomToolCallIdMarker), evt.EventType);
            }

            if (!root.TryGetProperty("content_block", out var cb)
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

    /// <summary>Re-serialize an object dropping a single top-level property.</summary>
    private static string RewriteWithout(JsonElement root, string topLevelKey)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals(topLevelKey)) continue;
                prop.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
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
