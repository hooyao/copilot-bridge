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

    /// <summary>Minimal <see cref="ICopilotClient"/> returning a fixed
    /// count_tokens response; the other methods are unused here.</summary>
    private sealed class CountTokensStubClient : CopilotBridge.Cli.Copilot.ICopilotClient
    {
        public ValueTask<System.Net.Http.HttpResponseMessage> PostCountTokensAsync(
            ReadOnlyMemory<byte> body, System.Threading.CancellationToken ct = default)
        {
            var resp = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(
                    System.Text.Encoding.UTF8.GetBytes("""{"input_tokens":42}""")),
            };
            resp.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            return new(resp);
        }

        public ValueTask<System.Net.Http.HttpResponseMessage> PostMessagesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            IReadOnlyList<string>? anthropicBeta = null,
            IReadOnlyDictionary<string, string?>? copilotHeaderOverrides = null,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<System.Net.Http.HttpResponseMessage> PostResponsesAsync(
            ReadOnlyMemory<byte> body, bool vision = false,
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<CopilotBridge.Cli.Models.Copilot.CopilotModelsResponse> GetModelsAsync(
            System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>Minimal <see cref="CopilotBridge.Cli.Auth.IAuthService"/> stub — the
    /// count_tokens endpoint reads only <c>CopilotApiBaseUrl</c> for its audit URL.</summary>
    private sealed class StubAuth : CopilotBridge.Cli.Auth.IAuthService
    {
        public bool IsAuthenticated => true;
        public string TokenLocation => "(test)";
        public string? CopilotApiBaseUrl => "https://api.test.githubcopilot.com";
        public DateTimeOffset? CopilotTokenExpiry => DateTimeOffset.MaxValue;
        public ValueTask<string> EnsureGitHubTokenAsync(System.Threading.CancellationToken ct = default) =>
            ValueTask.FromResult("gh-token");
        public ValueTask<string> GetCopilotTokenAsync(System.Threading.CancellationToken ct = default) =>
            ValueTask.FromResult("test-token");
        public void SignOut() { }
    }

    /// <summary>
    /// Regression (PR #20 review): <c>count_tokens</c> pushes NO pipeline and has
    /// no enter/exit lines, but it still emits a summary — and that summary, like
    /// every in-request line, must carry the trace id via the enricher prefix.
    /// The endpoint must therefore push the <c>ReqTrace</c> scope for its handler;
    /// without it the summary renders with NO id at all. Drives the REAL
    /// <see cref="ClaudeCodeCountTokensEndpoint.HandleAsync"/> through a real
    /// Serilog logger and asserts the summary line is prefixed with the id.
    /// Mutation guard: drop the endpoint's ReqTrace push → no prefix → RED.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task CountTokensSummary_CarriesTheTraceId_ViaHandlerScope()
    {
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

        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Path = "/cc/v1/messages/count_tokens";
        http.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            """{"model":"claude-opus-4-8","messages":[{"role":"user","content":"hi"}]}"""));
        http.Response.Body = new MemoryStream();

        await ClaudeCodeCountTokensEndpoint.HandleAsync(
            http,
            new CountTokensStubClient(),
            new StubAuth(),
            new RequestSummaryLogger(factory.CreateLogger<RequestSummaryLogger>()),
            TestAudit.Create(false));

        // Find the summary event (its template leads with the literal "summary")
        // and render it as production does; it must carry the [<id>] prefix.
        var summaryEvt = sink.Events.Single(e => e.MessageTemplate.Text.StartsWith("summary ", StringComparison.Ordinal));
        var rendered = RenderProduction(summaryEvt);
        Assert.Matches(@"^\[\d{8}-\d{6}-\d{4}\] summary count_tokens ", rendered);
    }
}
