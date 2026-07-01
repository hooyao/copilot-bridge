using CopilotBridge.Cli.Hosting.Options;

namespace CopilotBridge.Cli.Pipeline.Response.Detection;

/// <summary>
/// Maps a <see cref="ToolLeakSignal"/> to the Anthropic error wire shape it emits
/// and the HTTP status it uses in buffered delivery. Shared by both delivery
/// modes so streaming injection and buffered rejection agree.
/// </summary>
internal static class ToolLeakError
{
    /// <summary>The Anthropic <c>error.type</c> string for a signal.</summary>
    public static string ErrorType(ToolLeakSignal signal) => signal switch
    {
        ToolLeakSignal.ApiError => "api_error",
        _ => "overloaded_error",
    };

    /// <summary>The HTTP status used in buffered delivery for a signal.</summary>
    public static int HttpStatus(ToolLeakSignal signal) => signal switch
    {
        ToolLeakSignal.ApiError => 500,
        _ => 529,
    };

    /// <summary>Human-readable diagnostic, prefixed so a reader can attribute it.</summary>
    public const string Message =
        "[copilot-bridge] The backend leaked a tool call as text; retrying the turn.";

    /// <summary>
    /// The Anthropic error JSON for the SSE <c>error</c> event / buffered body.
    /// Hand-built (not via a DTO) so it is AOT-safe and identical in both paths:
    /// <c>{"type":"error","error":{"type":"…","message":"…"}}</c>.
    /// </summary>
    public static string Json(ToolLeakSignal signal) =>
        "{\"type\":\"error\",\"error\":{\"type\":\""
        + ErrorType(signal)
        + "\",\"message\":\""
        + Message
        + "\"}}";
}
