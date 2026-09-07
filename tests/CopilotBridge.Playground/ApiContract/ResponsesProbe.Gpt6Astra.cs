using System.Text.Json.Nodes;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

/// <summary>
/// Targeted live probes for Copilot's <c>gpt-6-astra</c> Responses model.
/// The public OpenAI migration guide documents a narrower effort vocabulary
/// and optional async tool calls, but Copilot wire behavior remains the source
/// of truth for the bridge catalog and routing policy.
/// </summary>
/// <remarks>
/// Run:
/// <code>dotnet test tests/CopilotBridge.Playground --filter "FullyQualifiedName~Gpt6Astra_" --logger "console;verbosity=detailed"</code>
/// These probes log live acceptance and raw event evidence; they intentionally
/// do not turn an expected 4xx contract boundary into a failing test.
/// </remarks>
public partial class ResponsesProbe
{
    private const string Gpt6Astra = "gpt-6-astra";

    private static readonly string?[] Gpt6AstraEfforts =
        [null, "minimal", "none", "low", "medium", "high", "xhigh", "max", "ultra"];

    [Fact]
    public async Task Gpt6Astra_LivenessProbe()
    {
        const string payload = """
          {
            "model": "gpt-6-astra",
            "instructions": "Reply with exactly: ok",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"reply: ok"}]}],
            "stream": false,
            "store": false
          }
          """;

        using var client = new PlaygroundClient();
        var (status, body) = await client.TryPostResponsesAsync(payload);
        _output.WriteLine($"[{Gpt6Astra}] liveness → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(body, 800)}");
    }

    [Theory]
    [MemberData(nameof(Gpt6AstraEffortMatrix))]
    public Task Gpt6Astra_Effort_ReProbe(string? effort) =>
        Effort_ProbeAcceptance(Gpt6Astra, effort);

    public static IEnumerable<object[]> Gpt6AstraEffortMatrix() =>
        from effort in Gpt6AstraEfforts select new object[] { effort! };

    [Theory]
    [MemberData(nameof(Gpt6AstraFieldMatrix))]
    public Task Gpt6Astra_Field_ReProbe(string label, string extraField) =>
        Field_ProbeAcceptance(Gpt6Astra, label, extraField);

    public static IEnumerable<object[]> Gpt6AstraFieldMatrix() =>
        from field in Fields select new object[] { field.Label, field.Json };

    [Theory]
    [MemberData(nameof(Gpt6AstraToolMatrix))]
    public Task Gpt6Astra_Tool_ReProbe(string label, string toolJson) =>
        Tool_ProbeAcceptance(Gpt6Astra, label, toolJson);

    public static IEnumerable<object[]> Gpt6AstraToolMatrix() =>
        from tool in Tools select new object[] { tool.Label, tool.Json };

    [Fact]
    public async Task Gpt6Astra_StructuredImageFunctionOutput_ProbeAcceptance()
    {
        using var client = new PlaygroundClient();
        var result = await MultimodalFunctionOutputProbe.ProbeAsync(client, Gpt6Astra);
        _output.WriteLine(
            $"[{Gpt6Astra}] multimodal function output first={(int)result.FirstStatus} "
            + $"second={(int?)result.SecondStatus} answer={result.Answer} supported={result.Supported}");
    }

    /// <summary>
    /// Replays the newest real gpt-5.6 Codex request while changing only the
    /// model id. This is the fidelity check for the proposed route: all client
    /// instructions, input items, tools, metadata, and streaming settings stay
    /// byte-for-value identical to traffic that already works on gpt-5.6.
    /// </summary>
    [Fact]
    public async Task Gpt6Astra_RealGpt56CodexBytes_ProbeAcceptance()
    {
        var (capturePath, body) = LoadNewestLivePassthroughGpt56Body();
        var sourceModel = body["model"]?.GetValue<string>()
            ?? throw new InvalidDataException($"Captured Codex body has no model: {capturePath}");
        Assert.StartsWith("gpt-5.6-", sourceModel, StringComparison.Ordinal);

        body["model"] = Gpt6Astra;
        using var client = new PlaygroundClient();
        var (status, raw) = await client.TryPostResponsesRawStreamAsync(body.ToJsonString());

        _output.WriteLine($"[{Gpt6Astra}] real {sourceModel} Codex bytes → {(int)status} {status}");
        _output.WriteLine($"  source capture: {capturePath}");
        _output.WriteLine($"  body: {Truncate(raw, 2400)}");
        WriteDistinctEventTypes(raw);
    }

