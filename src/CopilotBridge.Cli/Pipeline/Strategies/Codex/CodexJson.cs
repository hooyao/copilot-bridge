using System.Buffers;
using System.Text;
using System.Text.Json;

namespace CopilotBridge.Cli.Pipeline.Strategies.Codex;

/// <summary>
/// AOT-safe JSON helper shared by the Codex streaming state machines (T3/T4).
/// Encoding a string as a JSON literal via <c>JsonSerializer.Serialize(s)</c>
/// trips IL2026/IL3050 (it routes through the reflection serializer); this writes
/// directly with <see cref="Utf8JsonWriter"/> instead — fully source-gen-free.
/// </summary>
internal static class CodexJson
{
    /// <summary>
    /// Encode <paramref name="s"/> as a JSON string literal INCLUDING the
    /// surrounding quotes (e.g. <c>hello "x"</c> → <c>"hello \"x\""</c>).
    /// </summary>
    public static string EncodeString(string s)
    {
        var buf = new ArrayBufferWriter<byte>(s.Length + 2);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStringValue(s);
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }
}
