using Serilog.Core;
using Serilog.Events;

namespace CopilotBridge.Cli.Hosting.Logging;

/// <summary>
/// Formats the raw per-request trace id (the <c>ReqTrace</c> property the
/// pipeline-driving endpoints push onto the log context — just the id, e.g.
/// <c>20260702-032206-0001</c>) into the display form the output templates render:
/// <c>ReqTraceFmt = "[&lt;id&gt;] "</c>. When there is no <c>ReqTrace</c> (a
/// non-request line such as the startup banner) it adds nothing, so the template's
/// <c>{ReqTraceFmt}</c> token renders empty — no stray <c>[]</c>.
/// </summary>
/// <remarks>
/// This keeps the DATA (the bare trace id) separate from its PRESENTATION (the
/// brackets + trailing space): endpoints only ever push the id; changing the
/// wrapper (<c>[&lt;id&gt;] </c> → <c>&lt;id&gt; </c> → …) is a one-line edit
/// here, and non-request lines never carry an empty shell. Every in-request
/// line — pipeline stages, the enter/exit boundary lines, and the summary — is
/// rendered through this one prefix; no line self-renders its own id. AOT-clean:
/// no reflection.
/// </remarks>
internal sealed class ReqTraceFormatEnricher : ILogEventEnricher
{
    private const string SourceProperty = "ReqTrace";
    private const string TargetProperty = "ReqTraceFmt";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (!logEvent.Properties.TryGetValue(SourceProperty, out var value)
            || value is not ScalarValue { Value: string id }
            || id.Length == 0)
        {
            return; // no id → no ReqTraceFmt → {ReqTraceFmt} renders empty
        }

        // Presentation lives here, in one place. Trailing space keeps the id set
        // off from the message. EVERY in-request line — pipeline stages, the
        // enter/exit boundary lines, and the summary — gets the same prefix; no
        // line self-renders its own id, so there is nothing to double.
        var formatted = propertyFactory.CreateProperty(TargetProperty, $"[{id}] ");
        logEvent.AddPropertyIfAbsent(formatted);
    }
}
