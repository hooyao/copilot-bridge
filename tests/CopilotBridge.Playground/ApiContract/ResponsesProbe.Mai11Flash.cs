using System.Text.Json.Nodes;
using CopilotBridge.Cli.Pipeline.Routing;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>Exact-id probes for the live MAI-Code 1.1 Flash replacement candidate.</summary>
public partial class ResponsesProbe
{
    private const string Mai11Flash = "mai-code-1.1-flash";

    [Fact]
    public async Task MaiCodePicker_Retired_LivenessAssert()
    {
        const string payload = """
            {"model":"mai-code-1-flash-picker","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Reply ok"}]}],"stream":false,"store":false}
            """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"picker status={(int)status} body={Truncate(body, 1200)}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains("not available for integrator", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mai11Flash_LivenessProbe()
    {
        const string payload = """
            {"model":"mai-code-1.1-flash","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Reply ok"}]}],"stream":false,"store":false}
            """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{Mai11Flash}] liveness -> {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 800)}");
    }

    public static IEnumerable<object[]> Mai11FlashEfforts() =>
        from effort in new string?[] { null, "minimal", "none", "low", "medium", "high", "xhigh", "max", "ultra" }
        select new object[] { effort! };

    [Theory]
    [MemberData(nameof(Mai11FlashEfforts))]
    public Task Mai11Flash_Effort_ReProbe(string? effort) =>
        Effort_ProbeAcceptance(Mai11Flash, effort);

    public static IEnumerable<object[]> Mai11FlashFields() =>
        from field in Fields select new object[] { field.Label, field.Json };

    [Theory]
    [MemberData(nameof(Mai11FlashFields))]
    public Task Mai11Flash_Field_ReProbe(string label, string extraField) =>
        Field_ProbeAcceptance(Mai11Flash, label, extraField);

    public static IEnumerable<object[]> Mai11FlashTools() =>
        from tool in Tools select new object[] { tool.Label, tool.Json };

    [Theory]
    [MemberData(nameof(Mai11FlashTools))]
    public Task Mai11Flash_Tool_ReProbe(string label, string toolJson) =>
        Tool_ProbeAcceptance(Mai11Flash, label, toolJson);

    [Fact]
    public async Task Mai11Flash_StructuredImageFunctionOutput_ProbeAcceptance()
    {
        using var client = new PlaygroundClient();
        var result = await MultimodalFunctionOutputProbe.ProbeAsync(client, Mai11Flash);
        _output.WriteLine(
            $"[{Mai11Flash}] multimodal first={(int)result.FirstStatus} "
            + $"second={(int?)result.SecondStatus} answer={result.Answer} supported={result.Supported}");
    }

    [Theory]
    [InlineData("mai-code-1.1-flash")]
    [InlineData("gpt-5-mini")]
    public async Task MinimalWithWebSearch_LiveBackendFactMatchesCatalog(string model)
    {
        var baseline = $$$"""
            {"model":"{{{model}}}","instructions":"Reply with exactly: ok",
             "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
             "stream":false,"store":false,"reasoning":{"effort":"minimal"}}
            """;
        var withSearch = $$$"""
            {"model":"{{{model}}}","instructions":"Reply with exactly: ok",
             "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
             "stream":false,"store":false,"reasoning":{"effort":"minimal"},
             "tool_choice":"auto","tools":[{"type":"web_search"}]}
            """;
        using var client = new PlaygroundClient();
        var (baselineStatus, baselineBody) = await client.TryPostResponsesAsync(baseline);
        Assert.True(
            WireAcceptance.IsAccepted(baselineStatus, baselineBody, $"{model} minimal alone"),
            $"{model}: minimal alone rejected: {WireAcceptance.ErrorMessage(baselineBody)}");
        var (status, body) = await client.TryPostResponsesAsync(withSearch);
        var rejectedWithSearch = !WireAcceptance.IsAccepted(status, body, $"{model} minimal+web_search");
        _output.WriteLine($"[{model}] minimal alone -> {(int)baselineStatus}; with web_search -> {(int)status}");
        _output.WriteLine($"  body: {Truncate(body, 600)}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains("web_search", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CodexModelProfileCatalog.MinimalWithWebSearchRequiresHigherEffort, rejectedWithSearch);
        var snapshot = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)
            ?? throw new FileNotFoundException(ResponsesSnapshotFile);
        Assert.Equal(
            rejectedWithSearch,
            snapshot["models"]?[model]?["minimal_with_web_search_rejected"]?.GetValue<bool>());
    }

    /// <summary>
    /// Replays the exact model's real three-turn streaming Codex request and
    /// changes only reasoning.effort. This guards every effort fallback that
    /// would otherwise silently downgrade a supported request.
    /// </summary>
    [Theory]
    [InlineData("none", false)]
    [InlineData("xhigh", false)]
    [InlineData("max", false)]
    [InlineData("ultra", false)]
    [InlineData("minimal", false)]
    public async Task Mai11Flash_RealCodexBytes_EffortContract(string effort, bool expectedAccepted)
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(Mai11Flash);
        Assert.Equal(Mai11Flash, captured["model"]?.GetValue<string>());
        var reasoning = captured["reasoning"]?.AsObject();
        if (reasoning is null)
        {
            reasoning = new JsonObject();
            captured["reasoning"] = reasoning;
        }

        using var client = new PlaygroundClient();
        reasoning["effort"] = "high";
        var (baselineStatus, baselineBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(baselineStatus, baselineBody, $"{Mai11Flash} captured high"),
            $"Captured high-effort request was rejected: {WireAcceptance.ErrorMessage(baselineBody)}");

        reasoning["effort"] = effort;
        var (status, body) = await client.TryPostResponsesAsync(captured.ToJsonString());
        var accepted = WireAcceptance.IsAccepted(status, body, $"{Mai11Flash} captured {effort}");
        _output.WriteLine(
            $"[{Mai11Flash}] real Codex bytes high -> {(int)baselineStatus}; "
            + $"{effort} -> {(int)status}; capture={capturePath}");
        _output.WriteLine($"  body: {Truncate(body, 600)}");
        Assert.Equal(expectedAccepted, accepted);
        if (!expectedAccepted)
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
            if (effort == "minimal")
                Assert.Contains("web_search", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Mai11Flash_RealCodexBytes_MinimalWorksAfterRemovingOnlyWebSearch()
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(Mai11Flash);
        Assert.Equal(Mai11Flash, captured["model"]?.GetValue<string>());
        var reasoning = captured["reasoning"]?.AsObject();
        if (reasoning is null)
        {
            reasoning = new JsonObject();
            captured["reasoning"] = reasoning;
        }
        reasoning["effort"] = "minimal";

        using var client = new PlaygroundClient();
        var (blockedStatus, blockedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, blockedStatus);
        Assert.Contains("web_search", blockedBody, StringComparison.OrdinalIgnoreCase);

        var tools = captured["tools"]?.AsArray()
            ?? throw new InvalidDataException($"Captured request has no tools: {capturePath}");
        var removed = 0;
        for (var i = tools.Count - 1; i >= 0; i--)
        {
            if (tools[i]?["type"]?.GetValue<string>() != "web_search") continue;
            tools.RemoveAt(i);
            removed++;
        }
        Assert.Equal(1, removed);
        var (acceptedStatus, acceptedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        _output.WriteLine(
            $"[{Mai11Flash}] captured minimal+web_search -> {(int)blockedStatus}; "
            + $"same bytes without web_search -> {(int)acceptedStatus}; capture={capturePath}");
        Assert.True(
            WireAcceptance.IsAccepted(acceptedStatus, acceptedBody, $"{Mai11Flash} captured minimal without search"),
            WireAcceptance.ErrorMessage(acceptedBody));
    }

    [Theory]
    [InlineData("store_true")]
    [InlineData("service_tier")]
    [InlineData("image_generation")]
    public async Task Mai11Flash_RealCodexBytes_RejectUniformCoercionAxis(string axis)
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(Mai11Flash);
        Assert.Equal(Mai11Flash, captured["model"]?.GetValue<string>());
        using var client = new PlaygroundClient();
        var (baselineStatus, baselineBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(baselineStatus, baselineBody, $"{Mai11Flash} unmodified capture"),
            $"Unmodified capture was rejected: {WireAcceptance.ErrorMessage(baselineBody)}");

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
                    ?? throw new InvalidDataException($"Captured request has no tools: {capturePath}");
                tools.Add(JsonNode.Parse("""{"type":"image_generation","output_format":"png"}"""));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis), axis, null);
        }

        var (status, body) = await client.TryPostResponsesAsync(captured.ToJsonString());
        _output.WriteLine(
            $"[{Mai11Flash}] real Codex bytes unchanged -> {(int)baselineStatus}; "
            + $"{axis} -> {(int)status}; capture={capturePath}");
        _output.WriteLine($"  rejection: {Truncate(body, 600)}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        var error = JsonNode.Parse(body)?["error"]
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
