namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>appsettings.json</c> section <c>Pipeline:Detectors:RunawayGuard</c>.
/// Controls <see cref="Pipeline.Response.Detection.RunawayGuardDetector"/>, a volume
/// circuit-breaker on the streamed response: it aborts a degenerate-generation
/// runaway (a model stuck emitting an unbounded stream of tiny fragments) before it
/// hangs the client for minutes.
/// </summary>
/// <remarks>
/// <para>
/// Motivation: a gpt-5.5 turn once emitted a single <c>Write</c> tool call with
/// 27,643 <c>input_json_delta</c> events (avg ~3.6 chars each, ~17 MB on the wire)
/// whose content collapsed into repeated whitespace/garbage — it never terminated,
/// and the bridge relayed it for 6.5 minutes until the user cancelled
/// (<c>docs/gpt55-runaway-diagnosis.md</c>). This guard trips on the <b>volume</b>
/// signature (byte / delta-count budget), not on content, so it is robust and has
/// near-zero false positives — legitimate large outputs pass under generous limits.
/// </para>
/// <para>
/// On a trip it reuses the same abort machinery and error shape as
/// <see cref="ResponseLeakGuardOptions"/> (<c>overloaded_error</c> by default) so
/// Claude Code treats it as retryable. Read at startup (config is registered
/// <c>reloadOnChange:false</c>) — a restart is required after changing a value.
/// </para>
/// </remarks>
internal sealed class RunawayGuardOptions
{
    /// <summary>Master switch. When false the detector is never created — no
    /// counting, no allocation. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cumulative streamed delta-payload bytes budget across the whole response.
    /// When the sum of <c>content_block_delta</c> data lengths exceeds this, the
    /// guard aborts. Default 12 MiB — well above any legitimate single-response
    /// output (the runaway hit ~17 MB), so normal large answers pass.
    /// </summary>
    public long MaxDeltaBytes { get; set; } = 12L * 1024 * 1024;

    /// <summary>
    /// Per-content-block ceiling on the number of <c>content_block_delta</c> events.
    /// The degenerate signature is one tool call emitting tens of thousands of tiny
    /// fragments (the runaway hit 27,643 in a single block). Reset at each
    /// <c>content_block_start</c>. Default 20,000. A value ≤ 0 disables the
    /// delta-count check (the byte budget still applies).
    /// </summary>
    public int MaxDeltaCount { get; set; } = 20_000;

    /// <summary>
    /// Sliding-window size (in whitespace-delimited tokens) for the repetition-density
    /// signal, per content block. The guard trips when the trailing
    /// <see cref="RepetitionWindow"/> tokens are FULL and their unique-token ratio
    /// (distinct/window) is below <see cref="RepetitionMinUniqueRatio"/> — catching a
    /// degenerate single-token loop (observed: <c>claude-opus-4.8</c> repeating one
    /// token ~32,000× to <c>max_tokens</c>, ~1,010 deltas / ~500 KB) that stays under
    /// both volume budgets. Default 500. A value ≤ 0 disables the repetition signal
    /// (the byte and delta-count budgets still apply).
    /// </summary>
    public int RepetitionWindow { get; set; } = 500;

    /// <summary>
    /// Unique-token-ratio floor for the repetition signal: trip when
    /// <c>distinct/RepetitionWindow &lt; RepetitionMinUniqueRatio</c>. Default 0.05 —
    /// ~25× above the observed runaway ratio (~0.002) and ~17× below a normal diverse
    /// response (~0.88), a wide margin on both sides so legitimate large output never
    /// trips. Ignored when <see cref="RepetitionWindow"/> ≤ 0.
    /// </summary>
    public double RepetitionMinUniqueRatio { get; set; } = 0.05;

    /// <summary>Which error to raise on a trip. Default
    /// <see cref="ResponseDetectionSignal.OverloadedError"/> (retryable), shared with the
    /// response-leak guard so the wire shape is consistent.</summary>
    public ResponseDetectionSignal Signal { get; set; } = ResponseDetectionSignal.OverloadedError;
}
