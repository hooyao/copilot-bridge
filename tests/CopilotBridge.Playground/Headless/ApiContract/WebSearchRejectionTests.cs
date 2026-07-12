using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace CopilotBridge.Playground.Headless;

/// <summary>
/// Verifies the bridge short-circuits requests carrying Anthropic's
/// <c>web_search_*</c> server tools with a friendly 400, rather than letting
/// Copilot's opaque <c>unsupported_value</c> error bubble up to Claude Code.
/// The error body points the user at MCP as the search workaround.
/// </summary>
[SupportedOSPlatform("windows")]
[Trait("Category", "Integration")]
[Trait("Kind", "ApiContract")]
public class WebSearchRejectionTests : IClassFixture<BridgeFixture>
{
    private readonly BridgeFixture _bridge;
    private readonly ITestOutputHelper _output;

    public WebSearchRejectionTests(BridgeFixture bridge, ITestOutputHelper output)
    {
        _bridge = bridge;
        _output = output;
    }

    [Theory]
    [InlineData("web_search_20250305")]
    [InlineData("web_search_20260209")]
    public async Task BridgeReturnsFriendly400_ForWebSearchServerTool(string toolType)
    {
        var body = $$"""
          {
            "model": "claude-sonnet-4-6",
            "max_tokens": 16,
            "messages": [{ "role": "user", "content": "find me something" }],
            "tools": [
              { "type": "{{toolType}}", "name": "web_search", "max_uses": 1 }
            ]
          }
          """;

        using var http = new HttpClient();
        var resp = await http.PostAsync(
            $"{_bridge.BaseUrl}/cc/v1/messages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        var respBody = await resp.Content.ReadAsStringAsync();
        _output.WriteLine($"HTTP {(int)resp.StatusCode}");
        _output.WriteLine(respBody);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var json = JsonNode.Parse(respBody)!.AsObject();
        Assert.Equal("error", json["type"]?.GetValue<string>());
        var error = json["error"]!.AsObject();
        Assert.Equal("not_supported", error["type"]?.GetValue<string>());
        var msg = error["message"]?.GetValue<string>() ?? "";
        Assert.Contains(toolType, msg);
        Assert.Contains("MCP", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BridgePassesThrough_WhenNoServerToolPresent()
    {
        // Sanity: a normal request (no web_search) still works after the guard.
        var body = """
          {
            "model": "claude-haiku-4-5",
            "max_tokens": 8,
            "messages": [{ "role": "user", "content": "reply: ok" }]
          }
          """;

        using var http = new HttpClient();
        var resp = await http.PostAsync(
            $"{_bridge.BaseUrl}/cc/v1/messages",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
}
