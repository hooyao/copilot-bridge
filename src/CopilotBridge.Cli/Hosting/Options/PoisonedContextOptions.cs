namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:PoisonedContext</c>.
/// Controls <see cref="Pipeline.Stages.PoisonedContextScanStage"/>, a request-side
/// scan that detects a single tool stuck in a repeated-failure loop in the inbound
/// transcript and — when one tool's failure count crosses
/// <see cref="WarnThreshold"/> — logs one WARNING advising the user to compact.
/// </summary>
/// <remarks>
/// <para>
/// Motivation: the gpt-5.5 runaway (<c>docs/gpt55-runaway-diagnosis.md</c>) was
/// triggered by a context poisoned with 50 failed <c>Agent</c> sub-agent calls —
/// debris from a sub-agent model Copilot rejected, which Claude Code kept in the
/// transcript and resent every turn. The bridge can neither strip them (dropping a
/// <c>tool_result</c> without its paired <c>tool_use</c> 400s upstream) nor recover
/// the client's history: the only cure is the user compacting the session. So this
/// stage does not block or mutate the request — it makes the poisoning visible (a
/// per-request count, and a loud WARNING once one tool is clearly looping) so the
/// user knows to <c>/compact</c>. The bad output itself is caught downstream by the
/// response-side runaway guard (<c>Pipeline:Detectors:RunawayGuard</c>).
/// </para>
/// <para>
/// The detector is <b>lexical-independent</b>: it keys on the structural signal
/// (one tool name accumulating many failed <c>tool_result</c>s), not on any specific
/// error phrase, so it survives whatever wording Copilot returns next. Because Claude
/// Code replays the whole transcript each turn, once a session is poisoned this
/// WARNING repeats every turn until the user compacts — the intended nudge. Read at
/// startup (config is registered <c>reloadOnChange:false</c>) — a restart is required
/// after changing a value.
/// </para>
/// </remarks>
internal sealed class PoisonedContextOptions
{
    /// <summary>Master switch. When false the stage never scans — no walk, no
    /// allocation, <c>ctx.PoisonedToolResults</c> stays 0. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of failed <c>tool_result</c> blocks attributable to a <b>single tool</b>
    /// in one request at or above which the stage logs the "compact your session"
    /// WARNING. Keyed on the worst single tool (not the grand total) so the signal is
    /// "one tool keeps failing and being replayed", not "a few unrelated tools each
    /// failed once". The total failure count is still recorded on the summary line
    /// (<c>poisoned_tool_results=</c>) regardless of the warning. The runaway incident
    /// had one tool fail 50×; normal sessions sit well under the default. Default 5. A
    /// value ≤ 0 warns as soon as any single tool has one failure.
    /// </summary>
    public int WarnThreshold { get; set; } = 5;
}
