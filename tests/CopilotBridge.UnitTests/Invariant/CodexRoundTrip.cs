using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Models;
using CopilotBridge.Cli.Models.Anthropic.Request;
using CopilotBridge.Cli.Models.Responses;
using CopilotBridge.Cli.Pipeline.Adapters.Codex;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotBridge.UnitTests.Invariant;

/// <summary>
/// Drives the Codex T1 → IR → T2 round trip used by the A-invariant suite. T1
/// (<see cref="ResponsesToIrInboundAdapter"/>) maps a Codex
/// <see cref="ResponsesRequest"/> into the Anthropic-shape IR; T2
/// (<see cref="ResponsesRequestBuilder"/>) rebuilds the Responses wire body from
/// the IR. Asserting properties of this composite is asserting the real
/// production translators (the endpoint + strategy call exactly these).
/// </summary>
internal static class CodexRoundTrip
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static readonly ResponsesToIrInboundAdapter T1 =
        new(NullLogger<ResponsesToIrInboundAdapter>.Instance);

    private static readonly CodexModelProfileCatalog Profiles = new();

    public static IEnumerable<string> FixtureSlugs()
    {
        foreach (var f in Directory.EnumerateFiles(FixturesDir, "codex-request-*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(f); // codex-request-<slug>
            yield return name["codex-request-".Length..];
        }
    }

    public static string LoadBodyJson(string slug)
    {
        var path = Path.Combine(FixturesDir, $"codex-request-{slug}.json");
        var envelope = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        return envelope["body"]!.ToJsonString();
    }

    public static ResponsesRequest ParseRequest(string json) =>
        JsonSerializer.Deserialize(json, JsonContext.Default.ResponsesRequest)
        ?? throw new InvalidOperationException("codex fixture body deserialized to null");

    /// <summary>T1: Responses → IR.</summary>
    public static MessagesRequest ToIr(ResponsesRequest req) =>
        T1.AdaptAsync(req, EmptyHeaders, default).AsTask().GetAwaiter().GetResult();

    /// <summary>T2: IR → Responses wire bytes.</summary>
    public static byte[] ToResponsesWire(MessagesRequest ir) =>
        ResponsesRequestBuilder.Build(ir, Profiles).Body;

    /// <summary>Full round trip: fixture JSON → T1 → IR → T2 → emitted Responses body node.</summary>
    public static JsonNode RoundTrip(string fixtureJson)
    {
        var req = ParseRequest(fixtureJson);
        var ir = ToIr(req);
        var wire = ToResponsesWire(ir);
        return JsonNode.Parse(wire)!;
    }

    public static JsonNode ParseNode(string json) => JsonNode.Parse(json)!;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>();
}
