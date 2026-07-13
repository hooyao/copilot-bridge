using System.Text.Json.Serialization;

namespace CopilotBridge.Update.Wire;

/// <summary>
/// Source-generated JSON context for the frozen update wire types, compiled into
/// BOTH the bridge CLI and the <c>copilot-updater</c> executable from this shared
/// source. It sets no naming policy: every property carries an explicit
/// <c>[JsonPropertyName]</c>, so the wire bytes are fixed regardless of either
/// application's own serializer configuration. Reflection-based serialization is
/// never used for these types — they always go through this context, which is
/// AOT-safe and trim-safe.
/// </summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UpdatePlan))]
[JsonSerializable(typeof(UpdateControlMessage))]
[JsonSerializable(typeof(UpdateReadyMessage))]
internal partial class UpdateWireJsonContext : JsonSerializerContext;
