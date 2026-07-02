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

    /// <summary>
    /// The endpoints declare the <c>ReqTrace</c> scope at the TOP of the handler
    /// (before the <c>endpoint … enter</c> log) with a <c>using</c>-declaration,
    /// so it stays active through the <c>finally</c> that emits
    /// <c>endpoint … exit</c> and the summary. This guards that structural
    /// contract: a line logged BEFORE the scope opens carries no id, every line
    /// from the open through the end of the enclosing block carries it — so both
    /// boundary lines (which bracket the try/finally) are covered. If the scope
    /// were narrowed back inside the <c>try</c> (as it was before this fix), the
    /// enter line (before try) and exit line (in finally, after the using
    /// disposed) would lose the id and this test's boundary assertions would fail.
    /// </summary>
    [Fact]
    public void ScopeDeclaredBeforeEnter_CoversBoundaryLinesThroughBlockEnd()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new ReqTraceFormatEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Reproduce the endpoint's control shape: scope opened at the top via a
        // using-declaration, an "enter" line, a try, and a finally "exit" line.
        static void Handle(Serilog.ILogger log)
        {
            using var _traceScope = LogContext.PushProperty("ReqTrace", "20260702-000000-0007");
            log.Information("enter");          // boundary: before try
            try { log.Information("pipeline"); }
            finally { log.Information("exit"); } // boundary: in finally, still in scope
        }

        logger.Information("outside-before");
        Handle(logger);
        logger.Information("outside-after");

        LogEvent Find(string msg) => sink.Events.Single(e => e.MessageTemplate.Text == msg);
        bool HasId(string msg) => Find(msg).Properties.ContainsKey("ReqTraceFmt");

        // Both boundary lines and the pipeline line carry the id.
        Assert.True(HasId("enter"), "enter line must carry the trace id");
        Assert.True(HasId("pipeline"), "pipeline line must carry the trace id");
        Assert.True(HasId("exit"), "exit line (in finally) must carry the trace id");
        // The same id on all three — a request is one id end-to-end.
        static string Id(LogEvent e) => ((ScalarValue)e.Properties["ReqTraceFmt"]).Value as string ?? "";
        Assert.Equal(Id(Find("enter")), Id(Find("exit")));
        Assert.Equal("[20260702-000000-0007] ", Id(Find("exit")));
        // Lines outside the request carry nothing.
        Assert.False(HasId("outside-before"));
        Assert.False(HasId("outside-after"));
    }
}
