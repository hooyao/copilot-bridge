using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>Targeted live contract probes for Copilot's exact gpt-6.1-sol id.</summary>
public partial class ResponsesProbe
{
    private const string Gpt61Sol = "gpt-6.1-sol";

    public static IEnumerable<object[]> Gpt61SolEfforts() =>
        from effort in new string?[] { null, "minimal", "none", "low", "medium", "high", "xhigh", "max", "ultra" }
        select new object[] { effort! };

    /// <summary>
    /// The exact-model B2/B3 effort check remains independently runnable when
    /// an unrelated catalog row makes the all-model sweep fail first.
    /// </summary>
    [Fact]
    public async Task Gpt61Sol_LiveEffortFacts_MatchSnapshotAndCatalog()
    {
        using var client = new PlaygroundClient();
        var accepted = new List<string>();
        var rejected = new List<string>();
        foreach (var effort in EffortVocabulary)
        {
            var payload =
                "{\"model\":\"" + Gpt61Sol + "\","
                + "\"instructions\":\"Reply with exactly: ok\","
                + "\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"reply: ok\"}]}],"
                + "\"stream\":false,\"store\":false,"
                + "\"reasoning\":{\"effort\":\"" + effort + "\"},\"include\":[\"reasoning.encrypted_content\"]}";
            var (status, body) = await ProbeRetry.WithRetry(
                () => client.TryPostResponsesAsync(payload), $"{Gpt61Sol} effort={effort}");
            (WireAcceptance.IsAccepted(status, body, $"{Gpt61Sol} effort={effort}")
                ? accepted : rejected).Add(effort);
        }

        var snapshot = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)
            ?? throw new FileNotFoundException(ResponsesSnapshotFile);
        var effortFacts = snapshot["models"]?[Gpt61Sol]?["effort"]?.AsObject()
            ?? throw new InvalidDataException($"Snapshot has no {Gpt61Sol} effort facts");
        var snapshotAccepted = effortFacts["accepted"]?.AsArray()
            .Select(value => value!.GetValue<string>())
            ?? throw new InvalidDataException("Snapshot has no accepted efforts");
        var snapshotRejected = effortFacts["rejected"]?.AsArray()
            .Select(value => value!.GetValue<string>())
            ?? throw new InvalidDataException("Snapshot has no rejected efforts");
        var profile = new CodexModelProfileCatalog().Get(Gpt61Sol)
            ?? throw new InvalidDataException($"Catalog has no {Gpt61Sol} profile");
        Assert.Equal(snapshotAccepted.Order(StringComparer.Ordinal), accepted.Order(StringComparer.Ordinal));
        Assert.Equal(snapshotRejected.Order(StringComparer.Ordinal), rejected.Order(StringComparer.Ordinal));
        Assert.Equal(profile.AcceptedEfforts.Order(StringComparer.Ordinal), accepted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Gpt61Sol_LivenessProbe()
    {
        const string payload = """
          {
            "model": "gpt-6.1-sol",
            "instructions": "Reply with exactly: ok",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
            "stream": false,
            "store": false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{Gpt61Sol}] liveness -> {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 800)}");
    }

    [Theory]
    [MemberData(nameof(Gpt61SolEfforts))]
    public Task Gpt61Sol_Effort_ReProbe(string? effort) =>
        Effort_ProbeAcceptance(Gpt61Sol, effort);

    [Theory]
    [MemberData(nameof(Gpt61SolFields))]
    public Task Gpt61Sol_Field_ReProbe(string label, string extraField) =>
        Field_ProbeAcceptance(Gpt61Sol, label, extraField);

    public static IEnumerable<object[]> Gpt61SolFields() =>
        from field in Fields select new object[] { field.Label, field.Json };

    [Theory]
    [MemberData(nameof(Gpt61SolTools))]
    public Task Gpt61Sol_Tool_ReProbe(string label, string toolJson) =>
        Tool_ProbeAcceptance(Gpt61Sol, label, toolJson);

    public static IEnumerable<object[]> Gpt61SolTools() =>
        from tool in Tools select new object[] { tool.Label, tool.Json };

    [Fact]
    public async Task Gpt61Sol_StructuredImageFunctionOutput_ProbeAcceptance()
    {
        using var client = new PlaygroundClient();
        var result = await MultimodalFunctionOutputProbe.ProbeAsync(client, Gpt61Sol);
        _output.WriteLine(
            $"[{Gpt61Sol}] multimodal function output first={(int)result.FirstStatus} "
            + $"second={(int?)result.SecondStatus} answer={result.Answer} supported={result.Supported}");
    }

    /// <summary>
    /// Re-confirms each rewrite-causing effort rejection on a real Codex request
    /// already targeting this exact model. Only reasoning.effort changes; the
    /// captured streaming setting, input history, tool set and metadata remain.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("ultra")]
    public async Task Gpt61Sol_RealCodexBytes_RejectUnsupportedEffort(string rejectedEffort)
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(Gpt61Sol);
        Assert.Equal(Gpt61Sol, captured["model"]?.GetValue<string>());
        // This installed Codex binary omits reasoning for a model absent from
        // its own catalog. Add only the effort axis to its otherwise intact
        // captured request; preserve any sibling reasoning fields if a newer
        // client starts sending them.
        var reasoning = captured["reasoning"]?.AsObject();
        if (reasoning is null)
        {
            reasoning = new JsonObject();
            captured["reasoning"] = reasoning;
        }
        reasoning["effort"] = "max";

        using var client = new PlaygroundClient();
        var (acceptedStatus, acceptedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(acceptedStatus, acceptedBody, $"{Gpt61Sol} captured max effort"),
            $"Captured max-effort request was rejected: {WireAcceptance.ErrorMessage(acceptedBody)}");

        reasoning["effort"] = rejectedEffort;
        var (rejectedStatus, rejectedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        _output.WriteLine(
            $"[{Gpt61Sol}] real Codex bytes max -> {(int)acceptedStatus}; "
            + $"{rejectedEffort} -> {(int)rejectedStatus}; capture={capturePath}");
        _output.WriteLine($"  rejection: {Truncate(rejectedBody, 600)}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejectedStatus);
        Assert.Contains(rejectedEffort, rejectedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid_request_body", rejectedBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confirms the three backend-wide coercions on the exact model's captured
    /// streaming client request. Each case changes one field or one tool only.
    /// </summary>
    [Theory]
    [InlineData("store_true")]
    [InlineData("service_tier")]
    [InlineData("image_generation")]
    public async Task Gpt61Sol_RealCodexBytes_RejectUniformCoercionAxis(string axis)
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(Gpt61Sol);
        Assert.Equal(Gpt61Sol, captured["model"]?.GetValue<string>());

        using var client = new PlaygroundClient();
        var (acceptedStatus, acceptedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(acceptedStatus, acceptedBody, $"{Gpt61Sol} unmodified capture"),
            $"Unmodified capture was rejected: {WireAcceptance.ErrorMessage(acceptedBody)}");

        switch (axis)
        {
            case "store_true":
                Assert.Equal(false, captured["store"]?.GetValue<bool>());
                captured["store"] = true;
                break;
            case "service_tier":
                Assert.Null(captured["service_tier"]);
                captured["service_tier"] = "default";
                break;
            case "image_generation":
                var tools = captured["tools"]?.AsArray()
                    ?? throw new InvalidDataException($"Captured Codex body has no tools: {capturePath}");
                tools.Add(JsonNode.Parse("""{"type":"image_generation","output_format":"png"}"""));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis), axis, null);
        }

        var (rejectedStatus, rejectedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        _output.WriteLine(
            $"[{Gpt61Sol}] real Codex bytes unchanged -> {(int)acceptedStatus}; "
            + $"{axis} -> {(int)rejectedStatus}; capture={capturePath}");
        _output.WriteLine($"  rejection: {Truncate(rejectedBody, 600)}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejectedStatus);
        var error = JsonNode.Parse(rejectedBody)?["error"]
            ?? throw new InvalidDataException($"Copilot returned no error body for {axis}");
        Assert.Equal("unsupported_value", error["code"]?.GetValue<string>());
        Assert.Equal(axis switch
        {
            "store_true" => "store",
            "service_tier" => "service_tier",
            _ => "tools",
        }, error["param"]?.GetValue<string>());
    }
}
