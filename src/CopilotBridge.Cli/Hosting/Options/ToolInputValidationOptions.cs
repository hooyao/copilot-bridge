namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// What the tool-input validation detector does when it flags a real
/// <c>tool_use</c> block, per failure class. Both classes (malformed JSON, schema
/// violation) are things Claude Code recovers from natively, so the default is
/// <see cref="Observe"/> — record the flag but relay the response untouched. The two
/// abort variants fold the wire shape INTO the action so a single value fully
/// determines behaviour (there is no separate signal knob that could diverge from
/// the action).
/// </summary>
internal enum ToolInputAction
{
    /// <summary>
    /// Record the diagnosis (<c>tool_input_invalid=true</c> on the summary, a
    /// warning log) but do NOT abort — let the bytes reach Claude Code, which turns
    /// an invalid tool call into a retryable <c>is_error</c> tool_result and
    /// re-prompts the model. The default, because CC self-heals both classes
    /// (malformed JSON: <c>safeParseJSON → null → {} → strictObject.safeParse</c>;
    /// schema violation: <c>strictObject.safeParse</c>), so aborting only cuts off a
    /// recovery CC already performs.
    /// </summary>
    Observe = 0,

    /// <summary>
    /// Abort with an Anthropic <c>overloaded_error</c> (HTTP 529 in buffered
    /// delivery) — retryable. Opt-in: use only where CC does NOT self-heal.
    /// </summary>
    AbortOverloaded = 1,

    /// <summary>
    /// Abort with an Anthropic <c>api_error</c> (HTTP 500 in buffered delivery).
    /// Opt-in alternative to <see cref="AbortOverloaded"/>.
    /// </summary>
    AbortApiError = 2,
}

/// <summary>
/// Bound from <c>Pipeline:Detectors:ToolInputValidation</c>. Controls the response
/// detector that validates streamed <c>tool_use.input</c> JSON against the request's
/// declared tool schemas. Because Claude Code natively recovers from an invalid tool
/// call (it parses the input with <c>safeParseJSON</c> — malformed JSON falls back to
/// <c>{}</c> — then <c>zod strictObject.safeParse</c>, and on failure feeds the model
/// an <c>is_error</c> tool_result so it retries with corrected input), the detector
/// <b>observes by default and does not abort</b>. Aborting was found to cut off that
/// self-heal (e.g. a real <c>AskUserQuestion</c> emitted without the required
/// <c>question</c> field, which CC would have recovered from).
/// </summary>
/// <remarks>
/// Read at startup (config is registered <c>reloadOnChange:false</c>) — a restart is
/// required after changing a value.
/// </remarks>
internal sealed class ToolInputValidationOptions
{
    /// <summary>Master switch. When false the detector is never created — no
    /// scanning, no allocation. Default true so the diagnosis (<c>tool_input_invalid=</c>)
    /// is still emitted even though the default action is observe-only.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Action when the accumulated tool input is not valid JSON (a <c>JSON.parse</c>
    /// failure). Default <see cref="ToolInputAction.Observe"/> — Claude Code's
    /// <c>safeParseJSON</c> returns null and falls back to an empty object, then its
    /// schema check re-prompts the model, so CC recovers without the bridge aborting.
    /// </summary>
    public ToolInputAction MalformedJsonAction { get; set; } = ToolInputAction.Observe;

    /// <summary>
    /// Action when the tool input is valid JSON but violates the declared tool
    /// schema (missing required field, wrong type). Default
    /// <see cref="ToolInputAction.Observe"/> — Claude Code's <c>strictObject.safeParse</c>
    /// rejects it and feeds the model an <c>is_error</c> tool_result to retry, so the
    /// bridge must not abort or it cuts off that recovery.
    /// </summary>
    public ToolInputAction SchemaViolationAction { get; set; } = ToolInputAction.Observe;

    /// <summary>
    /// Only relevant when a class is set to an <c>Abort*</c> action. true (default):
    /// keep streaming and emit the SSE <c>error</c> when the bad tool block closes —
    /// the bad block's <c>content_block_stop</c> is never delivered, so it never
    /// enters context, though its deltas have already rendered. false: buffer the
    /// full response first so a bad response is returned as a real HTTP status/body
    /// before any bytes reach the client.
    /// </summary>
    public bool PreserveStream { get; set; } = true;
}