    /// <summary>
    /// Re-confirms the only Astra rewrite-causing finding on bytes captured from
    /// the real routed client: the exact low-effort request succeeds unchanged,
    /// while changing only <c>reasoning.effort</c> to a legacy boundary is rejected.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("ultra")]
    public async Task Gpt6Astra_RealCodexBytes_RejectUnsupportedEffort(string rejectedEffort)
    {
        var (capturePath, captured) = LoadNewestRealCodexBodyForModel(Gpt6Astra);
        Assert.Equal(Gpt6Astra, captured["model"]?.GetValue<string>());

        var reasoning = captured["reasoning"]?.AsObject()
            ?? throw new InvalidDataException($"Captured Codex body has no reasoning object: {capturePath}");
        Assert.Equal("low", reasoning["effort"]?.GetValue<string>());

        using var client = new PlaygroundClient();
        var (acceptedStatus, acceptedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        Assert.True(
            WireAcceptance.IsAccepted(acceptedStatus, acceptedBody, $"{Gpt6Astra} captured low effort"),
            $"Captured low-effort request was rejected: {WireAcceptance.ErrorMessage(acceptedBody)}");

        reasoning["effort"] = rejectedEffort;
        var (rejectedStatus, rejectedBody) = await client.TryPostResponsesAsync(captured.ToJsonString());
        _output.WriteLine(
            $"[{Gpt6Astra}] real Codex bytes low → {(int)acceptedStatus}; "
            + $"{rejectedEffort} → {(int)rejectedStatus}");
        _output.WriteLine($"  source capture: {capturePath}");
        _output.WriteLine($"  rejection: {Truncate(rejectedBody, 600)}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, rejectedStatus);
        Assert.Contains(rejectedEffort, rejectedBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invalid_request_body", rejectedBody, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Path, JsonObject Body) LoadNewestLivePassthroughGpt56Body()
    {
        var runs = Path.Combine(FindRepoRoot(), "tests", "behavior-runs");
        var manifests = Path.Combine(runs, "manifests");
        foreach (var manifestPath in Directory.EnumerateFiles(manifests, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            JsonObject? manifest;
            try { manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { continue; }

            var manifestModel = manifest?["model"]?.GetValue<string>();
            if (!string.Equals(manifest?["client"]?.GetValue<string>(), "codex", StringComparison.Ordinal) ||
                !string.Equals(manifest?["route"]?.GetValue<string>(), "/codex", StringComparison.Ordinal) ||
                !string.Equals(manifest?["scenario"]?.GetValue<string>(), "Passthrough", StringComparison.Ordinal) ||
                manifestModel?.StartsWith("gpt-5.6-", StringComparison.Ordinal) != true)
                continue;

            var traceDir = manifest?["traceDir"]?.GetValue<string>();
            if (traceDir is null || !Directory.Exists(traceDir)) continue;

            foreach (var capturePath in Directory.EnumerateFiles(traceDir, "*-upstream-req.json")
                         .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
            {
                JsonObject? wrapper;
                try { wrapper = JsonNode.Parse(File.ReadAllText(capturePath))?.AsObject(); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { continue; }

                if (!string.Equals(wrapper?["target"]?.GetValue<string>(), "/responses", StringComparison.Ordinal) ||
                    wrapper?["headers"]?["User-Agent"]?.GetValue<string>()?.Contains("codex", StringComparison.OrdinalIgnoreCase) != true ||
                    wrapper?["body"] is not JsonObject body ||
                    !string.Equals(body["model"]?.GetValue<string>(), manifestModel, StringComparison.Ordinal))
                    continue;

                return (capturePath, (JsonObject)body.DeepClone());
            }
        }

        throw new InvalidOperationException(
            $"No live Passthrough codex.exe gpt-5.6 capture was found under {manifests}.");
    }

    /// <summary>
    /// Captures both the legacy synchronous custom-tool stream and Astra's new
    /// async-tool extension. Official Responses documentation says the item and
    /// event families remain <c>custom_tool_call</c>; the live probe confirms
    /// what Copilot actually emits before the bridge models any response delta.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gpt6Astra_CustomToolStreaming_Probe(bool asyncTool)
    {
        var asyncProperty = asyncTool ? ",\"async\":true" : "";
        var payload = $$$"""
          {
            "model": "gpt-6-astra",
            "instructions": "You have an exec tool. You MUST call it with a short JavaScript expression and do not answer in prose.",
            "input": [{"type":"message","role":"user","content":[{"type":"input_text","text":"Use exec now to calculate 40 + 2."}]}],
            "stream": true,
            "store": false,
            "reasoning": {"effort":"low"},
            "tool_choice": "required",
            "tools": [
              {"type":"custom","name":"exec","description":"Run JavaScript."{{{asyncProperty}}},
               "format":{"type":"grammar","syntax":"lark","definition":"start: /[\\s\\S]+/"}}
            ]
          }
          """;

        using var client = new PlaygroundClient();
        var (status, raw) = await client.TryPostResponsesRawStreamAsync(payload);
        _output.WriteLine($"[{Gpt6Astra}] custom-tool async={asyncTool} → {(int)status} {status}");
        _output.WriteLine($"  body: {Truncate(raw, 2400)}");
        WriteDistinctEventTypes(raw);
    }

    private void WriteDistinctEventTypes(string raw)
    {
        var eventTypes = raw.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("event:", StringComparison.Ordinal))
            .Select(line => line["event:".Length..].Trim())
            .GroupBy(type => type, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in eventTypes)
            _output.WriteLine($"  {group.Count(),4}x event: {group.Key}");
    }
}
