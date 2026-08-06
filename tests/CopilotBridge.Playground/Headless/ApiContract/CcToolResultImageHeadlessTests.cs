using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Captured-shape contract for the Claude Code image-returning tool path. The source
/// fixture is a de-identified, reduced form of the real 2026-08-06 Claude Code
/// <c>Read(icon.png)</c> request: assistant <c>tool_use</c>, then user
/// <c>tool_result.content</c> with Anthropic text/image blocks. This test posts that
/// client shape through a real bridge endpoint and inspects the exact traced Responses
/// body/header, so preserving base64 as JSON text cannot masquerade as image fidelity.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class CcToolResultImageHeadlessTests
{
    private readonly ITestOutputHelper _output;

    public CcToolResultImageHeadlessTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CapturedClaudeImageToolResult_BecomesStructuredResponsesOutput()
    {
        const string callId = "toolu_captured_image_20260806";
        var sourceData = Convert.ToBase64String(PngGen.SolidRgbPng(100, 100, 255, 0, 0));
        var payload = (await File.ReadAllTextAsync(FindFixture()))
            .Replace("__PNG_BASE64__", sourceData, StringComparison.Ordinal);
        var expectedDataUrl = "data:image/png;base64," + sourceData;

        await using var bridge = await ServeProcess.StartAsync(new ServeInvocation(ServeScenario.CcToGpt));
        var reader = new BridgeLogReader(bridge.TraceDir);
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{bridge.BaseUrl}/cc/v1/messages");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
        // The audit sink writes its four trace files asynchronously, so the record can
        // still be incomplete the instant the HTTP response returns — poll until the
        // upstream status has landed rather than sampling once.
        BridgeLogEntry? entry = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            entry = reader.ReadNew().LastOrDefault(e =>
                e.InboundPath.EndsWith("/v1/messages", StringComparison.Ordinal)
                && e.UpstreamBody?["model"]?.GetValue<string>() == "gpt-5.6-sol");
            if (entry?.UpstreamStatus is >= 200 and < 600) break;
            await Task.Delay(250, cts.Token);
        }
        Assert.True(entry is not null, "no upstream request reached gpt-5.6-sol");

        _output.WriteLine($"bridge={bridge.BaseUrl} trace={bridge.TraceDir}");
        _output.WriteLine($"status={(int)response.StatusCode}/{entry.UpstreamStatus}");
        if (!response.IsSuccessStatusCode)
            _output.WriteLine(responseBody.Length <= 1000 ? responseBody : responseBody[..1000]);

        var outputItem = entry.UpstreamBody!["input"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "function_call_output");
        var output = Assert.IsType<JsonArray>(outputItem!["output"]);

        Assert.Equal(callId, outputItem["call_id"]!.GetValue<string>());
        Assert.Equal(["input_text", "input_image"],
            output.Select(item => item!["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("The image returned by the Read tool follows.",
            output[0]!["text"]!.GetValue<string>());
        Assert.Equal(expectedDataUrl, output[1]!["image_url"]!.GetValue<string>());
        Assert.Equal("true", entry.UpstreamReq!["headers"]!["copilot-vision-request"]!.GetValue<string>());
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.InRange(entry.UpstreamStatus, 200, 299);
    }

    private static string FindFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName,
                "tests",
                "CopilotBridge.Playground",
                "Fixtures",
                "cc-tool-result-image-captured-shape.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "cc-tool-result-image-captured-shape.json not found from " + AppContext.BaseDirectory);
    }
}
