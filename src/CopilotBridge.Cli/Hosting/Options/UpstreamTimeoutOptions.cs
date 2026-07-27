namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:UpstreamTimeout</c>.
/// Two independent <b>inactivity</b> (idle) budgets on <b>both</b> upstream forward
/// paths — <c>/cc</c> (Anthropic passthrough) and Codex (Responses) — NOT a
/// total-duration cap. As long as Copilot keeps making progress (headers arrive, or
/// SSE events keep coming) the relevant timer is reset, so a legitimately
/// slow-but-progressing request is never aborted.
/// </summary>
/// <remarks>
/// <para>These budgets are the <b>sole</b> upstream bound: the shared
/// <c>HttpClient</c> is registered with <c>Timeout.InfiniteTimeSpan</c>. A coarse
/// whole-request timeout was actively harmful — under <c>ResponseHeadersRead</c> it
/// bounds only the header wait on the streaming path, but on the <b>buffered</b>
/// path it bounds the entire request including the body, which is exactly the
/// non-streaming request Claude Code issues to recover a failed streaming turn.
/// Consequence: disabling BOTH budgets leaves upstream calls genuinely unbounded.
/// See <c>docs/timeout-chain.md</c>, <c>docs/pipeline-design.md</c>, and the
/// <c>add-upstream-idle-timeout</c> change for the incident that motivated them.</para>
/// <para>The budgets themselves are path-agnostic; only the <b>mid-stream
/// surfacing</b> differs by client protocol (see <see cref="StreamIdleAction"/> /
/// <see cref="StreamIdleSignal"/>, which apply to the <c>/cc</c> path — the Codex
/// path flushes a <c>response.failed</c> terminal instead of injecting an Anthropic
/// error the Responses client could not parse).</para>
/// <para>Each budget is independently disable-able: a value <c>&lt;= 0</c> means
/// "no bound on that phase" and arms no timer (zero overhead — the byte-identical
/// passthrough / translation hot path is unchanged). Read once at startup; RESTART
/// copilot-bridge after changing a value.</para>
/// </remarks>
internal sealed class UpstreamTimeoutOptions
{
    /// <summary>
    /// Inactivity budget (seconds) for Copilot to return response headers — the
    /// first byte — measured per send attempt (retry backoff does not count
    /// against it). A near-full-context prompt legitimately takes minutes to first
    /// byte (cache creation), so this is generous by design. On timeout the bridge
    /// aborts the send and, because no bytes have reached the client, returns a
    /// real <c>504 Gateway Timeout</c>. Also governs the buffered (non-streaming)
    /// path end-to-end, and drives Claude Code's <c>API_TIMEOUT_MS</c> (this value
    /// plus a margin). Default 240 (4 min), above a realistic cache-creation first
    /// byte. <c>&lt;= 0</c> disables, leaving the phase unbounded — there is no
    /// coarse client timeout behind it.
    /// </summary>
    public int FirstByteTimeoutSeconds { get; set; } = 240;

    /// <summary>
    /// Inactivity budget (seconds) for the gap between consecutive SSE events once
    /// the stream has started; reset on every event pulled from upstream, so a
    /// stream that keeps emitting is never aborted regardless of total length. On a
    /// gap beyond this the bridge aborts the read. Because headers are already
    /// sent, the wire status stays <c>200</c>; by default the bridge injects the
    /// same retryable <c>overloaded_error</c> the response guards use (so Claude
    /// Code re-attempts the turn), unless <see cref="StreamIdleAction"/> selects
    /// truncation. Drives Claude Code's <c>CLAUDE_STREAM_IDLE_TIMEOUT_MS</c> (this
    /// value plus a margin) so the client outlasts the bridge.
    /// <para>Default 240 (4 min). Copilot emits <b>no keepalive</b> while a model is
    /// thinking, so a deep-thinking turn is legitimately silent for minutes with
    /// nothing on the wire — a measured <c>claude-opus-5</c> turn at
    /// <c>effort=xhigh</c> opened a thinking block and then sent nothing for
    /// <b>600 s</b>. The former 60 s default aborted such a turn while it was
    /// perfectly healthy. 240 s covers ordinary long thinking; raise it further
    /// (e.g. 600) for consistently deep workloads, then re-run
    /// <c>config claude-code</c> so the client follows. See
    /// <c>docs/timeout-chain.md</c>.</para>
    /// A timeout observation is right-censored at cancellation and cannot establish
    /// whether upstream would later have resumed; tune this operator knob separately
    /// from failure recovery. <c>&lt;= 0</c> disables.
    /// </summary>
    public int StreamIdleTimeoutSeconds { get; set; } = 240;

    /// <summary>
    /// What the bridge does to the client stream when the stream-idle budget fires
    /// mid-response. <see cref="UpstreamTimeoutAction.Retry"/> (default) injects a
    /// retryable error event so Claude Code discards the partial stream and enters
    /// its non-streaming recovery path;
    /// <see cref="UpstreamTimeoutAction.Truncate"/> ends the stream with no error
    /// event (a silent cut-short 200) for operators who explicitly do not want a
    /// retry. Only relevant to the mid-stream phase — a first-byte timeout is
    /// always a real <c>504</c>.
    /// </summary>
    public UpstreamTimeoutAction StreamIdleAction { get; set; } = UpstreamTimeoutAction.Retry;

    /// <summary>
    /// Retryable-error wire shape used when <see cref="StreamIdleAction"/> is
    /// <see cref="UpstreamTimeoutAction.Retry"/>. Mirrors the response guards'
    /// <c>Signal</c> knob: <c>OverloadedError</c> (default) →
    /// <c>overloaded_error</c> (Claude Code retries and, after 3 consecutive,
    /// falls back opus→Sonnet); <c>ApiError</c> → <c>api_error</c>/500.
    /// </summary>
    public ResponseDetectionSignal StreamIdleSignal { get; set; } = ResponseDetectionSignal.OverloadedError;
}

/// <summary>
/// What to do to the client stream when a mid-stream idle timeout fires.
/// </summary>
internal enum UpstreamTimeoutAction
{
    /// <summary>Inject a retryable error event so Claude Code enters recovery.</summary>
    Retry = 0,

    /// <summary>End the stream with no error event (silent cut-short 200).</summary>
    Truncate = 1,
}
