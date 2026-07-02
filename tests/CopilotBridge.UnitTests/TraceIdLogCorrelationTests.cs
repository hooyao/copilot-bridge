using System.Collections.Concurrent;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Guards the trace-id log-correlation mechanism the endpoint relies on: a
/// property pushed onto Serilog's <see cref="LogContext"/> (as
/// <c>ClaudeCodeMessagesEndpoint</c> does with <c>ReqTrace</c>) must be enriched
/// onto every log event in that async scope when the logger is configured with
/// <c>Enrich.FromLogContext()</c>, and must be absent outside the scope. This is
/// the same wiring as <c>SerilogBootstrapper</c>; if it regresses, pipeline logs
/// lose their <c>req#&lt;traceId&gt;</c> correlation.
/// </summary>
public class TraceIdLogCorrelationTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public readonly ConcurrentQueue<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    [Fact]
    public void PushedProperty_IsEnrichedOntoLogsInScope_AndAbsentOutside()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("before");
        using (LogContext.PushProperty("ReqTrace", "req#20260702-000000-0001 "))
        {
            logger.Information("inside");
        }
        logger.Information("after");

        LogEvent Find(string msg) => sink.Events.Single(e => e.MessageTemplate.Text == msg);

        var inside = Find("inside");
        Assert.True(inside.Properties.ContainsKey("ReqTrace"));
        Assert.Contains("20260702-000000-0001", inside.Properties["ReqTrace"].ToString());

        Assert.False(Find("before").Properties.ContainsKey("ReqTrace"));
        Assert.False(Find("after").Properties.ContainsKey("ReqTrace"));
    }
}
