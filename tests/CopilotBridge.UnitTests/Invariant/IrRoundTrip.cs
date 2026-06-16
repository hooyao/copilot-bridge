using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Helpers for the A-invariant round-trip tests. The "IR round trip" is exactly
/// what the bridge does on the hot path: parse the inbound Anthropic JSON into
/// the IR (<see cref="MessagesRequest"/>) and serialize it back out through the
/// SAME source-gen path the strategy uses
/// (<c>CopilotMessagesPassthroughStrategy.ForwardAsync</c>:
/// <c>JsonSerializer.SerializeToUtf8Bytes(body, JsonContext.Default.MessagesRequest)</c>).
/// Asserting properties of THIS function is asserting the real production code,
/// not a test-only mirror.
/// </summary>
internal static class IrRoundTrip
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    /// <summary>Load a committed fixture's <c>body</c> as raw JSON text (the inbound bytes).</summary>
    public static string LoadFixtureBodyJson(string slug)
    {
        var path = Path.Combine(FixturesDir, $"cc-request-{slug}.json");
        var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return envelope["body"]!.ToJsonString();
    }

    /// <summary>All committed <c>cc-request-*.json</c> fixture slugs.</summary>
    public static IEnumerable<string> AllFixtureSlugs()
    {
        foreach (var f in Directory.EnumerateFiles(FixturesDir, "cc-request-*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f); // cc-request-<slug>
            yield return name["cc-request-".Length..];
        }
    }

    /// <summary>Parse inbound Anthropic JSON into the IR.</summary>
    public static MessagesRequest Parse(string json) =>
        JsonSerializer.Deserialize(json, JsonContext.Default.MessagesRequest)
        ?? throw new InvalidOperationException("fixture body deserialized to null");

    /// <summary>
    /// Serialize the IR exactly as the hot-path strategy does — same
    /// <see cref="JsonContext"/> TypeInfo, so the bytes are production bytes.
    /// </summary>
    public static byte[] SerializeLikeHotPath(MessagesRequest ir) =>
        JsonSerializer.SerializeToUtf8Bytes(ir, JsonContext.Default.MessagesRequest);

    /// <summary>Parse → serialize, returning the round-tripped UTF-8 bytes.</summary>
    public static byte[] RoundTripBytes(string inboundJson) =>
        SerializeLikeHotPath(Parse(inboundJson));

    /// <summary>Round-tripped output as a <see cref="JsonNode"/> tree (for the field-diff harness).</summary>
    public static JsonNode RoundTripNode(string inboundJson) =>
        JsonNode.Parse(RoundTripBytes(inboundJson))!;

    public static JsonNode ParseNode(string json) => JsonNode.Parse(json)!;
}
