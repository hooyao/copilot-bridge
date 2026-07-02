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
/// wrapper (<c>[]</c> → <c>&lt;&gt;</c> → <c>req#</c>) is a one-line edit here, and
/// non-request lines never carry an empty shell. The per-request summary line is
/// the one exception: it self-renders the id as <c>req#&lt;id&gt;</c> in its own
/// message and is logged inside the request scope, so this enricher skips the
/// bracket prefix for it (message leads with <c>req#</c>) to avoid printing the id
/// twice. AOT-clean: no reflection.
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

        // The per-request summary line self-renders the id via "req#{ReqTrace} …"
        // in its own message. It is logged INSIDE the request's ReqTrace scope
        // (so the enter/exit boundary lines are correlated), so without this guard
        // it would ALSO get the "[<id>] " prefix and print the id twice
        // ("[T] req#T …"). A line whose message already leads with "req#" owns its
        // id; adding the bracket is redundant, so skip it. Every other in-request
        // line (which does NOT self-render the id) still gets the prefix.
        if (logEvent.MessageTemplate.Text.StartsWith("req#", StringComparison.Ordinal))
        {
            return;
        }

        // Presentation lives here, in one place. Trailing space keeps the id set
        // off from the message.
        var formatted = propertyFactory.CreateProperty(TargetProperty, $"[{id}] ");
        logEvent.AddPropertyIfAbsent(formatted);
    }
}
