using CopilotBridge.Cli.Hosting.Options;
using CopilotBridge.Cli.Models.Anthropic.Request;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotBridge.Cli.Pipeline.Stages;

/// <summary>
/// Request-side scan for a poisoned transcript. Detects a single tool stuck in a
/// repeated-failure loop (<see cref="PoisonedContextScanner"/>) on the inbound IR
/// body, records the total failure-debris count on
/// <see cref="BridgeContext{TBody}.PoisonedToolResults"/>, and — when one tool's
/// failure count reaches <see cref="PoisonedContextOptions.WarnThreshold"/> — logs
/// one WARNING (naming that tool) telling the user to compact the session.
/// </summary>
/// <remarks>
/// <para>
/// Runs for BOTH backends (not vendor-gated): a context poisoned with earlier
/// failed-call debris degrades any weaker backend model, and this stage neither
/// mutates the body nor blocks the request — it only observes. The bad output a
/// poisoned context can induce is caught downstream by the response-side runaway
/// guard; this stage's job is to name the cause so the user knows the only fix is
/// <c>/compact</c> (the bridge cannot un-poison the client's transcript, and stripping
/// the blocks would 400 upstream).
/// </para>
/// <para>
/// The threshold is on the <b>worst single tool's</b> failure count, not the grand
/// total, so the signal is "one tool keeps failing and being replayed" (in the
/// runaway, <c>Agent</c> alone failed 50×) rather than "a few unrelated tools each
/// failed once" — an occasional one-off failure never nags.
/// </para>
/// <para>
/// Scoped DI service, placed in the shared <c>RequestStages</c> list so one
/// registration covers <c>/cc</c> and <c>/codex</c>. Reads <c>ctx.Request.Body</c>
/// only in <see cref="ApplyAsync"/> (never the constructor — the DI shell is
/// unpopulated at construction; see <see cref="BridgeContext{TBody}"/> remarks).
/// </para>
/// </remarks>
internal sealed class PoisonedContextScanStage : IRequestStage<MessagesRequest>
{
    private readonly PoisonedContextOptions _opts;
    private readonly BridgeContext<MessagesRequest> _ctx;
    private readonly ILogger<PoisonedContextScanStage> _log;

    public PoisonedContextScanStage(
        IOptionsSnapshot<PoisonedContextOptions> opts,
        BridgeContext<MessagesRequest> ctx,
        ILogger<PoisonedContextScanStage> log)
    {
        _opts = opts.Value;
        _ctx = ctx;
        _log = log;
    }

    public string Name => "PoisonedContextScan";

    public Task ApplyAsync()
    {
        if (!_opts.Enabled)
        {
            return Task.CompletedTask;
        }

        var result = PoisonedContextScanner.Scan(_ctx.Request.Body);
        // Record the TOTAL failure debris for telemetry (the summary line), while the
        // warning gate below keys off the WORST single tool — the replay-loop signal.
        _ctx.PoisonedToolResults = result.TotalFailures;

        // WarnThreshold <= 0 means "warn on any single failure"; otherwise warn only
        // once one tool has failed enough times to look like a replay loop (an
        // occasional one-off failure is normal and shouldn't nag). The count is still
        // recorded above regardless of whether we warn.
        var threshold = _opts.WarnThreshold > 0 ? _opts.WarnThreshold : 1;
        if (result.WorstToolFailures >= threshold)
        {
            _log.LogWarning(
                "context poisoned: tool '{Tool}' has {ToolFailures} failed tool_result(s) replayed in this "
                + "request ({TotalFailures} failed total; threshold {Threshold}). This is failure debris from "
                + "earlier calls the client keeps resending; a weaker backend model can be derailed by it and "
                + "the bridge cannot clean it — compact the session (/compact in Claude Code) to clear it.",
                result.WorstTool,
                result.WorstToolFailures,
                result.TotalFailures,
                threshold);
        }

        return Task.CompletedTask;
    }
}
