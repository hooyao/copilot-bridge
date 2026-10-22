using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>Captured desktop-style incremental inventory at the real HTTP edge.</summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public sealed class CodexCollaborationReplayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CapturedCollaborationInventory_ProducesAPlaintextSpawnCallAtNativeEdge()
    {
        var source = Environment.GetEnvironmentVariable("COPILOT_BRIDGE_TEST_CUSTOM_CREDENTIAL_SOURCE_DIRECTORY")
            ?? throw new InvalidOperationException("Set COPILOT_BRIDGE_TEST_CUSTOM_CREDENTIAL_SOURCE_DIRECTORY to a valid v4 success-test source.");
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "plaintext-collaboration.json")))!;
        var body = fixture["body"]!;
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Passthrough,
            CredentialSourceDirectory: source,
            CredentialStagingMode: CredentialStagingMode.CustomOAuthVersionFour,
            UseCustomOAuthApp: true,
            PlaintextCollaboration: true));
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var response = await http.PostAsync(bridge.BaseUrl + "/codex/responses",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("response.completed", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("response.failed", raw, StringComparison.Ordinal);
        var calls = raw.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
            .Select(line => JsonNode.Parse(line[6..])!)
            .Where(evt => evt["type"]?.GetValue<string>() == "response.output_item.done")
            .Select(evt => evt["item"]!)
            .Where(item => item["type"]?.GetValue<string>() == "function_call")
            .ToArray();
        var call = Assert.Single(calls);
        Assert.Equal("collaboration", call["namespace"]!.GetValue<string>());
        Assert.Equal("spawn_agent", call["name"]!.GetValue<string>());
        Assert.Empty(call["encrypted_function_args"]!.AsArray());
        Assert.Equal("Reply CAPTURED_PLAINTEXT_TASK_OK.",
            JsonNode.Parse(call["arguments"]!.GetValue<string>())!["message"]!.GetValue<string>());
        output.WriteLine($"[replay] Real additional_tools inventory -> plaintext spawn, complete task preserved; trace={bridge.TraceDir}");
    }
}
