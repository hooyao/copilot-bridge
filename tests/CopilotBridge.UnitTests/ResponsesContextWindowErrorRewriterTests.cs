using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>Narrow classifier contract for the one Copilot Responses context 400
/// Claude Code must see as Anthropic prompt-too-long.</summary>
public sealed class ResponsesContextWindowErrorRewriterTests
{
    private const string Confirmed =
        "{\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\",\"code\":\"invalid_request_body\"}}";

    [Fact]
    public void ExactSmallCcResponses400_RewritesDespiteTextPlainContentType()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "text/plain; charset=utf-8",
            ["Content-Length"] = Encoding.UTF8.GetByteCount(Confirmed).ToString(),
        };

        var rewritten = ResponsesContextWindowErrorRewriter.TryRewrite(
            "/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            Encoding.UTF8.GetBytes(Confirmed), headers, out var body);

        Assert.True(rewritten);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error",
            doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains("prompt is too long",
            doc.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("application/json", headers["Content-Type"]);
        Assert.Equal(body.Length.ToString(), headers["Content-Length"]);
    }

    public static IEnumerable<object[]> NearMisses()
    {
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 429, Confirmed];
        yield return ["/codex/responses", BackendVendor.CopilotResponses, 400, Confirmed];
        yield return ["/cc/v1/messages", BackendVendor.CopilotAnthropic, 400, Confirmed];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            Confirmed.Replace("invalid_request_body", "invalid_tool_schema")];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            "{\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\"}}"];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            Confirmed.Replace("Your input exceeds", "Another input exceeds")];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            "{\"error\":{\"code\":\"invalid_request_body\"}}"];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            "{\"error\":{\"message\":null,\"code\":\"invalid_request_body\"}}"];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400,
            "{\"note\":" + JsonSerializer.Serialize(
                "Your input exceeds the context window of this model. Please adjust your input and try again.")
                + ",\"error\":{\"message\":\"bad tool\",\"code\":\"invalid_request_body\"}}"];
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400, "not-json"];
    }

    [Theory]
    [MemberData(nameof(NearMisses))]
    internal void NearMiss_IsNotRewritten(
        string path, BackendVendor vendor, int status, string json)
    {
        var original = Encoding.UTF8.GetBytes(json);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Assert.False(ResponsesContextWindowErrorRewriter.TryRewrite(
            path, vendor, status, original, headers, out var body));
        Assert.Same(original, body);
    }

    [Fact]
    public void BodyAboveClassifierLimit_IsNotParsedBySubstring()
    {
        var padding = new string('x', ResponsesContextWindowErrorRewriter.MaxInspectionBytes);
        var body = Encoding.UTF8.GetBytes(
            "{\"padding\":" + JsonSerializer.Serialize(padding)
            + ",\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\",\"code\":\"invalid_request_body\"}}");

        Assert.False(ResponsesContextWindowErrorRewriter.TryRewrite(
            "/cc/v1/messages", BackendVendor.CopilotResponses, 400, body,
            new Dictionary<string, string>(), out _));
    }
}
