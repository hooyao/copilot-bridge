using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for <see cref="ResponseLeakError"/>: the kebab→PascalCase config-key
/// mapping (which must line up with the <see cref="ResponseLeakSignaturesOptions"/>
/// property that disables each signature), and that the retry message is actionable
/// (names the signature, its disable switch, and the restart requirement) while
/// staying safe to embed in hand-built JSON.
/// </summary>
public class ResponseLeakErrorTests
{
    [Theory]
    [InlineData("invoke", "Invoke")]
    [InlineData("task-notification", "TaskNotification")]
    [InlineData("teammate-message", "TeammateMessage")]
    [InlineData("channel", "Channel")]
    [InlineData("cross-session-message", "CrossSessionMessage")]
    [InlineData("tick", "Tick")]
    public void ConfigKey_MapsKebabToPascal(string signature, string expected)
    {
        // Contract: a kebab signature id maps to the PascalCase config property name
        // that disables it (cross-session-message → CrossSessionMessage).
        Assert.Equal(expected, ResponseLeakError.ConfigKey(signature));
    }

    [Fact]
    public void ConfigPath_IsFullSignaturesPath()
    {
        Assert.Equal(
            "Pipeline:Detectors:ResponseLeakGuard:Signatures:CrossSessionMessage",
            ResponseLeakError.ConfigPath("cross-session-message"));
    }

    [Fact]
    public void Message_NamesSignature_DisableKey_AndRestart()
    {
        // Contract: the message is actionable — names the tripped signature, the
        // exact switch to disable it, and that a restart is required.
        var msg = ResponseLeakError.Message("invoke");
        Assert.Contains("invoke", msg);
        Assert.Contains("Pipeline:Detectors:ResponseLeakGuard:Signatures:Invoke=false", msg);
        Assert.Contains("restart", msg);
    }

    [Fact]
    public void Message_IsJsonSafe_NoQuoteOrBackslash()
    {
        // Contract: the message is concatenated into hand-built JSON without
        // escaping, so it must contain no '"' or '\' for any signature.
        foreach (var sig in LeakSignatures.All)
        {
            var msg = ResponseLeakError.Message(sig);
            Assert.DoesNotContain("\"", msg);
            Assert.DoesNotContain("\\", msg);
        }
    }

    [Fact]
    public void Json_EmbedsSignatureMessage_AndParsesAsError()
    {
        var json = ResponseLeakError.Json(ResponseLeakSignal.OverloadedError, "channel");
        Assert.Contains("overloaded_error", json);
        Assert.Contains("Pipeline:Detectors:ResponseLeakGuard:Signatures:Channel=false", json);
        // Well-formed JSON (the embedded message introduced no stray quote/backslash).
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("overloaded_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }
}
