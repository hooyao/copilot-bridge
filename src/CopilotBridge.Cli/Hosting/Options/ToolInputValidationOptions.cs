namespace CopilotBridge.Cli.Hosting.Options;

/// <summary>
/// Bound from <c>Pipeline:Detectors:ToolInputValidation</c>. Controls the response
/// detector that validates streamed <c>tool_use.input</c> JSON against the request's
/// declared tool schemas and forces a clean retry on malformed tool arguments.
/// </summary>
internal sealed class ToolInputValidationOptions
{
    /// <summary>Master switch. Default true so malformed tool inputs fail before
    /// Claude Code rejects them as invalid tool parameters.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// true (default): keep streaming and emit an SSE error when a bad tool block
    /// closes. false: buffer the full response first so a bad response can be
    /// returned as a real HTTP status/body before any bytes reach the client.
    /// </summary>
    public bool PreserveStream { get; set; } = true;

    /// <summary>Which retryable error shape to emit. Default overloaded_error/529,
    /// matching the leak and runaway guards.</summary>
    public ResponseLeakSignal Signal { get; set; } = ResponseLeakSignal.OverloadedError;
}
