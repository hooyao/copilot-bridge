using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using CopilotBridge.Cli.Endpoints.ClaudeCode;
using CopilotBridge.Cli.Hosting.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;
using Xunit;

namespace CopilotBridge.UnitTests;

/// <summary>
/// Contract: the per-request INFO summary line carries the request's trace id
/// (the <c>BuildTraceId</c> value <c>yyyyMMdd-HHmmss-nnnn</c>) EXACTLY ONCE,
/// rendered the SAME way as every other in-request line — via
/// <see cref="ReqTraceFormatEnricher"/>'s <c>[&lt;id&gt;] </c> prefix from the
/// <c>ReqTrace</c> log-context property the endpoint pushes for the whole handler.
/// The summary message itself renders NO id (no <c>req#</c> self-label).
///
/// This is the design that structurally rules out two failure modes an earlier
/// self-rendered <c>req#{TraceId}</c> template suffered from:
/// <list type="bullet">
///   <item>the framework's default activity tracking injects an ambient
///   <c>Activity.TraceId</c> log scope; a <c>{TraceId}</c> template hole would be
///   shadowed by it and print a 32-hex framework id — with no id hole in the
///   message there is nothing to shadow; and</item>
///   <item>once the request scope covers the summary, a self-rendered id would be
///   doubled by the enricher prefix (<c>[T] req#T</c>) — with no self-rendered id
///   there is nothing to double.</item>
/// </list>
/// </summary>
public class SummaryTraceIdCollisionTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public readonly ConcurrentQueue<LogEvent> Events = new();
        public void Emit(LogEvent e) => Events.Enqueue(e);
    }

    /// <summary>Render a captured event exactly as the production sinks do
    /// (SerilogBootstrapper's <c>{ReqTraceFmt}{Message:lj}</c> templates).</summary>
    private static string RenderProduction(LogEvent e)
    {
        var formatter = new MessageTemplateTextFormatter("{ReqTraceFmt}{Message:lj}");
        var sw = new StringWriter();
        formatter.Format(e, sw);
        return sw.ToString();
    }

    [Fact]
    public void SummaryLine_UnderRequestScope_CarriesTraceIdOnce_ViaEnricherPrefix()
    {
        const string requestTraceId = "20260702-092136-0250";

        var sink = new CollectingSink();
        // Mirror the production Serilog wiring: FromLogContext surfaces the
        // endpoint's ReqTrace push, the enricher turns it into the "[<id>] "
        // display prefix the output templates render.
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new ReqTraceFormatEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();

        // MEL factory with the framework-default activity tracking that injects a
        // "TraceId" scope property from Activity.Current — the real host condition
        // (WebApplication.CreateSlimBuilder leaves ActivityTrackingOptions on).
        // Present here to prove it can no longer leak into the summary line.
        using var factory = LoggerFactory.Create(b =>
        {
            b.Configure(o => o.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId
                | ActivityTrackingOptions.SpanId
                | ActivityTrackingOptions.ParentId);
            b.AddProvider(new SerilogLoggerProvider(serilog, dispose: false));
            b.SetMinimumLevel(LogLevel.Trace);
        });

        // Listen ONLY to this test's own ActivitySource — a process-wide
        // ShouldListenTo would make unrelated ActivitySource.StartActivity()
        // calls in parallel tests start producing Activities, coupling them.
        const string sourceName = "summary-collision-test";
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var src = new ActivitySource(sourceName);
        using var activity = src.StartActivity("request");
        Assert.NotNull(activity);
        var ambientHex = activity!.TraceId.ToString();

        var logger = new RequestSummaryLogger(factory.CreateLogger<RequestSummaryLogger>());
        // The endpoint pushes ReqTrace around the WHOLE handler; the summary is
        // logged inside it (that's what correlates it with the enter/exit lines).
        using (LogContext.PushProperty("ReqTrace", requestTraceId))
        {
            logger.Log(new RequestSummary { Kind = "responses", StatusCode = 200 });
        }

        var rendered = RenderProduction(sink.Events.Single());

        // The id appears exactly once, via the enricher's "[<id>] " prefix.
        var occurrences = System.Text.RegularExpressions.Regex
            .Matches(rendered, System.Text.RegularExpressions.Regex.Escape(requestTraceId)).Count;
        Assert.Equal(1, occurrences);
        Assert.StartsWith($"[{requestTraceId}] ", rendered);
        // No leftover self-rendered "req#" label, and never the framework hex.
        Assert.DoesNotContain("req#", rendered);
        Assert.DoesNotContain(ambientHex, rendered);
    }

    [Fact]
    public void SummaryLine_OutsideAnyRequestScope_RendersNoTraceId()
    {
        // Defensive: if a summary is ever logged with no ReqTrace in context, it
        // simply renders without a prefix — no empty "[] " shell, no crash.
        var sink = new CollectingSink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.With(new ReqTraceFormatEnricher())
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var factory = LoggerFactory.Create(b =>
        {
            b.AddProvider(new SerilogLoggerProvider(serilog, dispose: false));
            b.SetMinimumLevel(LogLevel.Trace);
        });

        new RequestSummaryLogger(factory.CreateLogger<RequestSummaryLogger>())
            .Log(new RequestSummary { Kind = "messages", StatusCode = 200 });

        var rendered = RenderProduction(sink.Events.Single());
        // Renders straight into the message with no "[<id>] " prefix (the betas
        // fields contain "[" mid-line, so assert on the START not on "[" anywhere).
        Assert.StartsWith("summary messages ", rendered);
    }
}
