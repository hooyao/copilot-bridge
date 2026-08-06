using System.Buffers;
using System.Text.Json;
using CopilotBridge.Cli.Models.Common;

namespace CopilotBridge.Cli.Pipeline.Adapters.ClaudeCode;

/// <summary>
/// Claude-edge codec for a Responses reasoning item.
/// <para>The IR carries such an item as a hidden <c>redacted_thinking</c> block plus a
/// <c>bridge_reasoning_item</c> marker holding the whole original item (T3 pushes both,
/// without knowing which client is downstream). The Anthropic wire, however, has room
/// for exactly one string — <c>data</c> — so this edge folds the marker INTO that string
/// on the way out and unfolds it on the way back in. The fold is a fact about the CLAUDE
/// CLIENT PROTOCOL, which is why it lives here and not in a translator.</para>
/// <para>Round-trip proven against real Claude Code 2.1.220: an opaque <c>data</c> value
/// survives an assistant tool trajectory byte-for-byte and stays hidden from visible
/// output. The restored fields feed the IR part bag that T2 already pulls from.</para>
/// </summary>
internal static class ClaudeReasoningEnvelope
{
    /// <summary>The IR marker T3 stamps on a redacted-thinking block.</summary>
    internal const string Marker = "bridge_reasoning_item";

    // Long bridge-specific discriminator. Decoded only here, at the Claude edge, and
    // only after this exact prefix/version matches — a provider-native encrypted blob
    // never carries it and stays opaque.
    internal const string Prefix = "cbridge_rr_7f3a9d2c_v1:";
    private const int Version = 1;
    private const int MaxPayloadBytes = 1_048_576;
    private const int MaxEncodedLength = 1_398_104;

