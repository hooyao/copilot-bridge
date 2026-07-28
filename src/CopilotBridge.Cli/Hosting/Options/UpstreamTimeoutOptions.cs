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
/// <c>HttpClient</c> is registered with <c>Timeout.InfiniteTimeSpan</c>. The coarse
/// whole-request timeout it replaced was unconfigurable and redundant — both
/// forward paths use <c>ResponseHeadersRead</c>, under which it bounded only the
/// wait for headers, on buffered and streaming responses alike. The first-byte
/// budget below bounds that same phase and can be tuned. Consequence: disabling
/// BOTH budgets leaves upstream calls genuinely unbounded.
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
/// <para>The section also carries <see cref="KeepAliveIntervalSeconds"/>, which is
/// NOT a budget: it does not bound anything, it keeps the <b>downstream</b> client
/// alive while the bridge's own budgets do the judging.</para>
/// </remarks>
internal sealed class UpstreamTimeoutOptions
{
    /// <summary>
    /// Inactivity budget (seconds) for Copilot to return response headers — the
    /// first byte — measured per send attempt (retry backoff does not count
    /// against it). A near-full-context prompt legitimately takes minutes to first
    /// byte (cache creation), so this is generous by design. On timeout the bridge
    /// aborts the send and, because no bytes have reached the client, returns a
    /// real <c>504 Gateway Timeout</c>. Default 240 (4 min), above a realistic
    /// cache-creation first byte. <c>&lt;= 0</c> disables the bound.
    /// </summary>
    /// <remarks>
    /// <b>Bounds the header wait only</b>, in both streaming and buffered modes: the
    /// timer is disarmed the moment response headers arrive, and the body is then
    /// read with the caller's own token. A buffered (non-streaming) response that
    /// stalls AFTER headers is therefore not bounded by this budget — nor by
    /// anything else, since the coarse client timeout was removed. Bounding that
    /// would need a separate body-inactivity budget, which this change does not add.
    /// </remarks>
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
    /// Interval (seconds) between synthetic keepalive (<c>ping</c>) events the
    /// bridge injects into a <b>/cc</b> (Anthropic client) stream while the
    /// <b>upstream is silent</b>. Copilot sends no keepalive of its own while a
    /// model is thinking, so without this an Anthropic client's own idle watchdogs
    /// end a perfectly healthy deep-thinking turn. Injecting the keepalive the
    /// upstream omits makes THIS bridge the sole authority on when a stalled turn
    /// ends. Default 15 (s). <c>&lt;= 0</c> disables injection entirely.
    /// </summary>
    /// <remarks>
    /// <para><b>Silence-triggered, not periodic.</b> While upstream events arrive at
    /// gaps shorter than this interval the bridge injects nothing and arms no
    /// keepalive timer — the healthy path keeps its zero-overhead, byte-identical
    /// relay. A ping appearing in a trace is therefore itself the record that
    /// upstream went silent.</para>
    /// <para><b>Does NOT extend <see cref="StreamIdleTimeoutSeconds"/>.</b> An
    /// injected ping is an event the bridge SENT downstream, never one it RECEIVED
    /// upstream, so it never resets the idle budget. That is load-bearing: pings
    /// stop the client from judging silence, so if they also fed the bridge's budget
    /// nothing would be judging it and a hung upstream would stream pings forever.</para>
    /// <para><b>Must be shorter than the client's tightest watchdog to be useful.</b>
    /// For Claude Code that is the <b>180 s</b> byte-level watchdog, which the
    /// bridge's own <c>_CLAUDE_CODE_ASSUME_FIRST_PARTY_BASE_URL</c> key (written to
    /// unlock the native 1M context) selects in place of the 300 s default. The
    /// default 15 s leaves margin for a few buffered or dropped pings. A value
    /// <c>&gt;=</c> <see cref="StreamIdleTimeoutSeconds"/> means the idle budget
    /// always fires first and no ping is ever due — coherent, but pointless.</para>
    /// <para>Injection does not replace the client-side timeout keys
    /// <c>config claude-code</c> writes; those remain a second line of defence for
    /// the cases a runtime keepalive cannot cover (injection disabled, a buffering
    /// intermediary, a bridge that itself stalls). See
    /// <c>docs/timeout-chain.md</c>.</para>
    /// <para><b>/cc only.</b> The Codex (Responses) path is unaffected — a
    /// Responses-protocol keepalive is a separate, unprobed question.</para>
    /// </remarks>
    public int KeepAliveIntervalSeconds { get; set; } = 15;


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
