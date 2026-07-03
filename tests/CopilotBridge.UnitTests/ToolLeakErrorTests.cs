using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Pipeline.Response.Detection;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract tests for <see cref="ToolLeakError"/>: the kebab→PascalCase config-key
/// mapping (which must line up with the <see cref="ToolLeakSignaturesOptions"/>
/// property that disables each signature), and that the retry message is actionable
/// (names the signature, its disable switch, and the restart requirement) while
/// staying safe to embed in hand-built JSON.
/// </summary>
public class ToolLeakErrorTests
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
        Assert.Equal(expected, ToolLeakError.ConfigKey(signature));
    }

    [Fact]
    public void ConfigPath_IsFullSignaturesPath()
    {
        Assert.Equal(
            "Pipeline:Detectors:ToolLeakGuard:Signatures:CrossSessionMessage",
            ToolLeakError.ConfigPath("cross-session-message"));
    }

    [Fact]
    public void Message_NamesSignature_DisableKey_AndRestart()
    {
        // Contract: the message is actionable — names the tripped signature, the
        // exact switch to disable it, and that a restart is required.
        var msg = ToolLeakError.Message("invoke");
        Assert.Contains("invoke", msg);
        Assert.Contains("Pipeline:Detectors:ToolLeakGuard:Signatures:Invoke=false", msg);
        Assert.Contains("restart", msg);
    }

    [Fact]
    public void Message_IsJsonSafe_NoQuoteOrBackslash()
    {
        // Contract: the message is concatenated into hand-built JSON without
        // escaping, so it must contain no '"' or '\' for any signature.
        foreach (var sig in LeakSignatures.All)
        {
            var msg = ToolLeakError.Message(sig);
            Assert.DoesNotContain("\"", msg);
            Assert.DoesNotContain("\\", msg);
        }
    }

    [Fact]
    public void Json_EmbedsSignatureMessage_AndParsesAsError()
    {
        var json = ToolLeakError.Json(ToolLeakSignal.OverloadedError, "channel");
        Assert.Contains("overloaded_error", json);
        Assert.Contains("Pipeline:Detectors:ToolLeakGuard:Signatures:Channel=false", json);
        // Well-formed JSON (the embedded message introduced no stray quote/backslash).
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("error", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("overloaded_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
    }
}
