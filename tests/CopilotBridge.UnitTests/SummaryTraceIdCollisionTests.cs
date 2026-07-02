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
/// Contract: the per-request INFO summary line's <c>req#&lt;id&gt;</c> MUST render
/// the request's own trace id (<c>RequestSummary.TraceId</c>, the
/// <c>BuildTraceId</c> value <c>yyyyMMdd-HHmmss-nnnn</c>) — the SAME id that names
/// the trace JSON files and prefixes the pipeline log lines — so an operator can
/// move between the summary, the pipeline lines, and the trace files by one id.
///
/// It must render that id EVEN under the host's real logging setup, where
/// <c>WebApplication.CreateSlimBuilder()</c> leaves MEL's default
/// <see cref="ActivityTrackingOptions"/> on. That default injects the ambient
/// <c>Activity.TraceId</c> (a 32-char hex W3C trace id) into every in-request log
/// record as a scope property literally named <c>TraceId</c>. If the summary's
/// message-template hole is also named <c>{TraceId}</c>, that scope property
/// shadows the template argument at Serilog render time and <c>req#</c> prints the
/// framework hex instead of the request's id — breaking correlation. This test
/// reproduces that exact condition (real <see cref="SerilogLoggerProvider"/> +
/// framework ActivityTracking + a live <see cref="Activity"/>) and asserts the
/// summary still renders the request id.
/// </summary>
public class SummaryTraceIdCollisionTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public readonly ConcurrentQueue<LogEvent> Events = new();
        public void Emit(LogEvent e) => Events.Enqueue(e);
    }

    [Fact]
    public void SummaryReqId_RendersRequestTraceId_NotAmbientActivityHex()
    {
        const string requestTraceId = "20260702-092136-0250";

        var sink = new CollectingSink();
        // Mirror the production Serilog wiring (SerilogBootstrapper): the file/
        // console templates render {Message:lj}, and FromLogContext + the MEL
        // bridge surface framework scope properties as event properties.
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        // MEL factory with the framework-default activity tracking that injects a
        // "TraceId" scope property from Activity.Current — the real host condition
        // (WebApplication.CreateSlimBuilder leaves ActivityTrackingOptions on).
        using var factory = LoggerFactory.Create(b =>
        {
            b.Configure(o => o.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId
                | ActivityTrackingOptions.SpanId
                | ActivityTrackingOptions.ParentId);
            b.AddProvider(new SerilogLoggerProvider(serilog, dispose: false));
            b.SetMinimumLevel(LogLevel.Trace);
        });

        // A live, sampled Activity so Activity.Current.TraceId is a real 32-hex id.
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var src = new ActivitySource("summary-collision-test");
        using var activity = src.StartActivity("request");
        Assert.NotNull(activity); // guard: without a live Activity there is no collision to test
        var ambientHex = activity!.TraceId.ToString();

        var logger = new RequestSummaryLogger(factory.CreateLogger<RequestSummaryLogger>());
        logger.Log(new RequestSummary { Kind = "responses", TraceId = requestTraceId, StatusCode = 200 });

        // Render exactly as the production sinks do: the file/console output
        // templates emit the message with {Message:lj} (literal, unquoted). A
        // MessageTemplateTextFormatter over "{Message:lj}" reproduces that so the
        // assertion sees the real operator-facing text, not RenderMessage()'s
        // quoted form.
        var formatter = new MessageTemplateTextFormatter("{Message:lj}");
        var sw = new StringWriter();
        formatter.Format(sink.Events.Single(), sw);
        var rendered = sw.ToString();

        // The contract: req# shows the request's trace id, not the ambient hex.
        Assert.Contains($"req#{requestTraceId}", rendered);
        Assert.DoesNotContain(ambientHex, rendered);
    }

    /// <summary>
    /// Contract: the summary line prints its trace id EXACTLY ONCE. Because the
    /// endpoints now open the <c>ReqTrace</c> scope for the whole handler (so the
    /// enter/exit boundary lines are correlated), the summary — logged inside that
    /// scope — would ALSO get the enricher's <c>[&lt;id&gt;] </c> bracket prefix on
    /// top of its own self-rendered <c>req#&lt;id&gt;</c>, printing the id twice
    /// (<c>[T] req#T …</c>). The summary self-renders its id via <c>req#</c> and
    /// MUST NOT also carry the bracket prefix. This renders the FULL production
    /// output template (<c>{ReqTraceFmt}{Message:lj}</c>) under an active
    /// <c>ReqTrace</c> scope and asserts the id occurs once.
    /// </summary>
    [Fact]
    public void SummaryLine_UnderRequestScope_RendersTraceIdExactlyOnce()
    {
        const string requestTraceId = "20260702-092136-0250";

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

        var logger = new RequestSummaryLogger(factory.CreateLogger<RequestSummaryLogger>());
        // The endpoint pushes this scope around the WHOLE handler; the summary is
        // logged inside it (that's what correlates enter/exit). Reproduce that.
        using (LogContext.PushProperty("ReqTrace", requestTraceId))
        {
            logger.Log(new RequestSummary { Kind = "responses", TraceId = requestTraceId, StatusCode = 200 });
        }

        // Render exactly as the production sinks do: {ReqTraceFmt}{Message:lj}.
        var formatter = new MessageTemplateTextFormatter("{ReqTraceFmt}{Message:lj}");
        var sw = new StringWriter();
        formatter.Format(sink.Events.Single(), sw);
        var rendered = sw.ToString();

        // The id appears exactly once — via req#, with no redundant [id] prefix.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(rendered, System.Text.RegularExpressions.Regex.Escape(requestTraceId)).Count;
        Assert.Equal(1, occurrences);
        Assert.Contains($"req#{requestTraceId}", rendered);
        Assert.DoesNotContain($"[{requestTraceId}]", rendered);
    }
}
