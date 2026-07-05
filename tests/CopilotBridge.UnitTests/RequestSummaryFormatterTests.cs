using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Pins the rendering of the per-request INFO summary: all structured
/// placeholders are emitted, the effort field renders correctly in both
/// "same" and "remapped" forms, and the cache token counts appear in the
/// usage display.
/// </summary>
public class RequestSummaryFormatterTests
{
    private static (RequestSummaryLogger logger, RecordingLoggerProvider rec) BuildLogger()
    {
        var rec = new RecordingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(rec).SetMinimumLevel(LogLevel.Trace));
        return (new RequestSummaryLogger(factory.CreateLogger<RequestSummaryLogger>()), rec);
    }

    [Fact]
    public void Log_EmitsSingleInformationEvent_WithAllPlaceholders()
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary
        {
            Kind = "messages",
            RequestedModel = "claude-opus-4.8",
            ResolvedModel = "claude-opus-4.7-1m-internal",
            CanonicalProfileId = "claude-opus-4.7-1m-internal",
            TargetVendor = "CopilotAnthropic",
            TargetEndpoint = "/v1/messages",
            InboundBetas = new[] { "context-1m-2025-08-07", "interleaved-thinking-2025-05-14" },
            OutboundBetas = new[] { "interleaved-thinking-2025-05-14", "context-1m-2025-08-07" },
            InboundEffort = "max",
            OutboundEffort = "xhigh",
            MaxTokens = 8192,
            Usage = new UsageSnapshot { InputTokens = 1024, OutputTokens = 512, CacheReadInputTokens = 100, CacheCreationInputTokens = 50 },
            StatusCode = 200,
            Streaming = true,
            DurationMs = 1234,
            RunawayDetected = true,
            PoisonedToolResults = 7,
        });

        var evt = Assert.Single(rec.Events);
        Assert.Equal(LogLevel.Information, evt.Level);

        Assert.Equal("messages", evt.Properties["Kind"]);
        Assert.Equal("claude-opus-4.8", evt.Properties["RequestedModel"]);
        Assert.Equal("claude-opus-4.7-1m-internal", evt.Properties["ResolvedModel"]);
        Assert.Equal("claude-opus-4.7-1m-internal", evt.Properties["CanonicalProfileId"]);
        Assert.Equal("CopilotAnthropic", evt.Properties["TargetVendor"]);
        Assert.Equal("/v1/messages", evt.Properties["TargetEndpoint"]);
        Assert.Equal("context-1m-2025-08-07,interleaved-thinking-2025-05-14", evt.Properties["InboundBetasCsv"]);
        Assert.Equal("interleaved-thinking-2025-05-14,context-1m-2025-08-07", evt.Properties["OutboundBetasCsv"]);
        Assert.Equal("max→xhigh", evt.Properties["EffortDisplay"]);
        Assert.Equal("8192", evt.Properties["MaxTokensDisplay"]);
        Assert.Equal(200, evt.Properties["StatusCode"]);
        Assert.Equal(true, evt.Properties["Streaming"]);
        Assert.Equal(1234L, evt.Properties["DurationMs"]);
        // The two gpt-5.5-diagnosis fields must ride the same line as structured
        // properties so an operator can grep runaway= / poisoned_tool_results=.
        Assert.Equal(true, evt.Properties["RunawayDetected"]);
        Assert.Equal(7, evt.Properties["PoisonedToolResults"]);

        // The usage display string must include both the raw IO counters
        // and the cache fields — that's what the operator looks at to see
        // how the request is using prompt caching.
        var usageDisplay = Assert.IsType<string>(evt.Properties["UsageDisplay"]);
        Assert.Contains("in:1024", usageDisplay);
        Assert.Contains("out:512", usageDisplay);
        Assert.Contains("cache_read:100", usageDisplay);
        Assert.Contains("cache_creation:50", usageDisplay);
    }

    [Fact]
    public void EffortDisplay_SameValueIn_AndOut_RendersSingle()
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary
        {
            Kind = "messages",
            InboundEffort = "high",
            OutboundEffort = "high",
        });
        Assert.Equal("high", rec.Events.Single().Properties["EffortDisplay"]);
    }

    [Fact]
    public void EffortDisplay_BothNull_RendersNonePlaceholder()
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary { Kind = "messages" });
        Assert.Equal("(none)", rec.Events.Single().Properties["EffortDisplay"]);
    }

    [Fact]
    public void CountTokensKind_RendersAtSameLevel()
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary
        {
            Kind = "count_tokens",
            RequestedModel = "claude-opus-4.8",
            ResolvedModel = "claude-opus-4.8",
            Usage = new UsageSnapshot { InputTokens = 42 },
            StatusCode = 200,
        });

        var evt = rec.Events.Single();
        Assert.Equal("count_tokens", evt.Properties["Kind"]);
        Assert.Contains("in:42", Assert.IsType<string>(evt.Properties["UsageDisplay"]));
    }

    // --- Level-per-status: success at Info, 4xx at Warning, 5xx at Error.
    // Without this mapping a 400 from Copilot hid behind LogInformation
    // alongside 200s and was easy to miss in tail -f output.

    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(301)]
    [InlineData(399)]
    public void SuccessOrRedirectStatus_LogsAtInformation(int status)
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary { Kind = "messages", StatusCode = status });
        Assert.Equal(LogLevel.Information, rec.Events.Single().Level);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(499)]
    public void ClientErrorStatus_LogsAtWarning(int status)
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary { Kind = "messages", StatusCode = status });
        Assert.Equal(LogLevel.Warning, rec.Events.Single().Level);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(0)]   // strategy never reached upstream (network / auth)
    public void ServerErrorOrUnknownStatus_LogsAtError(int status)
    {
        var (rsl, rec) = BuildLogger();
        rsl.Log(new RequestSummary { Kind = "messages", StatusCode = status });
        Assert.Equal(LogLevel.Error, rec.Events.Single().Level);
    }
}
