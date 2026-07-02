using System.Collections.Concurrent;
using CopilotBridge.Cli.Hosting.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Guards the trace-id log-correlation mechanism the endpoints rely on. The
/// endpoints push the RAW trace id onto Serilog's <see cref="LogContext"/> as
/// <c>ReqTrace</c>; with <c>Enrich.FromLogContext()</c> that reaches every log
/// event in the async scope, and <see cref="ReqTraceFormatEnricher"/> turns it
/// into the bracketed display property <c>ReqTraceFmt = "[&lt;id&gt;] "</c> that
/// the output templates render. Outside a request there is no <c>ReqTrace</c>, so
/// no <c>ReqTraceFmt</c> — the template token renders empty (no stray
/// <c>[]</c>). Same wiring as <c>SerilogBootstrapper</c>; if it regresses,
/// pipeline logs lose their <c>[&lt;traceId&gt;]</c> correlation.
/// </summary>
public class TraceIdLogCorrelationTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public readonly ConcurrentQueue<LogEvent> Events = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    [Fact]
    public void RawId_IsFormattedInScope_AndAbsentOutside()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new ReqTraceFormatEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("before");
        using (LogContext.PushProperty("ReqTrace", "20260702-000000-0001"))
        {
            logger.Information("inside");
        }
        logger.Information("after");

        LogEvent Find(string msg) => sink.Events.Single(e => e.MessageTemplate.Text == msg);

        // In scope: the enricher produced the bracketed display form from the raw id.
        var inside = Find("inside");
        Assert.True(inside.Properties.ContainsKey("ReqTraceFmt"));
        var fmt = ((ScalarValue)inside.Properties["ReqTraceFmt"]).Value as string;
        Assert.Equal("[20260702-000000-0001] ", fmt);

        // Out of scope: no ReqTrace → no ReqTraceFmt → template token renders empty.
        Assert.False(Find("before").Properties.ContainsKey("ReqTraceFmt"));
        Assert.False(Find("after").Properties.ContainsKey("ReqTraceFmt"));
    }
}
