using System.Text;
using System.Text.Json;
using CopilotBridge.Cli.Pipeline;
using CopilotBridge.Cli.Pipeline.Strategies.Codex;
using Xunit;

namespace CopilotBridge.UnitTests;

public sealed class NativeCodexContextWindowErrorAdapterTests
{
    private const string Confirmed =
        "{\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\",\"code\":\"invalid_request_body\"}}";

    [Fact]
    public void ExactStreamingCodexRejectionCreatesBoundedNativeFailure()
    {
        var adapted = NativeCodexContextWindowErrorAdapter.TryCreate(
            "/codex/responses",
            BackendVendor.CopilotResponses,
            400,
            streaming: true,
            Encoding.UTF8.GetBytes(Confirmed),
            "gpt-model-\"quoted",
            out var failed);

        Assert.True(adapted);
        Assert.Equal("response.failed", failed.EventType);
        using var doc = JsonDocument.Parse(failed.Data);
        var response = doc.RootElement.GetProperty("response");
        Assert.Equal("failed", response.GetProperty("status").GetString());
        Assert.Equal("gpt-model-\"quoted", response.GetProperty("model").GetString());
        Assert.Equal("context_length_exceeded",
            response.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("usage").ValueKind);
    }

    public static IEnumerable<object[]> NearMisses()
    {
        yield return ["/cc/v1/messages", BackendVendor.CopilotResponses, 400, true, Confirmed];
        yield return ["/codex/responses", BackendVendor.CopilotAnthropic, 400, true, Confirmed];
        yield return ["/codex/responses", BackendVendor.CopilotResponses, 429, true, Confirmed];
        yield return ["/codex/responses", BackendVendor.CopilotResponses, 400, false, Confirmed];
        yield return ["/codex/responses", BackendVendor.CopilotResponses, 400, true,
            Confirmed.Replace("invalid_request_body", "invalid_tool_schema")];
        yield return ["/codex/responses", BackendVendor.CopilotResponses, 400, true,
            Confirmed.Replace("Your input exceeds", "Another input exceeds")];
        yield return ["/codex/responses", BackendVendor.CopilotResponses, 400, true, "not-json"];
    }

    [Theory]
    [MemberData(nameof(NearMisses))]
    internal void NearMissDoesNotCreateFailure(
        string path,
        BackendVendor vendor,
        int status,
        bool streaming,
        string body)
    {
        Assert.False(NativeCodexContextWindowErrorAdapter.TryCreate(
            path,
            vendor,
            status,
            streaming,
            Encoding.UTF8.GetBytes(body),
            "gpt-5.6-sol",
            out _));
    }

    [Fact]
    public void BodyAboveClassifierLimitDoesNotCreateFailure()
    {
        var padding = new string('x', ResponsesContextWindowErrorRewriter.MaxInspectionBytes);
        var body = Encoding.UTF8.GetBytes(
            "{\"padding\":" + JsonSerializer.Serialize(padding)
            + ",\"error\":{\"message\":\"Your input exceeds the context window of this model. Please adjust your input and try again.\",\"code\":\"invalid_request_body\"}}");

        Assert.False(NativeCodexContextWindowErrorAdapter.TryCreate(
            "/codex/responses",
            BackendVendor.CopilotResponses,
            400,
            streaming: true,
            body,
            "gpt-5.6-sol",
            out _));
    }
}
