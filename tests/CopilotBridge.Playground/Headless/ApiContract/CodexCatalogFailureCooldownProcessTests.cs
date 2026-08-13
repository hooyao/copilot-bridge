using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Real-process contract for the live Copilot overlay's negative cache. This
/// boots the actual CLI/config/DI graph; direct HTTP calls are intentional because
/// the contract is upstream-attempt suppression, not a client tool-dispatch verdict.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexCatalogFailureCooldownProcessTests
{
    private readonly ITestOutputHelper _output;

    public CodexCatalogFailureCooldownProcessTests(ITestOutputHelper output) =>
        _output = output;

    [Fact]
    public async Task RepeatedCatalogPollsMakeOneFailedOverlayAttemptPerCooldown()
    {
        using var cache = ClientBehaviorSupport.NewWorkDir("catalog-overlay-cooldown-cache");
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            ForceModelsFailure: true,
            CodexCatalogCacheDirectory: cache.Path,
            LiveOverlayFailureCooldownSeconds: 2,
            ForceCodexCatalogSourceAbsent: true));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var endpoint = $"{bridge.BaseUrl}/codex/models?client_version=0.147.0";

        for (var poll = 0; poll < 5; poll++)
            await AssertSafeCatalogAsync(http, endpoint);

        Assert.Equal(1, CountOccurrences(
            bridge.StderrAll, "TEST ONLY: forced Copilot /models failure attempt"));
        Assert.Equal(1, CountOccurrences(
            bridge.StderrAll, "Copilot model metadata refresh failed"));

        await Task.Delay(TimeSpan.FromMilliseconds(2_200));
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => AssertSafeCatalogAsync(http, endpoint)));

        Assert.Equal(2, CountOccurrences(
            bridge.StderrAll, "TEST ONLY: forced Copilot /models failure attempt"));
        Assert.Equal(2, CountOccurrences(
            bridge.StderrAll, "Copilot model metadata refresh failed"));
        _output.WriteLine(
            $"bridge={bridge.BaseUrl} cooldown=2s polls=13 attempts=2 warnings=2");
    }

    private static async Task AssertSafeCatalogAsync(HttpClient http, string endpoint)
    {
        using var response = await http.GetAsync(endpoint);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);
        var models = json.RootElement.GetProperty("models");
        Assert.Equal(JsonValueKind.Array, models.ValueKind);
        Assert.NotEmpty(models.EnumerateArray());
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }
        return count;
    }
}