    /// <summary>
    /// Fold a reasoning item into the single opaque string the Claude wire can carry.
    /// Returns false when the item lacks what a replay needs, so the caller can drop
    /// the block rather than hand the client state that cannot be replayed.
    /// </summary>
    /// <remarks>
    /// The item is stored WHOLE, not projected onto the fields this bridge happens to
    /// read today. A Responses reasoning item is an open shape — gpt-5.6 keeps adding
    /// fields — and the bridge does not interpret them, so it must not discard them:
    /// the replay has to give the backend back what the backend sent. Validation is
    /// therefore separate from storage; it checks that the required fields are present
    /// without narrowing what is carried.
    /// </remarks>
    internal static bool TryFold(JsonElement item, out string data)
    {
        data = "";
        if (!TryReadFields(item, out _, out _, out _, out _))
            return false;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WritePropertyName("item");
            item.WriteTo(writer);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaxPayloadBytes) return false;
        data = Prefix + ToBase64Url(buffer.WrittenSpan);
        return true;
    }

    /// <summary>
    /// Unfold a value Claude Code echoed back. <see cref="ClaudeReasoningUnfold.Absent"/>
    /// means "not our carrier" — an ordinary provider blob, left untouched.
    /// <see cref="ClaudeReasoningUnfold.Invalid"/> means the prefix matched but the
    /// payload did not: fail closed rather than forward arbitrary JSON upstream.
    /// </summary>
    internal static ClaudeReasoningUnfold TryUnfold(
        string data,
        out string encryptedContent,
        out ProviderExtensions? bag)
    {
        encryptedContent = "";
        bag = null;
        if (!data.StartsWith(Prefix, StringComparison.Ordinal))
            return ClaudeReasoningUnfold.Absent;

        var encoded = data.AsSpan(Prefix.Length);
        if (encoded.Length == 0 || encoded.Length > MaxEncodedLength)
            return ClaudeReasoningUnfold.Invalid;

        byte[] payload;
        try
        {
            payload = FromBase64Url(encoded);
        }
        catch (FormatException)
        {
            return ClaudeReasoningUnfold.Invalid;
        }
        if (payload.Length == 0 || payload.Length > MaxPayloadBytes)
            return ClaudeReasoningUnfold.Invalid;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasOnlyKnownFields(root))
                return ClaudeReasoningUnfold.Invalid;
            if (!root.TryGetProperty("v", out var version)
                || !version.TryGetInt32(out var versionValue)
                || versionValue != Version)
                return ClaudeReasoningUnfold.Invalid;
            if (!root.TryGetProperty("item", out var item)
                || item.ValueKind != JsonValueKind.Object
                || !TryReadFields(item, out var encrypted, out _, out _, out _))
                return ClaudeReasoningUnfold.Invalid;

            encryptedContent = encrypted;
            bag = BuildReasoningBag(item);
            return ClaudeReasoningUnfold.Valid;
        }
        catch (JsonException)
        {
            return ClaudeReasoningUnfold.Invalid;
        }
    }

    /// <summary>
    /// Rebuild the part-level <c>openai</c> bag T2 pulls from. The known fields keep
    /// the names T2 already reads (<c>reasoning_id</c> / <c>reasoning_summary</c> /
    /// <c>reasoning_content</c>); everything else the backend sent rides along under
    /// <c>reasoning_extra</c> so the replay can restore the item as received rather
    /// than as this build happens to model it.
    /// </summary>
    private static ProviderExtensions BuildReasoningBag(JsonElement item)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            var extras = new List<JsonProperty>();
            foreach (var property in item.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "id":
                        writer.WriteString("reasoning_id", property.Value.GetString());
                        break;
                    case "summary":
                        writer.WritePropertyName("reasoning_summary");
                        property.Value.WriteTo(writer);
                        break;
                    case "content":
                        writer.WritePropertyName("reasoning_content");
                        property.Value.WriteTo(writer);
                        break;
                    // encrypted_content rides the block's own Data; `type` is implied
                    // by the item this rebuilds. Everything else is unmodeled state.
                    case "encrypted_content" or "type":
                        break;
                    default:
                        extras.Add(property);
                        break;
                }
            }
            if (extras.Count > 0)
            {
                writer.WritePropertyName("reasoning_extra");
                writer.WriteStartObject();
                foreach (var extra in extras) extra.WriteTo(writer);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return new ProviderExtensions
        {
            ByProvider = new Dictionary<string, JsonElement>
            {
                [Codex.ResponsesToIrInboundAdapter.OpenAiProviderKey] = doc.RootElement.Clone(),
            },
        };
    }

    private static bool TryReadFields(
        JsonElement item,
        out string encrypted,
        out JsonElement summary,
        out string? id,
        out JsonElement? content)
    {
        encrypted = "";
        summary = default;
        id = null;
        content = null;
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("encrypted_content", out var encryptedElement)
            || encryptedElement.ValueKind != JsonValueKind.String
            || encryptedElement.GetString() is not { Length: > 0 } encryptedValue
            || !item.TryGetProperty("summary", out var summaryElement)
            || summaryElement.ValueKind != JsonValueKind.Array)
            return false;

        if (item.TryGetProperty("id", out var idElement))
        {
            if (idElement.ValueKind != JsonValueKind.String
                || idElement.GetString() is not { Length: > 0 } idValue)
                return false;
            id = idValue;
        }
        if (item.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind != JsonValueKind.Array) return false;
            content = contentElement;
        }

        encrypted = encryptedValue;
        summary = summaryElement;
        return true;
    }

    private static bool HasOnlyKnownFields(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("v" or "item"))
                return false;
        return true;
    }

    private static string ToBase64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(ReadOnlySpan<char> value)
    {
        var text = value.ToString().Replace('-', '+').Replace('_', '/');
        text = (text.Length & 3) switch
        {
            0 => text,
            2 => text + "==",
            3 => text + "=",
            _ => throw new FormatException("Invalid base64url length."),
        };
        return Convert.FromBase64String(text);
    }
}

internal enum ClaudeReasoningUnfold
{
    Absent,
    Valid,
    Invalid,
}

/// <summary>
/// A Claude client echoed a bridge reasoning carrier the bridge cannot interpret
/// (corrupt, oversized, or a newer version). Surfaced as a client-side 400 rather than
/// forwarded as arbitrary reasoning JSON.
/// </summary>
internal sealed class InvalidClaudeReasoningEnvelopeException()
    : Exception("invalid bridge reasoning replay state");
