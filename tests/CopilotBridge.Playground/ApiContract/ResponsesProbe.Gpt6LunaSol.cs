using System.Text.Json.Nodes;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>Targeted live probes for Copilot's exact GPT-6 Luna and Sol ids.</summary>
public partial class ResponsesProbe
{
    public static IEnumerable<object[]> Gpt6LunaSolModels()
    {
        yield return ["gpt-6-luna"];
        yield return ["gpt-6-sol"];
    }

    public static IEnumerable<object[]> Gpt6LunaSolEfforts() =>
        from model in Gpt6LunaSolModels().Select(row => (string)row[0])
        from effort in new string?[] { null, "minimal", "none", "low", "medium", "high", "xhigh", "max", "ultra" }
        select new object[] { model, effort! };

    [Theory]
    [MemberData(nameof(Gpt6LunaSolModels))]
    public async Task Gpt6LunaSol_LivenessProbe(string model)
    {
        var payload = $$"""
          {
            "model": "{{model}}",
            "instructions": "Reply with exactly: ok",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
            "stream": false,
            "store": false
          }
          """;
        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{model}] liveness → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 600)}");
    }

    [Theory]
    [MemberData(nameof(Gpt6LunaSolEfforts))]
    public Task Gpt6LunaSol_Effort_ReProbe(string model, string? effort) =>
        Effort_ProbeAcceptance(model, effort);

    [Theory]
    [MemberData(nameof(Gpt6LunaSolModels))]
    public async Task Gpt6LunaSol_StructuredImageFunctionOutput_ProbeAcceptance(string model)
    {
        using var client = new PlaygroundClient();
        var result = await MultimodalFunctionOutputProbe.ProbeAsync(client, model);
        _output.WriteLine(
            $"[{model}] multimodal function output first={(int)result.FirstStatus} "
            + $"second={(int?)result.SecondStatus} answer={result.Answer} supported={result.Supported}");
    }

    /// <summary>
    /// Re-confirms both rewrite-causing effort boundaries against a real captured
    /// Codex request for the exact model, changing only <c>reasoning.effort</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Gpt6LunaSolModels))]
    public async Task Gpt6LunaSol_RealCodexBytes_RejectUnsupportedEfforts(string model)
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(model);
        Assert.Equal(model, captured["model"]?.GetValue<string>());
        var reasoning = captured["reasoning"]?.AsObject()
            ?? throw new InvalidDataException($"Captured Codex body has no reasoning object: {capturePath}");
        reasoning["effort"] = "max";

        using var client = new PlaygroundClient();
        var (acceptedStatus, acceptedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(acceptedStatus, acceptedBody, $"{model} captured max effort"),
            $"Captured max-effort request was rejected: {WireAcceptance.ErrorMessage(acceptedBody)}");

        foreach (var rejectedEffort in new[] { "minimal", "ultra" })
        {
            reasoning["effort"] = rejectedEffort;
            var (status, body) = await client.TryPostResponsesAsync(captured.ToJsonString());
            _output.WriteLine($"[{model}] real Codex bytes {rejectedEffort} → {(int)status} {status}");
            _output.WriteLine($"  source capture: {capturePath}");
            _output.WriteLine($"  rejection: {Truncate(body, 600)}");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
            Assert.Contains(rejectedEffort, body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("invalid_request_body", body, StringComparison.OrdinalIgnoreCase);
        }
    }
}
