using System.Text.Json.Nodes;
using CopilotBridge.Playground.Contract;
using Xunit;

namespace CopilotBridge.Playground;

public partial class ResponsesProbe
{
    internal static readonly string[] NovemberCandidates =
    [
        "gpt-6.2-astra", "gpt-6.2-sol", "gpt-5.6-luna-utility",
        "mai-code-2", "grok-4.6", "grok-4.7",
    ];

    private static string NovemberEvidenceDirectory()
    {
        var directory = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(ContractSnapshot.PathFor(ResponsesSnapshotFile))!,
            "..", "tests", "behavior-runs", "model-sync-20261118"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>Observe exact backend contracts before registering any candidate.</summary>
    [Fact]
    public async Task November2026_CandidateContracts_Probe()
    {
        await Parallel.ForEachAsync(NovemberCandidates,
            new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (model, ct) =>
            {
                using var client = new PlaygroundClient();
                var (status, body) = await ProbeRetry.WithRetry(
                    () => client.TryPostResponsesAsync(LivenessBody(model), ct: ct), model + " liveness");
                _output.WriteLine($"[{model}] liveness -> {(int)status}: {Truncate(body, 2000)}");
                await File.WriteAllTextAsync(Path.Combine(NovemberEvidenceDirectory(), model + "-liveness.json"),
                    new JsonObject { ["status"] = (int)status, ["body"] = body }.ToJsonString(), ct);
                var facts = WireAcceptance.IsAccepted(status, body, model + " liveness")
                    ? await ProbeResponsesModelFactsAsync(client, model)
                    : new JsonObject { ["unavailable_status"] = (int)status, ["error_body"] = body };
                await File.WriteAllTextAsync(Path.Combine(NovemberEvidenceDirectory(), model + "-facts.json"),
                    facts.ToJsonString(new() { WriteIndented = true }), ct);
                _output.WriteLine($"[{model}] facts: {facts.ToJsonString()}");
            });
    }

    /// <summary>Absence from discovery alone never proves retirement.</summary>
    [Theory]
    [InlineData("gpt-5.4")]
    [InlineData("gpt-5.4-mini")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.6-terra")]
    [InlineData("gpt-5-mini")]
    public async Task November2026_UnadvertisedProfile_LivenessProbe(string model)
    {
        using var client = new PlaygroundClient();
        var (status, body) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(LivenessBody(model)), model + " unadvertised liveness");
        _output.WriteLine($"[{model}] liveness -> {(int)status}: {body}");
        await File.WriteAllTextAsync(Path.Combine(NovemberEvidenceDirectory(), model + "-liveness.json"),
            new JsonObject { ["status"] = (int)status, ["body"] = body }.ToJsonString());
        var (vscodeStatus, vscodeBody) = await ProbeRetry.WithRetry(
            () => client.TryPostResponsesAsync(LivenessBody(model), integrationIdOverride: "vscode-chat"),
            model + " vscode-chat liveness");
        _output.WriteLine($"[{model}] vscode-chat liveness -> {(int)vscodeStatus}: {vscodeBody}");
        await File.WriteAllTextAsync(Path.Combine(NovemberEvidenceDirectory(), model + "-vscode-liveness.json"),
            new JsonObject { ["status"] = (int)vscodeStatus, ["body"] = vscodeBody }.ToJsonString());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains("model_not_available_for_integrator", body, StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, vscodeStatus);
        Assert.Contains("model_not_supported", vscodeBody, StringComparison.Ordinal);
    }

    private static string LivenessBody(string model) => new JsonObject
    {
        ["model"] = model,
        ["instructions"] = "Reply with exactly: ok",
        ["input"] = "reply: ok",
        ["stream"] = false,
        ["store"] = false,
    }.ToJsonString();

    public static IEnumerable<object[]> NovemberReviewedModelRows() =>
        new[] { "gpt-6.2-astra", "gpt-6.2-sol", "gpt-5.6-luna-utility", "mai-code-2" }
            .Select(model => new object[] { model });

    [Theory]
    [MemberData(nameof(NovemberReviewedModelRows))]
    public Task November2026_RealCodexBytes_RewriteBoundaries(string model) =>
        AssertRealCodexRewriteBoundariesAsync(model);

    [Fact]
    public async Task November2026_ReviewedContracts_MatchSnapshotAndCatalog()
    {
        var snapshot = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)!["models"]!.AsObject();
        var live = new JsonObject();
        foreach (var row in NovemberReviewedModelRows())
        {
            var model = (string)row[0];
            using var client = new PlaygroundClient();
            var facts = await ProbeResponsesModelFactsAsync(client, model);
            live[model] = facts;
            var diff = ContractSnapshot.Diff(snapshot[model]!.AsObject(), facts);
            Assert.True(diff.Count == 0, model + ": " + string.Join("\n", diff));
        }
        AssertCatalogMatchesLive(live, requireCompleteCatalog: false);
    }

    [Fact]
    public void November2026_CommittedBackendFacts_MatchCatalog()
    {
        var snapshot = ContractSnapshot.ReadOrNull(ResponsesSnapshotFile)!["models"]!.AsObject();
        var reviewed = new JsonObject();
        foreach (var row in NovemberReviewedModelRows())
            reviewed[(string)row[0]] = snapshot[(string)row[0]]!.DeepClone();
        AssertCatalogMatchesLive(reviewed, requireCompleteCatalog: false);
    }

    [Theory]
    [InlineData("grok-4.6")]
    [InlineData("grok-4.7")]
    public async Task November2026_PassthroughCandidateBackendFacts_MatchSnapshot(string model)
    {
        using var client = new PlaygroundClient();
        var live = await ProbeResponsesModelFactsAsync(client, model);
        var snapshot = ContractSnapshot.ReadOrNull("copilot-passthrough-candidates-snapshot.json")!;
        var diff = ContractSnapshot.Diff(snapshot["models"]![model]!.AsObject(), live);
        Assert.True(diff.Count == 0, model + ": " + string.Join("\n", diff));
    }
}
