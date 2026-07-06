namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>Pipeline:Detectors:ToolInputValidation</c>. Controls the response
/// detector that validates streamed <c>tool_use.input</c> JSON against the request's
/// declared tool schemas and aborts on malformed tool arguments so the bad block
/// never enters the client's conversation context.
/// </summary>
/// <remarks>
/// <para>
/// The abort is a stop-loss, not a guaranteed retry: it keeps the malformed
/// <c>tool_use</c> block out of Claude Code's context (a block is only committed at
/// <c>content_block_stop</c>, which the abort replaces). Whether the client retries
/// automatically depends on it — see
/// <see cref="Pipeline.Response.Detection.ToolInputValidationDetector"/> — and, with
/// the default <c>PreserveStream=true</c>, on
/// <c>CLAUDE_CODE_DISABLE_NONSTREAMING_FALLBACK</c>.
/// </para>
/// <para>
/// Read at startup (config is registered <c>reloadOnChange:false</c>) — a restart is
/// required after changing a value.
/// </para>
/// </remarks>
internal sealed class ToolInputValidationOptions
{
    /// <summary>Master switch. Default true so malformed tool inputs are stopped
    /// before Claude Code commits them into the conversation context.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// true (default): keep streaming (preserve TTFT) and emit an SSE <c>error</c>
    /// event when a bad tool block closes — the bad block's <c>content_block_stop</c>
    /// is never delivered, so it never enters context, though its deltas have already
    /// rendered. false: buffer the full response first so a bad response is returned
    /// as a real HTTP status/body before any bytes reach the client.
    /// </summary>
    public bool PreserveStream { get; set; } = true;

    /// <summary>Which retryable error shape to emit. Default overloaded_error/529,
    /// matching the leak and runaway guards.</summary>
    public ResponseLeakSignal Signal { get; set; } = ResponseLeakSignal.OverloadedError;
}
