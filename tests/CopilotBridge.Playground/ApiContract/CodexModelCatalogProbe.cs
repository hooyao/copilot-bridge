using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Routing;
using Xunit;

namespace CopilotBridge.Playground;

public partial class ResponsesProbe
{
    /// <summary>
    /// Captures the raw live Copilot <c>/models</c> entries for every exact
    /// Responses profile the bridge can execute. The committed snapshot is
    /// evidence and an API-contract fixture, not authority for request-shape
    /// acceptance; large-context acceptance is independently probed with real
    /// Codex bytes by <see cref="OneMillionClass_RealCodexBytes_AcceptBeyondFormer272kCeiling"/>.
    /// </summary>
    [Fact]
    public async Task CaptureBridgeResponsesModelCapabilities()
    {
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryRequestAsync(HttpMethod.Get, "/models");
        Assert.Equal(System.Net.HttpStatusCode.OK, status);

        var root = JsonNode.Parse(body)?.AsObject()
            ?? throw new InvalidDataException("Copilot /models response was not a JSON object.");
        var data = root["data"]?.AsArray()
            ?? throw new InvalidDataException("Copilot /models response had no data array.");
        var profiles = new CodexModelProfileCatalog();
        var exactIds = profiles.KnownIds.ToHashSet(StringComparer.Ordinal);
        var selected = new JsonArray();

        foreach (var item in data.OfType<JsonObject>())
        {
            var id = item["id"]?.GetValue<string>();
            if (id is not null && exactIds.Contains(id))
                selected.Add(item.DeepClone());
        }

        var found = selected.OfType<JsonObject>()
            .Select(item => item["id"]?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.Ordinal);
        var missing = profiles.KnownIds.Where(id => !found.Contains(id)).Order(StringComparer.Ordinal).ToArray();
        var snapshot = new JsonObject
        {
            ["_meta"] = new JsonObject
            {
                ["captured_utc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["endpoint"] = "/models",
                ["account_type"] = "enterprise",
                ["selection"] = "exact CodexModelProfileCatalog.KnownIds; no fuzzy matches",
                ["warning"] = "Advertised capabilities are hints. Live request probes remain authoritative.",
            },
            ["missing_bridge_profile_ids"] = new JsonArray(missing.Select(id => JsonValue.Create(id)).ToArray()),
            ["data"] = selected,
        };

        _output.WriteLine($"Captured {selected.Count} live entries; {missing.Length} bridge profile ids missing from /models.");
        foreach (var id in missing) _output.WriteLine($"  missing: {id}");

        if (Environment.GetEnvironmentVariable("BRIDGE_REGEN_CATALOG_SNAPSHOT") == "1")
        {
            var path = Path.Combine(FindRepoRoot(), "docs", "copilot-codex-model-capabilities-snapshot.json");
            File.WriteAllText(path, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            _output.WriteLine($"[seeded] {path}");
        }
    }
}
