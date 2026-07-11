using System.Net.ServerSentEvents;
using System.Text.Json;
using CopilotBridge.Cli.Models.Anthropic.Request;
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
        _log.LogDebug("adapter {Name}: identity-buffered  bytes={Bytes}", Name, irBody.Length);
        return ValueTask.FromResult(irBody);
    }

    /// <summary>
    /// Return the event unchanged unless its data carries a bridge marker; if it does,
    /// re-emit the same event with the two marker keys removed from the tool_use
    /// content_block. Byte-identical (same instance) for every marker-free event.
    /// </summary>
    private SseItem<string> ScrubMarkers(SseItem<string> evt)
    {
        var data = evt.Data;
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
}
