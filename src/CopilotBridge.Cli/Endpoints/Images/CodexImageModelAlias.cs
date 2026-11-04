using System.Text.Json;
using CopilotBridge.Cli.Pipeline.Routing;

namespace CopilotBridge.Cli.Endpoints.Images;

/// <summary>Maps Codex's native image-model id while retaining the other request bytes.</summary>
internal static class CodexImageModelAlias
{
    public static byte[]? Rewrite(ReadOnlyMemory<byte> body, IReadOnlyList<ImageModelAlias> aliases)
    {
        if (aliases.Count == 0) return null;
        try
        {
            var reader = new Utf8JsonReader(body.Span);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            long start = -1, end = -1;
            string? target = null;
            var modelFields = 0;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1
                    || !reader.ValueTextEquals("model"u8)) continue;
                modelFields++;
                if (!reader.Read()) return null;
                if (reader.TokenType == JsonTokenType.String)
                {
                    var source = reader.GetString();
                    foreach (var alias in aliases)
                    {
                        if (!string.Equals(alias.From, source, StringComparison.Ordinal)) continue;
                        target = alias.To;
                        start = reader.TokenStartIndex;
                        end = reader.BytesConsumed;
                        break;
                    }
                }
            }
            if (modelFields != 1 || target is null || start < 0 || end < 0) return null;
            var encoded = JsonEncodedText.Encode(target).EncodedUtf8Bytes;
            var replacement = new byte[encoded.Length + 2];
            replacement[0] = (byte)'"';
            encoded.CopyTo(replacement.AsSpan(1));
            replacement[^1] = (byte)'"';
            var from = checked((int)start);
            var to = checked((int)end);
            var result = new byte[checked(body.Length - (to - from) + replacement.Length)];
            body.Span[..from].CopyTo(result);
            replacement.CopyTo(result.AsSpan(from));
            body.Span[to..].CopyTo(result.AsSpan(from + replacement.Length));
            return result;
        }
        catch (JsonException) { return null; }
    }
}
