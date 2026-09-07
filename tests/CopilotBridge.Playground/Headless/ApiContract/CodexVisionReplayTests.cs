using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Replays the sanitized real Codex view_image custom-call/output pair through
/// the endpoint. The image content must remain intact and activate the vision
/// header. Live-client understanding is judged separately by the behavior skill.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CodexVisionReplayTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CapturedViewImageOutput_RoutedToAstra_PreservesImageAndSetsVisionHeader()
    {
        var credentialSource = Environment.GetEnvironmentVariable(
            "COPILOT_BRIDGE_TEST_PLUGIN_CREDENTIAL_SOURCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(credentialSource))
            throw new InvalidOperationException(
                "Set COPILOT_BRIDGE_TEST_PLUGIN_CREDENTIAL_SOURCE_DIRECTORY to an authorized "
                + "scratch version-3 credential source.");

        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Codex", "native-vision-tool-output.json")))!;
        var body = fixture["body"]!;
        var capturedOutput = body["input"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "custom_tool_call_output");
        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(
            ServeScenario.Gpt56ToAstra,
            CredentialSourceDirectory: credentialSource,
            CredentialStagingMode: CredentialStagingMode.CopilotPluginVersionThree));
        var reader = new BridgeLogReader(bridge.TraceDir);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{bridge.BaseUrl}/codex/responses");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("response.completed", responseBody, StringComparison.Ordinal);

        BridgeLogEntry? entry = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (entry?.UpstreamBody is null && DateTime.UtcNow < deadline)
        {
            entry = reader.ReadNew().SingleOrDefault(value => value.UpstreamBody is not null);
            if (entry is null) await Task.Delay(100);
        }
        Assert.NotNull(entry?.UpstreamBody);
        var upstream = entry!.UpstreamBody!;
        Assert.Equal("gpt-6-astra", upstream["model"]!.GetValue<string>());
        Assert.Equal("low", upstream["reasoning"]!["effort"]!.GetValue<string>());
        var actualOutput = upstream["input"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "custom_tool_call_output");
        Assert.True(JsonNode.DeepEquals(capturedOutput, actualOutput),
            "The real image and adjacent native tool-output content must remain value-identical.");
        Assert.Equal("true", entry.UpstreamReq?["headers"]?["copilot-vision-request"]?.GetValue<string>());
        output.WriteLine($"[replay] Astra/low, native image output preserved, vision header true; trace={bridge.TraceDir}");
    }
}
